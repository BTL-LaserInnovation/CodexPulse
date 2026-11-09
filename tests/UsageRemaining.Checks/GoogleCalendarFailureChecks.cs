using System.IO;
using System.Net;
using System.Net.Http;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class GoogleCalendarChecks
{
    private const string CandidateToken = """{"access_token":"candidate-fixture-access","refresh_token":"candidate-fixture-refresh","expires_in":3600}""";
    private const string CandidateProfile = """{"email":"candidate@example.invalid"}""";
    private const string SecretMarker = "hostile-fixture-secret-do-not-display";

    private static async Task ApiErrorClassification(Action<bool, string> check)
    {
        var generic = await CalendarFailureMessage("{}", check);
        foreach (var (name, body, expected) in new[]
        {
            ("legacy disabled API", """{"error":{"message":"hostile-fixture-secret-do-not-display","errors":[{"reason":"accessNotConfigured"}]}}""", "Google Calendar API"),
            ("modern disabled Calendar API", """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","metadata":{"service":"calendar-json.googleapis.com","consumer":"hostile-fixture-secret-do-not-display"}}]}}""", "Google Calendar API"),
            ("legacy insufficient permissions", """{"error":{"errors":[{"reason":"insufficientPermissions"}]}}""", "권한이 부족"),
            ("modern insufficient scope", """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"ACCESS_TOKEN_SCOPE_INSUFFICIENT"}]}}""", "권한이 부족"),
            ("legacy rate quota", """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}""", "요청 한도"),
            ("legacy user rate quota", """{"error":{"errors":[{"reason":"userRateLimitExceeded"}]}}""", "요청 한도"),
            ("modern quota", """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"QUOTA_EXCEEDED"}]}}""", "요청 한도")
        })
        {
            var message = await CalendarFailureMessage(body, check);
            check(message != generic && message.Contains(expected, StringComparison.Ordinal) && !message.Contains(SecretMarker),
                "Google structured 403 distinguishes " + name + " without remote message leakage");
        }
        foreach (var (name, body) in new[]
        {
            ("unknown reason", """{"error":{"message":"hostile-fixture-secret-do-not-display","errors":[{"reason":"unknownReason"}]}}"""),
            ("message-only spoof", """{"error":{"message":"SERVICE_DISABLED accessNotConfigured hostile-fixture-secret-do-not-display"}}"""),
            ("untyped modern spoof", """{"error":{"details":[{"reason":"SERVICE_DISABLED"}]}}"""),
            ("another API disabled", """{"error":{"details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"SERVICE_DISABLED","metadata":{"service":"another-api.googleapis.com"}}]}}"""),
            ("wrong JSON field types", """{"error":{"errors":[{"reason":42}],"details":true}}"""),
            ("non-object JSON", "[\"hostile-fixture-secret-do-not-display\"]"),
            ("malformed JSON", "{ hostile-fixture-secret-do-not-display accessNotConfigured"),
            ("oversized error", "{\"error\":{\"message\":\"" + new string('x', 17 * 1024) + "\",\"errors\":[{\"reason\":\"accessNotConfigured\"}]}}")
        })
        {
            var message = await CalendarFailureMessage(body, check);
            check(message == generic && !message.Contains(SecretMarker),
                "Google 403 " + name + " uses sanitized generic fallback");
        }
    }

    private static async Task<string> CalendarFailureMessage(string body, Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        var store = new MemoryCredentials();
        using var service = new GoogleCalendarService(http, Configuration(), store);
        await service.InitializeAsync();
        var before = service.State;
        var saved = store.Value;
        transport.Enqueue("GET", "calendarList", body, HttpStatusCode.Forbidden);
        try { await service.RefreshAsync(Start, End); throw new InvalidOperationException("Forbidden fixture request unexpectedly succeeded"); }
        catch (GoogleCalendarException error)
        {
            check(ReferenceEquals(before, service.State) && ReferenceEquals(saved, store.Value),
                "Google API denial preserves current account credentials and synchronized state");
            transport.AssertComplete(check);
            return error.Message;
        }
    }

    private static async Task OAuthPostCallbackFailures(Action<bool, string> check)
    {
        foreach (var connected in new[] { false, true })
        foreach (var stage in new[] { "token", "profile", "calendar", "save" })
        {
            using var transport = new ScriptedHandler();
            using var http = new HttpClient(transport);
            var store = new MemoryCredentials { Value = connected ? MemoryCredentials.Fixture : null };
            using var service = new GoogleCalendarService(http, Configuration(), store);
            await service.InitializeAsync();
            var before = service.State;
            var saved = store.Value;
            var changes = 0;
            service.Changed += (_, _) => changes++;
            transport.Enqueue("POST", "oauth2.googleapis.com/token", stage == "token" ? """{"error":"invalid_grant","error_description":"hostile-fixture-secret-do-not-display"}""" : CandidateToken,
                stage == "token" ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
            if (stage != "token") transport.Enqueue("GET", "userinfo", stage == "profile" ? """{"error":{"message":"hostile-fixture-secret-do-not-display"}}""" : CandidateProfile,
                stage == "profile" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            if (stage is "calendar" or "save") transport.Enqueue("GET", "calendarList", stage == "calendar" ? """{"error":{"errors":[{"reason":"accessNotConfigured"}],"message":"hostile-fixture-secret-do-not-display"}}""" : Calendars,
                stage == "calendar" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
            if (stage == "save") store.Saving = (_, _) => throw new IOException("Fixture credential persistence failure");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task<string>? callback = null;
            try
            {
                await service.ConnectAsync(address => callback = SendAcceptedOAuthCallback(address, timeout.Token), timeout.Token);
                throw new InvalidOperationException("Post-callback " + stage + " fixture unexpectedly succeeded");
            }
            catch (Exception error) when (error is GoogleCalendarException or IOException)
            {
                check(!error.Message.Contains(SecretMarker), "Google post-callback " + stage + " failure does not expose provider secrets");
            }
            if (callback is null) throw new InvalidOperationException("OAuth callback fixture did not start");
            var acknowledgement = await callback;
            check(acknowledgement.Contains("still", StringComparison.OrdinalIgnoreCase) && acknowledgement.Contains("app", StringComparison.OrdinalIgnoreCase),
                "Google valid callback acknowledgement defers final " + stage + " result to the app");
            check(ReferenceEquals(before, service.State) && ReferenceEquals(saved, store.Value) && changes == 0 && service.State.Connected == connected,
                "Google post-callback " + stage + " failure atomically preserves " + (connected ? "existing connected account" : "disconnected state"));
            transport.AssertComplete(check);
        }
    }

    private static async Task OAuthCommitBoundary(Action<bool, string> check)
    {
        using var transport = new ScriptedHandler();
        using var http = new HttpClient(transport);
        var saving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryCredentials { Value = null, Saving = async (_, token) => { saving.SetResult(); await releaseSave.Task.WaitAsync(token); } };
        using var service = new GoogleCalendarService(http, Configuration(), store);
        transport.Enqueue("POST", "oauth2.googleapis.com/token", CandidateToken);
        transport.Enqueue("GET", "userinfo", CandidateProfile);
        transport.Enqueue("GET", "calendarList", Calendars);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Task<string>? callback = null;
        var connect = service.ConnectAsync(address => callback = SendAcceptedOAuthCallback(address, timeout.Token), timeout.Token);
        try
        {
            await saving.Task.WaitAsync(timeout.Token);
            var acknowledgement = callback is null ? throw new InvalidOperationException("OAuth callback fixture did not start") : await callback;
            check(!connect.IsCompleted && !service.State.Connected && store.Value is null && acknowledgement.Contains("still", StringComparison.OrdinalIgnoreCase),
                "Google callback and completed candidate HTTP steps do not publish connected before credential persistence");
        }
        finally { releaseSave.TrySetResult(); }
        await connect;
        check(service.State.Connected && store.Value?.AccountEmail == "candidate@example.invalid",
            "Google connection publishes only after candidate credentials are persisted");
        transport.AssertComplete(check);
    }

    private static async Task<string> SendAcceptedOAuthCallback(Uri authorization, CancellationToken token)
    {
        var fields = QueryFields(authorization.Query.TrimStart('?'));
        using var local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(8) };
        var target = new Uri(new Uri(fields["redirect_uri"]), "?state=" + Uri.EscapeDataString(fields["state"]) + "&code=fixture-authorization-code");
        using var response = await local.GetAsync(target, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }
}
