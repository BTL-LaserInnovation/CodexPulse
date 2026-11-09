using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UsageRemaining.Widget.Services;
using Velopack.Locators;
using Velopack.Sources;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static int RunUpdateTransportChecks(bool network, bool forceFallback = false)
    {
        try
        {
            if (network) UpdateTransportChecks.RunNetworkAsync(forceFallback).GetAwaiter().GetResult();
            else
            {
                UpdateTransportChecks.RunAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"All {passed} update transport checks passed.");
            }
            return 0;
        }
        catch (Exception error)
        {
            if (network) Console.Error.WriteLine($"UPDATE NETWORK: failure={error.GetType().Name}; status={UpdateTransportChecks.Status(error)}");
            else Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static int RunLegacyUpdateNetworkCheck()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var root = UpdateTransportChecks.NewRoot("update-legacy-network");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            new HttpClientFileDownloader().DownloadFile(
                AppUpdateService.ReleaseSource + "/BTL.CodexPulse-1.3.0-full.nupkg",
                Path.Combine(root, "legacy-full.nupkg.partial"), _ => { }, null, 20d / 60, cancel.Token)
                .GetAwaiter().GetResult();
            Console.WriteLine($"LEGACY UPDATE NETWORK: downloaded; elapsedSeconds={timer.Elapsed.TotalSeconds:0.0}");
        }
        catch (Exception error)
        {
            Console.WriteLine($"LEGACY UPDATE NETWORK: failure={error.GetType().Name}; status={UpdateTransportChecks.Status(error)}; elapsedSeconds={timer.Elapsed.TotalSeconds:0.0}");
        }
        return 0;
    }
}

