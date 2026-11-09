using System.Windows;
using System.Windows.Controls;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static async Task GoogleFailedConnectionDialogChecks(MainWindow window, FakeGoogleCalendarService google)
    {
        const string reason = "Google Calendar API fixture is disabled. Enable Calendar API and retry.";
        google.SetConnection(false, true);
        google.ConnectFailure = new GoogleCalendarException(reason);
        Exception? failure = null;
        var observed = false;
        var connections = google.Connects;
        _ = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            GoogleAccountDialog? account = null;
            try
            {
                await WaitUntilAsync(() => Application.Current.Windows.OfType<GoogleAccountDialog>().Any(),
                    "failed first Google connection opens account dialog");
                account = Application.Current.Windows.OfType<GoogleAccountDialog>().Single();
                account.UpdateLayout();
                var status = (TextBlock)account.FindName("StatusText");
                var retry = (Button)account.FindName("ConnectButton");
                Check(google.Connects == connections + 1 && !google.State.Connected &&
                    status.Text == reason && status.IsVisible && status.ActualHeight > 0 && retry.IsEnabled,
                    "first automatic Google failure dialog visibly retains the connection reason and enables retry");
                Check(((FrameworkElement)account.FindName("CalendarPanel")).Visibility == Visibility.Collapsed &&
                    ((Button)account.FindName("SaveButton")).Visibility == Visibility.Collapsed,
                    "post-callback failure keeps disconnected account dialog free of connected controls");
                Capture(account, "google-account-first-connection-failure");
                google.ConnectFailure = null;
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => google.State.Connected && retry.IsEnabled,
                    "Google failed connection can retry successfully in the same dialog");
                Check(google.Connects == connections + 2 && !status.Text.Contains(reason, StringComparison.Ordinal) &&
                    ((FrameworkElement)account.FindName("CalendarPanel")).Visibility == Visibility.Visible,
                    "successful Google retry clears the original error and reveals calendar management");
                observed = true;
            }
            catch (Exception exception) { failure = exception; }
            finally { google.ConnectFailure = null; account?.Close(); }
        }));
        ((Button)window.FindName("GoogleConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => observed || failure is not null, "Google failure dialog inspection completes");
        if (failure is not null) throw failure;

        var connectedError = new GoogleAccountDialog(google, _ => throw new InvalidOperationException("No browser expected"),
            initialError: reason) { Owner = window };
        connectedError.Show();
        google.SetConnection(true, true);
        Check(((TextBlock)connectedError.FindName("StatusText")).Text == reason &&
            ((FrameworkElement)connectedError.FindName("CalendarPanel")).Visibility == Visibility.Visible,
            "connected account preserves an initial refresh failure while calendar controls remain available");
        connectedError.Close();

        google.SetConnection(false, true);
        google.ConnectFailure = new GoogleCalendarException("   ");
        var blankError = new GoogleAccountDialog(google, _ => throw new InvalidOperationException("No browser expected")) { Owner = window };
        blankError.Show();
        try
        {
            ((Button)blankError.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => ((Button)blankError.FindName("ConnectButton")).IsEnabled,
                "Google blank failure restores retry controls");
            Check(!string.IsNullOrWhiteSpace(((TextBlock)blankError.FindName("StatusText")).Text),
                "Google blank connection failure receives a visible fallback reason");
        }
        finally { google.ConnectFailure = null; blankError.Close(); }
    }
}
