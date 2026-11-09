using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class GoogleCalendarChecks
{
    private const string Calendars = """{"items":[{"id":"fixture-calendar","summary":"Fixture","accessRole":"owner","primary":true,"timeZone":"America/New_York"},{"id":"readonly-calendar","summary":"Read only","accessRole":"reader","timeZone":"Asia/Seoul"}]}""";
    private const string AllDay = """{"id":"all-day","etag":"\"version-1\"","organizer":{"self":true},"summary":"Three days","start":{"date":"2026-11-09"},"end":{"date":"2026-11-12"}}""";
    private const string Instance = """{"id":"instance","etag":"\"instance-v1\"","organizer":{"self":true},"summary":"DST event","recurringEventId":"master","originalStartTime":{"dateTime":"2026-11-01T01:30:00-04:00","timeZone":"America/New_York"},"start":{"dateTime":"2026-11-01T01:30:00-04:00","timeZone":"America/New_York"},"end":{"dateTime":"2026-11-01T01:30:00-05:00","timeZone":"America/New_York"},"reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":30}]},"extendedProperties":{"private":{"fixture":"preserve-me"}}}""";
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMonths(2);

    public static async Task RunAsync(Action<bool, string> check, bool oauthLoopback = false)
    {
        Mapping(check);
        HolidayMapping(check);
        await ConfigurationRecheck(check);
        await SynchronizationAndWrites(check);
        await CancellationIsAtomic(check);
        await InvalidAuthorization(check);
        await ApiErrorClassification(check);
        await ProtectedStore(check);
        if (oauthLoopback)
        {
            await OAuthLoopback(check);
            await OAuthPostCallbackFailures(check);
            await OAuthCommitBoundary(check);
        }
    }

    private static async Task ConfigurationRecheck(Action<bool, string> check)
    {
        var originalDirectory = Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR");
        var originalId = Environment.GetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID");
        var originalSecret = Environment.GetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_SECRET");
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "google-configuration-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "google-oauth-client.json"), "{}");
        try
        {
            Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", directory);
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID", null);
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_SECRET", null);
            using var transport = new ScriptedHandler();
            using var http = new HttpClient(transport);
            var credentials = new MemoryCredentials { Value = null };
            using var reloadable = new GoogleCalendarService(http, credentialStore: credentials);
            using var injected = new GoogleCalendarService(http, new GoogleOAuthConfiguration(), new MemoryCredentials { Value = null });
            check(!reloadable.State.IsConfigured && !injected.State.IsConfigured,
                "Google registration fixture starts unconfigured using isolated developer configuration");
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID", "fixture-rechecked-client.invalid");
            await reloadable.InitializeAsync();
            await injected.InitializeAsync();
            check(reloadable.State.IsConfigured && !reloadable.State.Connected && !injected.State.IsConfigured,
                "Google recheck reloads default registration and preserves injected configuration without authorization");
            credentials.Value = MemoryCredentials.Fixture;
            await reloadable.InitializeAsync();
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID", null);
            await reloadable.InitializeAsync();
            check(reloadable.State.IsConfigured && reloadable.State.Connected && reloadable.State.AccountEmail == MemoryCredentials.Fixture.AccountEmail &&
                transport.Requests.Count == 0,
                "Google recheck preserves established registration and connected credentials without HTTP");
            transport.AssertComplete(check);
        }
        finally
        {
            Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", originalDirectory);
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_ID", originalId);
            Environment.SetEnvironmentVariable("CODEX_PULSE_GOOGLE_CLIENT_SECRET", originalSecret);
        }
    }

    private static async Task OAuthLoopback(Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        var store = new MemoryCredentials { Value = null };
        using var service = new GoogleCalendarService(http, Configuration(), store);
        Dictionary<string, string>? authorization = null;
        transport.Enqueue("POST", "oauth2.googleapis.com/token", """{"access_token":"oauth-fixture-access","refresh_token":"oauth-fixture-refresh","expires_in":3600,"scope":"openid email https://www.googleapis.com/auth/calendar.events https://www.googleapis.com/auth/calendar.calendarlist.readonly"}""", inspect: request =>
        {
            var fields = QueryFields(request.Body);
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(fields["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            check(authorization is not null && authorization["code_challenge_method"] == "S256" && authorization["code_challenge"] == challenge &&
                fields["code"] == "fixture-authorization-code" && fields["redirect_uri"] == authorization["redirect_uri"],
                "Google OAuth exchanges code with matching PKCE verifier and loopback redirect");
        });
        transport.Enqueue("GET", "openidconnect.googleapis.com/v1/userinfo", """{"email":"oauth-fixture@example.invalid"}""");
        transport.Enqueue("GET", "calendarList", Calendars);
        Task? callback = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await service.ConnectAsync(address =>
        {
            authorization = QueryFields(address.Query.TrimStart('?'));
            var redirect = new Uri(authorization["redirect_uri"]);
            check(address.Scheme == "https" && address.Host == "accounts.google.com" && redirect.Host == "127.0.0.1" &&
                authorization["scope"].Contains("calendar.events") && authorization["prompt"].Contains("select_account") && authorization["state"].Length >= 32,
                "Google OAuth requests account choice scopes and a random loopback state");
            callback = SendOAuthCallbacks(redirect, authorization["state"], check, timeout.Token);
        }, timeout.Token);
        if (callback is null) throw new InvalidOperationException("OAuth fixture callback was not started");
        await callback;
        check(service.State.Connected && service.State.AccountEmail == "oauth-fixture@example.invalid" &&
            service.State.SelectedCalendarIds.SetEquals(new[] { "fixture-calendar" }) && store.Value?.RefreshToken == "oauth-fixture-refresh",
            "Google OAuth persists connected identity and primary calendar after valid callback");
        var before = service.State;
        using var cancel = new CancellationTokenSource();
        await Expect<OperationCanceledException>(() => service.ConnectAsync(_ => cancel.Cancel(), cancel.Token), check,
            "Google OAuth cancellation closes loopback authorization wait");
        check(ReferenceEquals(before, service.State) && store.Value?.AccountEmail == "oauth-fixture@example.invalid",
            "Google cancelled account change preserves existing connected account");
        transport.AssertComplete(check);
    }

    private static async Task SendOAuthCallbacks(Uri redirect, string state, Action<bool, string> check, CancellationToken token)
    {
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        using var invalid = await local.GetAsync(new Uri(redirect, "?state=wrong-fixture-state&code=invalid-code"), token);
        check(invalid.StatusCode == HttpStatusCode.BadRequest, "Google OAuth rejects mismatched callback state before token exchange");
        using var valid = await local.GetAsync(new Uri(redirect, "?state=" + Uri.EscapeDataString(state) + "&code=fixture-authorization-code"), token);
        check(valid.IsSuccessStatusCode, "Google OAuth accepts valid loopback callback after rejected state");
    }

    private static Dictionary<string, string> QueryFields(string query) => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
            parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "", StringComparer.Ordinal);

    private static void Mapping(Action<bool, string> check)
    {
        var calendar = new GoogleCalendarInfo { Id = "fixture-calendar", AccessRole = "owner", TimeZone = "America/New_York" };
        var span = new GoogleCalendarEvent { AllDay = true, StartDate = new(2026, 11, 6), EndDate = new(2026, 11, 12) };
        check(CalendarEventPresentation.OccursOn(span, new(2026, 11, 6)) && CalendarEventPresentation.OccursOn(span, new(2026, 11, 11)) &&
            !CalendarEventPresentation.OccursOn(span, new(2026, 11, 5)) && !CalendarEventPresentation.OccursOn(span, new(2026, 11, 12)),
            "Google multiday presentation crosses week boundary and excludes end date");
        var localMidnight = CalendarEventPresentation.LocalMidnight(new(2026, 11, 10));
        var timed = new GoogleCalendarEvent { Start = localMidnight.AddHours(-2), End = localMidnight };
        check(CalendarEventPresentation.OccursOn(timed, new(2026, 11, 9)) && !CalendarEventPresentation.OccursOn(timed, new(2026, 11, 10)),
            "Google timed event ending at local midnight does not occupy following date");
        var raw = JsonNode.Parse(Instance)!.AsObject();
        var value = GoogleCalendarService.ParseEvent(calendar, raw);
        raw["summary"] = "Mutated transport object";
        check(value.Raw["summary"]?.GetValue<string>() == "DST event", "Google parsed event owns a copy of provider fields");
        var patch = GoogleCalendarService.BuildEventPatch(value with { Summary = "Only changed title" });
        check(patch.Count == 1 && patch["summary"]?.GetValue<string>() == "Only changed title",
            "Google title-only patch preserves provider metadata recurrence and original time offsets");
        var guestRaw = JsonNode.Parse(Instance)!.AsObject();
        guestRaw["organizer"] = new JsonObject { ["email"] = "someone-else@example.invalid" };
        check(!GoogleCalendarService.ParseEvent(calendar, guestRaw, "fixture@example.invalid").CanWrite,
            "Google organizer-owned event without guest modification permission stays read only");
        guestRaw["guestsCanModify"] = true;
        check(GoogleCalendarService.ParseEvent(calendar, guestRaw, "fixture@example.invalid").CanWrite,
            "Google explicitly granted guest modification permits editing");
        guestRaw["eventType"] = "birthday";
        check(!GoogleCalendarService.ParseEvent(calendar, guestRaw).CanWrite, "Google special birthday event is protected from unsupported writes");
        try
        {
            GoogleCalendarService.ValidateEvent(value with { Recurrence = ["RRULE:FREQ=WEEKLY"] });
            check(false, "Google recurrence rule cannot be applied directly to an instance");
        }
        catch (ArgumentException) { check(true, "Google recurrence rule cannot be applied directly to an instance"); }
    }

    private static async Task SynchronizationAndWrites(Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        var credentials = new MemoryCredentials();
        using var service = new GoogleCalendarService(http, Configuration(), credentials);
        await service.InitializeAsync();
        check(service.State.Connected && service.State.AccountEmail == "fixture@example.invalid" && transport.Requests.Count == 0,
            "Google initialization restores isolated credentials without HTTP");
        transport.Enqueue("GET", "users/me/calendarList", Calendars);
        transport.Enqueue("GET", "events", "{\"items\":[" + AllDay + "],\"nextPageToken\":\"page-two\"}");
        transport.Enqueue("GET", "pageToken=page-two", "{\"items\":[" + Instance + "],\"nextSyncToken\":\"sync-one\"}");
        await service.RefreshAsync(Start, End);
        var day = service.State.Events.Single(e => e.Id == "all-day");
        var instance = service.State.Events.Single(e => e.Id == "instance");
        check(day.AllDay && day.StartDate == new DateOnly(2026, 11, 9) && day.EndDate == new DateOnly(2026, 11, 12),
            "Google all-day start and exclusive end survive paginated synchronization");
        check(instance.End - instance.Start == TimeSpan.FromHours(1) && instance.TimeZone == "America/New_York" &&
            instance.RecurringEventId == "master" && instance.OriginalStart == instance.Start && instance.ReminderMinutes == 30,
            "Google DST offsets recurrence instance identity and reminder remain explicit");
        check(service.State.Calendars.Count == 2 && !service.State.Calendars.Single(c => c.Id == "readonly-calendar").CanWrite,
            "Google read-only calendar access is preserved");

        transport.Enqueue("GET", "users/me/calendarList", Calendars);
        transport.Enqueue("GET", "syncToken=sync-one", "{\"items\":[{\"id\":\"all-day\",\"status\":\"cancelled\"}],\"nextSyncToken\":\"sync-two\"}");
        await service.RefreshAsync(Start, End);
        check(service.State.Events.Count == 1 && service.State.Events[0].Id == "instance", "Google incremental tombstones remove cancelled events without losing unchanged events");

        transport.Enqueue("GET", "users/me/calendarList", Calendars);
        transport.Enqueue("GET", "syncToken=sync-two", "{}", HttpStatusCode.Gone);
        transport.Enqueue("GET", "events", "{\"items\":[" + AllDay + "," + Instance + "],\"nextSyncToken\":\"sync-three\"}");
        await service.RefreshAsync(Start, End);
        check(service.State.Events.Count == 2 && !transport.Requests[^1].Uri.Contains("syncToken="), "Google expired sync token performs complete replacement refresh");

        var before = service.State;
        transport.Enqueue("PATCH", "events/instance", "{}", HttpStatusCode.PreconditionFailed, request =>
        {
            check(request.IfMatch == "\"instance-v1\"", "Google updates send the original ETag precondition");
            var patch = JsonNode.Parse(request.Body)!.AsObject();
            check(patch["summary"]?.GetValue<string>() == "Edited locally" && patch["recurringEventId"] is null,
                "Google instance edit changes editable fields without rewriting recurrence identity");
        });
        await Expect<GoogleCalendarConflictException>(() => service.SaveAsync(instance with { Summary = "Edited locally" }), check,
            "Google ETag conflict rejects overwrite");
        check(ReferenceEquals(before, service.State), "Google conflict leaves synchronized state unchanged");
        var count = transport.Requests.Count;
        await Expect<GoogleCalendarException>(() => service.SaveAsync(day with { CalendarId = "readonly-calendar" }), check,
            "Google read-only calendar rejects local writes");
        check(transport.Requests.Count == count, "Google read-only rejection sends no write request");

        transport.Enqueue("GET", "events/master", """{"id":"master","etag":"\"master-v1\"","organizer":{"self":true},"summary":"Weekly master","start":{"dateTime":"2026-11-01T01:30:00-04:00","timeZone":"America/New_York"},"end":{"dateTime":"2026-11-01T02:30:00-05:00","timeZone":"America/New_York"},"recurrence":["RRULE:FREQ=WEEKLY"]}""");
        var master = await service.GetEventAsync("fixture-calendar", "master");
        check(master.RecurringEventId is null && master.Recurrence.SequenceEqual(new[] { "RRULE:FREQ=WEEKLY" }) && master.ETag == "\"master-v1\"",
            "Google entire-series lookup uses the master and its own ETag");
        var allDayPatch = GoogleCalendarService.BuildEventPatch(day with { Id = "", Raw = new() });
        check(allDayPatch["end"]?["date"]?.GetValue<string>() == "2026-11-12", "Google new all-day event serializes exclusive end");
        transport.Enqueue("PATCH", "events/all-day", AllDay.Replace("Three days", "Saved title"), inspect: request =>
            check(JsonNode.Parse(request.Body)?["end"] is null, "Google title-only edit leaves unchanged exclusive end untouched"));
        var saved = await service.SaveAsync(day with { Summary = "Saved title" });
        check(saved.Summary == "Saved title" && service.State.Events.Any(e => e.Id == "all-day" && e.Summary == "Saved title"), "Google successful save commits returned server event");
        transport.Enqueue("DELETE", "events/all-day", "", HttpStatusCode.NoContent, request =>
            check(request.IfMatch == "\"version-1\"", "Google delete sends ETag precondition"));
        await service.DeleteAsync(saved);
        check(service.State.Events.All(e => e.Id != "all-day"), "Google successful delete removes synchronized event");
        const string generatedId = "0123456789abcdef0123456789abcdef";
        var draft = day with { Id = "", ETag = "", Raw = new(), ClientGeneratedId = generatedId };
        transport.Enqueue("POST", "/events", "{}", HttpStatusCode.InternalServerError, request =>
            check(JsonNode.Parse(request.Body)?["id"]?.GetValue<string>() == generatedId, "Google insert uses stable client-generated identity"));
        transport.Enqueue("GET", "events/" + generatedId, AllDay.Replace("all-day", generatedId));
        var recovered = await service.SaveAsync(draft);
        check(recovered.Id == generatedId && service.State.Events.Count(e => e.Id == generatedId) == 1,
            "Google ambiguous insert recovers the created event without issuing duplicate insert");
        transport.Enqueue("DELETE", "events/master", "", HttpStatusCode.NoContent);
        await service.DeleteAsync(master);
        check(service.State.Events.All(e => e.Id != "master" && e.RecurringEventId != "master"),
            "Google series deletion immediately removes its visible instances");
        transport.Enqueue("GET", "calendarList", Calendars);
        transport.Enqueue("GET", "syncToken=sync-three", "{\"items\":[],\"nextSyncToken\":\"sync-four\"}");
        await service.RefreshAsync(Start, End);
        check(service.State.Events.All(e => e.RecurringEventId != "master"), "Google incremental refresh does not resurrect deleted series instances");
        transport.AssertComplete(check);
    }

    private static async Task CancellationIsAtomic(Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        using var service = new GoogleCalendarService(http, Configuration(), new MemoryCredentials());
        await service.InitializeAsync();
        transport.Enqueue("GET", "calendarList", Calendars);
        transport.Enqueue("GET", "events", "{\"items\":[" + AllDay + "],\"nextSyncToken\":\"atomic-one\"}");
        await service.RefreshAsync(Start, End);
        var before = service.State;
        using var cancelled = new CancellationTokenSource();
        transport.Enqueue("GET", "calendarList", Calendars);
        transport.Enqueue("GET", "events", "{\"items\":[{\"id\":\"all-day\",\"status\":\"cancelled\"}],\"nextPageToken\":\"stop-here\"}");
        transport.Enqueue("GET", "pageToken=stop-here", "{}", inspect: _ => cancelled.Cancel());
        await Expect<OperationCanceledException>(() => service.RefreshAsync(Start, End, cancelled.Token), check,
            "Google caller cancellation aborts a paginated refresh");
        check(ReferenceEquals(before, service.State) && service.State.Events.Single().Id == "all-day",
            "Google cancellation publishes no partial page or deletion");
        await service.DisconnectAsync();
        check(!service.State.Connected && service.State.Events.Count == 0, "Google disconnect clears local state after cancellation");
        transport.AssertComplete(check);
    }

    private static async Task InvalidAuthorization(Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        var store = new MemoryCredentials { Value = MemoryCredentials.Fixture with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) } };
        using var service = new GoogleCalendarService(http, Configuration(), store);
        await service.InitializeAsync();
        transport.Enqueue("POST", "oauth2.googleapis.com/token", """{"error":"invalid_grant","error_description":"fixture-secret-must-not-appear"}""", HttpStatusCode.BadRequest);
        try { await service.RefreshAsync(Start, End); check(false, "Google revoked authorization fails"); }
        catch (GoogleCalendarException exception)
        {
            check(!exception.Message.Contains("fixture-secret") && !service.State.Connected, "Google revoked authorization disconnects with a sanitized message");
        }
        transport.AssertComplete(check);
    }

    private static async Task ProtectedStore(Action<bool, string> check)
    {
        var directory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "google-credential-checks", Guid.NewGuid().ToString("N"));
        var store = new GoogleCalendarCredentialStore(directory);
        await store.SaveAsync(MemoryCredentials.Fixture, CancellationToken.None);
        var path = Path.Combine(directory, "google-auth", "credentials.dpapi");
        var encrypted = await File.ReadAllBytesAsync(path);
        check(!Encoding.UTF8.GetString(encrypted).Contains("fixture-refresh-token"), "Google credential file protects refresh token at rest");
        var loaded = await store.LoadAsync(CancellationToken.None);
        check(loaded?.AccessToken == MemoryCredentials.Fixture.AccessToken && loaded.RefreshToken == MemoryCredentials.Fixture.RefreshToken &&
            loaded.SelectedCalendarIds.SequenceEqual(MemoryCredentials.Fixture.SelectedCalendarIds),
            "Google protected credential store roundtrips isolated credentials");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Expect<OperationCanceledException>(() => store.SaveAsync(MemoryCredentials.Fixture with { AccountEmail = "changed@example.invalid" }, cancelled.Token), check,
            "Google protected credential save honors caller cancellation");
        var unchanged = await store.LoadAsync(CancellationToken.None);
        check(unchanged?.AccountEmail == "fixture@example.invalid" && !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(),
            "Google cancelled credential write retains original file and cleans temporary file");
        await store.ClearAsync(CancellationToken.None);
        check(!File.Exists(path), "Google protected credential disconnect removes isolated local token");
    }

    private static GoogleOAuthConfiguration Configuration() => new() { ClientId = "fixture-client.invalid", ClientSecret = "fixture-client-secret" };

    private static async Task Expect<TException>(Func<Task> action, Action<bool, string> check, string name) where TException : Exception
    {
        try { await action(); check(false, name); }
        catch (TException) { check(true, name); }
    }

    private sealed class MemoryCredentials : IGoogleCalendarCredentialStore
    {
        public static readonly GoogleCalendarCredentials Fixture = new()
        {
            AccessToken = "fixture-access-token", RefreshToken = "fixture-refresh-token", AccountEmail = "fixture@example.invalid",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(4), SelectedCalendarIds = ["fixture-calendar"]
        };
        public GoogleCalendarCredentials? Value { get; set; } = Fixture;
        public Func<GoogleCalendarCredentials, CancellationToken, Task>? Saving { get; set; }
        public Task<GoogleCalendarCredentials?> LoadAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Value); }
        public async Task SaveAsync(GoogleCalendarCredentials credentials, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Saving is not null) await Saving(credentials, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Value = credentials;
        }
        public Task ClearAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Value = null; return Task.CompletedTask; }
    }

    private sealed record Request(string Method, string Uri, string Body, string IfMatch);
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(string Method, string Uri, string Body, HttpStatusCode Status, Action<Request>? Inspect)> pending = new();
        public List<Request> Requests { get; } = [];
        public void Enqueue(string method, string uri, string body, HttpStatusCode status = HttpStatusCode.OK, Action<Request>? inspect = null) => pending.Enqueue((method, uri, body, status, inspect));
        public void AssertComplete(Action<bool, string> check) => check(pending.Count == 0, "Google scripted transport consumed every expected request");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = new Request(request.Method.Method, request.RequestUri!.AbsoluteUri,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken), string.Join(",", request.Headers.IfMatch.Select(tag => tag.ToString())));
            Requests.Add(actual);
            if (pending.Count == 0) throw new InvalidOperationException("Unexpected fixture HTTP request " + actual.Method + " " + actual.Uri);
            var expected = pending.Dequeue();
            if (actual.Method != expected.Method || !actual.Uri.Contains(expected.Uri, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected fixture {expected.Method} {expected.Uri}; received {actual.Method} {actual.Uri}");
            expected.Inspect?.Invoke(actual);
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(expected.Status) { Content = new StringContent(expected.Body, Encoding.UTF8, "application/json") };
        }
    }
}
