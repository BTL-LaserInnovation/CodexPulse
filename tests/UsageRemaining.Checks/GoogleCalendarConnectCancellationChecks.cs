using System.Net;
using System.Net.Http;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class GoogleCalendarChecks
{
    private static async Task OAuthCancelRetryBoundaries(Action<bool, string> check)
    {
        using (var transport = new ScriptedHandler())
        using (var http = new HttpClient(transport))
        {
            var store = new MemoryCredentials();
            using var service = new GoogleCalendarService(http, Configuration(), store);
            await service.InitializeAsync();
            var before = service.State;
            var saved = store.Value;
            var changes = 0;
            service.Changed += (_, _) => changes++;
            using var canceled = new CancellationTokenSource();
            Uri? oldAuthorization = null;
            var abandoned = service.ConnectAsync(address => oldAuthorization = address, canceled.Token);
            canceled.Cancel();
            await Expect<OperationCanceledException>(() => abandoned, check, "abandoned Google browser listener finishes when canceled");
            check(ReferenceEquals(before, service.State) && ReferenceEquals(saved, store.Value) && changes == 0
                && transport.Requests.Count == 0, "canceling the browser wait preserves the current account and sends no token request");

            transport.Enqueue("POST", "oauth2.googleapis.com/token", CandidateToken);
            transport.Enqueue("GET", "userinfo", CandidateProfile);
            transport.Enqueue("GET", "calendarList", Calendars);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Uri? newAuthorization = null;
            var retry = service.ConnectAsync(address => newAuthorization = address, deadline.Token);
            if (oldAuthorization is null || newAuthorization is null) throw new InvalidOperationException("Missing OAuth fixture URL");
            var oldFields = QueryFields(oldAuthorization.Query.TrimStart('?'));
            var newFields = QueryFields(newAuthorization.Query.TrimStart('?'));
            check(oldFields["state"] != newFields["state"], "Google retry creates a fresh authorization state after complete cancellation cleanup");
            using var local = new HttpClient(new HttpClientHandler { UseProxy = false });
            var stale = new Uri(new Uri(newFields["redirect_uri"]), "?state=" + Uri.EscapeDataString(oldFields["state"]) + "&code=old-fixture-code");
            using var rejected = await local.GetAsync(stale, deadline.Token);
            check(rejected.StatusCode == HttpStatusCode.BadRequest && !retry.IsCompleted && transport.Requests.Count == 0
                && ReferenceEquals(saved, store.Value) && changes == 0,
                "an old browser state cannot authorize or overwrite a new waiting Google connection");
            var acknowledgement = await SendAcceptedOAuthCallback(newAuthorization, deadline.Token);
            await retry;
            check(service.State.AccountEmail == "candidate@example.invalid" && store.Value?.AccountEmail == "candidate@example.invalid"
                && changes == 1 && acknowledgement.Contains("still", StringComparison.OrdinalIgnoreCase),
                "Google can reconnect successfully in the same service after canceling an abandoned browser");
            transport.AssertComplete(check);
        }

        foreach (var stage in new[] { "token", "profile", "calendar", "save", "saved-before-cancel-observed" })
        {
            using var transport = new ScriptedHandler();
            using var http = new HttpClient(transport);
            var store = new MemoryCredentials();
            using var service = new GoogleCalendarService(http, Configuration(), store);
            await service.InitializeAsync();
            var before = service.State;
            var saved = store.Value;
            var changes = 0;
            service.Changed += (_, _) => changes++;
            using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRestore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.Enqueue("POST", "oauth2.googleapis.com/token", CandidateToken,
                inspect: _ => { if (stage == "token") canceled.Cancel(); });
            if (stage != "token") transport.Enqueue("GET", "userinfo", CandidateProfile,
                inspect: _ => { if (stage == "profile") canceled.Cancel(); });
            if (stage is not ("token" or "profile")) transport.Enqueue("GET", "calendarList", Calendars,
                inspect: _ => { if (stage == "calendar") canceled.Cancel(); });
            if (stage is "save" or "saved-before-cancel-observed")
            {
                store.IgnoreCancellationAfterSaving = stage == "saved-before-cancel-observed";
                store.Saving = (credentials, _) =>
                {
                    if (credentials.AccountEmail == "candidate@example.invalid") canceled.Cancel();
                    else if (stage == "saved-before-cancel-observed")
                    {
                        restoring.TrySetResult();
                        return releaseRestore.Task;
                    }
                    return Task.CompletedTask;
                };
            }
            using var callbackDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            Task<string>? callback = null;
            var pending = service.ConnectAsync(address =>
                callback = SendAcceptedOAuthCallback(address, callbackDeadline.Token), canceled.Token);
            if (stage == "saved-before-cancel-observed")
            {
                try
                {
                    await restoring.Task.WaitAsync(callbackDeadline.Token);
                    check(!pending.IsCompleted && ReferenceEquals(before, service.State) && changes == 0,
                        "canceled Google connection does not finish or publish while persisted credentials are being restored");
                }
                finally { releaseRestore.TrySetResult(); }
            }
            await Expect<OperationCanceledException>(() => pending, check,
                "Google cancellation wins at " + stage + " boundary");
            if (callback is not null) await callback;
            check(ReferenceEquals(before, service.State) && ReferenceEquals(saved, store.Value) && changes == 0,
                "canceled " + stage + " connection cannot replace stored credentials cached state or account identity");
            transport.AssertComplete(check);
        }
    }
}
