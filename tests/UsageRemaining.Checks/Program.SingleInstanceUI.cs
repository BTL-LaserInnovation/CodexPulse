using System.Reflection;
using System.Windows.Threading;
using UsageRemaining.Widget;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckQueuedStartupActivation(App app)
    {
        var request = RequestExistingAppActivationAsync(app);
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        Check(request.IsCompletedSuccessfully && app.MainWindow is null && AppActivationPending(app),
            "duplicate launch before main-window creation queues its activation in App");
    }

    private static Task<bool> RequestExistingAppActivationAsync(App app) =>
        (Task<bool>)((typeof(App).GetMethod("RequestActivationAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing App activation seam")).Invoke(app, [CancellationToken.None])
            ?? throw new InvalidOperationException("Missing App activation result"));

    private static bool AppActivationPending(App app) =>
        (bool)(typeof(App).GetField("activationPending", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(app)
            ?? throw new InvalidOperationException("Missing pending App activation seam"));
}
