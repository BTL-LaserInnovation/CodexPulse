using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void SeedGoogleCalendarGeometry(MainWindow window)
    {
        var date = new DateOnly(2026, 11, 9);
        var at = CalendarEventPresentation.LocalMidnight(date).AddHours(12);
        WindowField<WidgetState>(window, "state").PublicCalendar = new(at, null,
            [new(at, date, "regular", "Reset fixture", null),
                new(at, date, "banked", "Credit fixture", null),
                new(at, date, "regular", "Announcement fixture", null, IsAnnouncement: true)], "Fixture only");
        SetWindowField(window, "snapshot", new UsageSnapshot(at, [new("primary", 37, 10080, at)]));
        var now = DateTimeOffset.UtcNow;
        SetWindowField(window, "creditBalance", new ResetCreditBalance(2,
            [new("font-one", "codexRateLimits", "available", now.AddDays(-1), now.AddDays(1), "Font fixture one", null),
                new("font-two", "codexRateLimits", "available", now.AddDays(-1), now.AddDays(2), "Font fixture two", null)], true));
        InvokeStartupRender(window, "RenderCredits");
    }

    private static Rect VisualBounds(FrameworkElement element, Visual relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool OverlapsInterior(Rect first, Rect second) =>
        Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left) > .1 &&
        Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top) > .1;

    private static void CheckGoogleCalendarGeometry(MainWindow window)
    {
        window.UpdateLayout();
        var days = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("DaysPanel");
        var cell = days.Children.OfType<Button>().Single(button => button.Tag is DateTime date && date == new DateTime(2026, 11, 9));
        var content = (Grid)cell.Content;
        var markers = Descendants<Ellipse>(content).ToArray();
        var colors = markers.Select(marker => ((SolidColorBrush)(marker.Stroke ?? marker.Fill)).Color).ToArray();
        Check(markers.Length == 4 && new[] { "MintBrush", "VioletBrush", "SkyBrush", "CoralBrush" }
            .All(key => colors.Contains(((SolidColorBrush)window.FindResource(key)).Color)),
            "Google geometry fixture shares one date with all four distinct Codex markers");
        var cellBounds = VisualBounds(cell, window);
        var contentBounds = VisualBounds(content, window);
        var dateSurface = (Border)cell.Template.FindName("ButtonSurface", cell);
        var surfaceBounds = VisualBounds(dateSurface, window);
        Check(Math.Abs(days.ActualHeight - 180) < .1 && Math.Abs(cell.ActualHeight - 20) < .1 &&
            Math.Abs(days.ActualHeight / 6 - 30) < .1 && contentBounds.Bottom <= surfaceBounds.Bottom + .1 &&
            markers.All(marker => surfaceBounds.Contains(VisualBounds(marker, window))),
            "six 30 DIP calendar rows enclose date and all four Codex dots inside the 20 DIP date box");
        var overlay = (Canvas)window.FindName("GoogleCalendarOverlay");
        var bars = overlay.Children.OfType<Button>().Where(button => button.Tag is GoogleCalendarEvent value && value.Id == "span").ToArray();
        var rowTop = VisualBounds(days, window).Top + Math.Floor((cellBounds.Top - VisualBounds(days, window).Top) / 30) * 30;
        var bar = bars.Single(button =>
            VisualBounds(button, window).Top >= rowTop && VisualBounds(button, window).Bottom <= rowTop + 30.1 &&
            VisualBounds(button, window).Left < cellBounds.Right && VisualBounds(button, window).Right > cellBounds.Left);
        var barBounds = VisualBounds(bar, window);
        var paintedLine = Descendants<Border>(bar).FirstOrDefault(border => border.ActualHeight > 0 && border.ActualHeight <= 2.1);
        Check(paintedLine is not null, "Google period has a visible two DIP line inside its larger click target");
        var lineBounds = VisualBounds(paintedLine!, window);
        Check(barBounds.Top >= rowTop + 19.9 && barBounds.Bottom <= rowTop + 26.1 &&
            lineBounds.Top >= rowTop + 20.9 && lineBounds.Bottom <= rowTop + 23.1 &&
            lineBounds.Top - surfaceBounds.Bottom >= .9 && !OverlapsInterior(surfaceBounds, lineBounds) &&
            !OverlapsInterior(cellBounds, barBounds) && markers.All(marker => !OverlapsInterior(VisualBounds(marker, window), lineBounds)),
            "actual Google line and full six DIP hit target stay outside the date box with a one DIP visible gap");
        var nextCell = days.Children.OfType<Button>().Single(button => button.Tag is DateTime date && date == new DateTime(2026, 11, 16));
        var nextSurface = (Border)nextCell.Template.FindName("ButtonSurface", nextCell);
        var nextSurfaceBounds = VisualBounds(nextSurface, window);
        var ownGap = lineBounds.Top - surfaceBounds.Bottom;
        var nextGap = nextSurfaceBounds.Top - lineBounds.Bottom;
        Check(ownGap >= .9 && ownGap <= 1.1 && nextGap >= 6.9 && ownGap < nextGap,
            "painted Google period line is closer to its own date box than the next week's date box");
        var weeks = bars.Select(button => (int)Math.Floor(Canvas.GetTop(button) / 30)).Order().ToArray();
        Check(weeks.SequenceEqual(new[] { 0, 1 }) && bars.All(button => button.ActualHeight == 6) &&
            Math.Abs(Canvas.GetLeft(bars.Single(button => Canvas.GetTop(button) < 30)) - (5 * overlay.ActualWidth / 7 + 2)) < .1,
            "Google week-crossing period creates two separate bottom lanes beginning at Friday and Sunday");
        var count = overlay.Children.OfType<Grid>().Single(grid =>
            Descendants<TextBlock>(grid).Any(text => text.Text == "+3") &&
            VisualBounds(grid, window).Left >= cellBounds.Left && VisualBounds(grid, window).Left < cellBounds.Right);
        var countBounds = VisualBounds(count, window);
        var number = content.Children.OfType<TextBlock>().Single();
        Check(countBounds.Top <= cellBounds.Top + 2.1 && countBounds.Right <= cellBounds.Right + .1 &&
            !OverlapsInterior(countBounds, VisualBounds(number, window)) &&
            markers.All(marker => !OverlapsInterior(countBounds, VisualBounds(marker, window))),
            $"Google same-day count occupies the upper right without covering the date or Codex dots (cell {cellBounds}; count {countBounds}; date {VisualBounds(number, window)})");
        var iconCell = days.Children.OfType<Button>().Single(button => button.Tag is DateTime date && date == new DateTime(2026, 11, 16));
        var iconCellBounds = VisualBounds(iconCell, window);
        var icon = overlay.Children.OfType<Button>().Single(button => button.Content is Viewbox &&
            button.ToolTip?.ToString()?.Contains("Google single-day icon") == true);
        var iconBounds = VisualBounds(icon, window);
        var iconDate = ((Grid)iconCell.Content).Children.OfType<TextBlock>().Single();
        Check(iconBounds.Top <= iconCellBounds.Top + 2.1 && iconBounds.Right <= iconCellBounds.Right + .1 &&
            !OverlapsInterior(iconBounds, VisualBounds(iconDate, window)),
            "Google single-day icon stays inside the upper right of its date without covering the date number");
        var rows = ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<FrameworkElement>().ToArray();
        var scroll = (ScrollViewer)window.FindName("CalendarDetailScroll");
        Check(rows.Take(3).All(row => Math.Abs(row.ActualHeight - 22) < .1 && Math.Abs(row.Margin.Bottom - 2) < .1) &&
            VisualBounds(rows[2], scroll).Bottom <= scroll.ViewportHeight + .1,
            "selected-day viewport shows three complete 22 DIP rows with two DIP gaps");
    }

    private static void CheckGoogleCreditFontParity(MainWindow window)
    {
        window.UpdateLayout();
        var creditRows = ((StackPanel)window.FindName("CreditList")).Children.OfType<Grid>().ToArray();
        var creditText = creditRows.SelectMany(row => row.Children.OfType<TextBlock>()).ToArray();
        var eventRows = ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<FrameworkElement>().ToArray();
        var eventText = eventRows.SelectMany(row => Descendants<TextBlock>(row)).ToArray();
        var font = creditText.First().FontSize;
        Check(creditRows.Length == 2 && creditText.Length == 6 && font == 10 &&
            creditText.All(text => text.FontSize == font) && eventText.Length >= 8 && eventText.All(text => text.FontSize == font),
            "actual credit name expiry and remaining columns match calendar title and time fonts");
        Check(new[] { "CreditsHint", "GoogleSyncText", "SelectedDateText" }
            .Select(name => (TextBlock)window.FindName(name)).All(text => text.FontSize == font),
            "calendar and credit list hints share the same readable font size");
        var originalDate = WindowField<DateTime>(window, "selectedDate");
        SetWindowField(window, "selectedDate", new DateTime(2026, 11, 20));
        InvokeStartupRender(window, "RenderCalendar");
        var empty = ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<TextBlock>().Single();
        Check(empty.FontSize == font, "empty calendar list uses the actual credit list font size");
        SetWindowField(window, "selectedDate", originalDate);
        InvokeStartupRender(window, "RenderCalendar");
    }

    private static async Task GoogleConnectionEntryChecks(MainWindow window, FakeGoogleCalendarService google)
    {
        await GoogleFailedConnectionDialogChecks(window, google);
        foreach (var name in new[] { "GoogleConnectButton", "GoogleAddButton", "GoogleRefreshButton" })
        {
            google.SetConnection(false, true);
            var button = (Button)window.FindName(name);
            Check(button.IsEnabled, name + " remains available while Google is disconnected");
            var before = google.Connects;
            Exception? failure = null;
            var observed = false;
            _ = window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                GoogleAccountDialog? account = null;
                try
                {
                    await WaitUntilAsync(() => Application.Current.Windows.OfType<GoogleAccountDialog>().Any(), name + " opens account management");
                    account = Application.Current.Windows.OfType<GoogleAccountDialog>().Single();
                    Check(google.Connects == before + 1 && google.State.Connected && google.BrowserCallbacks == google.Connects,
                        name + " begins configured account connection directly with browser callback");
                    Check(((FrameworkElement)account.FindName("CalendarPanel")).Visibility == Visibility.Visible,
                        name + " shows calendar management after successful connection");
                    observed = true;
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    if (name == "GoogleAddButton")
                        _ = window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            var editor = Application.Current.Windows.OfType<GoogleEventDialog>().SingleOrDefault();
                            if (editor is null) failure = new InvalidOperationException("Google add did not continue to event editor after connecting");
                            else editor.Close();
                        }));
                    account?.Close();
                }
            }));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => observed || failure is not null, name + " route finishes fake account inspection");
            if (failure is not null) throw failure;
        }

        google.SetConnection(false, false);
        var initializations = google.Initializations;
        var connections = google.Connects;
        var browserCalls = 0;
        var popup = new GoogleAccountDialog(google, _ => browserCalls++,
            token => google.InitializeAsync(token)) { Owner = window };
        popup.Show();
        Capture(popup, "google-account-missing-registration");
        var primary = (Button)popup.FindName("ConnectButton");
        var explanation = ((TextBlock)popup.FindName("ConfigurationText")).Text + " " + ((TextBlock)popup.FindName("AccountHintText")).Text;
        Check(primary.IsEnabled && primary.Content?.ToString() == "연결 다시 확인" && explanation.Contains("개발자") &&
            new[] { "OAuth", "client", "json", "환경 변수", "설치하세요", "설치해야" }.All(term => !explanation.Contains(term, StringComparison.OrdinalIgnoreCase)),
            "missing Google registration explains developer action with an enabled human-readable recheck");
        Check(new[] { "SaveButton", "DisconnectButton", "CalendarPanel" }.All(name =>
            ((FrameworkElement)popup.FindName(name)).Visibility == Visibility.Collapsed),
            "disconnected Google popup hides save disconnect and calendar selection");
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => google.Initializations > initializations && primary.IsEnabled, "Google popup reloads registration through recheck callback");
        Check(!google.State.IsConfigured && google.Connects == connections && browserCalls == 0,
            "missing registration recheck stays actionable without attempting account authorization");
        google.StateAfterInitialize = google.State with { IsConfigured = true };
        google.InvokeBrowserFixture = true;
        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => google.State.Connected && browserCalls == 1, "Google registration recheck proceeds to browser login when registration becomes available");
        popup.Close();

        browserCalls = 0;
        google.SetConnection(false, true);
        google.InvokeBrowserFixture = true;
        popup = new GoogleAccountDialog(google, address =>
        {
            Check(address.Host == "accounts.google.com", "Google account popup passes browser destination to injected callback");
            browserCalls++;
        }) { Owner = window };
        popup.Show();
        Capture(popup, "google-account-ready-login");
        ((Button)popup.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => google.State.Connected && browserCalls == 1, "Google account login invokes fake browser callback once");
        popup.Close();
        google.InvokeBrowserFixture = false;
    }
}
