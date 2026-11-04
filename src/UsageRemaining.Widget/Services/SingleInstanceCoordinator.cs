using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UsageRemaining.Widget.Services;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const byte Ready = 1;
    private const byte Activate = 2;
    private const byte Accepted = 3;
    private const byte Retry = 4;
    private readonly Mutex mutex;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly object listenerLock = new();
    private CancellationTokenSource? listenerCancellation;
    private NamedPipeServerStream? listener;
    private bool listenerStopped;
    private bool ownsMutex;
    private bool disposed;

    public SingleInstanceCoordinator(string? dataDirectory = null)
    {
        dataDirectory ??= Environment.GetEnvironmentVariable("USAGEREMAINING_DATA_DIR");
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        var scope = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        scope += "|" + process.SessionId;
        if (dataDirectory is not null)
            scope += "|" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)).ToUpperInvariant();
        // Stable across executable paths, versions and updater replacement directories.
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        PipeName = "CodexPulse.UsageRemaining.SingleInstance." + key;
        MutexName = @"Local\" + PipeName;
        mutex = new Mutex(false, MutexName);
    }

    public string MutexName { get; }
    public string PipeName { get; }
    public Task Completion { get; private set; } = Task.CompletedTask;

    public bool TryAcquireOwnership()
    {
        VerifyOwnerThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownsMutex) return true;
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        return ownsMutex;
    }

    public void StartListening(Func<CancellationToken, Task<bool>> activationHandler)
    {
        ArgumentNullException.ThrowIfNull(activationHandler);
        VerifyOwnerThread();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ownsMutex) throw new InvalidOperationException("Only the primary instance can listen for activation.");
        if (listenerCancellation is not null) throw new InvalidOperationException("The activation listener has already started.");
        var firstListener = CreateListener();
        var cancellation = new CancellationTokenSource();
        listenerCancellation = cancellation;
        listener = firstListener;
        // The listener must never capture the WPF synchronization context.
        Completion = Task.Run(() => ListenAsync(firstListener, activationHandler, cancellation.Token));
    }

    public bool TryActivateExistingInstance(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var expired = 0;
        // Repeated cancellation also covers an I/O submitted just as the deadline fires.
        // Closing a SafeHandle alone can defer closure until an outstanding read completes.
        using var deadline = new Timer(_ =>
        {
            Interlocked.Exchange(ref expired, 1);
            try
            {
                if (pipe.IsConnected) _ = CancelIoEx(pipe.SafePipeHandle, IntPtr.Zero);
            }
            catch (InvalidOperationException ex)
            {
                Debug.WriteLine("Activation deadline ended with the pipe: " + ex.Message);
            }
        }, null, timeout, TimeSpan.FromMilliseconds(25));
        try
        {
            pipe.Connect((int)Math.Ceiling(timeout.TotalMilliseconds));
            if (Volatile.Read(ref expired) != 0 || pipe.ReadByte() != Ready || Volatile.Read(ref expired) != 0 ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)) return false;
            // Ask Windows for the actual pipe owner rather than trusting a PID sent over IPC.
            _ = AllowSetForegroundWindow(processId);
            if (Volatile.Read(ref expired) != 0) return false;
            pipe.WriteByte(Activate);
            return Volatile.Read(ref expired) == 0 && pipe.ReadByte() == Accepted && Volatile.Read(ref expired) == 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        {
            Debug.WriteLine("Existing-instance activation retry: " + ex.Message);
            return false;
        }
    }

    public void StopListening()
    {
        lock (listenerLock)
        {
            if (listenerCancellation is null || listenerStopped) return;
            listenerStopped = true;
            listenerCancellation.Cancel();
            listener?.Dispose();
            listener = null;
            var cancellation = listenerCancellation;
            _ = Completion.ContinueWith(_ => cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        VerifyOwnerThread();
        if (disposed) return;
        StopListening();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
        disposed = true;
    }

    private NamedPipeServerStream CreateListener() => new(PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ListenAsync(NamedPipeServerStream firstListener,
        Func<CancellationToken, Task<bool>> activationHandler, CancellationToken cancellationToken)
    {
        var pipe = firstListener;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using (pipe)
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        requestCancellation.CancelAfter(TimeSpan.FromSeconds(1));
                        var token = requestCancellation.Token;
                        await pipe.WriteAsync(new byte[] { Ready }, token).ConfigureAwait(false);
                        var command = new byte[1];
                        if (await pipe.ReadAsync(command, token).ConfigureAwait(false) == 1 && command[0] == Activate)
                        {
                            var accepted = await activationHandler(token).ConfigureAwait(false);
                            await pipe.WriteAsync(new byte[] { accepted ? Accepted : Retry }, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            Debug.WriteLine("Activation request ended: " + ex.Message);
                    }
                }
                lock (listenerLock)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    pipe = CreateListener();
                    listener = pipe;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failed IPC endpoint never permits a second state-writing widget.
            Debug.WriteLine("Activation listener stopped: " + ex);
        }
        finally
        {
            pipe.Dispose();
            lock (listenerLock) listener = null;
        }
    }

    private void VerifyOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Instance ownership must stay on its creating thread.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafePipeHandle pipe, IntPtr overlapped);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
