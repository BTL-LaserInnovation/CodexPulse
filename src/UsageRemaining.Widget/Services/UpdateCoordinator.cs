using System.Diagnostics;

namespace UsageRemaining.Widget.Services;

public sealed class UpdateCoordinator
{
    public const long CheckIntervalMilliseconds = 5 * 60 * 1000;
    private readonly IAppUpdateService service;
    private readonly Func<long> getTicks;
    private readonly CancellationTokenSource lifetime = new();
    private Task activeOperation = Task.CompletedTask;
    private bool stopped;
    private long nextCheck = long.MinValue;

    public UpdateCoordinator(IAppUpdateService service, Func<long>? getTicks = null)
    {
        this.service = service;
        this.getTicks = getTicks ?? (() => Environment.TickCount64);
        if (!service.IsInstalled) Trace.WriteLine("Update checks are disabled for an unpackaged build.");
    }

    public event EventHandler? Changed;
    public AppUpdate? AvailableUpdate { get; private set; }
    public bool IsDownloaded { get; private set; }
    public bool IsBusy { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public bool IsInstalled => service.IsInstalled;

    public Task CheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (stopped || IsBusy || IsDownloaded || !service.IsInstalled || getTicks() < nextCheck)
            return Task.CompletedTask;
        nextCheck = getTicks() + CheckIntervalMilliseconds;
        return activeOperation = RunAsync(async token =>
        {
            var update = await service.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            // Keep an offered update available when a later feed is temporarily empty.
            if (update is not null) AvailableUpdate = update;
            Trace.WriteLine(update is null ? "No newer update was found." : $"Update {update.Version} is available.");
        }, cancellationToken);
    }

    public Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (stopped || IsBusy || IsDownloaded || AvailableUpdate is not { } update)
            return Task.CompletedTask;
        return activeOperation = RunAsync(async token =>
        {
            Progress = 0;
            await service.DownloadAsync(update, value =>
            {
                Progress = Math.Clamp(value, 0, 100);
                Changed?.Invoke(this, EventArgs.Empty);
            }, token);
            token.ThrowIfCancellationRequested();
            IsDownloaded = true;
        }, cancellationToken);
    }

    public void PrepareRestart()
    {
        if (stopped || IsBusy || !IsDownloaded || AvailableUpdate is not { } update)
            throw new InvalidOperationException("An update must finish downloading before restarting.");
        service.PrepareRestart(update);
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        IsBusy = true;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        try { await operation(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Trace.TraceWarning("Update operation failed: {0}", ex);
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        lifetime.Cancel();
        await activeOperation;
        service.Dispose();
        lifetime.Dispose();
    }
}
