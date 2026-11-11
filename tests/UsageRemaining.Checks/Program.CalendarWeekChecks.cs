using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CalendarWeekRangeChecks()
    {
        foreach (var day in new[] { 15, 16, 19, 21 })
        {
            var range = CalendarDisplayRange.FromWeek(new DateTime(2026, 11, day, 17, 30, 0));
            Check(range.Start == new DateTime(2026, 11, 15) && range.EndExclusive == new DateTime(2026, 11, 22)
                && range.DayCount == 7 && range.RowCount == 1, "calendar week is Sunday through Saturday for November " + day);
        }
        var year = CalendarDisplayRange.FromWeek(new DateTime(2027, 1, 1));
        Check(year.Start == new DateTime(2026, 12, 27) && year.EndExclusive == new DateTime(2027, 1, 3)
            && year.Contains(new DateTime(2027, 1, 2)) && !year.Contains(new DateTime(2027, 1, 3)),
            "calendar week crosses year boundary with exclusive Sunday end");
        var leap = CalendarDisplayRange.FromWeek(new DateTime(2024, 2, 29));
        Check(leap.Start == new DateTime(2024, 2, 25) && leap.EndExclusive == new DateTime(2024, 3, 3),
            "calendar week includes leap day and next month");
        foreach (var monthDate in new[] { new DateTime(2026, 11, 19), new DateTime(2024, 2, 29), new DateTime(2027, 1, 1) })
        {
            var range = CalendarDisplayRange.FromMonth(monthDate);
            Check(range.DayCount == 42 && range.RowCount == 6 && range.Start.DayOfWeek == DayOfWeek.Sunday
                && range.Contains(new DateTime(monthDate.Year, monthDate.Month, 1))
                && range.Contains(new DateTime(monthDate.Year, monthDate.Month, DateTime.DaysInMonth(monthDate.Year, monthDate.Month))),
                "calendar monthly range retains six complete weeks for " + monthDate.ToString("yyyy-MM"));
        }
        var folder = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "calendar-week-state", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "widget-state.json"), "{\"IsCollapsed\":true}");
        var store = new LocalStateStore(folder);
        var old = store.Load();
        Check(old.IsCollapsed && !old.IsCalendarCollapsed, "old settings default calendar to full month independently of title collapse");
        foreach (var appCollapsed in new[] { false, true })
        {
            old.IsCollapsed = appCollapsed;
            old.IsCalendarCollapsed = true;
            Check(store.Save(old) && store.Load().IsCalendarCollapsed && store.Load().IsCollapsed == appCollapsed,
                "calendar week preference persists independently with title collapse " + appCollapsed);
        }
    }

    private static int CalendarWeekUiChecks()
    {
        CalendarWeekRangeChecks();
        var folder = System.IO.Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "calendar-week-ui", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("USAGEREMAINING_DATA_DIR", folder);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(System.IO.Path.Combine("src", "UsageRemaining.Widget", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "services", "clr-namespace:UsageRemaining.Widget.Services;assembly=CodexPulse"),
            source.Root!.Element(presentation + "Application.Resources")!.Elements().Select(element => new XElement(element)));
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var exitCode = 0;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                var google = new FakeGoogleCalendarService();
                window = new MainWindow(new UpdateChecks.FakeUpdateService(), new StartupChecks.FakeStartupService(), google);
                DetachStartupRuntimeLoading(window);
                window.Show();
                window.UpdateLayout();
                var state = WindowField<WidgetState>(window, "state");
                var today = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
                var thisWeek = CalendarDisplayRange.FromWeek(today);
                var days = (UniformGrid)window.FindName("DaysPanel");
                var toggle = (Button)window.FindName("CalendarCollapseButton");
                var previous = (Button)window.FindName("PreviousPeriodButton");
                var next = (Button)window.FindName("NextPeriodButton");
                Check(days.Children.Count == 42 && window.Height == 558, "calendar initially shows complete month at existing expanded height");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(state.IsCalendarCollapsed && !state.IsCollapsed && days.Rows == 1 && days.Children.Count == 7,
                    "calendar toggle changes only calendar mode to seven days");
                Check(WeekDates(window).SequenceEqual(Enumerable.Range(0, 7).Select(index => thisWeek.Start.AddDays(index)))
                    && WindowField<DateTime>(window, "selectedDate") == today,
                    "entering week shows and selects current PC-local today in its Sunday-Saturday week");
                Check(window.Height == 408 && Math.Abs(days.ActualHeight - 30) < .1
                    && Math.Abs(((FrameworkElement)window.FindName("ExpandedPanel")).ActualHeight - 344) < .1,
                    "calendar week removes five rows and 150 DIP of window height");
                Check(AutomationProperties.GetName(previous) == "이전 주" && AutomationProperties.GetName(next) == "다음 주"
                    && AutomationProperties.GetName(toggle).Contains("월"), "calendar and navigation accessible labels reflect week mode");
                Check(!WindowInteraction.CanDragFrom(toggle), "calendar toggle is excluded from drag handling");

                SeedWeekFixture(window, google, today);
                InvokeStartupRender(window, "RenderCalendar");
                window.UpdateLayout();
                var selected = days.Children.OfType<Button>().Single(button => (DateTime)button.Tag == today);
                var number = ((Grid)selected.Content).Children.OfType<TextBlock>().Single();
                Check(((SolidColorBrush)number.Foreground).Color == ((SolidColorBrush)window.FindResource("CoralBrush")).Color,
                    "holiday date stays coral while calendar is collapsed");
                var markers = Descendants<Ellipse>((Grid)selected.Content).ToArray();
                Check(markers.Length == 4, "all four Codex markers survive inside the week date cell");
                var detail = (StackPanel)window.FindName("CalendarEventList");
                Check(detail.Children.OfType<FrameworkElement>().Any(row => row.Tag is GoogleCalendarEvent { Id: "week-holiday" })
                    && detail.Children.OfType<FrameworkElement>().Any(row => row.Tag is GoogleCalendarEvent { Id: "week-span" }),
                    "week selected-day list retains holiday and Google event rows");
                Check(Math.Abs(((ScrollViewer)window.FindName("CalendarDetailScroll")).ActualHeight - 48) < .1
                    && ((StackPanel)window.FindName("CreditList")).Children.Count == 2,
                    "calendar compression preserves agenda viewport and reset pass rows");
                CheckWeekBars(window, "week-span", 0, 7);
                var calendarOrigin = VisualBounds(days, window);
                var headerNames = new[] { "CalendarCollapseButton", "MonthText", "GoogleConnectButton", "PreviousPeriodButton", "NextPeriodButton" };
                var headerBounds = headerNames.Select(name => VisualBounds((FrameworkElement)window.FindName(name), window)).ToArray();
                Check(headerBounds.All(bounds => bounds.Left >= 0 && bounds.Right <= window.ActualWidth)
                    && headerBounds.SelectMany((bounds, index) => headerBounds.Skip(index + 1).Select(other => OverlapsInterior(bounds, other))).All(overlap => !overlap),
                    "calendar header controls fit 360 DIP without overlapping");
                var footer = VisualBounds((FrameworkElement)window.FindName("FooterRefreshButton"), window);
                Check(footer.Bottom <= window.ActualHeight && footer.Top > calendarOrigin.Bottom,
                    "footer remains visible below compressed calendar and lists");
                Capture(window, "calendar-week-current-fixture");

                var yearDate = new DateTime(2027, 1, 1);
                SelectFixtureWeek(window, yearDate);
                var crossingStart = new DateTime(2026, 12, 27);
                var january = days.Children.OfType<Button>().Single(button => (DateTime)button.Tag == yearDate);
                january.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(WeekDates(window).First() == crossingStart && WeekDates(window).Last() == new DateTime(2027, 1, 2)
                    && WindowField<DateTime>(window, "selectedDate") == yearDate,
                    "selecting next-year day keeps same visible cross-year week");
                Check(days.Children.OfType<Button>().All(button => ((Grid)button.Content).Children.OfType<TextBlock>().Single().Opacity == 1),
                    "cross-month week dates remain fully readable");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(WeekDates(window).First() == new DateTime(2027, 1, 3), "next button moves one week across year boundary");
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(WeekDates(window).First() == crossingStart, "previous button returns exactly one week");
                Capture(window, "calendar-week-year-boundary-fixture");

                var leap = new DateTime(2024, 2, 29);
                SelectFixtureWeek(window, leap);
                Check(WeekDates(window).First() == new DateTime(2024, 2, 25) && WeekDates(window).Last() == new DateTime(2024, 3, 2),
                    "week view renders leap day and March dates together");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(WeekDates(window).First() == new DateTime(2024, 3, 3), "week navigation remains seven days after leap day");

                CheckCalendarRollover(window);
                InvokeWeekMethod(window, "Today_Click", null, new RoutedEventArgs());
                window.UpdateLayout();
                Check(WeekDates(window).First() == thisWeek.Start && WindowField<DateTime>(window, "selectedDate") == today,
                    "Today returns from manual navigation to current local week");

                SetWindowField(window, "googleInitialized", true);
                google.RefreshRequests.Clear();
                await RefreshFixtureGoogle(window);
                Check(google.RefreshRequests.Last() == (CalendarEventPresentation.LocalMidnight(DateOnly.FromDateTime(thisWeek.Start.AddDays(-2))),
                    CalendarEventPresentation.LocalMidnight(DateOnly.FromDateTime(thisWeek.EndExclusive.AddDays(2)))),
                    "Google week query matches seven visible days with timezone margins");
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                google.RefreshBehavior = (_, _, _) => google.RefreshRequests.Count == 1 ? release.Task : Task.CompletedTask;
                google.RefreshRequests.Clear();
                var pending = RefreshFixtureGoogle(window);
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var navigated = CalendarDisplayRange.FromWeek(thisWeek.Start.AddDays(7));
                release.SetResult();
                await pending;
                await WaitUntilAsync(() => google.RefreshRequests.Count >= 2 && !WindowField<bool>(window, "googleRefreshing"), "queued week Google refresh completes");
                Check(google.RefreshRequests.Last() == (CalendarEventPresentation.LocalMidnight(DateOnly.FromDateTime(navigated.Start.AddDays(-2))),
                    CalendarEventPresentation.LocalMidnight(DateOnly.FromDateTime(navigated.EndExclusive.AddDays(2)))),
                    "in-flight Google refresh follows latest navigated week without losing queued request");
                google.RefreshBehavior = null;
                SetWindowField(window, "googleInitialized", false);

                ((Button)window.FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(state.IsCollapsed && state.IsCalendarCollapsed && window.Height is 84 or 112,
                    "title collapse keeps calendar week preference independent");
                ((Button)window.FindName("CollapseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(!state.IsCollapsed && state.IsCalendarCollapsed && window.Height == 408 && days.Children.Count == 7,
                    "title expand restores calendar week size rather than full month");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(!state.IsCalendarCollapsed && days.Rows == 6 && days.Children.Count == 42 && window.Height == 558
                    && AutomationProperties.GetName(previous) == "이전 달", "calendar expand restores complete month and month navigation");
                var beforeMonth = WindowField<DateTime>(window, "month");
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(WindowField<DateTime>(window, "month") == beforeMonth.AddMonths(1), "month navigation still moves one calendar month");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(WindowField<DateTime>(window, "selectedDate") == today, "re-entering week returns to today after browsing another month");
                Check(new LocalStateStore(folder).Load().IsCalendarCollapsed, "calendar mode is saved by the real toggle handler");
                window.Close();
                await WaitUntilAsync(() => WindowField<bool>(window, "closeComplete"), "week fixture closes cleanly");
                window = new MainWindow(new UpdateChecks.FakeUpdateService(), new StartupChecks.FakeStartupService(), new FakeGoogleCalendarService());
                DetachStartupRuntimeLoading(window);
                window.Show();
                window.UpdateLayout();
                Check(window.Height == 408 && ((UniformGrid)window.FindName("DaysPanel")).Children.Count == 7,
                    "reopening app restores calendar-only collapsed preference");
                window.Close();
                await WaitUntilAsync(() => WindowField<bool>(window, "closeComplete"), "restored week fixture closes cleanly");
                window = null;
                Console.WriteLine($"All {passed} calendar week checks passed. Fake accounts and isolated settings only.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exitCode = 1; }
            finally { if (window is not null && !WindowField<bool>(window, "closing")) window.Close(); app.Shutdown(exitCode); }
        }));
        app.Run();
        return exitCode;
    }

    private static IEnumerable<DateTime> WeekDates(MainWindow window) =>
        ((UniformGrid)window.FindName("DaysPanel")).Children.OfType<Button>().Select(button => (DateTime)button.Tag);

    private static void SelectFixtureWeek(MainWindow window, DateTime date)
    {
        SetWindowField(window, "visibleWeek", CalendarDisplayRange.FromWeek(date).Start);
        SetWindowField(window, "selectedDate", date);
        SetWindowField(window, "month", new DateTime(date.Year, date.Month, 1));
        InvokeStartupRender(window, "RenderCalendar");
        window.UpdateLayout();
    }

    private static object? InvokeWeekMethod(MainWindow window, string name, params object?[] args) =>
        (typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing week seam " + name)).Invoke(window, args);

    private static Task RefreshFixtureGoogle(MainWindow window) => (Task)InvokeWeekMethod(window, "RefreshGoogleAsync", true)!;

    private static void SeedWeekFixture(MainWindow window, FakeGoogleCalendarService google, DateTime date)
    {
        var day = DateOnly.FromDateTime(date);
        var range = CalendarDisplayRange.FromWeek(date);
        var at = CalendarEventPresentation.LocalMidnight(day).AddHours(12);
        WindowField<WidgetState>(window, "state").PublicCalendar = new(at, null,
            [new(at, day, "regular", "Reset fixture", null), new(at, day, "banked", "Pass fixture", null),
                new(at, day, "regular", "Announcement fixture", null, IsAnnouncement: true)], "Fixture only");
        SetWindowField(window, "snapshot", new UsageSnapshot(at, [new("primary", 37, 10080, at)]));
        var now = DateTimeOffset.UtcNow;
        SetWindowField(window, "creditBalance", new ResetCreditBalance(2,
            [new("week-pass-one", "codexRateLimits", "available", now.AddDays(-1), now.AddDays(1), "Week fixture one", null),
                new("week-pass-two", "codexRateLimits", "available", now.AddDays(-1), now.AddDays(2), "Week fixture two", null)], true));
        const string holidayId = "ko.south_korea#holiday@group.v.calendar.google.com";
        google.SetFixtureState(google.State with
        {
            Calendars = google.State.Calendars.Append(new GoogleCalendarInfo { Id = holidayId, Summary = "대한민국의 휴일", AccessRole = "reader" }).ToArray(),
            Events = [FakeGoogleCalendarService.Event("week-span", "Week-wide span", DateOnly.FromDateTime(range.Start.AddDays(-3)), DateOnly.FromDateTime(range.EndExclusive.AddDays(3))),
                FakeGoogleCalendarService.Event("week-holiday", "Fixture holiday", day, day.AddDays(1)) with { CalendarId = holidayId, Description = "공휴일", CanWrite = false }]
        });
        InvokeStartupRender(window, "RenderCredits");
    }

    private static void CheckWeekBars(MainWindow window, string id, int start, int end)
    {
        var overlay = (Canvas)window.FindName("GoogleCalendarOverlay");
        var bars = overlay.Children.OfType<Button>().Where(button => button.Tag is GoogleCalendarEvent value && value.Id == id).ToArray();
        Check(bars.Length == 1 && Math.Abs(Canvas.GetLeft(bars[0]) - (start * overlay.ActualWidth / 7 + 2)) < .1
            && Math.Abs(bars[0].ActualWidth - ((end - start) * overlay.ActualWidth / 7 - 4)) < .1
            && Canvas.GetTop(bars[0]) >= 20 && Canvas.GetTop(bars[0]) + bars[0].ActualHeight <= 30,
            "Google span clips to visible week and stays below date/reset markers");
    }

    private static void CheckCalendarRollover(MainWindow window)
    {
        var saturday = new DateTime(2026, 11, 14);
        SelectFixtureWeek(window, saturday);
        SetWindowField(window, "displayedToday", saturday);
        InvokeWeekMethod(window, "UpdateCalendarToday", saturday.AddDays(1));
        InvokeStartupRender(window, "RenderCalendar");
        Check(WeekDates(window).First() == new DateTime(2026, 11, 15) && WindowField<DateTime>(window, "selectedDate") == saturday.AddDays(1),
            "following today advances week on Saturday-to-Sunday rollover");
        SelectFixtureWeek(window, new DateTime(2026, 11, 11));
        SetWindowField(window, "displayedToday", saturday);
        InvokeWeekMethod(window, "UpdateCalendarToday", saturday.AddDays(1));
        InvokeStartupRender(window, "RenderCalendar");
        Check(WeekDates(window).First() == new DateTime(2026, 11, 8) && WindowField<DateTime>(window, "selectedDate") == new DateTime(2026, 11, 11),
            "local date rollover preserves manually selected historical week");
        SelectFixtureWeek(window, saturday);
        SetWindowField(window, "visibleWeek", new DateTime(2026, 11, 22));
        SetWindowField(window, "displayedToday", saturday);
        InvokeWeekMethod(window, "UpdateCalendarToday", saturday.AddDays(1));
        InvokeStartupRender(window, "RenderCalendar");
        Check(WeekDates(window).First() == new DateTime(2026, 11, 22), "rollover does not hijack browsed week while old today remains selected");
    }
}
