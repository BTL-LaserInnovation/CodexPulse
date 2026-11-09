using System.Net.Http;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace UsageRemaining.Widget.Services;

public sealed record AppUpdate(string Version)
{
    internal UpdateInfo? Release { get; init; }
}

public interface IAppUpdateService : IDisposable
{
    bool IsInstalled { get; }
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken);
    void PrepareRestart(AppUpdate update);
}

public sealed partial class AppUpdateService : IAppUpdateService
{
    public const string ReleaseSource = "https://github.com/BTL-LaserInnovation/CodexPulse/releases/latest/download";
    private readonly CancellableDownloader downloader;
    private readonly UpdateManager manager;

    public AppUpdateService() : this(new CancellableDownloader(), null)
    {
    }

    // The caller retains ownership of the injected client.
    public AppUpdateService(HttpClient http, IVelopackLocator locator)
        : this(new CancellableDownloader(http), locator)
    {
    }

    private AppUpdateService(CancellableDownloader downloader, IVelopackLocator? locator)
    {
        this.downloader = downloader;
        manager = new(new SimpleWebSource(ReleaseSource, downloader, 1),
            new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false }, locator);
    }

    public bool IsInstalled => manager.IsInstalled;

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        downloader.OperationToken = cancellationToken;
        var release = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return release is null ? null : new(release.TargetFullRelease.Version.ToString()) { Release = release };
    }

    public Task DownloadAsync(AppUpdate update, Action<int> progress, CancellationToken cancellationToken)
    {
        downloader.OperationToken = cancellationToken;
        return manager.DownloadUpdatesAsync(RequireRelease(update), progress, cancellationToken);
    }

    public void PrepareRestart(AppUpdate update) =>
        manager.WaitExitThenApplyUpdates(RequireRelease(update).TargetFullRelease, silent: false, restart: true);

    private static UpdateInfo RequireRelease(AppUpdate update) =>
        update.Release ?? throw new ArgumentException("The update was not checked by this service.", nameof(update));

    public void Dispose() => downloader.Dispose();
}
