using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckCompactAgendaLayout(MainWindow window, FakeGoogleCalendarService google)
    {
        var state = WindowField<WidgetState>(window, "state");
        var publicCalendar = state.PublicCalendar;
        var snapshot = WindowField<UsageSnapshot?>(window, "snapshot");
        var selected = WindowField<DateTime>(window, "selectedDate");
        var week = WindowField<DateTime>(window, "visibleWeek");
        var original = google.State;
        var scroll = (ScrollViewer)window.FindName("CalendarDetailScroll");
        var names = new[] { "SelectedDateText", "GoogleRefreshButton", "GoogleAddButton", "CalendarLegend" };
        try
        {
            state.PublicCalendar = null;
            SetWindowField(window, "snapshot", null);
            SetWindowField(window, "selectedDate", new DateTime(2026, 11, 11));
            SetWindowField(window, "visibleWeek", new DateTime(2026, 11, 11));
            foreach (var weekly in new[] { false, true })
            {
                state.IsCalendarCollapsed = weekly;
                InvokeStartupRender(window, "ApplyCollapsedState");
                foreach (var count in new[] { 0, 1, 2, 3 })
                {
                    google.SetFixtureState(original with { Events = Enumerable.Range(0, count).Select(index =>
                        FakeGoogleCalendarService.Event("agenda-" + index, "Agenda fixture " + index,
                            new(2026, 11, 11), new(2026, 11, 12))).ToArray() });
                    InvokeStartupRender(window, "RenderCalendar");
                    scroll.ScrollToTop();
                    window.UpdateLayout();
                    var bounds = names.Select(name => VisualBounds((FrameworkElement)window.FindName(name), window)).ToArray();
                    Check(bounds.Zip(bounds.Skip(1)).All(pair => pair.First.Right <= pair.Second.Left + .1)
                        && bounds.All(value => Math.Abs((value.Top + value.Bottom) / 2 - (bounds[0].Top + bounds[0].Bottom) / 2) < 1)
                        && Math.Abs(bounds[^1].Right - VisualBounds(scroll, window).Right) < .1,
                        $"agenda date refresh add and far-right legend fit one aligned row in {(weekly ? "week" : "month")} with {count} entries");
                    Check(Math.Abs(scroll.ViewportHeight - 48) < .1 && window.Height == (weekly ? 422 : 572)
                        && (scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible) == (count > 2),
                        "agenda retains two-row viewport and stable window height; overflow only above two entries");
                    if (count == 3)
                    {
                        var rows = ((StackPanel)window.FindName("CalendarEventList")).Children.OfType<FrameworkElement>().ToArray();
                        Check(VisualBounds(rows[1], scroll).Bottom <= scroll.ViewportHeight + .1
                            && VisualBounds(rows[2], scroll).Top >= scroll.ViewportHeight - .1,
                            "agenda initially exposes exactly two complete rows");
                        scroll.ScrollToBottom();
                        window.UpdateLayout();
                        Check(VisualBounds(rows[2], scroll).Bottom <= scroll.ViewportHeight + .1,
                            "agenda overflow remains reachable with internal scrolling");
                        scroll.ScrollToTop();
                        window.UpdateLayout();
                        Capture(window, weekly ? "agenda-two-row-week" : "agenda-two-row-month");
                    }
                }
            }
        }
        finally
        {
            state.IsCalendarCollapsed = false;
            state.PublicCalendar = publicCalendar;
            SetWindowField(window, "snapshot", snapshot);
            SetWindowField(window, "selectedDate", selected);
            SetWindowField(window, "visibleWeek", week);
            google.SetFixtureState(original);
            InvokeStartupRender(window, "ApplyCollapsedState");
            InvokeStartupRender(window, "RenderCalendar");
            window.UpdateLayout();
        }
    }

    private static async Task CheckAgendaSyncFeedback(MainWindow window, FakeGoogleCalendarService google)
    {
        var original = google.State;
        var initialized = WindowField<bool>(window, "googleInitialized");
        var refresh = (Button)window.FindName("GoogleRefreshButton");
        var syncedAt = new DateTimeOffset(2026, 11, 11, 0, 12, 0, TimeSpan.Zero);
        Task Refresh() => (Task)typeof(MainWindow).GetMethod("RefreshGoogleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [true])!;
        string Feedback() => refresh.ToolTip?.ToString() ?? "";
        try
        {
            google.SetFixtureState(original with { LastSyncedAt = syncedAt, Warning = "Fixture sync warning" });
            InvokeStartupRender(window, "RenderGoogleConnection");
            Check(Feedback().Contains("Fixture sync warning")
                && ((SolidColorBrush)refresh.Foreground).Color == ((SolidColorBrush)window.FindResource("CoralBrush")).Color
                && AutomationProperties.GetHelpText(refresh).Contains("Fixture sync warning"),
                "agenda warning remains visible through refresh color tooltip and accessibility help");
            google.SetFixtureState(google.State with { Warning = null });
            SetWindowField(window, "googleInitialized", true);
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            google.RefreshBehavior = (_, _, _) => pending.Task;
            var request = Refresh();
            Check(!refresh.IsEnabled && ToolTipService.GetShowOnDisabled(refresh)
                && Feedback().Contains("동기화 중") && Feedback().Contains(LocalTime.Convert(syncedAt).ToString("HH:mm")),
                "disabled syncing refresh retains status and last local sync time in tooltip");
            pending.SetException(new IOException("Fixture agenda refresh failure"));
            await request;
            Check(refresh.IsEnabled && Feedback().Contains("Fixture agenda refresh failure"),
                "failed agenda refresh exposes error and enables retry");
            google.RefreshBehavior = null;
            await Refresh();
            Check(refresh.IsEnabled && !Feedback().Contains("Fixture agenda refresh failure")
                && ((SolidColorBrush)refresh.Foreground).Color != ((SolidColorBrush)window.FindResource("CoralBrush")).Color,
                "successful refresh clears stale error feedback");
        }
        finally
        {
            google.RefreshBehavior = null;
            google.SetFixtureState(original);
            SetWindowField(window, "googleInitialized", initialized);
            InvokeStartupRender(window, "RenderGoogleConnection");
        }
    }
}
