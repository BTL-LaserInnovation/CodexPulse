using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int passed;
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.FromHours(9));

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("app-server")) return FakeServer();
        if (args.Contains("--single-instance-helper")) return SingleInstanceHelper(args);
        if (args.Contains("--single-instance")) return SingleInstanceChecks();
        var singlePublished = Array.IndexOf(args, "--single-instance-published");
        if (singlePublished >= 0) return SingleInstancePublishedChecks(args[singlePublished + 1]);
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        try
        {
            var previewIndex = Array.IndexOf(args, "--compact-preview");
            if (previewIndex >= 0)
                return CompactPreview(previewIndex + 1 < args.Length ? args[previewIndex + 1] : "weekly");
            if (args.Contains("--startup-preview") || args.Contains("--startup-ui")) return StartupUiChecks();
            if (args.Contains("--update-ui")) return UpdateUiChecks();
            if (args.Contains("--google-ui")) return GoogleUiChecks();
            if (args.Contains("--google-preview") || args.Contains("--google-preview-smoke")) return GooglePreview(args.Contains("--google-preview-smoke"));
            if (args.Contains("--ui") || args.Contains("--ui-fixture")) return UiChecks(args.Contains("--ui-fixture"), args.Contains("--skip-tray"));
            var installedIndex = Array.IndexOf(args, "--published-installed");
            if (installedIndex >= 0) return RunAsyncChecks(live: false, published: true, installed: true,
                publishedPath: installedIndex + 1 < args.Length ? args[installedIndex + 1] : null);
            if (args.Contains("--published")) return RunAsyncChecks(live: false, published: true);
            CoreChecks();
            StartupChecks.Run(Check);
            ReleaseFeatureChecks.Run(Check);
            return RunAsyncChecks(args.Contains("--live"), calendarLive: args.Contains("--calendar-live"), googleOAuth: args.Contains("--google-oauth"));
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static UsageSnapshot Parse(string text) => UsageParser.Parse(Json(text), Now);
    private static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL: " + name); Interlocked.Increment(ref passed); Console.WriteLine("PASS: " + name); }

    private static void CoreChecks()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":27.5,"windowDurationMins":300,"resetsAt":1793750400},"secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1794009600}}}""");
        Check(snapshot.Windows[0].RemainingPercent == 72.5 && snapshot.Windows[1].RemainingPercent == 0, "remaining percent and exhausted quota");
        Check(snapshot.Windows[0].Label == "5시간 한도" && snapshot.Windows[1].Label == "주간 한도", "provider duration labels");
        Check(Parse("""{"rateLimits":{"primary":{"usedPercent":-20},"secondary":{"usedPercent":120}}}""").Windows.Select(w => w.RemainingPercent).SequenceEqual(new double[] { 100, 0 }), "range clamp");
        Check(Parse("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":{"usedPercent":"wrong"}}}""").Windows.Count == 0, "invalid percentages stay unknown");
        Check(Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":null,"resetsAt":9223372036854775807},"secondary":null}}""").Windows.Single().ResetsAt is null, "null optional fields and out-of-range timestamps");
        Check(Parse("null").Windows.Count == 0 && Parse("{}").Windows.Count == 0, "missing provider result");
        Check(Parse("""{"rateLimits":{"primary":{"usedPercent":1,"resetsAt":253402300799}}}""").Windows.Single().ResetsAt == DateTimeOffset.FromUnixTimeSeconds(253402300799), "valid UTC boundary retained for timezone conversion");
        LocalTimeChecks.Run(Check);
        Check(Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":30}}},"rateLimits":{"primary":{"usedPercent":90}}}""").Windows.Single().RemainingPercent == 70, "prefer Codex bucket");
        Check(Parse("""{"rateLimits":{"limitId":"unrelated","primary":{"usedPercent":10}}}""").Windows.Count == 0, "ignore unrelated quota");
        var history = new AccountHistory();
        var old = new UsageSnapshot(Now, [new("primary", 80, 300, Now.AddHours(1))]);
        history.Observe(old);
        Check(history.Resets.Count == 0, "first run does not invent history");
        var scheduled = new UsageSnapshot(Now.AddMinutes(1), [new("primary", 80, 300, Now.AddHours(5))]);
        history.Observe(scheduled);
        Check(history.Resets.Count == 0, "schedule change alone is not reset");
        history.Observe(new(Now.AddMinutes(2), [new("primary", 3, 300, Now.AddHours(6))]));
        Check(history.Resets.Count == 1 && history.Resets[0].ObservedAt == Now.AddMinutes(2), "reset observation timestamp and evidence");
        history.Observe(history.LastSnapshot!);
        Check(history.Resets.Count == 1, "duplicate sample deduplicated");
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "state-checks", Guid.NewGuid().ToString("N"));
        var store = new LocalStateStore(folder);
        var state = new WidgetState { AlwaysOnTop = false, Opacity = .7, ActiveAccount = "account-a" };
        state.Accounts["account-a"] = history;
        state.Accounts["account-b"] = new();
        Check(store.Save(state), "atomic state write");
        var loaded = store.Load();
        Check(!loaded.AlwaysOnTop && loaded.Opacity == .7 && loaded.Accounts["account-a"].Resets.Count == 1 && loaded.Accounts["account-b"].Resets.Count == 0, "settings roundtrip and account isolation");
        File.WriteAllText(Path.Combine(folder, "widget-state.json"), "{ broken");
        Check(store.Load().Accounts.Count == 0 && store.Warning is not null, "corrupt state recovery");
        File.WriteAllText(Path.Combine(folder, "widget-state.json"), """{"Opacity":0,"Accounts":{"bad":null,"ok":{"LastSnapshot":{"CapturedAt":"2026-11-04T00:00:00Z","Windows":null},"Resets":null}}}""");
        loaded = store.Load();
        Check(loaded.Opacity == .55 && loaded.Accounts.Count == 1 && loaded.Accounts["ok"].LastSnapshot is null, "malformed state validation");
    }

    private static int RunAsyncChecks(bool live, bool published = false, bool calendarLive = false,
        bool installed = false, string? publishedPath = null, bool googleOAuth = false)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await ReleaseFeatureChecks.RunAsync(Check);
                await UpdateChecks.RunAsync(Check);
                await GoogleCalendarChecks.RunAsync(Check, googleOAuth);
                await using (var fake = new CodexAppServerClient(Environment.ProcessPath!))
                {
                    var requests = await Task.WhenAll(fake.ReadAccountAsync(cancel.Token), fake.ReadRateLimitsAsync(cancel.Token));
                    Check(requests[0].GetProperty("account").GetProperty("type").GetString() == "chatgpt" && UsageParser.Parse(requests[1], Now).Windows.Single().RemainingPercent == 63, "stdio initialization and concurrent RPC matching");
                    Check(fake.AccountRevision == 1, "account revision advances before usage response completes");
                    using var cancelled = new CancellationTokenSource();
                    cancelled.Cancel();
                    try { await fake.ReadRateLimitsAsync(cancelled.Token); Check(false, "caller cancellation"); }
                    catch (OperationCanceledException) { Check(true, "caller cancellation"); }
                    try { await fake.StartLoginAsync(cancel.Token); Check(false, "provider error sanitization"); }
                    catch (InvalidOperationException ex) { Check(!ex.Message.Contains("secret-test"), "provider error sanitization"); }
                    var consumed = await fake.ConsumeResetCreditAsync("fake-request", "fake-credit", cancel.Token);
                    Check(consumed.GetProperty("outcome").GetString() == "reset", "consume RPC sends explicit selected credit and idempotency key to fake server");
                    var repeated = await fake.ConsumeResetCreditAsync("fake-request", "fake-credit", cancel.Token);
                    Check(repeated.GetProperty("outcome").GetString() == "alreadyRedeemed", "same fake operation key reconciles instead of consuming another credit");
                    var pending = fake.ReadRateLimitsAsync(cancel.Token);
                    await Task.Delay(50);
                    await fake.DisposeAsync();
                    try { await pending; Check(false, "pending request ends on disposal"); }
                    catch (Exception ex) when (ex is ObjectDisposedException or IOException) { Check(true, "pending request ends on disposal"); }
                }
                if (live)
                {
                    var path = CodexExecutableLocator.FindExecutable() ?? throw new InvalidOperationException("Codex executable not found");
                    await using var client = new CodexAppServerClient(path);
                    var account = await client.ReadAccountAsync(cancel.Token);
                    var loggedIn = account.TryGetProperty("account", out var a) && a.ValueKind == JsonValueKind.Object && a.GetProperty("type").GetString() == "chatgpt";
                    Check(loggedIn, "live ChatGPT authentication (no credentials printed)");
                    var result = await client.ReadRateLimitsAsync(cancel.Token);
                    var data = UsageParser.Parse(result, DateTimeOffset.UtcNow);
                    Check(data.Windows.Count > 0, "live Codex usage received");
                    foreach (var w in data.Windows) Console.WriteLine($"LIVE: {w.Label} ({w.Key}, {w.DurationMinutes}min): {w.RemainingPercent:0.#}% remaining; reset={w.ResetsAt:O}");
                    var credits = ResetCreditBalance.Parse(result);
                    Console.WriteLine($"LIVE: reset credit count={credits.AvailableCount}, detail rows={credits.Credits.Count}, detailsKnown={credits.DetailsKnown}");
                }
                if (live || calendarLive)
                {
                    using var publicClient = new PublicResetCalendarClient();
                    var calendar = await publicClient.FetchAsync(cancel.Token);
                    Check(calendar.Source == PublicResetCalendar.ProviderId && calendar.Events.Count > 0,
                        "live OpenTheRank history received without account credentials");
                    Console.WriteLine($"CALENDAR: {calendar.Events.Count(e => !e.IsAnnouncement)} history records, {calendar.Events.Count(e => e.IsAnnouncement)} preannouncements");
                    var syncCalendar = publicClient.Fetch(cancel.Token);
                    Check(calendar.Events.SequenceEqual(syncCalendar.Events), "live OpenTheRank synchronous and asynchronous paths agree");
                }
                if (live || published) await VerifyPublishedAsync(installed, publishedPath);
                Console.WriteLine($"All {passed} checks passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally { app.Shutdown(exitCode); }
        }));
        app.Run();
        return exitCode;
    }


    private static async Task VerifyPublishedAsync(bool installed = false, string? publishedPath = null)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("App assembly version unavailable");
        var path = publishedPath ?? (installed
            ? Path.Combine(Directory.GetCurrentDirectory(), "publish", version, "installed", "CodexPulse.exe")
            : Path.Combine(Directory.GetCurrentDirectory(), "publish", version, "CodexPulse.exe"));
        if (!File.Exists(path))
            throw new FileNotFoundException("Publish the selected profile before running its smoke check.", path);
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", installed ? "installed-smoke" : "published-smoke", Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.Environment["USAGEREMAINING_DATA_DIR"] = folder;
        if (!installed)
        {
            var emptyRuntime = Path.Combine(folder, "empty-runtime");
            Directory.CreateDirectory(emptyRuntime);
            start.Environment["DOTNET_ROOT"] = emptyRuntime;
            start.Environment["DOTNET_ROOT_X64"] = emptyRuntime;
            start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Published executable did not start");
        try
        {
            await Task.Delay(3000);
            process.Refresh();
            var window = FindWidgetWindow(process.Id);
            Check(!process.HasExited && window != IntPtr.Zero,
                installed ? "framework-dependent installed EXE opens using installed Desktop Runtime" : "published single EXE opens without installed runtime");
            CheckNativeIcons(path, window);
            VerifyNativeTrayBeforeClose(window);
            Check(PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero), "published widget accepts close without a taskbar button");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(process.ExitCode == 0 && File.Exists(Path.Combine(folder, "widget-state.json")), "published EXE clean exit and saved settings");
            VerifyNativeTrayAfterClose(window);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

}
