using System.Diagnostics;
using System.Windows;
using Velopack;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

internal static class Program
{
    private static SingleInstanceCoordinator? primaryInstance;

    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistrationService.RemoveOnUninstall()).Run();
        SingleInstanceCoordinator? instance = null;
        try
        {
            instance = new SingleInstanceCoordinator();
            var startup = Stopwatch.StartNew();
            while (!instance.TryAcquireOwnership())
            {
                if (instance.TryActivateExistingInstance(TimeSpan.FromMilliseconds(500))) return;
                if (startup.Elapsed >= TimeSpan.FromSeconds(6))
                {
                    MessageBox.Show("실행 중인 Codex Pulse에 연결하지 못했습니다. 잠시 후 다시 실행해 주세요.",
                        "Codex Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Thread.Sleep(50);
            }
            primaryInstance = instance;
            var app = new App();
            instance.StartListening(app.RequestActivationAsync);
            app.InitializeComponent();
            app.Run();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBox.Show("Codex Pulse를 시작하지 못했습니다.\n" + ex.Message,
                "Codex Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (primaryInstance is null) instance?.Dispose();
            else
            {
                primaryInstance.StopListening();
                // Keep ownership rooted on the main STA until Windows ends this process,
                // including WPF shutdown and updater/process-exit callbacks.
                GC.KeepAlive(primaryInstance);
            }
        }
    }
}
