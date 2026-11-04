using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace UsageRemaining.Widget.Services;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly string _executable;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private Process? _process;
    private Task? _readerTask;
    private Task? _stderrTask;
    private Task? _disposeTask;
    private Exception? _terminalError;
    private long _nextRequestId;
    private long _accountRevision;
    private bool _initialized;
    private bool _disposed;

    public CodexAppServerClient(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("Codex 실행 파일을 지정해 주세요.", nameof(executable));

        _executable = executable;
        _lifetimeToken = _lifetime.Token;
    }

    public event EventHandler? RateLimitsChanged;
    public event EventHandler? AccountChanged;
    public event EventHandler? LoginCompleted;
    public long AccountRevision => Interlocked.Read(ref _accountRevision);

    public Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("account/rateLimits/read", new { }, cancellationToken);
    }

    public Task<JsonElement> ReadAccountAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("account/read", new { refreshToken = false }, cancellationToken);
    }

    public Task<JsonElement> ConsumeResetCreditAsync(string idempotencyKey, string selectedCreditId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedCreditId);
        // The caller retains this key after an ambiguous result; consuming a credit is never retried here.
        return ExecuteAsync("account/rateLimitResetCredit/consume",
            new { idempotencyKey, creditId = selectedCreditId }, cancellationToken);
    }

    public async Task<string?> StartLoginAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync("account/login/start", new { type = "chatgpt" }, cancellationToken)
            .ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("authUrl", out var authUrl) && authUrl.ValueKind == JsonValueKind.String
            ? authUrl.GetString()
            : null;
    }

    private async Task<JsonElement> ExecuteAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _lifetimeToken);
        try
        {
            await EnsureInitializedAsync(linked.Token).ConfigureAwait(false);
            return await SendRequestAsync(method, parameters, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Codex 요청이 취소되었습니다.", cancellationToken);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Codex 응답 시간이 초과되었습니다. 다시 연결해 주세요.");
        }
        catch (OperationCanceledException)
        {
            ThrowIfUnavailable();
            throw new IOException("Codex 연결이 종료되었습니다. 다시 연결해 주세요.");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_initialized)
                return;

            StartProcess();
            try
            {
                await SendRequestAsync("initialize", new
                {
                    clientInfo = new { name = "codex_pulse", title = "Codex Pulse", version = "1.0.0" },
                    capabilities = new { experimentalApi = false }
                }, cancellationToken).ConfigureAwait(false);
                await WriteMessageAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            catch
            {
                FailConnection(new IOException("Codex 초기화에 실패했습니다. 다시 연결해 주세요."));
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private void StartProcess()
    {
        lock (_stateLock)
        {
            ThrowIfUnavailable();
            if (_process is not null)
                return;

            var startInfo = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--listen");
            startInfo.ArgumentList.Add("stdio://");
            var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                process.Dispose();
                _terminalError = new IOException("Codex를 시작하지 못했습니다. 실행 파일을 확인해 주세요.");
                throw _terminalError;
            }

            _process = process;
            _readerTask = ReadOutputAsync(process);
            _stderrTask = DrainErrorAsync(process);
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Cancellation can race a response after the caller has stopped awaiting it.
        _ = completion.Task.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _pending[id] = completion;
        try
        {
            // Recheck after registering so a simultaneous pipe failure cannot strand this request.
            ThrowIfUnavailable();
            await WriteMessageAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            var process = _process ?? throw new IOException("Codex 연결이 준비되지 않았습니다.");
            try
            {
                await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                FailConnection(new IOException("Codex에 요청을 전송하지 못했습니다. 다시 연결해 주세요."));
                if (exception is OperationCanceledException)
                    throw;
                throw new IOException("Codex에 요청을 전송하지 못했습니다. 다시 연결해 주세요.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadOutputAsync(Process process)
    {
        try
        {
            while (!_lifetimeToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(_lifetimeToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                    throw new JsonException();

                if (message.TryGetProperty("method", out var methodElement))
                {
                    if (message.TryGetProperty("id", out var serverId))
                    {
                        // This read-only client never approves server-initiated actions.
                        await WriteMessageAsync(new
                        {
                            id = serverId.Clone(),
                            error = new { code = -32601, message = "Method not found" }
                        }, _lifetimeToken).ConfigureAwait(false);
                    }
                    else if (methodElement.ValueKind == JsonValueKind.String)
                    {
                        switch (methodElement.GetString())
                        {
                            case "account/rateLimits/updated":
                                QueueNotification(RateLimitsChanged);
                                break;
                            case "account/updated":
                                Interlocked.Increment(ref _accountRevision);
                                QueueNotification(AccountChanged);
                                break;
                            case "account/login/completed":
                                QueueNotification(LoginCompleted);
                                break;
                        }
                    }
                    continue;
                }

                if (!message.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id)
                    || !_pending.TryRemove(id, out var completion))
                    continue;

                if (message.TryGetProperty("error", out _))
                    completion.TrySetException(new InvalidOperationException("Codex 요청을 처리하지 못했습니다. 로그인 상태와 연결을 확인해 주세요."));
                else if (message.TryGetProperty("result", out var result))
                    completion.TrySetResult(result.Clone());
                else
                    completion.TrySetException(new IOException("Codex 응답 형식이 올바르지 않습니다. 다시 연결해 주세요."));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or JsonException or OperationCanceledException)
        {
            // Payloads and stderr may contain authentication data; never include them in failures.
        }
        finally
        {
            FailConnection(new IOException("Codex 연결이 종료되었습니다. 다시 연결해 주세요."));
        }
    }

    private async Task DrainErrorAsync(Process process)
    {
        var buffer = new char[1024];
        try
        {
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), _lifetimeToken).ConfigureAwait(false) != 0)
            {
                // Drain without retaining or displaying potentially sensitive diagnostics.
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // Shutdown closes the redirected stream.
        }
    }

    private void QueueNotification(EventHandler? handlers)
    {
        if (handlers is null)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;
            }
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                    // A subscriber must not stop protocol reads or other subscribers.
                }
            }
        });
    }

    private void ThrowIfUnavailable()
    {
        lock (_stateLock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CodexAppServerClient), "Codex 연결이 닫혔습니다.");
            if (_terminalError is not null)
                throw _terminalError;
        }
    }

    private void FailConnection(Exception error)
    {
        lock (_stateLock)
        {
            _terminalError ??= error;
            error = _terminalError;
        }
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var completion))
                completion.TrySetException(error);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _lifetime.Cancel();
        FailConnection(new ObjectDisposedException(nameof(CodexAppServerClient), "Codex 연결이 닫혔습니다."));
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or TimeoutException or AggregateException)
            {
                // Continue releasing owned pipes even if the process has already exited or cannot be waited on.
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            await Task.WhenAll(_readerTask ?? Task.CompletedTask, _stderrTask ?? Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Disposal is bounded even when an OS pipe does not finish closing promptly.
        }
        _lifetime.Dispose();
    }
}
