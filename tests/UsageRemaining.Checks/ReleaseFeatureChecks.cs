using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static class ReleaseFeatureChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private const string FixtureFeed = """<section data-cx-cal><a class="cx-stamp" data-id="one" data-at="2026-11-01T00:43:20.047Z" data-type="banked"></a></section>""";

    public static void Run(Action<bool, string> check)
    {
        var missing = Balance("{}");
        check(missing.AvailableCount is null && !missing.DetailsKnown && missing.Credits.Count == 0,
            "missing reset-credit balance remains unknown");
        var partial = Balance("""{"rateLimitResetCredits":{"availableCount":3,"credits":null}}""");
        check(partial.AvailableCount == 3 && !partial.DetailsKnown && partial.Credits.Count == 0,
            "credit count preserved when per-credit details unavailable");
        var capped = Balance("""{"rateLimitResetCredits":{"availableCount":3,"credits":[{"id":"one","resetType":"codexRateLimits","status":"available"}]}}""");
        check(capped.AvailableCount == 3 && capped.DetailsKnown && capped.Credits.Count == 1,
            "partial credit detail list never replaces backend total count");
        var unknownCount = Balance("""{"rateLimitResetCredits":{"credits":[{"id":"one","resetType":"codexRateLimits","status":"available"}]}}""");
        check(unknownCount.AvailableCount is null && unknownCount.DetailsKnown && unknownCount.Credits.Count == 1,
            "known credit details do not fabricate missing total count");
        var balance = Balance("""{"rateLimitResetCredits":{"availableCount":0,"credits":[{"id":"permanent","resetType":"codexRateLimits","status":"available","expiresAt":null},{"id":"later","resetType":"codexRateLimits","status":"available","expiresAt":1793923200},{"id":"earlier","resetType":"codexRateLimits","status":"available","expiresAt":1793836800},{"id":"earlier","resetType":"codexRateLimits","status":"available","expiresAt":1793836800}]}}""");
        check(balance.AvailableCount == 0 && balance.DetailsKnown && balance.Credits.Count == 3,
            "authoritative zero count retained and identical credit duplicates collapsed");
        check(balance.Credits.Select(c => c.Id).SequenceEqual(["earlier", "later", "permanent"]),
            "credits sort earliest expiry first and unknown expiry last");
        var permanent = balance.Credits.Single(c => c.Id == "permanent");
        check(permanent.IsAvailable(Now) && !(permanent with { Status = "consumed" }).IsAvailable(Now)
            && !(permanent with { ResetType = "other" }).IsAvailable(Now),
            "credit availability respects status and supported reset type");
        check(!(permanent with { GrantedAt = Now.AddSeconds(1) }).IsAvailable(Now)
            && !(permanent with { ExpiresAt = Now }).IsAvailable(Now)
            && (permanent with { ExpiresAt = Now.AddSeconds(1) }).IsAvailable(Now),
            "credit grant and expiry boundary checks");
        foreach (var expiry in new[] { "1e300", "\"invalid\"", "{}" })
        {
            var malformed = Balance("{\"rateLimitResetCredits\":{\"credits\":[{\"id\":\"bad\",\"resetType\":\"codexRateLimits\",\"status\":\"available\",\"expiresAt\":" + expiry + "}]}}");
            check(!malformed.Credits.Single().IsAvailable(Now), "malformed explicit credit expiry fails closed: " + expiry);
        }
        var malformedGrant = Balance("""{"rateLimitResetCredits":{"credits":[{"id":"unverified-grant","resetType":"codexRateLimits","status":"available","grantedAt":"unknown"}]}}""");
        check(!malformedGrant.Credits.Single().IsAvailable(Now),
            "malformed explicit credit grant time fails closed");
        var conflicts = Balance("""{"rateLimitResetCredits":{"credits":[{"id":"same","resetType":"codexRateLimits","status":"available"},{"id":"same","resetType":"codexRateLimits","status":"consumed"},{"id":"same","resetType":"codexRateLimits","status":"available"},{"id":"   "},null,12]}}""");
        check(conflicts.Credits.Count == 1 && !conflicts.Credits.Single().IsAvailable(Now),
            "conflicting duplicate IDs remain unavailable and invalid entries ignored");
        check(Balance("""{"rateLimitResetCredits":{"availableCount":-1,"credits":[]}}""").AvailableCount is null,
            "negative credit counts remain unknown");

        OpenTheRankChecks.Run(check);

        var text = new TextBlock { Text = "ordinary background text" };
        var background = new Border { Child = text };
        check(WindowInteraction.CanDragFrom(text) && WindowInteraction.CanDragFrom(background)
            && !WindowInteraction.CanDragFrom(null), "ordinary surface descendants support window dragging");
        var buttonText = new TextBlock();
        var button = new Button { Content = buttonText };
        var scrollText = new TextBlock();
        var scroll = new ScrollViewer { Content = scrollText };
        check(!WindowInteraction.CanDragFrom(button) && !WindowInteraction.CanDragFrom(buttonText)
            && !WindowInteraction.CanDragFrom(new Slider()) && !WindowInteraction.CanDragFrom(scrollText)
            && !WindowInteraction.CanDragFrom(new CheckBox()) && !WindowInteraction.CanDragFrom(new TextBox()),
            "interactive controls and their descendants excluded from background dragging");
        GC.KeepAlive(scroll);
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var handler = new FeedHandler();
        using var http = new HttpClient(handler);
        using (var client = new PublicResetCalendarClient(http))
        {
            check((await client.FetchAsync(CancellationToken.None)).Events.Count == 1
                && client.Fetch(CancellationToken.None).Events.Count == 1 && handler.ValidRequestCount == 2,
                "public HTTP sync and async paths use expected unauthenticated feed request");
            handler.Status = HttpStatusCode.ServiceUnavailable;
            check(await ThrowsAsync<HttpRequestException>(() => client.FetchAsync(CancellationToken.None))
                && Throws<HttpRequestException>(() => client.Fetch(CancellationToken.None)),
                "public HTTP sync and async paths reject unsuccessful status");
            handler.Status = HttpStatusCode.OK;
            handler.Payload = new string('x', 2 * 1024 * 1024 + 1);
            check(await ThrowsAsync<IOException>(() => client.FetchAsync(CancellationToken.None))
                && Throws<IOException>(() => client.Fetch(CancellationToken.None)),
                "public HTTP sync and async reject oversized declared response");
            handler.OmitLength = true;
            check(await ThrowsAsync<IOException>(() => client.FetchAsync(CancellationToken.None))
                && Throws<IOException>(() => client.Fetch(CancellationToken.None)),
                "public HTTP sync and async bound streamed body without content length");
            handler.Payload = "{}";
            check(await ThrowsAsync<InvalidDataException>(() => client.FetchAsync(CancellationToken.None))
                && Throws<InvalidDataException>(() => client.Fetch(CancellationToken.None)),
                "public HTTP sync and async reject unexpected HTML layout");
            handler.Payload = FixtureFeed;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            check(await ThrowsAsync<OperationCanceledException>(() => client.FetchAsync(cancellation.Token))
                && Throws<OperationCanceledException>(() => client.Fetch(cancellation.Token)),
                "public HTTP sync and async honor caller cancellation");
        }
        using var borrowedResponse = await http.GetAsync("https://example.com/borrowed-client");
        check(borrowedResponse.IsSuccessStatusCode && !handler.Disposed,
            "disposing calendar client preserves borrowed HTTP client lifetime");
        await using var account = new CodexAppServerClient("not-started.exe");
        check(await ThrowsAsync<ArgumentException>(() => account.ConsumeResetCreditAsync(" ", "credit", CancellationToken.None))
            && await ThrowsAsync<ArgumentException>(() => account.ConsumeResetCreditAsync("request", " ", CancellationToken.None)),
            "consume validates explicit credit ID and idempotency key before starting process");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static ResetCreditBalance Balance(string value) => ResetCreditBalance.Parse(Json(value));

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        return false;
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return true; }
        return false;
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public string Payload { get; set; } = FixtureFeed;
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool OmitLength { get; set; }
        public bool Disposed { get; private set; }
        public int ValidRequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.Host == "opentherank.com")
            {
                if (request.Method != HttpMethod.Get || request.RequestUri.AbsoluteUri != PublicResetCalendar.PublicSource
                    || request.Headers.UserAgent.ToString() != "CodexPulse/" + typeof(PublicResetCalendarClient).Assembly.GetName().Version?.ToString(3)
                    || !request.Headers.Accept.Any(a => a.MediaType == "text/html") || request.Headers.Authorization is not null)
                    throw new InvalidOperationException("Unexpected public feed request.");
                ValidRequestCount++;
            }
            return new HttpResponseMessage(Status)
            {
                Content = OmitLength ? new UnknownLengthContent(Payload) : new StringContent(Payload)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class UnknownLengthContent(string payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(Encoding.UTF8.GetBytes(payload)).AsTask();
        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
            => new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false);
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(payload), writable: false));
    }
}
