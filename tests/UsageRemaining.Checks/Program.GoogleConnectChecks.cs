using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using UsageRemaining.Widget;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static async Task GoogleConnectionCancelChecks(MainWindow window, FakeGoogleCalendarService google)
    {
        var connect = (Button)window.FindName("GoogleConnectButton");
        var refresh = (Button)window.FindName("GoogleRefreshButton");
        var add = (Button)window.FindName("GoogleAddButton");
        var originalTimeout = WindowField<TimeSpan>(window, "googleConnectTimeout");
        SetWindowField(window, "googleInitialized", true);
        google.SetConnection(false, true);

        // Model a closed browser that never returns a callback, then delay receiver cleanup.
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        google.ConnectBehavior = async (_, token) =>
        {
            waiting.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                canceled.SetResult();
                await cleanup.Task;
                throw;
            }
        };
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await waiting.Task;
        Check(connect.IsEnabled && connect.Content?.ToString() == "연결 취소" && !add.IsEnabled && !refresh.IsEnabled
            && AutomationProperties.GetName(connect).Contains("취소"),
            "abandoned browser leaves an enabled accessible cancel action while other Google actions stay gated");
        Capture(window, "google-connect-waiting-cancel");
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await canceled.Task;
        var attempts = google.Connects;
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(WindowField<bool>(window, "googleConnecting") && !connect.IsEnabled && google.Connects == attempts,
            "cancel keeps attempt ownership and blocks retry until receiver cleanup finishes");
        cleanup.SetResult();
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleConnecting"), "Google cancel receiver cleanup finishes");
        Check(!google.State.Connected && connect.IsEnabled && add.IsEnabled && refresh.IsEnabled
            && connect.Content?.ToString() == "Google 연결" && connect.ToolTip?.ToString()?.Contains("취소") == true
            && !Application.Current.Windows.OfType<GoogleAccountDialog>().Any(),
            "cancel restores retry without an error dialog or process restart");
        Capture(window, "google-connect-canceled-retry");

        // A late callback must not turn a canceled Add/Refresh route into a new editor or refresh.
        foreach (var entry in new[] { add, refresh })
        {
            var lateReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            google.ConnectBehavior = (_, _) => lateReply.Task;
            var beforeRefresh = google.RefreshRequests.Count;
            entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(WindowField<bool>(window, "googleConnecting"), entry.Name + " starts a cancelable Google connection");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            lateReply.SetResult();
            await WaitUntilAsync(() => !WindowField<bool>(window, "googleConnecting"), "late canceled Google reply completes");
            Check(!google.State.Connected && google.RefreshRequests.Count == beforeRefresh
                && !Application.Current.Windows.OfType<Window>().Any(dialog => dialog is GoogleEventDialog or GoogleAccountDialog),
                "late canceled " + entry.Name + " callback cannot open a dialog publish connection or continue refreshing");
        }

        google.ConnectBehavior = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        SetWindowField(window, "googleConnectTimeout", TimeSpan.FromMilliseconds(150));
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleConnecting"), "Google timeout restores connection controls");
        Check(!google.State.Connected && connect.IsEnabled && connect.Content?.ToString() == "Google 연결"
            && connect.ToolTip?.ToString()?.Contains("시간") == true
            && !Application.Current.Windows.OfType<GoogleAccountDialog>().Any(),
            "expired browser wait offers immediate retry and a friendly timeout notice without an error modal");
        Capture(window, "google-connect-timeout-retry");
        SetWindowField(window, "googleConnectTimeout", originalTimeout);

        google.ConnectBehavior = null;
        var afterLoginRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        google.RefreshBehavior = (_, _, _) => afterLoginRefresh.Task;
        SetWindowField(window, "googleConnectTimeout", TimeSpan.FromMilliseconds(150));
        Exception? dialogFailure = null;
        var managed = false;
        _ = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            GoogleAccountDialog? account = null;
            try
            {
                await WaitUntilAsync(() => google.State.Connected && WindowField<bool>(window, "googleRefreshing"),
                    "successful Google authentication starts ordinary calendar refresh");
                await Task.Delay(300);
                Check(!WindowField<bool>(window, "googleConnecting") && !connect.Content!.ToString()!.Contains("취소")
                    && google.State.Connected,
                    "completed authentication ends cancel ownership and deadline before calendar refresh");
                afterLoginRefresh.SetResult();
                await WaitUntilAsync(() => Application.Current.Windows.OfType<GoogleAccountDialog>().Any(),
                    "retry after cancellation opens successful calendar management");
                account = Application.Current.Windows.OfType<GoogleAccountDialog>().Single();
                Check(google.State.Connected && ((FrameworkElement)account.FindName("CalendarPanel")).IsVisible,
                    "retry after cancel and timeout connects in the same widget process");
                await Task.Delay(200);
                Check(connect.ToolTip?.ToString()?.Contains("초과") != true && google.State.Connected,
                    "normal account management cannot expire the already completed login");
                managed = true;
            }
            catch (Exception error) { dialogFailure = error; }
            finally { afterLoginRefresh.TrySetResult(); account?.Close(); }
        }));
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => managed || dialogFailure is not null, "Google retry inspection finishes");
        if (dialogFailure is not null) throw dialogFailure;
        await WaitUntilAsync(() => !WindowField<bool>(window, "googleConnectBusy"), "successful retry ends connection attempt");
        google.RefreshBehavior = null;
        SetWindowField(window, "googleConnectTimeout", originalTimeout);
        Check(connect.IsEnabled && connect.Content?.ToString() == "Google ✓", "successful retry restores ordinary connected account action");

        google.SetConnection(false, true);
        var pendingClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCloseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeCancellation = false;
        google.ConnectBehavior = async (_, token) =>
        {
            pendingClose.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { closeCancellation = true; await releaseCloseCleanup.Task; throw; }
        };
        connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await pendingClose.Task;
        window.Close();
        await WaitUntilAsync(() => closeCancellation, "widget shutdown requests pending Google cancellation");
        Check(!WindowField<bool>(window, "closeComplete") && !google.Disposed,
            "widget shutdown waits for Google cancellation and rollback cleanup before disposing service or closing");
        releaseCloseCleanup.SetResult();
        await WaitUntilAsync(() => closeCancellation && WindowField<bool>(window, "closeComplete"),
            "widget shutdown cancels a pending browser wait and cleans up without a modal");
    }
}
