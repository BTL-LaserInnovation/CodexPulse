using Velopack;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistrationService.RemoveOnUninstall()).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
