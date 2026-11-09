using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public sealed partial class GoogleCalendarService
{
    private async Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(GoogleCalendarCredentials? candidate, CancellationToken token)
    {
        var result = new List<GoogleCalendarInfo>();
        string? page = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var path = "users/me/calendarList?maxResults=250" + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var response = await ApiAsync(HttpMethod.Get, path, null, null, token, candidate).ConfigureAwait(false);
            if (response["items"] is JsonArray items)
                foreach (var raw in items.OfType<JsonObject>())
                {
                    var calendar = new GoogleCalendarInfo { Id = Text(raw, "id"), Summary = Text(raw, "summary"),
                        AccessRole = Text(raw, "accessRole"), TimeZone = Text(raw, "timeZone"), Primary = Flag(raw, "primary") };
                    if (!string.IsNullOrEmpty(calendar.Id) && calendar.AccessRole is "reader" or "writer" or "owner") result.Add(calendar);
                }
            page = OptionalText(response, "nextPageToken");
            if (page is not null && !visited.Add(page)) throw new GoogleCalendarException("Google 캘린더 페이지 정보가 올바르지 않습니다. 다시 시도해 주세요.");
        } while (!string.IsNullOrEmpty(page));
        return result.GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.Last()).OrderByDescending(c => c.Primary).ThenBy(c => c.Summary).ToArray();
    }

    private async Task<CalendarCache> ReadCalendarAsync(GoogleCalendarInfo calendar, DateTimeOffset start, DateTimeOffset end,
        CalendarCache? previous, CancellationToken token)
    {
        var incremental = previous is not null && previous.Start == start && previous.End == end && previous.SyncToken.Length > 0;
        try { return await ReadCalendarPagesAsync(calendar, start, end, incremental ? previous : null, token).ConfigureAwait(false); }
        catch (GoogleSyncExpiredException)
        {
            // Keep the published cache until the replacement full snapshot has completed.
            return await ReadCalendarPagesAsync(calendar, start, end, null, token).ConfigureAwait(false);
        }
    }

    private async Task<CalendarCache> ReadCalendarPagesAsync(GoogleCalendarInfo calendar, DateTimeOffset start, DateTimeOffset end,
        CalendarCache? previous, CancellationToken token)
    {
        var events = previous is null ? new Dictionary<string, GoogleCalendarEvent>(StringComparer.Ordinal) :
            previous.Events.ToDictionary(p => p.Key, p => ParseEvent(calendar, p.Value.Raw, _state.AccountEmail), StringComparer.Ordinal);
        string? page = null;
        var sync = "";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var parameters = new Dictionary<string, string>
            {
                ["singleEvents"] = "true", ["showDeleted"] = "true", ["maxResults"] = "2500"
            };
            if (previous is null)
            {
                parameters["timeMin"] = start.ToString("o", CultureInfo.InvariantCulture);
                parameters["timeMax"] = end.ToString("o", CultureInfo.InvariantCulture);
            }
            else parameters["syncToken"] = previous.SyncToken;
            if (!string.IsNullOrEmpty(page)) parameters["pageToken"] = page;
            var response = await ApiAsync(HttpMethod.Get, "calendars/" + Uri.EscapeDataString(calendar.Id) + "/events?" + Query(parameters), null, null, token).ConfigureAwait(false);
            if (response["items"] is JsonArray items)
                foreach (var raw in items.OfType<JsonObject>())
                {
                    var value = ParseEvent(calendar, raw, _state.AccountEmail);
                    if (string.IsNullOrEmpty(value.Id)) continue;
                    if (value.Cancelled) events.Remove(value.Id);
                    else events[value.Id] = value;
                }
            page = OptionalText(response, "nextPageToken");
            if (page is not null && !visited.Add(page)) throw new GoogleCalendarException("Google 일정 페이지 정보가 올바르지 않습니다. 다시 시도해 주세요.");
            if (string.IsNullOrEmpty(page)) sync = Text(response, "nextSyncToken");
        } while (!string.IsNullOrEmpty(page));
        return new(start, end, sync, events);
    }

    private async Task<JsonObject> InsertAsync(string calendarId, JsonObject body, CancellationToken token)
    {
        try { return await ApiAsync(HttpMethod.Post, "calendars/" + Uri.EscapeDataString(calendarId) + "/events", body, null, token).ConfigureAwait(false); }
        catch (GoogleInsertUncertainException) { throw; }
    }

    private async Task<JsonObject> ApiAsync(HttpMethod method, string path, JsonObject? body, string? etag, CancellationToken token,
        GoogleCalendarCredentials? candidate = null)
    {
        if (candidate is null) await EnsureAccessTokenAsync(false, token).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(method, Api + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (candidate ?? _credentials!).AccessToken);
            if (!string.IsNullOrEmpty(etag)) request.Headers.TryAddWithoutValidation("If-Match", etag);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            HttpResponseMessage response;
            try { response = await SendTransportAsync(request, token).ConfigureAwait(false); }
            catch (GoogleCalendarException) when (method == HttpMethod.Post)
            {
                throw new GoogleInsertUncertainException();
            }
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && candidate is null && attempt == 0)
                {
                    await EnsureAccessTokenAsync(true, token).ConfigureAwait(false);
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new GoogleCalendarConflictException();
                if (response.StatusCode == HttpStatusCode.Gone) throw new GoogleSyncExpiredException();
                if (response.StatusCode == HttpStatusCode.NotFound) throw new GoogleNotFoundException();
                if (response.StatusCode == HttpStatusCode.Conflict && method == HttpMethod.Post)
                {
                    var existing = await ApiAsync(HttpMethod.Get, path + "/" + Uri.EscapeDataString(Text(body, "id")), null, null, token, candidate).ConfigureAwait(false);
                    VerifyRecoveredInsert(body!, existing);
                    return existing;
                }
                if ((int)response.StatusCode >= 500 && method == HttpMethod.Post) throw new GoogleInsertUncertainException();
                if (!response.IsSuccessStatusCode) throw await ApiFailureAsync(response, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NoContent || method == HttpMethod.Delete) return new JsonObject();
                return await ReadObjectAsync(response, token).ConfigureAwait(false);
            }
        }
        throw new GoogleCalendarException("Google 인증이 만료되었습니다. 다시 연결해 주세요.");
    }

    private async Task EnsureAccessTokenAsync(bool force, CancellationToken token)
    {
        RequireConnection();
        if (!force && _credentials!.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return;
        if (string.IsNullOrWhiteSpace(_credentials!.RefreshToken))
            throw new GoogleCalendarException("Google 연결을 갱신할 수 없습니다. 다시 연결해 주세요.");
        JsonObject response;
        try
        {
            response = await RequestTokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = _credentials.RefreshToken
            }, token).ConfigureAwait(false);
        }
        catch (GoogleInvalidGrantException)
        {
            await _store.ClearAsync(token).ConfigureAwait(false);
            _credentials = null;
            _cache = new(StringComparer.Ordinal);
            Publish(new() { IsConfigured = _configuration.IsConfigured, ConfigurationMessage = _configuration.ConfigurationMessage,
                Warning = "Google 연결 권한이 만료되거나 취소되었습니다. 다시 연결해 주세요." });
            throw new GoogleCalendarException("Google 연결 권한이 만료되거나 취소되었습니다. 다시 연결해 주세요.");
        }
        var updated = ReadCredentials(response, _credentials);
        await _store.SaveAsync(updated, token).ConfigureAwait(false);
        _credentials = updated;
    }

    private async Task<JsonObject> RequestTokenAsync(Dictionary<string, string> values, CancellationToken token)
    {
        values["client_id"] = _configuration.ClientId;
        if (!string.IsNullOrWhiteSpace(_configuration.ClientSecret)) values["client_secret"] = _configuration.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(values) };
        using var response = await SendTransportAsync(request, token).ConfigureAwait(false);
        var body = await ReadObjectAsync(response, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (Text(body, "error") == "invalid_grant") throw new GoogleInvalidGrantException();
            throw new GoogleCalendarException("Google 로그인을 완료하지 못했습니다. OAuth 설정과 승인한 권한을 확인해 주세요.");
        }
        if (body["scope"] is JsonValue scope && scope.TryGetValue<string>(out var granted))
        {
            var scopes = granted.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (!scopes.Contains("https://www.googleapis.com/auth/calendar") &&
                (!scopes.Contains("https://www.googleapis.com/auth/calendar.events") ||
                 !scopes.Contains("https://www.googleapis.com/auth/calendar.calendarlist.readonly")))
                throw new GoogleCalendarException("Google Calendar 읽기 및 쓰기 권한을 모두 승인해 주세요.");
        }
        return body;
    }

    private static GoogleCalendarCredentials ReadCredentials(JsonObject body, GoogleCalendarCredentials? previous)
    {
        var access = Text(body, "access_token");
        var refresh = OptionalText(body, "refresh_token") ?? previous?.RefreshToken ?? "";
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh))
            throw new GoogleCalendarException("Google에서 오프라인 연결 정보를 받지 못했습니다. 다시 연결하여 권한을 승인해 주세요.");
        var seconds = body["expires_in"]?.GetValue<int>() ?? 3600;
        return (previous ?? new GoogleCalendarCredentials()) with { AccessToken = access, RefreshToken = refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 86400)) };
    }

    private static void VerifyRecoveredInsert(JsonObject requested, JsonObject existing)
    {
        var calendar = new GoogleCalendarInfo();
        var expected = ParseEvent(calendar, requested);
        var actual = ParseEvent(calendar, existing);
        if (expected.Id != actual.Id || expected.Summary != actual.Summary || expected.Description != actual.Description ||
            expected.Location != actual.Location || expected.AllDay != actual.AllDay || expected.StartDate != actual.StartDate ||
            expected.EndDate != actual.EndDate || expected.Start != actual.Start || expected.End != actual.End ||
            !expected.Recurrence.SequenceEqual(actual.Recurrence) || expected.UseDefaultReminders != actual.UseDefaultReminders ||
            expected.ReminderMinutes != actual.ReminderMinutes || actual.Cancelled)
            throw new GoogleCalendarConflictException();
    }

    private async Task<HttpResponseMessage> SendTransportAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (HttpRequestException) { throw new GoogleCalendarException("Google에 연결할 수 없습니다. 네트워크 연결을 확인해 주세요."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new GoogleCalendarException("Google 요청 시간이 초과되었습니다. 다시 시도해 주세요.");
        }
    }

    private static async Task<JsonObject> ReadObjectAsync(HttpResponseMessage response, CancellationToken token)
    {
        // ResponseHeadersRead completes before the body is received; HttpClient's
        // header timeout alone cannot bound a stalled JSON response stream.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await JsonNode.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false) as JsonObject
                ?? throw new GoogleCalendarException("Google 응답 형식이 올바르지 않습니다. 다시 시도해 주세요.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new GoogleCalendarException("Google 응답을 받는 시간이 초과되었습니다. 다시 시도해 주세요."); }
        catch (JsonException) { throw new GoogleCalendarException("Google 응답을 읽을 수 없습니다. 다시 시도해 주세요."); }
        catch (IOException) { throw new GoogleCalendarException("Google 응답을 받는 중 연결이 끊겼습니다. 다시 시도해 주세요."); }
    }

    private static async Task<GoogleCalendarException> ApiFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden) return ApiFailure(response.StatusCode);
        const int maximumBytes = 16 * 1024;
        if (response.Content.Headers.ContentLength > maximumBytes) return ApiFailure(response.StatusCode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = new byte[maximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > maximumBytes) return ApiFailure(response.StatusCode);
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
            var message = ForbiddenFailureMessage(document.RootElement);
            return message is null ? ApiFailure(response.StatusCode) : new GoogleCalendarException(message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return ApiFailure(response.StatusCode); }
        catch (Exception error) when (error is JsonException or IOException or HttpRequestException)
        { return ApiFailure(response.StatusCode); }
    }

    private static string? ForbiddenFailureMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object) return null;
        if (error.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            foreach (var entry in errors.EnumerateArray())
            {
                var message = ForbiddenReasonMessage(ErrorText(entry, "reason"));
                if (message is not null) return message;
            }
        if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            foreach (var entry in details.EnumerateArray())
            {
                if (ErrorText(entry, "@type") != "type.googleapis.com/google.rpc.ErrorInfo") continue;
                var reason = ErrorText(entry, "reason");
                if (reason == "SERVICE_DISABLED" && entry.TryGetProperty("metadata", out var metadata))
                {
                    if (metadata.ValueKind != JsonValueKind.Object) continue;
                    if (metadata.TryGetProperty("service", out var service) &&
                        (service.ValueKind != JsonValueKind.String || service.GetString() != "calendar-json.googleapis.com")) continue;
                }
                var message = ForbiddenReasonMessage(reason);
                if (message is not null) return message;
            }
        return null;
    }

    private static string? ErrorText(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static string? ForbiddenReasonMessage(string? reason) => reason switch
    {
        "SERVICE_DISABLED" or "accessNotConfigured" =>
            "Google Calendar API가 사용 중지되어 있습니다. OAuth 클라이언트와 같은 Google Cloud 프로젝트에서 Google Calendar API를 사용 설정한 뒤 다시 연결해 주세요.",
        "ACCESS_TOKEN_SCOPE_INSUFFICIENT" or "insufficientPermissions" =>
            "Google Calendar 권한이 부족합니다. 다시 연결하여 캘린더 목록 읽기와 일정 읽기·쓰기 권한을 모두 승인해 주세요.",
        "rateLimitExceeded" or "userRateLimitExceeded" or "QUOTA_EXCEEDED" =>
            "Google Calendar 요청 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
        _ => null
    };

    private static GoogleCalendarException ApiFailure(HttpStatusCode status) => new(status switch
    {
        HttpStatusCode.Unauthorized => "Google 인증이 만료되었습니다. 다시 연결해 주세요.",
        HttpStatusCode.Forbidden => "Google Calendar 권한이 없거나 요청 한도를 초과했습니다. 승인한 권한을 확인하고 잠시 후 다시 시도해 주세요.",
        HttpStatusCode.TooManyRequests => "Google 요청 한도를 초과했습니다. 잠시 후 다시 시도해 주세요.",
        HttpStatusCode.BadRequest => "Google에서 일정 요청을 처리하지 못했습니다. 날짜, 시간대 및 반복 규칙을 확인해 주세요.",
        _ => "Google Calendar 요청을 완료하지 못했습니다. 잠시 후 다시 시도해 주세요."
    });

    private static string EventPath(string calendar, string id)
    {
        if (string.IsNullOrWhiteSpace(calendar) || string.IsNullOrWhiteSpace(id)) throw new ArgumentException("캘린더와 일정 ID가 필요합니다.");
        return "calendars/" + Uri.EscapeDataString(calendar) + "/events/" + Uri.EscapeDataString(id);
    }

    private static string Query(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed class GoogleInvalidGrantException : GoogleCalendarException
    {
        public GoogleInvalidGrantException() : base("Google 연결 권한이 만료되거나 취소되었습니다. 다시 연결해 주세요.") { }
    }
}
