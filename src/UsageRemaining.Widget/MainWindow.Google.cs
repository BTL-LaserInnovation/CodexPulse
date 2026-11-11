using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private readonly IGoogleCalendarService googleCalendar;
    private bool googleInitialized;
    private bool googleRefreshing;
    private bool googleRefreshQueued;
    private long nextGoogleRefresh;
    private string? googleError;
    private string? selectedGoogleEvent;
    private Window? googleDialog;
    private bool googleConnecting;
    private bool googleConnectBusy;
    private Task googleConnectTask = Task.CompletedTask;
    private CancellationTokenSource? googleConnectCancellation;
    private TimeSpan googleConnectTimeout = TimeSpan.FromMinutes(5);
    private bool googleConnectCancelled;
    private string? googleConnectNotice;
    private bool googleDeleting;
    private const double ListFontSize = 10;

    private async Task InitializeGoogleAsync()
    {
        try
        {
            await googleCalendar.InitializeAsync(lifetime.Token);
            googleInitialized = true;
            await RefreshGoogleAsync(force: true);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { googleError = ex.Message; }
        finally { if (!closing) { RenderGoogleConnection(); RenderCalendar(); } }
    }

    private void GoogleCalendar_Changed(object? sender, EventArgs e)
    {
        if (closing || Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(() => GoogleCalendar_Changed(sender, e)));
            return;
        }
        RenderGoogleConnection();
        RenderCalendar();
    }

    private void RenderGoogleConnection()
    {
        if (!ready || closing) return;
        var connection = googleCalendar.State;
        GoogleConnectButton.Content = googleConnecting
            ? googleConnectCancellation?.IsCancellationRequested == true ? "취소 중…" : "연결 취소"
            : connection.Connected ? "Google ✓" : "Google 연결";
        AutomationProperties.SetName(GoogleConnectButton, googleConnecting
            ? googleConnectCancellation?.IsCancellationRequested == true ? "Google 연결 취소 중" : "Google 연결 취소"
            : "Google 캘린더 연결 및 관리");
        GoogleConnectButton.ToolTip = googleConnecting ? "진행 중인 Google 연결을 취소합니다. 브라우저의 로그인 창은 닫아도 됩니다."
            : googleError ?? googleConnectNotice ?? (connection.Connected
            ? connection.AccountEmail + "\n캘린더 선택 · 계정 변경 · 연결 해제"
            : "Google 캘린더 연결 및 관리");
        GoogleConnectButton.IsEnabled = (!googleConnectBusy || googleConnecting && googleConnectCancellation?.IsCancellationRequested == false) && !googleDeleting && !updateApplying;
        GoogleAddButton.IsEnabled = !updateApplying && !googleConnectBusy && !googleDeleting;
        GoogleRefreshButton.IsEnabled = !googleRefreshing && !googleConnectBusy && !googleDeleting && !updateApplying;
        GoogleAddButton.ToolTip = !connection.Connected ? "Google 계정을 연결하고 일정 추가" : "선택한 날짜에 Google 일정 추가";
        var status = googleDeleting ? "Google 일정 삭제 처리 중…"
            : googleRefreshing ? "Google 일정 동기화 중…"
            : googleError is not null ? "Google · " + googleError
            : connection.Warning is not null ? "Google 동기화 확인 필요"
            : googleConnecting ? googleConnectCancellation?.IsCancellationRequested == true ? "Google 연결 취소 처리 중…" : "Google 계정 연결 중…"
            : googleConnectNotice is not null ? googleConnectNotice
            : connection.Connected ? connection.LastSyncedAt is { } at ? $"Google · {LocalTime.Convert(at):HH:mm} 동기화" : "Google · 연결됨"
            : "Google 캘린더를 연결할 수 있습니다";
        SetGoogleRefreshStatus(status);
    }

    private void SetGoogleRefreshStatus(string status, bool attention = false)
    {
        var connection = googleCalendar.State;
        var lines = new List<string>
        {
            connection.Connected ? "Google 일정 새로 고침" : "Google 계정을 연결하고 일정 새로 고침",
            status
        };
        if (googleError is not null && status != "Google · " + googleError) lines.Add(googleError);
        if (connection.Warning is not null) lines.Add(connection.Warning);
        if (connection.LastSyncedAt is { } at)
        {
            var synced = $"Google · {LocalTime.Convert(at):HH:mm} 동기화";
            if (status != synced) lines.Add(synced);
        }
        lines.Add(connection.Connected
            ? $"{connection.AccountEmail}\n선택한 캘린더 {connection.SelectedCalendarIds.Count}개 · 60초마다 변경 확인\nPC 시간대: {TimeZoneInfo.Local.DisplayName}"
            : "Google 연결을 누르면 브라우저에서 계정을 선택할 수 있습니다.");
        var tooltip = string.Join("\n", lines);
        GoogleRefreshButton.ToolTip = tooltip;
        AutomationProperties.SetHelpText(GoogleRefreshButton, tooltip);
        GoogleRefreshButton.Foreground = Theme(attention || googleError is not null || connection.Warning is not null ? "CoralBrush" : "MutedBrush");
    }

    private async Task RefreshGoogleAsync(bool force = false)
    {
        if (closing || updateApplying || googleDeleting || !googleInitialized) return;
        if (googleRefreshing) { googleRefreshQueued |= force; return; }
        if (!force && Environment.TickCount64 < nextGoogleRefresh) return;
        nextGoogleRefresh = Environment.TickCount64 + 60_000;
        if (!googleCalendar.State.Connected) { RenderGoogleConnection(); return; }
        googleRefreshing = true;
        RenderGoogleConnection();
        try
        {
            var range = VisibleCalendarRange;
            var first = DateOnly.FromDateTime(range.Start);
            // Include a margin so all-day events in a calendar's different time zone
            // cannot disappear at either edge of the computer's visible range.
            await googleCalendar.RefreshAsync(CalendarEventPresentation.LocalMidnight(first.AddDays(-2)),
                CalendarEventPresentation.LocalMidnight(DateOnly.FromDateTime(range.EndExclusive).AddDays(2)), lifetime.Token);
            googleError = null;
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { googleError = ex.Message; }
        finally
        {
            googleRefreshing = false;
            if (!closing) { RenderGoogleConnection(); RenderCalendar(); }
            if (googleRefreshQueued && !closing)
            {
                googleRefreshQueued = false;
                _ = RefreshGoogleAsync(force: true);
            }
        }
    }

    private async void GoogleRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateApplying || googleDialog is not null || googleConnectBusy || googleDeleting) return;
        if (await EnsureGoogleConnectedAsync()) await RefreshGoogleAsync(force: true);
    }

    private async void GoogleConnect_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateApplying || googleDialog is not null || googleDeleting) return;
        if (googleConnecting)
        {
            if (googleConnectCancellation is { IsCancellationRequested: false } attempt)
            {
                googleConnectCancelled = true;
                attempt.Cancel();
                RenderGoogleConnection();
            }
            return;
        }
        if (googleConnectBusy) return;
        if (!googleCalendar.State.Connected)
        {
            await EnsureGoogleConnectedAsync();
            return;
        }
        ShowGoogleAccountDialog();
        await RefreshGoogleAsync(force: true);
    }

    private void ShowGoogleAccountDialog()
    {
        if (closing || googleDeleting) return;
        var dialog = new GoogleAccountDialog(googleCalendar, OpenGoogleBrowser,
            token => googleCalendar.InitializeAsync(token), initialError: googleError) { Owner = this, Topmost = Topmost };
        var completion = googleConnectBusy ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completion is not null) googleConnectTask = completion.Task;
        googleDialog = dialog;
        BackgroundOpacity.SetValue(dialog, 1);
        try { dialog.ShowDialog(); }
        finally { googleDialog = null; completion?.TrySetResult(); }
    }

    private async Task<bool> EnsureGoogleConnectedAsync()
    {
        if (closing || googleConnectBusy || googleDeleting) return false;
        if (googleCalendar.State.Connected) return true;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        attempt.CancelAfter(googleConnectTimeout);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        googleConnectTask = completion.Task;
        googleConnectCancellation = attempt;
        googleConnectCancelled = false;
        googleConnectNotice = null;
        googleConnecting = true;
        googleConnectBusy = true;
        googleError = null;
        try
        {
            RenderGoogleConnection();
            attempt.Token.ThrowIfCancellationRequested();
            await googleCalendar.InitializeAsync(attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            googleInitialized = true;
            if (!googleCalendar.State.IsConfigured)
            {
                EndGoogleConnectAttempt(attempt);
                SetGoogleRefreshStatus("Google 연결 준비가 아직 완료되지 않았습니다", attention: true);
                ShowGoogleAccountDialog();
                attempt.Token.ThrowIfCancellationRequested();
                return googleCalendar.State.Connected;
            }
            SetGoogleRefreshStatus("브라우저에서 Google 로그인을 완료하세요");
            await googleCalendar.ConnectAsync(address =>
            {
                attempt.Token.ThrowIfCancellationRequested();
                OpenGoogleBrowser(address);
            }, attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
            EndGoogleConnectAttempt(attempt);
            await RefreshGoogleAsync(force: true);
            attempt.Token.ThrowIfCancellationRequested();
            if (googleCalendar.State.Connected) ShowGoogleAccountDialog();
            attempt.Token.ThrowIfCancellationRequested();
            return googleCalendar.State.Connected;
        }
        catch (GoogleCalendarCredentialRestoreException ex)
        {
            googleError = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && (attempt.IsCancellationRequested || ex is GoogleCalendarConnectionTimeoutException))
        {
            if (!closing)
                googleConnectNotice = googleConnectCancelled
                    ? "Google 연결을 취소했습니다. Google 연결을 눌러 다시 시도하세요. 이전 브라우저 로그인 창은 닫아도 됩니다."
                    : "Google 로그인 시간이 초과되었습니다. Google 연결을 눌러 다시 시도하세요. 이전 브라우저 로그인 창은 닫아도 됩니다.";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            googleError = ex.Message;
            if (attempt.IsCancellationRequested) return false;
            EndGoogleConnectAttempt(attempt);
            if (!closing) ShowGoogleAccountDialog();
            if (attempt.IsCancellationRequested) return false;
            if (!closing && googleCalendar.State.Connected)
            {
                await RefreshGoogleAsync(force: true);
                return !closing && !attempt.IsCancellationRequested;
            }
            return false;
        }
        finally
        {
            googleConnecting = false;
            googleConnectBusy = false;
            googleConnectCancellation = null;
            try { if (!closing) RenderGoogleConnection(); }
            finally { completion.TrySetResult(); }
        }
    }

    private void EndGoogleConnectAttempt(CancellationTokenSource attempt)
    {
        attempt.CancelAfter(Timeout.InfiniteTimeSpan);
        googleConnecting = false;
        googleConnectCancellation = null;
        RenderGoogleConnection();
    }

    private static void OpenGoogleBrowser(Uri address)
    {
        if (address.Scheme != "https" || address.Host != "accounts.google.com")
            throw new InvalidOperationException("Google 로그인 주소를 확인할 수 없습니다.");
        Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
    }

    private async void GoogleAdd_Click(object sender, RoutedEventArgs e)
    {
        if (closing || updateApplying || googleDialog is not null || googleConnectBusy || googleDeleting) return;
        if (!await EnsureGoogleConnectedAsync()) return;
        if (!googleCalendar.State.Calendars.Any(calendar => calendar.CanWrite))
        {
            SetGoogleRefreshStatus("일정을 추가할 수 있는 캘린더가 없습니다", attention: true);
            ShowGoogleAccountDialog();
            return;
        }
        OpenGoogleEvent(null);
    }

    private void OpenGoogleEvent(GoogleCalendarEvent? value)
    {
        if (closing || updateApplying || googleDeleting || googleConnectBusy || googleDialog is not null || !googleCalendar.State.Connected) return;
        if (value is null && !googleCalendar.State.Calendars.Any(calendar => calendar.CanWrite)) return;
        var connectionAccount = googleCalendar.State.AccountEmail;
        async Task Apply(GoogleEventDialogRequest request, CancellationToken cancellationToken)
        {
            if (!googleCalendar.State.Connected || googleCalendar.State.AccountEmail != connectionAccount)
                throw new InvalidOperationException("Google 계정이 변경되었습니다. 창을 닫고 다시 선택하세요.");
            if (request.Action == GoogleEventDialogAction.Delete)
            {
                await googleCalendar.DeleteAsync(request.Event, cancellationToken);
                selectedGoogleEvent = null;
            }
            else
            {
                var saved = await googleCalendar.SaveAsync(request.Event, cancellationToken);
                selectedGoogleEvent = CalendarEventPresentation.Key(saved);
            }
            googleError = null;
            nextGoogleRefresh = 0;
        }
        Func<CancellationToken, Task<GoogleCalendarEvent>>? loadSeries = value?.RecurringEventId is { } series
            ? token => googleCalendar.GetEventAsync(value.CalendarId, series, token) : null;
        var dialog = new GoogleEventDialog(googleCalendar.State.Calendars, value,
            DateOnly.FromDateTime(selectedDate), Apply, loadSeries) { Owner = this, Topmost = Topmost };
        googleDialog = dialog;
        BackgroundOpacity.SetValue(dialog, 1);
        try { dialog.ShowDialog(); }
        finally
        {
            googleDialog = null;
            if (!closing) { RenderGoogleConnection(); RenderCalendar(); _ = RefreshGoogleAsync(force: true); }
        }
    }

    private IReadOnlyList<GoogleCalendarEvent> GoogleEventsFor(DateTime day) => googleCalendar.State.Events
        .Where(value => CalendarEventPresentation.OccursOn(value, DateOnly.FromDateTime(day)))
        .OrderBy(value => value.AllDay ? 0 : 1).ThenBy(value => value.Start).ThenBy(value => value.Summary).ToArray();

    private static readonly Brush GoogleEventBrush = new SolidColorBrush(Color.FromRgb(241, 217, 160));

    private void RenderCalendarEventList(IReadOnlyList<CalendarEntry> codexEntries)
    {
        CalendarEventList.Children.Clear();
        foreach (var entry in codexEntries)
        {
            var pieces = entry.Text.Split(" · ", 2);
            CalendarEventList.Children.Add(CalendarListRow("C", Theme(entry.Color), pieces[0],
                pieces.Length > 1 ? pieces[1] : entry.Text, entry.Text + "\n" + entry.Summary, null));
        }
        foreach (var value in GoogleEventsFor(selectedDate))
        {
            var row = CalendarListRow("▣", GoogleEventBrush, CalendarEventPresentation.TimeLabel(value),
                string.IsNullOrWhiteSpace(value.Summary) ? "(제목 없음)" : value.Summary,
                CalendarEventPresentation.Description(value), value);
            CalendarEventList.Children.Add(row);
        }
        if (CalendarEventList.Children.Count == 0)
            CalendarEventList.Children.Add(new TextBlock { Text = "이 날짜에 등록된 일정이 없습니다.",
                FontSize = ListFontSize, Foreground = Theme("MutedBrush"), Margin = new Thickness(0, 3, 0, 0) });
    }

    private FrameworkElement CalendarListRow(string symbol, Brush brush, string time, string title, string tooltip, GoogleCalendarEvent? value)
    {
        var grid = new Grid { Height = 22 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement mark = value is null
            ? new TextBlock { Text = symbol, Foreground = brush, FontSize = 10, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            : GoogleCalendarGlyph();
        var when = new TextBlock { Text = time, FontSize = ListFontSize, FontWeight = FontWeights.Normal, Foreground = Theme("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new TextBlock { Text = title, FontSize = ListFontSize, FontWeight = FontWeights.Normal, Foreground = Theme("TextBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(when, 1); Grid.SetColumn(text, 2);
        grid.Children.Add(mark); grid.Children.Add(when); grid.Children.Add(text);
        if (value is null) { grid.Margin = new Thickness(0, 0, 0, 2); grid.ToolTip = tooltip; return grid; }
        var row = new Grid { Height = 22, Margin = new Thickness(0, 0, 0, 2), Tag = value };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        var button = new Button { Name = "GoogleEventEditButton", Content = grid, Height = 22, Padding = new Thickness(0), ToolTip = tooltip,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = value, IsEnabled = !googleDeleting };
        if (CalendarEventPresentation.Key(value) == selectedGoogleEvent) button.BorderBrush = GoogleEventBrush;
        AutomationProperties.SetName(button, "Google 일정 " + title + " " + time);
        button.Click += (_, _) => OpenGoogleEvent(value);
        row.Children.Add(button);
        if (CanDeleteGoogleEvent(value))
        {
            var delete = new Button { Name = "GoogleEventDeleteButton", Width = 22, Height = 22, Padding = new Thickness(0), Tag = value,
                ToolTip = "이 Google 일정 삭제", IsEnabled = !googleDeleting && !googleRefreshing && !googleConnectBusy && !updateApplying,
                Content = new Path { Width = 10, Height = 12, Stretch = Stretch.Fill, Stroke = Theme("MutedBrush"), StrokeThickness = 1,
                    Data = Geometry.Parse("M 1,3 L 9,3 M 3,3 L 3,1 L 7,1 L 7,3 M 2,3 L 2.5,11 L 7.5,11 L 8,3 M 4,5 L 4,9 M 6,5 L 6,9") } };
            Grid.SetColumn(delete, 1);
            AutomationProperties.SetName(delete, "Google 일정 삭제 " + title + " " + time);
            delete.Click += async (_, _) => await DeleteGoogleEventAsync(value);
            row.Children.Add(delete);
        }
        return row;
    }

    private static Grid GoogleCalendarGlyph()
    {
        var glyph = new Grid { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        glyph.Children.Add(new Border { BorderBrush = GoogleEventBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 0, 0) });
        glyph.Children.Add(new Path { Data = Geometry.Parse("M 0,5 L 12,5 M 3,0 L 3,4 M 9,0 L 9,4"),
            Stroke = GoogleEventBrush, StrokeThickness = 1 });
        return glyph;
    }

    private void GoogleOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => RenderGoogleCalendarOverlay();

    private void RenderGoogleCalendarOverlay()
    {
        if (!ready || closing) return;
        var overlay = GoogleCalendarOverlay;
        overlay.Children.Clear();
        if (overlay.ActualWidth <= 0 || overlay.ActualHeight <= 0) return;
        var displayRange = VisibleCalendarRange;
        var first = DateOnly.FromDateTime(displayRange.Start);
        var width = overlay.ActualWidth / 7;
        var height = overlay.ActualHeight / displayRange.RowCount;
        var all = googleCalendar.State.Events.Where(value => !value.Cancelled && !GoogleHolidayCalendar.IsHolidayCalendar(value.CalendarId))
            .Select(value => (Value: value, Dates: CalendarEventPresentation.Dates(value)))
            .Where(item => item.Dates is not null).OrderBy(item => item.Dates!.Value.Start)
            .ThenByDescending(item => item.Dates!.Value.EndExclusive.DayNumber - item.Dates!.Value.Start.DayNumber).ToArray();
        for (var week = 0; week < displayRange.RowCount; week++)
        {
            var weekStart = first.AddDays(week * 7);
            var occupied = new bool[7];
            foreach (var item in all)
            {
                var range = item.Dates!.Value;
                if (range.EndExclusive.DayNumber - range.Start.DayNumber < 2) continue;
                var start = Math.Max(0, range.Start.DayNumber - weekStart.DayNumber);
                var end = Math.Min(7, range.EndExclusive.DayNumber - weekStart.DayNumber);
                if (start >= end || Enumerable.Range(start, end - start).Any(index => occupied[index])) continue;
                for (var day = start; day < end; day++) occupied[day] = true;
                var bar = new Button { Width = (end - start) * width - 4, Height = 6, MinHeight = 0, Padding = new Thickness(0),
                    Style = (Style)FindResource("GooglePeriodButton"), Foreground = GoogleEventBrush,
                    ToolTip = CalendarEventPresentation.Description(item.Value), Tag = item.Value };
                AutomationProperties.SetName(bar, "Google 연속 일정 " + item.Value.Summary);
                var value = item.Value;
                bar.Click += (_, _) => SelectGoogleEvent(value, weekStart.AddDays(start));
                Canvas.SetLeft(bar, start * width + 2); Canvas.SetTop(bar, week * height + CalendarDateHeight);
                overlay.Children.Add(bar);
            }
            for (var day = 0; day < 7; day++)
            {
                var date = weekStart.AddDays(day);
                var events = GoogleEventsFor(date.ToDateTime(TimeOnly.MinValue))
                    .Where(value => !GoogleHolidayCalendar.IsHolidayCalendar(value.CalendarId)).ToArray();
                if (events.Length == 0) continue;
                if (!occupied[day] && events.Length == 1)
                {
                    var value = events[0];
                    var icon = new Button { Content = new Viewbox { Width = 9, Height = 6, Child = GoogleCalendarGlyph() }, Foreground = GoogleEventBrush,
                        Height = 8, Width = 13, MinHeight = 0, Padding = new Thickness(0),
                        ToolTip = CalendarEventPresentation.Description(value) };
                    AutomationProperties.SetName(icon, "Google 일정 " + value.Summary);
                    icon.Click += (_, _) => SelectGoogleEvent(value, date);
                    Canvas.SetLeft(icon, (day + 1) * width - 17); Canvas.SetTop(icon, week * height + 2);
                    overlay.Children.Add(icon);
                }
                if (events.Length > 1)
                {
                    var count = new Grid { Width = 15, Height = 9, IsHitTestVisible = false };
                    var background = new Border { Background = Theme("SurfaceBrush"), CornerRadius = new CornerRadius(2) };
                    background.SetBinding(OpacityProperty, new Binding { Path = new PropertyPath("(0)", BackgroundOpacity.ValueProperty),
                        RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
                    count.Children.Add(background);
                    count.Children.Add(new TextBlock { Text = "+" + (events.Length - 1), FontSize = 7, Foreground = GoogleEventBrush,
                        HorizontalAlignment = HorizontalAlignment.Center });
                    Canvas.SetLeft(count, (day + 1) * width - 18); Canvas.SetTop(count, week * height + 2);
                    overlay.Children.Add(count);
                }
            }
        }
    }

    private void SelectGoogleEvent(GoogleCalendarEvent value, DateOnly date)
    {
        selectedDate = date.ToDateTime(TimeOnly.MinValue);
        selectedGoogleEvent = CalendarEventPresentation.Key(value);
        RenderCalendar();
        var selected = CalendarEventList.Children.OfType<FrameworkElement>().FirstOrDefault(button => button.Tag is GoogleCalendarEvent item
            && CalendarEventPresentation.Key(item) == selectedGoogleEvent);
        selected?.BringIntoView();
    }
}
