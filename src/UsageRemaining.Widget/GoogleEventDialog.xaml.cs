using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public enum GoogleEventDialogAction { Save, Delete }
public enum GoogleEventDialogScope { ThisEvent, EntireSeries }
public sealed record GoogleEventDialogRequest(GoogleEventDialogAction Action, GoogleCalendarEvent Event, GoogleEventDialogScope Scope);

public partial class GoogleEventDialog : Window
{
    private readonly GoogleCalendarEvent? original;
    private GoogleCalendarEvent current;
    private readonly Func<GoogleEventDialogRequest, CancellationToken, Task> apply;
    private readonly Func<CancellationToken, Task<GoogleCalendarEvent>>? loadSeries;
    private readonly CancellationTokenSource lifetime = new();
    private bool initialized;
    private bool busy;
    private bool closeRequested;
    private bool complete;
    private int loadedScope;
    private readonly Dictionary<DatePicker, string> invalidDateTexts = new();
    private readonly HashSet<DatePicker> restoringDateTexts = new();
    private readonly HashSet<System.Windows.Controls.Calendar> connectedDateCalendars = new();

    public GoogleEventDialog(IReadOnlyList<GoogleCalendarInfo> calendars, GoogleCalendarEvent? existing, DateOnly date,
        Func<GoogleEventDialogRequest, CancellationToken, Task> apply,
        Func<CancellationToken, Task<GoogleCalendarEvent>>? loadSeries = null)
    {
        this.original = existing;
        this.apply = apply;
        this.loadSeries = loadSeries;
        current = existing ?? new GoogleCalendarEvent { StartDate = date, EndDate = date.AddDays(1), AllDay = true, CanWrite = true };
        InitializeComponent();
        BackgroundOpacity.SetValue(this, 1);
        if (existing is null)
        {
            CalendarInput.ItemsSource = calendars.Where(item => item.CanWrite).ToArray();
            CalendarInput.SelectedItem = calendars.FirstOrDefault(item => item.CanWrite && item.Primary) ?? calendars.FirstOrDefault(item => item.CanWrite);
        }
        else
        {
            var calendar = calendars.FirstOrDefault(item => item.Id == existing.CalendarId)
                ?? new GoogleCalendarInfo { Id = existing.CalendarId, Summary = existing.CalendarId, AccessRole = existing.CanWrite ? "writer" : "reader" };
            CalendarInput.ItemsSource = new[] { calendar };
            CalendarInput.SelectedItem = calendar;
        }
        CalendarInput.IsEnabled = existing is null;
        ScopePanel.Visibility = existing?.RecurringEventId is not null && loadSeries is not null ? Visibility.Visible : Visibility.Collapsed;
        FillFields();
        initialized = true;
        Loaded += (_, _) =>
        {
            ConnectDateEditor(StartDateInput);
            ConnectDateEditor(EndDateInput);
            CancelButton.Focus();
        };
        Closing += OnClosing;
        Closed += (_, _) => { lifetime.Cancel(); lifetime.Dispose(); };
    }

