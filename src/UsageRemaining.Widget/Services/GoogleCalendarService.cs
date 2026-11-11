using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public sealed partial class GoogleCalendarService : IGoogleCalendarService
{
    private const string Api = "https://www.googleapis.com/calendar/v3/";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scopes = "openid email https://www.googleapis.com/auth/calendar.calendarlist.readonly https://www.googleapis.com/auth/calendar.events";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private GoogleOAuthConfiguration _configuration;
    private readonly bool _ownsConfiguration;
    private readonly IGoogleCalendarCredentialStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _operationLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _operations = new();
    private GoogleCalendarCredentials? _credentials;
    private Dictionary<string, CalendarCache> _cache = new(StringComparer.Ordinal);
    private volatile GoogleCalendarState _state;
    private bool _disposed;

    public GoogleCalendarService(HttpClient? httpClient = null, GoogleOAuthConfiguration? configuration = null,
        IGoogleCalendarCredentialStore? credentialStore = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _ownsHttp = httpClient is null;
        _configuration = configuration ?? GoogleOAuthConfiguration.Load();
        _ownsConfiguration = configuration is null;
        _store = credentialStore ?? new GoogleCalendarCredentialStore();
        _state = new() { IsConfigured = _configuration.IsConfigured, ConfigurationMessage = _configuration.ConfigurationMessage };
    }

    public GoogleCalendarState State => _state;
    public event EventHandler? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_ownsConfiguration && _credentials is null)
            {
                _configuration = GoogleOAuthConfiguration.Load();
                Publish(_state with { IsConfigured = _configuration.IsConfigured, ConfigurationMessage = _configuration.ConfigurationMessage });
            }
            if (!_configuration.IsConfigured || _credentials is not null) return;
            GoogleCalendarCredentials? saved;
            try { saved = await _store.LoadAsync(linked.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
            {
                Publish(_state with { Warning = "저장된 Google 연결 정보를 읽을 수 없습니다. 다시 연결해 주세요." });
                return;
            }
            linked.Token.ThrowIfCancellationRequested();
            if (saved is null || string.IsNullOrWhiteSpace(saved.RefreshToken)) return;
            _credentials = saved;
            Publish(_state with { Connected = true, AccountEmail = saved.AccountEmail,
                SelectedCalendarIds = saved.SelectedCalendarIds.ToHashSet(StringComparer.Ordinal), Warning = null });
        }
        finally { _gate.Release(); }
    }

    public async Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        using var linked = CreateCancellation(cancellationToken);
        linked.CancelAfter(TimeSpan.FromMinutes(5));
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (!_configuration.IsConfigured) throw new GoogleCalendarException(_configuration.ConfigurationMessage);
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            // Reserve an ephemeral IPv4 loopback port, then bind only that loopback address.
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var redirect = $"http://127.0.0.1:{port}/";
            using var listener = new HttpListener();
            listener.Prefixes.Add(redirect);
            try { listener.Start(); }
            catch (HttpListenerException) { throw new GoogleCalendarException("Google 로그인용 로컬 수신기를 열 수 없습니다. 잠시 후 다시 시도해 주세요."); }
            using var stopListener = linked.Token.Register(() =>
            {
                try { listener.Stop(); }
                catch (ObjectDisposedException) { }
                catch (HttpListenerException) { }
            });
            var authorization = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + Query(new Dictionary<string, string>
            {
                ["client_id"] = _configuration.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
                ["scope"] = Scopes, ["state"] = state, ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent select_account"
            }));
            linked.Token.ThrowIfCancellationRequested();
            openBrowser(authorization);
            string? code = null;
            while (code is null)
            {
                linked.Token.ThrowIfCancellationRequested();
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                var valid = context.Request.Url?.AbsolutePath == "/" && context.Request.QueryString["state"] == state;
                var error = context.Request.QueryString["error"];
                code = valid ? context.Request.QueryString["code"] : null;
                var success = valid && string.IsNullOrEmpty(error) && !string.IsNullOrWhiteSpace(code);
                var bytes = Encoding.UTF8.GetBytes(success
                    ? "Google 인증 응답을 받았습니다. Codex Pulse에서 계정과 캘린더를 확인하며 연결을 마무리하고 있습니다. 앱으로 돌아가 최종 연결 결과를 확인해 주세요. 이 창은 닫아도 됩니다.\nGoogle authorization response received. Codex Pulse is still completing the connection. Return to the app for the final result."
                    : "Google 인증 응답을 처리하지 못했습니다. Codex Pulse에서 다시 시도해 주세요. 이 창은 닫아도 됩니다.");
                context.Response.StatusCode = valid ? 200 : 400;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                try { await context.Response.OutputStream.WriteAsync(bytes, linked.Token).ConfigureAwait(false); }
                finally { context.Response.Close(); }
                if (valid && !string.IsNullOrEmpty(error)) throw new GoogleCalendarException("Google 로그인이 취소되었거나 권한이 승인되지 않았습니다.");
                if (!success) code = null;
            }
            var token = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier
            }, linked.Token).ConfigureAwait(false);
            var candidate = ReadCredentials(token, null);
            using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", candidate.AccessToken);
            using var profileResponse = await SendTransportAsync(profileRequest, linked.Token).ConfigureAwait(false);
            if (!profileResponse.IsSuccessStatusCode) throw ApiFailure(profileResponse.StatusCode);
            var profile = await ReadObjectAsync(profileResponse, linked.Token).ConfigureAwait(false);
            var email = Text(profile, "email");
            if (string.IsNullOrWhiteSpace(email)) throw new GoogleCalendarException("Google 계정 정보를 확인할 수 없습니다. 이메일 권한을 승인해 주세요.");
            var calendars = await ListCalendarsAsync(candidate, linked.Token).ConfigureAwait(false);
            candidate = candidate with { AccountEmail = email, SelectedCalendarIds = calendars.Where(c => c.Primary).Select(c => c.Id).ToArray() };
            linked.Token.ThrowIfCancellationRequested();
            var previous = await _store.LoadAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var persisted = false;
            try
            {
                await _store.SaveAsync(candidate, linked.Token).ConfigureAwait(false);
                persisted = true;
                linked.Token.ThrowIfCancellationRequested();
            }
            catch (Exception saveError) when (saveError is not OutOfMemoryException && linked.IsCancellationRequested)
            {
                if (!persisted) throw new OperationCanceledException(linked.Token);
                // Keep the gate until rollback finishes, so a cancelled attempt cannot
                // overwrite a newer connection or leave its credentials on disk.
                try
                {
                    if (previous is null) await _store.ClearAsync(CancellationToken.None).ConfigureAwait(false);
                    else await _store.SaveAsync(previous, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw new GoogleCalendarCredentialRestoreException();
                }
                throw new OperationCanceledException(linked.Token);
            }
            // Completion is committed synchronously after the final cancellation check.
            _credentials = candidate;
            _cache = new(StringComparer.Ordinal);
            Publish(_state with { Connected = true, AccountEmail = email, Calendars = calendars, Events = [],
                SelectedCalendarIds = candidate.SelectedCalendarIds.ToHashSet(StringComparer.Ordinal), LastSyncedAt = null, Warning = null });
        }
        catch (Exception error) when (linked.IsCancellationRequested &&
            error is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException)
        {
            if (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested && !_operations.IsCancellationRequested)
                throw new GoogleCalendarConnectionTimeoutException();
            throw new OperationCanceledException("Google 연결이 취소되었습니다.", error, linked.Token);
        }
        finally { _gate.Release(); }
    }

    public async Task RefreshAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        if (end <= start) throw new ArgumentException("일정 조회 종료 시간은 시작 시간보다 늦어야 합니다.");
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var calendars = await ListCalendarsAsync(null, linked.Token).ConfigureAwait(false);
            var selected = _state.SelectedCalendarIds.Intersect(calendars.Select(c => c.Id), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var next = new Dictionary<string, CalendarCache>(StringComparer.Ordinal);
            foreach (var calendar in calendars.Where(c => selected.Contains(c.Id)))
            {
                _cache.TryGetValue(calendar.Id, out var previous);
                next[calendar.Id] = await ReadCalendarAsync(calendar, start, end, previous, linked.Token).ConfigureAwait(false);
            }
            linked.Token.ThrowIfCancellationRequested();
            _cache = next;
            Publish(_state with { Calendars = calendars, SelectedCalendarIds = selected,
                Events = VisibleEvents(next, start, end), LastSyncedAt = DateTimeOffset.Now, Warning = null });
        }
        finally { _gate.Release(); }
    }

    public async Task SelectCalendarsAsync(IEnumerable<string> calendarIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(calendarIds);
        var requested = calendarIds.ToHashSet(StringComparer.Ordinal);
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var valid = requested.Intersect(_state.Calendars.Select(c => c.Id), StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var credentials = _credentials! with { SelectedCalendarIds = valid.ToArray() };
            await _store.SaveAsync(credentials, linked.Token).ConfigureAwait(false);
            _credentials = credentials;
            _cache = _cache.Where(p => valid.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            Publish(_state with { SelectedCalendarIds = valid, Events = _state.Events.Where(e => valid.Contains(e.CalendarId)).ToArray() });
        }
        finally { _gate.Release(); }
    }

    public async Task<GoogleCalendarEvent> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var calendar = FindCalendar(calendarId);
            var raw = await ApiAsync(HttpMethod.Get, EventPath(calendarId, eventId), null, null, linked.Token).ConfigureAwait(false);
            return ParseEvent(calendar, raw, _state.AccountEmail);
        }
        finally { _gate.Release(); }
    }

    public async Task<GoogleCalendarEvent> SaveAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default)
    {
        ValidateEvent(value);
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var calendar = FindCalendar(value.CalendarId);
            if (!calendar.CanWrite) throw new GoogleCalendarException("이 캘린더에는 일정을 쓸 권한이 없습니다.");
            var creating = string.IsNullOrEmpty(value.Id);
            if (!creating && (!value.CanWrite || string.IsNullOrWhiteSpace(value.ETag)))
                throw new GoogleCalendarException("수정 권한 또는 최신 일정 정보가 없습니다. 일정을 다시 불러와 주세요.");
            if (!creating && value.Raw.Count > 0 && !ParseEvent(calendar, value.Raw, _state.AccountEmail).CanWrite)
                throw new GoogleCalendarException("이 일정에는 수정 권한이 없습니다.");
            var body = BuildEventPatch(value, calendar.TimeZone);
            var id = creating ? value.ClientGeneratedId : value.Id;
            if (creating) body["id"] = id;
            JsonObject saved;
            if (creating)
            {
                // A stable client ID makes retrying an ambiguous insert safe from duplicate creation.
                try { saved = await InsertAsync(value.CalendarId, body, linked.Token).ConfigureAwait(false); }
                catch (GoogleInsertUncertainException)
                {
                    try
                    {
                        saved = await ApiAsync(HttpMethod.Get, EventPath(value.CalendarId, id), null, null, linked.Token).ConfigureAwait(false);
                        VerifyRecoveredInsert(body, saved);
                    }
                    catch (GoogleNotFoundException) { saved = await InsertAsync(value.CalendarId, body, linked.Token).ConfigureAwait(false); }
                }
            }
            else saved = await ApiAsync(HttpMethod.Patch, EventPath(value.CalendarId, id), body, value.ETag, linked.Token).ConfigureAwait(false);
            var result = ParseEvent(calendar, saved, _state.AccountEmail);
            var seriesMaster = string.IsNullOrEmpty(value.RecurringEventId) &&
                (value.Recurrence.Length > 0 || value.Raw["recurrence"] is JsonArray { Count: > 0 });
            if (seriesMaster)
            {
                // Expanded instances must be rebuilt by Google after any master edit.
                if (_cache.TryGetValue(value.CalendarId, out var cache)) _cache[value.CalendarId] = cache with { SyncToken = "" };
                Publish(_state with { Warning = "반복 일정 저장 완료 · 일정 목록을 새로 확인하고 있습니다." });
            }
            else UpdateCachedEvent(result);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default)
    {
        using var linked = CreateCancellation(cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            RequireConnection();
            var calendar = FindCalendar(value.CalendarId);
            if (!calendar.CanWrite || !value.CanWrite || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.ETag))
                throw new GoogleCalendarException("이 일정을 삭제할 권한 또는 최신 일정 정보가 없습니다.");
            if (value.Raw.Count > 0 && !ParseEvent(calendar, value.Raw, _state.AccountEmail).CanWrite)
                throw new GoogleCalendarException("이 일정에는 삭제 권한이 없습니다.");
            await ApiAsync(HttpMethod.Delete, EventPath(value.CalendarId, value.Id), null, value.ETag, linked.Token).ConfigureAwait(false);
            bool Removed(GoogleCalendarEvent candidate) => candidate.CalendarId == value.CalendarId &&
                (candidate.Id == value.Id || (value.RecurringEventId is null && candidate.RecurringEventId == value.Id));
            if (_cache.TryGetValue(value.CalendarId, out var cache))
                foreach (var id in cache.Events.Where(pair => Removed(pair.Value)).Select(pair => pair.Key).ToArray()) cache.Events.Remove(id);
            Publish(_state with { Events = _state.Events.Where(e => !Removed(e)).ToArray(), Warning = null });
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Cancel in-flight HTTP/OAuth before waiting for the serialization gate.
        CancellationTokenSource interrupted;
        lock (_operationLock)
        {
            interrupted = _operations;
            interrupted.Cancel();
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try { await _gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch
        {
            ResetOperations(interrupted);
            throw;
        }
        try
        {
            await _store.ClearAsync(linked.Token).ConfigureAwait(false);
            _credentials = null;
            _cache = new(StringComparer.Ordinal);
            ResetOperations(interrupted);
            Publish(new() { IsConfigured = _configuration.IsConfigured, ConfigurationMessage = _configuration.ConfigurationMessage });
        }
        finally
        {
            ResetOperations(interrupted);
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_operationLock) _operations.Cancel();
        // Gates and cancellation sources remain valid until outstanding awaiters finish.
        if (_ownsHttp) _http.Dispose();
        Changed = null;
    }

    private CancellationTokenSource CreateCancellation(CancellationToken token)
    {
        lock (_operationLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _operations.Token);
        }
    }

    private void ResetOperations(CancellationTokenSource interrupted)
    {
        lock (_operationLock)
        {
            if (!ReferenceEquals(_operations, interrupted) || _disposed) return;
            _operations = new();
            interrupted.Dispose();
        }
    }

    private void RequireConnection()
    {
        if (_credentials is null || !_state.Connected) throw new GoogleCalendarException("먼저 Google Calendar에 연결해 주세요.");
    }

    private GoogleCalendarInfo FindCalendar(string id) => _state.Calendars.FirstOrDefault(c => c.Id == id)
        ?? throw new GoogleCalendarException("선택한 Google 캘린더를 찾을 수 없습니다. 새로 고침해 주세요.");

    private void Publish(GoogleCalendarState state)
    {
        _state = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record CalendarCache(DateTimeOffset Start, DateTimeOffset End, string SyncToken, Dictionary<string, GoogleCalendarEvent> Events);
    private sealed class GoogleNotFoundException : GoogleCalendarException { public GoogleNotFoundException() : base("Google 일정을 찾을 수 없습니다.") { } }
    private sealed class GoogleSyncExpiredException : GoogleCalendarException
    {
        public GoogleSyncExpiredException() : base("Google 동기화 정보가 만료되었습니다. 다시 새로 고침해 주세요.") { }
    }
    private sealed class GoogleInsertUncertainException : GoogleCalendarException
    {
        public GoogleInsertUncertainException() : base("Google 일정 생성 결과를 확인할 수 없습니다. 같은 창에서 다시 저장하거나 새로 고침해 주세요.") { }
    }
}
