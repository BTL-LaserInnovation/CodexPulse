using System.IO;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static class UpdateChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        long ticks = 10_000;
        var service = new FakeUpdateService();
        var updates = new UpdateCoordinator(service, () => ticks);
        await updates.CheckIfDueAsync();
        check(service.Checks == 1 && updates.AvailableUpdate is null && !updates.IsBusy,
            "installed startup check completes with no update and no offered action");
        ticks += UpdateCoordinator.CheckIntervalMilliseconds - 1;
        await updates.CheckIfDueAsync();
        check(service.Checks == 1, "update feed is not polled before five-minute boundary");
        service.Offered = new("1.2.1");
        ticks++;
        await updates.CheckIfDueAsync();
        check(service.Checks == 2 && updates.AvailableUpdate?.Version == "1.2.1",
            "update feed is checked exactly at independent five-minute boundary");
        service.CheckFailure = new IOException("offline fixture");
        ticks += UpdateCoordinator.CheckIntervalMilliseconds;
        await updates.CheckIfDueAsync();
        check(updates.Error == "offline fixture" && updates.AvailableUpdate?.Version == "1.2.1" && !updates.IsBusy,
            "feed failure preserves offered update and restores idle state");
        service.CheckFailure = null;
        service.Offered = null;
        ticks += UpdateCoordinator.CheckIntervalMilliseconds;
        await updates.CheckIfDueAsync();
        check(updates.AvailableUpdate?.Version == "1.2.1" && updates.Error is null,
            "temporarily empty feed preserves offered update and clears prior error");
        check(Throws<InvalidOperationException>(updates.PrepareRestart) && service.Restarts == 0,
            "update cannot apply before download completion");

        service.HoldDownload = true;
        using (var cancel = new CancellationTokenSource())
        {
            var downloading = updates.DownloadAsync(cancel.Token);
            check(updates.IsBusy && service.Downloads == 1, "download enters busy state before yielding");
            await updates.DownloadAsync();
            ticks += UpdateCoordinator.CheckIntervalMilliseconds;
            await updates.CheckIfDueAsync();
            check(service.Downloads == 1 && service.Checks == 4 && service.Restarts == 0,
                "busy download suppresses duplicate download and overlapping feed check");
            service.ReportProgress(-20);
            check(updates.Progress == 0, "download progress clamps values below zero");
            service.ReportProgress(150);
            check(updates.Progress == 100, "download progress clamps values above one hundred");
            check(Throws<InvalidOperationException>(updates.PrepareRestart), "busy download blocks apply");
            cancel.Cancel();
            await downloading.WaitAsync(TimeSpan.FromSeconds(3));
        }
        check(!updates.IsBusy && !updates.IsDownloaded && updates.Error is null && updates.AvailableUpdate is not null,
            "download cancellation restores available update without an error or apply");
        service.HoldDownload = false;
        service.DownloadFailure = new IOException("download fixture failure");
        await updates.DownloadAsync();
        check(!updates.IsBusy && !updates.IsDownloaded && updates.Error == "download fixture failure" && service.Restarts == 0,
            "failed download restores idle state and never applies");
        service.DownloadFailure = null;
        await updates.DownloadAsync();
        check(updates.IsDownloaded && updates.Error is null && !updates.IsBusy && service.Downloads == 3,
            "failed or cancelled update download can be retried successfully");
        var downloads = service.Downloads;
        var probes = service.Checks;
        await updates.DownloadAsync();
        ticks += UpdateCoordinator.CheckIntervalMilliseconds;
        await updates.CheckIfDueAsync();
        check(service.Downloads == downloads && service.Checks == probes,
            "ready update is stable without duplicate download or replacement feed query");
        updates.PrepareRestart();
        check(service.Restarts == 1 && service.Applied?.Version == "1.2.1",
            "explicit restart prepares exactly the successfully downloaded release");
        await updates.StopAsync();
        await updates.StopAsync();
        await updates.CheckIfDueAsync();
        check(service.Disposals == 1 && Throws<InvalidOperationException>(updates.PrepareRestart),
            "stopping update coordinator disposes service once and blocks future apply");

        var portable = new FakeUpdateService { IsInstalled = false };
        var portableUpdates = new UpdateCoordinator(portable);
        await portableUpdates.CheckIfDueAsync();
        await portableUpdates.DownloadAsync();
        check(portable.Checks == 0 && portable.Downloads == 0 && portableUpdates.AvailableUpdate is null,
            "portable execution never starts installed update checks or downloads");
        await portableUpdates.StopAsync();

        var pending = new FakeUpdateService { HoldCheck = true };
        var pendingUpdates = new UpdateCoordinator(pending);
        var pendingCheck = pendingUpdates.CheckIfDueAsync();
        await pendingUpdates.CheckIfDueAsync();
        check(pendingUpdates.IsBusy && pending.Checks == 1, "startup feed check rejects duplicate activation");
        await pendingUpdates.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await pendingCheck;
        check(!pendingUpdates.IsBusy && pending.Disposals == 1 && pendingUpdates.Error is null,
            "shutdown cancels pending feed request before disposing updater");

        var active = new FakeUpdateService { Offered = new("1.2.2"), HoldDownload = true };
        var activeUpdates = new UpdateCoordinator(active);
        await activeUpdates.CheckIfDueAsync();
        var activeDownload = activeUpdates.DownloadAsync();
        await activeUpdates.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await activeDownload;
        check(!activeUpdates.IsDownloaded && !activeUpdates.IsBusy && active.Disposals == 1 && active.Restarts == 0,
            "shutdown cancels pending download without staging a restart");
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    internal sealed class FakeUpdateService : IAppUpdateService
    {
        public bool IsInstalled { get; set; } = true;
        public AppUpdate? Offered { get; set; }
        public Exception? CheckFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public bool HoldCheck { get; set; }
        public bool HoldDownload { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Restarts { get; private set; }
        public int Disposals { get; private set; }
        public AppUpdate? Applied { get; private set; }
        private Action<int>? report;

        public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
        {
            Checks++;
            if (HoldCheck) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (CheckFailure is not null) throw CheckFailure;
            return Offered;
        }

        public async Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            report = progress;
            if (HoldDownload) await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (DownloadFailure is not null) throw DownloadFailure;
            progress(100);
        }

        public void ReportProgress(int progress) => report?.Invoke(progress);
        public void PrepareRestart(AppUpdate update) { Restarts++; Applied = update; }
        public void Dispose() => Disposals++;
    }
}