    private void FillFields()
    {
        var wasInitialized = initialized;
        initialized = false;
        HeadingText.Text = original is null ? "새 Google 일정" : "Google 일정";
        TitleInput.Text = current.Summary;
        LocationInput.Text = current.Location ?? "";
        DescriptionInput.Text = current.Description ?? "";
        AllDayInput.IsChecked = current.AllDay;
        var start = LocalTime.Convert(current.Start ?? DateTimeOffset.Now);
        var end = LocalTime.Convert(current.End ?? start.AddHours(1));
        StartDateInput.SelectedDate = (current.AllDay ? current.StartDate ?? DateOnly.FromDateTime(start.DateTime) : DateOnly.FromDateTime(start.DateTime)).ToDateTime(TimeOnly.MinValue);
        EndDateInput.SelectedDate = (current.AllDay ? (current.EndDate ?? current.StartDate?.AddDays(1) ?? DateOnly.FromDateTime(end.DateTime)).AddDays(-1) : DateOnly.FromDateTime(end.DateTime)).ToDateTime(TimeOnly.MinValue);
        invalidDateTexts.Clear();
        StartTimeInput.Text = start.ToString("HH:mm", CultureInfo.InvariantCulture);
        EndTimeInput.Text = end.ToString("HH:mm", CultureInfo.InvariantCulture);
        RepeatInput.SelectedIndex = current.Recurrence.Length == 0 ? 0 : 4;
        ReminderInput.SelectedIndex = original is null ? 0 : 3;
        ReminderMinutesInput.Text = (current.ReminderMinutes ?? 10).ToString(CultureInfo.InvariantCulture);
        ReminderMinutesInput.IsEnabled = ReminderInput.SelectedIndex == 2;
        var readOnly = original is not null && !current.CanWrite;
        ReadOnlyBadge.Visibility = readOnly ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.IsEnabled = !readOnly;
        SaveButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.Visibility = original is null || readOnly ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Content = readOnly ? "닫기" : "취소";
        TimeZoneText.Text = $"{TimeZoneInfo.Local.DisplayName} · 이 컴퓨터의 시간";
        if (original is null && CalendarInput.SelectedItem is null)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = "일정을 쓸 수 있는 캘린더가 없습니다.";
        }
        UpdateTimeFields();
        UpdateRepeatField();
        initialized = wasInitialized;
    }

    private void UpdateRepeatField()
    {
        RepeatInput.IsEnabled = current.CanWrite && (original?.RecurringEventId is null || loadedScope == 1);
    }

    private void UpdateTimeFields()
    {
        if (StartTimeInput is null || EndTimeInput is null) return;
        var timed = AllDayInput.IsChecked != true;
        StartTimeInput.IsEnabled = timed;
        EndTimeInput.IsEnabled = timed;
        StartTimeInput.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        EndTimeInput.Visibility = timed ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(StartDateInput, timed ? 1 : 3);
        Grid.SetColumnSpan(EndDateInput, timed ? 1 : 3);
        DateHintText.Text = timed ? "날짜 yyyy-MM-dd · 시간 HH:mm" : "종일 일정 · 종료에는 마지막 날짜를 입력하세요.";
    }

    private void AllDay_Changed(object sender, RoutedEventArgs e) => UpdateTimeFields();

    private void ConnectDateEditor(DatePicker picker)
    {
        picker.ApplyTemplate();
        if (picker.Template.FindName("PART_TextBox", picker) is DatePickerTextBox editor)
        {
            editor.TextChanged += (_, _) =>
            {
                if (!restoringDateTexts.Contains(picker)) invalidDateTexts.Remove(picker);
            };
        }
    }

    private void DatePicker_Opened(object? sender, RoutedEventArgs e)
    {
        if (sender is not DatePicker picker) return;
        // Popup calendars are separate visual trees and keep an opaque surface.
        if (picker.Template.FindName("PART_Popup", picker) is Popup { Child: System.Windows.Controls.Calendar calendar })
        {
            BackgroundOpacity.SetValue(calendar, 1);
            if (connectedDateCalendars.Add(calendar))
            {
                calendar.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, args) =>
                {
                    if (args.OriginalSource is not CalendarDayButton { IsBlackedOut: false, DataContext: DateTime selectedDate }) return;
                    // Choosing the already-selected day is still an explicit correction of invalid text.
                    invalidDateTexts.Remove(picker);
                    restoringDateTexts.Remove(picker);
                    picker.SelectedDate = selectedDate;
                    picker.Text = selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }), true);
            }
        }
    }

    private void DatePicker_SelectedDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DatePicker picker && !restoringDateTexts.Contains(picker)) invalidDateTexts.Remove(picker);
    }

    private void DatePicker_ValidationError(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        if (sender is not DatePicker picker) return;
        e.ThrowException = false;
        invalidDateTexts[picker] = e.Text;
        restoringDateTexts.Add(picker);
        // WPF otherwise restores the previous valid selection after an invalid edit loses focus.
        // Preserve the edit and the error so Save cannot silently submit the old date instead.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            try
            {
                if (invalidDateTexts.TryGetValue(picker, out var invalidText)
                    && picker.Template.FindName("PART_TextBox", picker) is DatePickerTextBox editor)
                    editor.Text = invalidText;
            }
            finally { restoringDateTexts.Remove(picker); }
        }));
    }

    private DateOnly ReadDate(DatePicker picker)
    {
        var text = invalidDateTexts.TryGetValue(picker, out var invalidText) ? invalidText
            : picker.Template.FindName("PART_TextBox", picker) is DatePickerTextBox editor ? editor.Text : picker.Text;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            picker.Focus();
            throw new InvalidOperationException("날짜를 yyyy-MM-dd 형식으로 입력하거나 달력에서 선택하세요.");
        }
        return date;
    }
    private void Reminder_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReminderMinutesInput is not null) ReminderMinutesInput.IsEnabled = ReminderInput.SelectedIndex == 2;
    }

    private async void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || busy || loadSeries is null || ScopeInput.SelectedIndex == loadedScope) return;
        var target = ScopeInput.SelectedIndex;
        SetBusy(true);
        try
        {
            var value = target == 1 ? await loadSeries(lifetime.Token) : original;
            if (value is null) return;
            if (target == 1 && !value.CanWrite) throw new InvalidOperationException("반복 일정 전체에 대한 수정 권한이 없습니다.");
            current = value;
            loadedScope = target;
            FillFields();
            StatusText.Text = target == 1 ? "전체 반복 일정의 원래 날짜와 내용을 불러왔습니다." : "선택한 일정의 내용을 불러왔습니다.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            initialized = false;
            ScopeInput.SelectedIndex = loadedScope;
            initialized = true;
            StatusText.Text = exception.Message;
        }
        finally { SetBusy(false); }
    }

    private GoogleCalendarEvent ReadFields()
    {
        if (string.IsNullOrWhiteSpace(TitleInput.Text)) throw new InvalidOperationException("일정 제목을 입력하세요.");
        if (CalendarInput.SelectedItem is not GoogleCalendarInfo calendar || !calendar.CanWrite) throw new InvalidOperationException("쓸 수 있는 캘린더를 선택하세요.");
        var startDate = ReadDate(StartDateInput);
        var endDate = ReadDate(EndDateInput);
        var allDay = AllDayInput.IsChecked == true;
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;
        if (allDay)
        {
            if (endDate < startDate || endDate == DateOnly.MaxValue) throw new InvalidOperationException("종료 날짜는 시작 날짜 이후여야 합니다.");
        }
        else
        {
            start = ReadInstant(startDate, StartTimeInput.Text, current.Start);
            end = ReadInstant(endDate, EndTimeInput.Text, current.End);
            if (end <= start) throw new InvalidOperationException("종료 시각은 시작 시각 이후여야 합니다.");
        }
        int? minutes = current.ReminderMinutes;
        var useDefault = current.UseDefaultReminders;
        if (ReminderInput.SelectedIndex != 3)
        {
            useDefault = ReminderInput.SelectedIndex == 0;
            minutes = null;
            if (ReminderInput.SelectedIndex == 2)
            {
                if (!int.TryParse(ReminderMinutesInput.Text, out var parsed) || parsed < 0 || parsed > 40320) throw new InvalidOperationException("알림은 0–40320분 사이로 입력하세요.");
                minutes = parsed;
            }
        }
        var recurrence = RepeatInput.SelectedIndex switch
        {
            0 => Array.Empty<string>(), 1 => ["RRULE:FREQ=DAILY"], 2 => ["RRULE:FREQ=WEEKLY"], 3 => ["RRULE:FREQ=MONTHLY"], _ => current.Recurrence
        };
        return current with
        {
            CalendarId = calendar.Id, Summary = TitleInput.Text.Trim(), Location = LocationInput.Text, Description = DescriptionInput.Text,
            AllDay = allDay, StartDate = allDay ? startDate : null, EndDate = allDay ? endDate.AddDays(1) : null,
            Start = start, End = end, TimeZone = allDay ? null : !current.AllDay && start == current.Start && end == current.End ? current.TimeZone : LocalTimeZoneId(),
            Recurrence = recurrence, UseDefaultReminders = useDefault, ReminderMinutes = minutes,
            RemindersChanged = original is not null && ReminderInput.SelectedIndex != 3
        };
    }

    private static string LocalTimeZoneId()
    {
        var zone = TimeZoneInfo.Local;
        if (zone.HasIanaId) return zone.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana)) return iana;
        throw new InvalidOperationException("이 컴퓨터의 시간대를 Google 시간대로 변환할 수 없습니다.");
    }

    private static DateTimeOffset ReadInstant(DateOnly date, string text, DateTimeOffset? previous)
    {
        if (!TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidOperationException("시간을 24시간 HH:mm 형식으로 입력하세요.");
        // Keep the original instant, offset and seconds when the displayed minute was not changed.
        if (previous is { } originalInstant)
        {
            var originalLocal = LocalTime.Convert(originalInstant);
            if (DateOnly.FromDateTime(originalLocal.DateTime) == date && originalLocal.ToString("HH:mm", CultureInfo.InvariantCulture) == text.Trim()) return originalInstant;
        }
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (TimeZoneInfo.Local.IsInvalidTime(local)) throw new InvalidOperationException("이 시각은 일광 절약 시간 전환으로 존재하지 않습니다.");
        if (TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new InvalidOperationException("이 시각은 일광 절약 시간 전환으로 두 번 존재합니다. 다른 시각을 선택하세요.");
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try { await ApplyAsync(GoogleEventDialogAction.Save, ReadFields()); }
        catch (Exception exception) { StatusText.Text = exception.Message; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || original is null || !current.CanWrite) return;
        var scopeText = loadedScope == 1 ? "반복 일정 전체" : "이 일정";
        var confirm = new GoogleActionConfirmDialog(this, $"{scopeText}을 삭제할까요?", $"{current.Summary}\nGoogle 캘린더에서도 삭제됩니다.", "삭제");
        if (confirm.ShowDialog() == true) await ApplyAsync(GoogleEventDialogAction.Delete, current);
    }

    private async Task ApplyAsync(GoogleEventDialogAction action, GoogleCalendarEvent value)
    {
        SetBusy(true);
        StatusText.Text = action == GoogleEventDialogAction.Save ? "저장 중…" : "삭제 중…";
        try
        {
            await apply(new GoogleEventDialogRequest(action, value, loadedScope == 1 ? GoogleEventDialogScope.EntireSeries : GoogleEventDialogScope.ThisEvent), lifetime.Token);
            complete = true;
            DialogResult = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { StatusText.Text = exception.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        EditorPanel.IsEnabled = !value && (original is null || current.CanWrite);
        SaveButton.IsEnabled = !value && CalendarInput.SelectedItem is GoogleCalendarInfo calendar && calendar.CanWrite;
        DeleteButton.IsEnabled = !value;
        CancelButton.Content = value ? "중단" : original is not null && !current.CanWrite ? "닫기" : "취소";
        UpdateRepeatField();
        if (!value && closeRequested && !complete) { complete = true; Close(); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy && !complete)
        {
            e.Cancel = true;
            closeRequested = true;
            lifetime.Cancel();
            StatusText.Text = "작업 중단을 기다리는 중…";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (busy) { closeRequested = true; lifetime.Cancel(); StatusText.Text = "작업 중단을 기다리는 중…"; }
        else DialogResult = false;
    }

    private void Dialog_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && WindowInteraction.CanDragFrom(e.OriginalSource as DependencyObject)) DragMove();
    }
}
