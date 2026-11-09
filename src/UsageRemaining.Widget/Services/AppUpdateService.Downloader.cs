using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack.Sources;

namespace UsageRemaining.Widget.Services;

public sealed partial class AppUpdateService
{
    // Velopack's feed API has no cancellation token. Carry the operation token into its async downloader.
    private sealed class CancellableDownloader : IFileDownloader, IDisposable
    {
        private const int MaximumFeedBytes = 2 * 1024 * 1024;
        private const string RepositoryPath = "/BTL-LaserInnovation/CodexPulse/releases/";
        private const string ApiRoot = "https://api.github.com/repos/BTL-LaserInnovation/CodexPulse/releases/";
        private static readonly Regex StableVersion = new(@"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);
        private readonly HttpClient http;
        private readonly bool ownsHttp;

        public CancellableDownloader() : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, true)
        {
        }

        public CancellableDownloader(HttpClient http) : this(http, false)
        {
        }

        private CancellableDownloader(HttpClient http, bool ownsHttp)
        {
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.ownsHttp = ownsHttp;
        }

        public CancellationToken OperationToken { get; set; }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        {
            using var total = CreateTotalLimit(timeout, OperationToken);
            try
            {
                using var primary = CreateAttemptLimit(total.Token, 15);
                return await ReadBytesAsync(url, headers, false, primary.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (CanFallback(exception, total.Token) && TryGetAsset(url, true, out _))
            {
                TraceFallback(exception, "releases.win.json");
                var assetUrl = await ResolveAssetAsync("releases.win.json", null, total.Token).ConfigureAwait(false);
                using var fallback = CreateAttemptLimit(total.Token, 60);
                return await ReadBytesAsync(assetUrl, null, true, fallback.Token).ConfigureAwait(false);
            }
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout) =>
            Encoding.UTF8.GetString(await DownloadBytes(url, headers, timeout).ConfigureAwait(false));

        public async Task DownloadFile(string url, string targetFile, Action<int>? progress,
            IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(OperationToken, cancelToken);
            using var total = CreateTotalLimit(timeout, operation.Token);
            var fileOpened = false;
            try
            {
                using var primary = CreateAttemptLimit(total.Token, 15);
                await WriteFileAsync(url, targetFile, progress, headers, false, primary.Token, () => fileOpened = true).ConfigureAwait(false);
            }
            catch (Exception exception) when (CanFallback(exception, total.Token) && TryGetAsset(url, false, out _))
            {
                TryGetAsset(url, false, out var asset);
                TraceFallback(exception, asset.Name);
                // Truncation is a local operation: a disk failure must never initiate another network attempt.
                if (fileOpened)
                {
                    await using var partial = new FileStream(targetFile, FileMode.Truncate, FileAccess.Write, FileShare.None, 1, true);
                }
                var assetUrl = await ResolveAssetAsync(asset.Name, asset.Version, total.Token).ConfigureAwait(false);
                using var fallback = CreateAttemptLimit(total.Token, 60);
                await WriteFileAsync(assetUrl, targetFile, progress, null, true, fallback.Token).ConfigureAwait(false);
            }
        }

        private static CancellationTokenSource CreateTotalLimit(double timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var seconds = double.IsFinite(timeout) && timeout > 0 ? Math.Min(timeout * 60, 90) : 90;
            var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            return limit;
        }

        private static CancellationTokenSource CreateAttemptLimit(CancellationToken token, int seconds)
        {
            token.ThrowIfCancellationRequested();
            var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));
            return limit;
        }

        private static bool CanFallback(Exception exception, CancellationToken totalToken) =>
            !totalToken.IsCancellationRequested &&
            exception is HttpRequestException or NetworkReadException or OperationCanceledException;

        private static void TraceFallback(Exception exception, string name)
        {
            var reason = exception is HttpRequestException { StatusCode: { } status }
                ? "HTTP " + (int)status
                : exception.GetType().Name;
            Trace.TraceWarning("Update asset {0}: primary download failed ({1}); trying the GitHub API.", name, reason);
        }

        private static bool TryGetAsset(string url, bool feed, out (string Name, string? Version) asset)
        {
            asset = default;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                !uri.AbsolutePath.StartsWith(RepositoryPath, StringComparison.Ordinal)) return false;

            var path = uri.AbsolutePath[RepositoryPath.Length..];
            string? tag = null;
            string name;
            if (path.StartsWith("latest/download/", StringComparison.Ordinal)) name = path["latest/download/".Length..];
            else if (path.StartsWith("download/", StringComparison.Ordinal))
            {
                var parts = path.Split('/');
                if (parts.Length != 3 || !parts[1].StartsWith('v')) return false;
                tag = parts[1][1..];
                name = parts[2];
            }
            else return false;

            if (feed)
            {
                if (tag is not null || name != "releases.win.json") return false;
                asset = (name, null);
                return true;
            }

