using System.Windows;
using System.Windows.Threading;

namespace UsageRemaining.Widget;

public partial class App : Application
{
    private bool activationPending;
    private MainWindow? activationWindow;

    internal Task<bool> RequestActivationAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return Task.FromResult(false);
        try
        {
            return Dispatcher.InvokeAsync(() =>
            {
                var window = activationWindow ?? MainWindow as MainWindow;
                if (Dispatcher.HasShutdownStarted || window is { IsClosing: true }) return false;
                activationPending = true;
                ActivatePendingWindow();
                return true;
            }, DispatcherPriority.Normal, cancellationToken).Task;
        }
        catch (InvalidOperationException) when (Dispatcher.HasShutdownStarted)
        {
            return Task.FromResult(false);
        }
    }

    internal void ActivatePendingWindow()
    {
        Dispatcher.VerifyAccess();
        if (!activationPending || activationWindow is not { IsLoaded: true, IsClosing: false } window) return;
        activationPending = false;
        window.ActivateExistingInstance();
    }

    internal void MainWindowLoaded(MainWindow window)
    {
        Dispatcher.VerifyAccess();
        // Retain the window through shutdown, when Application.MainWindow can become null.
        activationWindow = window;
        ActivatePendingWindow();
    }
}
