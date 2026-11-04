using System.IO;
using System.Net.Http;
using System.Text;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public sealed class PublicResetCalendarClient : IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public PublicResetCalendarClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = RequestTimeout };
    }

    public async Task<PublicResetCalendar> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = CreateRequest();
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateLength(response);
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) != 0)
        {
            ValidateReceivedLength(buffer.Length + read);
            buffer.Write(chunk, 0, read);
        }
        timeout.Token.ThrowIfCancellationRequested();
        return PublicResetCalendar.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), DateTimeOffset.UtcNow);
    }

    public PublicResetCalendar Fetch(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var request = CreateRequest();
        using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        ValidateLength(response);
        using var stream = response.Content.ReadAsStream(timeout.Token);
        // Headers-only completion ends HttpClient's timeout coverage; abort a blocked body read on cancellation.
        using var cancellation = timeout.Token.Register(stream.Dispose);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) != 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                ValidateReceivedLength(buffer.Length + read);
                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception exception) when (timeout.IsCancellationRequested && exception is IOException or ObjectDisposedException)
        {
            throw new OperationCanceledException("Public reset calendar request was cancelled.", exception, timeout.Token);
        }
        timeout.Token.ThrowIfCancellationRequested();
        return PublicResetCalendar.Parse(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), DateTimeOffset.UtcNow);
    }

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, PublicResetCalendar.PublicSource);
        request.Headers.UserAgent.ParseAdd("CodexPulse/" + typeof(PublicResetCalendarClient).Assembly.GetName().Version?.ToString(3));
        request.Headers.Accept.ParseAdd("text/html");
        return request;
    }

    private static void ValidateLength(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentLength is { } length) ValidateReceivedLength(length);
    }

    private static void ValidateReceivedLength(long length)
    {
        if (length > MaximumResponseBytes) throw new IOException("Public reset calendar response is too large.");
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
