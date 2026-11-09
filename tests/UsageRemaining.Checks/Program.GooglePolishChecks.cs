using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UsageRemaining.Widget;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class Program
{
    private static void CheckGoogleHolidayPresentation(MainWindow window, FakeGoogleCalendarService google)
    {
        var original = google.State;
        const string holidayId = "ko.south_korea#holiday@group.v.calendar.google.com";
        var holiday = FakeGoogleCalendarService.Event("public-holiday", "Public holiday fixture", new(2026, 11, 9), new(2026, 11, 12)) with
        {
            CalendarId = holidayId, Description = "공휴일", CanWrite = false
        };
        var observance = holiday with { Id = "observance", Summary = "Observance fixture", Description = "기념일\n기념일을 숨기려면 설정으로 이동하세요.", StartDate = new(2026, 11, 12), EndDate = new(2026, 11, 13) };
        var personal = holiday with { Id = "personal-readonly", CalendarId = "readonly-calendar", Summary = "Personal all-day holiday", StartDate = new(2026, 11, 13), EndDate = new(2026, 11, 14) };
        google.SetFixtureState(original with
        {
            Calendars = original.Calendars.Append(new GoogleCalendarInfo { Id = holidayId, Summary = "대한민국의 휴일", AccessRole = "reader" }).ToArray(),
            Events = original.Events.Concat([holiday, observance, personal]).ToArray(),
            SelectedCalendarIds = original.SelectedCalendarIds.Append(holidayId).ToHashSet()
        });
        try
        {
            InvokeStartupRender(window, "RenderCalendar");
            window.UpdateLayout();
            var days = (UniformGrid)window.FindName("DaysPanel");
            Button Cell(int day) => days.Children.OfType<Button>().Single(button => button.Tag is DateTime date && date == new DateTime(2026, 11, day));
            Color NumberColor(int day) => ((SolidColorBrush)((Grid)Cell(day).Content).Children.OfType<TextBlock>().Single().Foreground).Color;
            var sunday = NumberColor(15);
            Check(new[] { 9, 10, 11 }.All(day => NumberColor(day) == sunday) && sunday == ((SolidColorBrush)window.FindResource("CoralBrush")).Color,
                "Google selected Monday and every public-holiday span date use the exact Sunday coral foreground");
            Check(NumberColor(12) != sunday && NumberColor(13) != sunday && NumberColor(14) != sunday,
                "Google observance exclusive end personal read-only all-day and Saturday retain ordinary date colors");
            Check(((SolidColorBrush)Cell(9).BorderBrush).Color == ((SolidColorBrush)window.FindResource("TextBrush")).Color &&
                Descendants<System.Windows.Shapes.Ellipse>((Grid)Cell(9).Content).Count() == 4,
                "Google holiday on a selected Codex date keeps the selection outline and all four Codex markers");
            var overlay = (Canvas)window.FindName("GoogleCalendarOverlay");
            Check(overlay.Children.OfType<Button>().All(button => button.Tag is not GoogleCalendarEvent value || value.CalendarId != holidayId) &&
                overlay.Children.OfType<Grid>().Any(grid => Descendants<TextBlock>(grid).Any(text => text.Text == "+3")) &&
                !overlay.Children.OfType<Grid>().Any(grid => Descendants<TextBlock>(grid).Any(text => text.Text == "+4")) &&
                overlay.Children.OfType<Button>().All(button => button.ToolTip?.ToString()?.Contains("Observance fixture") != true),
                "Google holiday-calendar events never create period lines icons or same-day count additions");
            Check(((StackPanel)window.FindName("CalendarEventList")).Children.OfType<FrameworkElement>().Any(row => row.Tag is GoogleCalendarEvent value && value.Id == holiday.Id) &&
                google.State.Events.Count == original.Events.Count + 3,
                "Google holiday detail rows remain available and source events remain in synchronized state");
            Capture(window, "google-holiday-selected-monday-fixture");
            SetWindowField(window, "selectedDate", new DateTime(2026, 11, 12));
            InvokeStartupRender(window, "RenderCalendar");
            window.UpdateLayout();
            Check(((StackPanel)window.FindName("CalendarEventList")).Children.OfType<FrameworkElement>().Any(row => row.Tag is GoogleCalendarEvent value && value.Id == observance.Id) &&
                ((Canvas)window.FindName("GoogleCalendarOverlay")).Children.OfType<Button>().All(button => button.ToolTip?.ToString()?.Contains("Observance fixture") != true),
                "Google observance remains a readable detail row without a date-grid icon");
            Capture(window, "google-observance-detail-fixture");
        }
        finally
        {
            google.SetFixtureState(original);
            SetWindowField(window, "selectedDate", new DateTime(2026, 11, 9));
            InvokeStartupRender(window, "RenderCalendar");
        }
    }

    private static DatePickerTextBox DateEditor(DatePicker picker)
    {
        picker.ApplyTemplate();
        return (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
    }

    private static async Task GoogleDatePickerChecks(MainWindow owner, IReadOnlyList<GoogleCalendarInfo> calendars)
    {
        var original = FakeGoogleCalendarService.Event("picker", "DatePicker fixture", new(2026, 11, 9), new(2026, 11, 12));
        GoogleEventDialogRequest? captured = null;
        var calls = 0;
        var dialog = new GoogleEventDialog(calendars, original, new(2026, 11, 9), (request, _) =>
        {
            captured = request;
            calls++;
            throw new IOException("Fixture keeps editor open");
        }) { Owner = owner };
        dialog.Show();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var start = (DatePicker)dialog.FindName("StartDateInput");
        var end = (DatePicker)dialog.FindName("EndDateInput");
        var save = (Button)dialog.FindName("SaveButton");
        var startText = DateEditor(start);
        var endText = DateEditor(end);
        Check(start.SelectedDate == new DateTime(2026, 11, 9) && end.SelectedDate == new DateTime(2026, 11, 11) &&
            startText.Text == "2026-11-09" && endText.Text == "2026-11-11" && startText.IsVisible && endText.IsVisible,
            "Google DatePicker fields display editable ISO dates and inclusive all-day end");
        var open = (Button)start.Template.FindName("PART_Button", start);
        ((Slider)owner.FindName("OpacitySlider")).Value = .55;
        CheckOpaqueGoogleWindow(owner, dialog, "event editor at translucent main");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var popup = (Popup)start.Template.FindName("PART_Popup", start);
        Check(start.IsDropDownOpen && popup.IsOpen && popup.Child is Calendar { IsVisible: true },
            "Google date selector button opens a real WPF calendar popup");
        var calendar = (Calendar)popup.Child;
        calendar.UpdateLayout();
        var dayButtons = Descendants<CalendarDayButton>(calendar).Where(button => button.IsVisible).ToArray();
        Check(calendar.ActualWidth >= 260 && calendar.ActualHeight >= 270 && dayButtons.Length == 42 &&
            dayButtons.All(button => button.ActualWidth >= 30 && button.ActualHeight >= 28) &&
            ((SolidColorBrush)calendar.Background).Color == Color.FromRgb(32, 43, 80) &&
            dayButtons.All(button => button.Template.FindName("DaySurface", button) is Border),
            "Google popup uses the app dark theme and 42 complete themed date buttons at actual WPF bounds");
        Capture(dialog, "google-datepicker-open-editor-fixture");
        CheckAlpha(RenderPixels(calendar), 10, 10, 1, "Google calendar popup background");
        CaptureGoogleElement(calendar, "google-datepicker-open-popup-fixture");
        var item = (CalendarItem)calendar.Template.FindName("PART_CalendarItem", calendar);
        var header = (Button)item.Template.FindName("PART_HeaderButton", item);
        var previous = (Button)item.Template.FindName("PART_PreviousButton", item);
        var next = (Button)item.Template.FindName("PART_NextButton", item);
        Check(header.Content?.ToString()?.Contains("11월") == true &&
            new[] { "일", "월", "화", "수", "목", "금", "토" }.All(day => Descendants<TextBlock>(calendar).Any(text => text.IsVisible && text.Text == day)),
            "Google popup shows Korean month header and all seven visible weekday headings");
        Check(BackgroundOpacity.GetValue(calendar) == 1 && calendar.Background.Opacity == 1 &&
            dayButtons.All(button => button.Opacity == 1),
            "Google popup keeps an opaque background while the main window is translucent");
        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        calendar.UpdateLayout();
        Check(calendar.DisplayDate.Month == 12 && header.Content?.ToString()?.Contains("12월") == true,
            "Google popup next-month button changes the live month and localized header");
        previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        calendar.UpdateLayout();
        Check(calendar.DisplayMode == CalendarMode.Year && Descendants<CalendarButton>(calendar).Count(button => button.IsVisible) == 12,
            "Google popup month header opens twelve complete themed month choices");
        CaptureGoogleElement(calendar, "google-datepicker-year-popup-fixture");
        header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        calendar.UpdateLayout();
        Check(calendar.DisplayMode == CalendarMode.Decade && Descendants<CalendarButton>(calendar).Count(button => button.IsVisible) == 12,
            "Google popup year header opens twelve complete themed year choices");
        CaptureGoogleElement(calendar, "google-datepicker-decade-popup-fixture");
        calendar.DisplayMode = CalendarMode.Month;
        calendar.DisplayDate = new DateTime(2026, 11, 1);
        calendar.SelectedDate = new DateTime(2026, 11, 10);
        start.IsDropDownOpen = false;
        Check(start.SelectedDate == new DateTime(2026, 11, 10) && DateEditor(start).Text == "2026-11-10",
            "Google calendar popup selection updates its paired editable date field");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 1 && captured?.Event.StartDate == new DateOnly(2026, 11, 10) && captured.Event.EndDate == new DateOnly(2026, 11, 12),
            "Google popup date selection saves with inclusive editor and exclusive API end");
        foreach (var invalid in new[] { "2026-02-30", "not-a-date", "" })
        {
            endText.Focus();
            endText.Text = invalid;
            save.Focus();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(calls == 1 && dialog.IsVisible && ((TextBlock)dialog.FindName("StatusText")).Text.Length > 0,
                "Google typed invalid or empty date blocks save after focus change: " + invalid);
        }
        endText.Text = "2026-11-14";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 2 && captured?.Event.EndDate == new DateOnly(2026, 11, 15),
            "Google correcting typed date clears validation and preserves exclusive API end");
        startText.Focus();
        startText.Text = "2026-02-30";
        save.Focus();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 2, "Google invalid start date is rejected before same-day calendar correction");
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        calendar = (Calendar)((Popup)start.Template.FindName("PART_Popup", start)).Child;
        calendar.UpdateLayout();
        var sameDay = Descendants<CalendarDayButton>(calendar).Single(button => button.DataContext is DateTime date && date == new DateTime(2026, 11, 10));
        sameDay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        start.IsDropDownOpen = false;
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(calls == 3 && captured?.Event.StartDate == new DateOnly(2026, 11, 10) && startText.Text == "2026-11-10",
            "Google selecting the same calendar date explicitly repairs preserved invalid text");
        dialog.Close();

        var timed = original with
        {
            AllDay = false, StartDate = null, EndDate = null,
            Start = new DateTimeOffset(2026, 11, 9, 10, 17, 43, TimeSpan.FromHours(-4)),
            End = new DateTimeOffset(2026, 11, 9, 11, 29, 57, TimeSpan.FromHours(-4)),
            TimeZone = "America/New_York", Recurrence = ["RRULE:FREQ=WEEKLY;BYDAY=MO"]
        };
        captured = null;
        dialog = new GoogleEventDialog(calendars, timed, new(2026, 11, 9), (request, _) =>
        {
            captured = request;
            throw new IOException("Fixture keeps editor open");
        }) { Owner = owner };
        dialog.Show();
        await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(((DatePicker)dialog.FindName("StartDateInput")).ActualWidth > 150 &&
            ((TextBox)dialog.FindName("StartTimeInput")).ActualWidth >= 75 && ((TextBox)dialog.FindName("StartTimeInput")).IsVisible,
            "Google timed DatePicker and HH:mm field both fit the real editor layout");
        ((TextBox)dialog.FindName("TitleInput")).Text = "Timed title changed";
        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(captured?.Event.Start?.EqualsExact(timed.Start!.Value) == true && captured.Event.End?.EqualsExact(timed.End!.Value) == true &&
            captured.Event.TimeZone == timed.TimeZone && captured.Event.Recurrence.SequenceEqual(timed.Recurrence),
            "Google title-only DatePicker edit preserves timed seconds original offset zone and recurrence");
        Capture(dialog, "google-datepicker-timed-fixture");
        dialog.Close();
    }

    private static void CaptureGoogleElement(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        foreach (var scale in new[] { 1.0, 1.5 })
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine("artifacts", $"{name}-{scale:0.0}x.png"));
            encoder.Save(output);
        }
    }
}
