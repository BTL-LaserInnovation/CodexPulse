using System.IO;
using System.Net.Http;
using Velopack;
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

public sealed class AppUpdateService : IAppUpdateService
{
    public const string ReleaseSource = "https://github.com/BTL-LaserInnovation/CodexPulse/releases/latest/download";
    private readonly CancellableDownloader downloader = new();
    private readonly UpdateManager manager;

    public AppUpdateService()
    {
        manager = new(new SimpleWebSource(ReleaseSource, downloader, 1),
            new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false });
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

    // Velopack's feed API has no cancellation token. Carry the operation token into its async downloader.
    private sealed class CancellableDownloader : IFileDownloader, IDisposable
    {
        private const int MaximumFeedBytes = 2 * 1024 * 1024;
        private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(1) };
        private readonly HttpClientFileDownloader files = new();
        public CancellationToken OperationToken { get; set; }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers is not null)
                foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(OperationToken);
            limit.CancelAfter(TimeSpan.FromMinutes(timeout));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumFeedBytes)
                throw new InvalidDataException("The update feed exceeds its size limit.");
            await using var input = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumFeedBytes)
                    throw new InvalidDataException("The update feed exceeds its size limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout) =>
            System.Text.Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

        public async Task DownloadFile(string url, string targetFile, Action<int>? progress,
            IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, cancelToken);
            await files.DownloadFile(url, targetFile, progress ?? (_ => { }), headers, timeout, linked.Token).ConfigureAwait(false);
        }

        public void Dispose() => http.Dispose();
    }
}