internal static class UpdateTransportChecks
{
    private const string PackageName = "BTL.CodexPulse-1.3.1-full.nupkg";
    private const string ApiBase = "/repos/BTL-LaserInnovation/CodexPulse/releases/";
    private static readonly byte[] InertUpdater = Encoding.UTF8.GetBytes("Inert updater fixture. Never execute.");

    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            var error = await DownloadAsync(fixture);
            check(error is null && fixture.IsComplete && fixture.ApiCalls == 0 && fixture.PrimaryPackages == 1,
                "real update manager downloads the primary package without using the API");
            check(fixture.Progress.Count > 0 && fixture.Progress[^1] == 100 && fixture.Progress.All(p => p is >= 0 and <= 100) &&
                File.ReadAllBytes(fixture.UpdateExe).SequenceEqual(InertUpdater),
                "verified fixture reports progress and extracts only inert updater bytes inside its isolated root");
            fixture.Service.Dispose();
            using var response = await fixture.Client.GetAsync("https://fixture.invalid/borrowed-client");
            check(response.IsSuccessStatusCode && fixture.Client.Timeout == fixture.OriginalTimeout,
                "disposing an update service leaves its borrowed HTTP client usable and unmodified");
        }

        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) })
        {
            var error = await DownloadAsync(fixture);
            check(error is null && fixture.IsComplete && fixture.MetadataCalls == 1 && fixture.ApiPackages == 1 && fixture.CorrectApiHeaders,
                "primary HTTP 504 recovers through exact-version metadata and an official octet-stream asset request");
        }

        using (var fixture = new Fixture())
        {
            fixture.Primary = () => StreamHttp(new ReadStream(fixture.Payload[..37], _ => throw new IOException("fixture network interruption")), fixture.Payload.Length);
            var error = await DownloadAsync(fixture);
            check(error is null && fixture.IsComplete && fixture.ApiPackages == 1,
                "an interrupted primary response falls back and truncates its previously written partial bytes");
        }

        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout), Asset = () => Http(HttpStatusCode.ServiceUnavailable) })
        {
            var error = await DownloadAsync(fixture);
            check(error is HttpRequestException && fixture.NoComplete && fixture.ApiPackages == 1,
                "an API package HTTP failure never leaves a completed update");
        }

        var invalidMetadata = new (string Name, Action<Fixture> Change)[]
        {
            ("missing assets", f => f.Metadata.Remove("assets")),
            ("wrong release tag", f => f.Metadata["tag_name"] = "v1.3.2"),
            ("wrong asset name", f => f.PackageAsset["name"] = "BTL.CodexPulse-1.3.2-full.nupkg"),
            ("duplicate asset", f => ((JsonArray)f.Metadata["assets"]!).Add(f.PackageAsset.DeepClone())),
            ("negative asset identifier", f => f.PackageAsset["id"] = -1),
            ("draft release", f => f.Metadata["draft"] = true),
            ("prerelease", f => f.Metadata["prerelease"] = true),
            ("incomplete asset upload", f => f.PackageAsset["state"] = "starter")
        };
        foreach (var item in invalidMetadata)
        {
            using var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) };
            item.Change(fixture);
            var error = await DownloadAsync(fixture);
            check(error is InvalidDataException && fixture.NoComplete && fixture.ApiPackages == 0,
                $"API metadata rejects {item.Name} before requesting package bytes");
        }

        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) })
        {
            fixture.PackageAsset["url"] = "https://untrusted.invalid/asset";
            fixture.PackageAsset["browser_download_url"] = "https://untrusted.invalid/another-asset";
            var error = await DownloadAsync(fixture);
            check(error is null && fixture.IsComplete && fixture.Requests.All(r => r.Host is "github.com" or "api.github.com"),
                "API asset URLs are constructed from the fixed repository and numeric identifier");
        }

        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) })
        {
            var corrupt = fixture.Payload.ToArray();
            corrupt[^1] ^= 1;
            fixture.Asset = () => Http(HttpStatusCode.OK, corrupt);
            var error = await DownloadAsync(fixture);
            check(error is not null && error.GetType().Name == "ChecksumFailedException" && fixture.NoComplete,
                "real Velopack SHA256 verification rejects a same-length corrupt API package");
        }
        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) })
        {
            fixture.Asset = () => Http(HttpStatusCode.OK, fixture.Payload[..^1]);
            var error = await DownloadAsync(fixture);
            check(error is not null && error.GetType().Name == "ChecksumFailedException" && fixture.NoComplete,
                "real Velopack size verification rejects a truncated API package");
        }

        using (var fixture = new Fixture())
        using (var cancel = new CancellationTokenSource())
        {
            var update = await fixture.Service.CheckAsync(CancellationToken.None) ?? throw new InvalidOperationException("Fixture update missing");
            cancel.Cancel();
            var error = await CaptureAsync(() => fixture.Service.DownloadAsync(update, fixture.Progress.Add, cancel.Token));
            check(error is OperationCanceledException && fixture.NoComplete && fixture.PrimaryPackages == 0 && fixture.ApiCalls == 0,
                "pre-cancelled real-manager download sends no package or API request");
        }
        using (var fixture = new Fixture())
        using (var cancel = new CancellationTokenSource())
        {
            fixture.Primary = () => StreamHttp(new ReadStream(fixture.Payload[..37], token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); }), fixture.Payload.Length);
            var error = await DownloadAsync(fixture, cancel.Token);
            check(error is OperationCanceledException && fixture.NoComplete && fixture.ApiCalls == 0,
                "caller cancellation during the primary response never starts API fallback");
        }
        using (var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) })
        using (var cancel = new CancellationTokenSource())
        {
            fixture.Asset = () => StreamHttp(new ReadStream(fixture.Payload[..37], token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); }), fixture.Payload.Length);
            var error = await DownloadAsync(fixture, cancel.Token);
            check(error is OperationCanceledException && fixture.NoComplete && fixture.ApiPackages == 1,
                "caller cancellation interrupts an API response body without completing an update");
        }

        foreach (var mode in new[] { "malformed JSON", "oversized declared JSON", "oversized streamed JSON" })
        {
            using var fixture = new Fixture { Primary = () => Http(HttpStatusCode.GatewayTimeout) };
            fixture.MetadataResponse = () =>
            {
                if (mode == "malformed JSON") return Http(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{"));
                if (mode == "oversized streamed JSON") return StreamHttp(new ReadStream(new byte[2 * 1024 * 1024 + 1]));
                var response = Http(HttpStatusCode.OK, Encoding.UTF8.GetBytes("{}"));
                response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
                return response;
            };
            var error = await DownloadAsync(fixture);
            check(error is InvalidDataException && fixture.NoComplete && fixture.ApiPackages == 0,
                $"API metadata bounds reject {mode} without downloading a package");
        }

        using (var fixture = new Fixture())
        {
            fixture.BeforePrimary = () => Directory.CreateDirectory(fixture.PartialPath);
            var error = await DownloadAsync(fixture);
            check(error is IOException or UnauthorizedAccessException && fixture.NoComplete && fixture.ApiCalls == 0,
                "a local destination failure does not trigger API fallback");
        }

        using (var fixture = new Fixture { FailPrimaryFeed = true })
        {
            var error = await DownloadAsync(fixture);
            check(error is null && fixture.IsComplete && fixture.ApiFeeds == 1 && fixture.MetadataCalls == 1 && fixture.ApiPackages == 0 && fixture.CorrectApiHeaders,
                "an unavailable primary feed recovers through latest-release metadata and the API feed asset");
        }
    }

    public static async Task RunNetworkAsync(bool forceFallback = false)
    {
        var root = NewRoot("update-network");
        var packages = Path.Combine(root, "packages");
        Directory.CreateDirectory(packages);
        using var handler = new NetworkHandler(forceFallback);
        using var http = new HttpClient(handler);
        using var service = new AppUpdateService(http, Locator(root, packages));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var update = await service.CheckAsync(cancel.Token) ?? throw new InvalidOperationException("No public update was offered");
        await service.DownloadAsync(update, _ => { }, cancel.Token);
        var package = Directory.GetFiles(packages, "*.nupkg").Single();
        await using var input = File.OpenRead(package);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancel.Token));
        if (forceFallback && handler.ApiRequests < 4)
            throw new InvalidOperationException("The public API fallback was not exercised");
        Console.WriteLine($"UPDATE NETWORK: version={update.Version}; sha256={hash}; bytes={input.Length}; apiRequests={handler.ApiRequests}; forcedPrimary504={forceFallback}");
    }

    private sealed class NetworkHandler(bool forceFallback) : DelegatingHandler(new HttpClientHandler())
    {
        public int ApiRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (forceFallback && request.RequestUri?.Host == "github.com")
                return Task.FromResult(Http(HttpStatusCode.GatewayTimeout));
            if (request.RequestUri?.Host == "api.github.com") ApiRequests++;
            return base.SendAsync(request, cancellationToken);
        }
    }

    public static string Status(Exception error) => error is HttpRequestException { StatusCode: { } status } ? ((int)status).ToString() : "none";

    public static string NewRoot(string category)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", category, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static TestVelopackLocator Locator(string root, string packages) => new(
        "BTL.CodexPulse", "1.2.2", packages, Path.Combine(root, "app"), root, Path.Combine(root, "Update.exe"), "win");

    private static async Task<Exception?> DownloadAsync(Fixture fixture, CancellationToken token = default)
    {
        var update = await fixture.Service.CheckAsync(CancellationToken.None) ?? throw new InvalidOperationException("Fixture update missing");
        if (update.Version != "1.3.1") throw new InvalidOperationException("Fixture offered the wrong version");
        return await CaptureAsync(() => fixture.Service.DownloadAsync(update, fixture.Progress.Add, token));
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try { await operation(); return null; }
        catch (Exception error) when (error is not OutOfMemoryException) { return error; }
    }

    private static HttpResponseMessage Http(HttpStatusCode status, byte[]? body = null) => new(status)
    {
        Content = new ByteArrayContent(body ?? [])
    };

    private static HttpResponseMessage StreamHttp(Stream stream, long? length = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        if (length.HasValue) response.Content.Headers.ContentLength = length;
        return response;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = NewRoot("update-transport");
        public string Packages { get; }
        public string CompletePath => Path.Combine(Packages, PackageName);
        public string PartialPath => CompletePath + ".partial";
        public string UpdateExe => Path.Combine(Root, "Update.exe");
        public byte[] Payload { get; } = Package();
        public byte[] Feed { get; }
        public HttpClient Client { get; }
        public AppUpdateService Service { get; }
        public TimeSpan OriginalTimeout { get; }
        public JsonObject Metadata { get; } = JsonNode.Parse("""
            {"tag_name":"v1.3.1","draft":false,"prerelease":false,"assets":[
              {"id":701,"name":"BTL.CodexPulse-1.3.1-full.nupkg","state":"uploaded"},
              {"id":702,"name":"releases.win.json","state":"uploaded"}]}
            """)!.AsObject();
        public JsonObject PackageAsset => Metadata["assets"]![0]!.AsObject();
        public Func<HttpResponseMessage>? Primary { get; set; }
        public Func<HttpResponseMessage>? Asset { get; set; }
        public Func<HttpResponseMessage>? MetadataResponse { get; set; }
        public Action? BeforePrimary { get; set; }
        public bool FailPrimaryFeed { get; set; }
        public int PrimaryPackages { get; private set; }
        public int MetadataCalls { get; private set; }
        public int ApiPackages { get; private set; }
        public int ApiFeeds { get; private set; }
        public int ApiCalls => MetadataCalls + ApiPackages + ApiFeeds;
        public bool CorrectApiHeaders { get; private set; } = true;
        public List<int> Progress { get; } = [];
        public List<Uri> Requests { get; } = [];
        public bool NoComplete => !File.Exists(CompletePath) && !Directory.EnumerateFiles(Packages, "*.nupkg").Any();
        public bool IsComplete => File.Exists(CompletePath) && File.ReadAllBytes(CompletePath).SequenceEqual(Payload) && !File.Exists(PartialPath);

        public Fixture()
        {
            Packages = Path.Combine(Root, "packages");
            Directory.CreateDirectory(Packages);
            Directory.CreateDirectory(Path.Combine(Root, "app"));
            Feed = JsonSerializer.SerializeToUtf8Bytes(new { Assets = new[] { new {
                PackageId = "BTL.CodexPulse", Version = "1.3.1", Type = "Full", FileName = PackageName,
                SHA1 = Convert.ToHexString(SHA1.HashData(Payload)), SHA256 = Convert.ToHexString(SHA256.HashData(Payload)), Size = Payload.LongLength
            } } });
            Client = new HttpClient(new Handler(this));
            OriginalTimeout = Client.Timeout;
            Service = new AppUpdateService(Client, Locator(Root, Packages));
        }

        private HttpResponseMessage Respond(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException("Fixture request URI missing");
            Requests.Add(uri);
            if (uri.Host == "fixture.invalid") return Http(HttpStatusCode.OK);
            if (uri.Host == "github.com" && uri.AbsolutePath.EndsWith("/releases.win.json", StringComparison.Ordinal))
                return FailPrimaryFeed ? Http(HttpStatusCode.GatewayTimeout) : Http(HttpStatusCode.OK, Feed);
            if (uri.Host == "github.com" && uri.AbsolutePath.EndsWith("/" + PackageName, StringComparison.Ordinal))
            {
                PrimaryPackages++;
                BeforePrimary?.Invoke();
                return Primary?.Invoke() ?? Http(HttpStatusCode.OK, Payload);
            }
            if (uri.Host != "api.github.com" || !uri.AbsolutePath.StartsWith(ApiBase, StringComparison.Ordinal))
                throw new InvalidOperationException("An update request escaped the fixture repository");
            var metadata = !uri.AbsolutePath.Contains("/assets/", StringComparison.Ordinal);
            var expectedAccept = metadata ? "application/vnd.github+json" : "application/octet-stream";
            CorrectApiHeaders &= request.Headers.Accept.Any(h => h.MediaType == expectedAccept) &&
                request.Headers.UserAgent.Count > 0 && request.Headers.Authorization is null;
            if (metadata)
            {
                MetadataCalls++;
                var expected = FailPrimaryFeed && ApiFeeds == 0 ? ApiBase + "latest" : ApiBase + "tags/v1.3.1";
                if (uri.AbsolutePath != expected) throw new InvalidOperationException("The update metadata request did not preserve its release version");
                return MetadataResponse?.Invoke() ?? Http(HttpStatusCode.OK, Encoding.UTF8.GetBytes(Metadata.ToJsonString()));
            }
            if (uri.AbsolutePath == ApiBase + "assets/701")
            {
                ApiPackages++;
                return Asset?.Invoke() ?? Http(HttpStatusCode.OK, Payload);
            }
            if (uri.AbsolutePath == ApiBase + "assets/702")
            {
                ApiFeeds++;
                return Http(HttpStatusCode.OK, Feed);
            }
            throw new InvalidOperationException("Unexpected fixture asset identifier");
        }

        public void Dispose() { Service.Dispose(); Client.Dispose(); }

        private sealed class Handler(Fixture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(owner.Respond(request, cancellationToken));
        }
    }

    private static byte[] Package()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var manifest = new StreamWriter(zip.CreateEntry("BTL.CodexPulse.nuspec").Open(), new UTF8Encoding(false)))
                manifest.Write("""
                    <package><metadata><id>BTL.CodexPulse</id><version>1.3.1</version><title>Fixture</title>
                    <authors>Fixture</authors><description>Isolated update transport fixture</description>
                    <mainExe>CodexPulse.exe</mainExe></metadata></package>
                    """);
            using (var updater = zip.CreateEntry("lib/app/Squirrel.exe").Open()) updater.Write(InertUpdater);
            using (var application = zip.CreateEntry("lib/app/CodexPulse.exe").Open()) application.WriteByte(0);
        }
        return output.ToArray();
    }

    // Non-seekable so StreamContent can model interrupted bodies and unknown Content-Length.
    private sealed class ReadStream(byte[] prefix, Action<CancellationToken>? terminal = null) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position == prefix.Length) { terminal?.Invoke(cancellationToken); return ValueTask.FromResult(0); }
            var count = Math.Min(buffer.Length, prefix.Length - position);
            prefix.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