            const string prefix = "BTL.CodexPulse-";
            const string suffix = "-full.nupkg";
            if (uri.Query.Length != 0 || name.Length <= prefix.Length + suffix.Length || !name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(suffix, StringComparison.Ordinal)) return false;
            var version = name[prefix.Length..^suffix.Length];
            if (!StableVersion.IsMatch(version) || (tag is not null && tag != version)) return false;
            asset = (name, version);
            return true;
        }

        private async Task<string> ResolveAssetAsync(string name, string? version, CancellationToken token)
        {
            using var metadata = CreateAttemptLimit(token, 10);
            var endpoint = version is null ? "latest" : "tags/v" + version;
            var bytes = await ReadBytesAsync(ApiRoot + endpoint, null, true, metadata.Token, true).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
                    !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
                    !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    throw InvalidMetadata();
                var tagName = tag.GetString();
                if (tagName is null || !tagName.StartsWith('v') || !StableVersion.IsMatch(tagName[1..]) ||
                    (version is not null && tagName != "v" + version)) throw InvalidMetadata();

                long assetId = 0;
                var matches = 0;
                foreach (var candidate in assets.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.Object ||
                        !candidate.TryGetProperty("name", out var candidateName) || candidateName.ValueKind != JsonValueKind.String ||
                        !candidate.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
                        !id.TryGetInt64(out var value) || value <= 0)
                        throw InvalidMetadata();
                    if (candidateName.GetString() != name) continue;
                    if (!candidate.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                        state.GetString() != "uploaded") throw InvalidMetadata();
                    assetId = value;
                    matches++;
                }
                if (matches != 1) throw InvalidMetadata();
                // Never follow URLs supplied in metadata; the allowlisted repository and numeric ID define this URL.
                return ApiRoot + "assets/" + assetId.ToString(CultureInfo.InvariantCulture);
            }
            catch (JsonException)
            {
                throw InvalidMetadata();
            }
        }

        private static InvalidDataException InvalidMetadata() => new("The update release metadata is invalid.");

        private async Task<HttpResponseMessage> SendAsync(string url, IDictionary<string, string>? headers,
            bool api, CancellationToken token, bool metadata = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (api)
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "CodexPulse-Updater");
                request.Headers.TryAddWithoutValidation("Accept", metadata ? "application/vnd.github+json" : "application/octet-stream");
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            }
            else if (headers is not null)
                foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new HttpRequestException("The update server could not be reached.", null, exception.StatusCode);
            }
            if (response.IsSuccessStatusCode) return response;
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException("The update server returned HTTP " + (int)statusCode + ".", null, statusCode);
        }

        private async Task<byte[]> ReadBytesAsync(string url, IDictionary<string, string>? headers, bool api,
            CancellationToken token, bool metadata = false)
        {
            using var response = await SendAsync(url, headers, api, token, metadata).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaximumFeedBytes)
                throw new InvalidDataException("The update feed exceeds its size limit.");
            await using var input = await OpenInputAsync(response, token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await ReadInputAsync(input, buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumFeedBytes)
                    throw new InvalidDataException("The update feed exceeds its size limit.");
                output.Write(buffer, 0, count);
            }
            if (response.Content.Headers.ContentLength is { } length && output.Length != length)
                throw new NetworkReadException();
            return output.ToArray();
        }

        private async Task WriteFileAsync(string url, string targetFile, Action<int>? progress,
            IDictionary<string, string>? headers, bool api, CancellationToken token, Action? fileOpened = null)
        {
            using var response = await SendAsync(url, headers, api, token).ConfigureAwait(false);
            await using var input = await OpenInputAsync(response, token).ConfigureAwait(false);
            await using var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            fileOpened?.Invoke();
            var length = response.Content.Headers.ContentLength;
            var buffer = new byte[81920];
            long received = 0;
            var reported = 0;
            progress?.Invoke(0);
            int count;
            while ((count = await ReadInputAsync(input, buffer, token).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                received += count;
                var percent = length > 0 ? (int)Math.Clamp(received * 100d / length.Value, 0, 99) : 0;
                if (percent == reported) continue;
                reported = percent;
                progress?.Invoke(percent);
            }
            if (length.HasValue && received != length.Value) throw new NetworkReadException();
            await output.FlushAsync(token).ConfigureAwait(false);
            progress?.Invoke(100);
        }

        private static async Task<Stream> OpenInputAsync(HttpResponseMessage response, CancellationToken token)
        {
            try
            {
                return await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new NetworkReadException();
            }
            catch (HttpRequestException)
            {
                throw new HttpRequestException("The update download was interrupted.");
            }
        }

        private static async Task<int> ReadInputAsync(Stream input, byte[] buffer, CancellationToken token)
        {
            try
            {
                return await input.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new NetworkReadException();
            }
            catch (HttpRequestException)
            {
                throw new HttpRequestException("The update download was interrupted.");
            }
        }

        private sealed class NetworkReadException : IOException
        {
            public NetworkReadException() : base("The update download was interrupted.")
            {
            }
        }

        public void Dispose()
        {
            if (ownsHttp) http.Dispose();
        }
    }
}
