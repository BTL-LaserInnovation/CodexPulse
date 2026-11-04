using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private sealed record CalendarEntry(string Text, string Color, string? Url, string? Summary = null);

    private async Task RefreshPublicCalendarAsync()
    {
        if (closing || publicRefreshing || Environment.TickCount64 < nextPublicRefresh) return;
        publicRefreshing = true;
        nextPublicRefresh = Environment.TickCount64 + 300_000;
        try
        {
            state.PublicCalendar = await publicClient.FetchAsync(lifetime.Token);
            var latest = state.PublicCalendar.Events.Where(e => !e.IsScheduled && !e.IsAnnouncement)
                .Select(e => e.Time is { } time ? LocalTime.Convert(time).Date : e.Date.ToDateTime(TimeOnly.MinValue)).DefaultIfEmpty().Max();
            publicStatus = $"공개 {state.PublicCalendar.Events.Count}건" + (latest == default ? "" : $" · 최근 {latest:MM/dd}");
            if (!store.Save(state)) publicStatus += " · 저장 실패";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            publicStatus = state.PublicCalendar is null ? "공개 이력 연결 실패" : "공개 이력 · 이전 자료";
        }
        finally
        {
            publicRefreshing = false;
            if (!closing) RenderCalendar();
        }
    }

    private void RenderCalendar()
    {
        if (!ready) return;
        MonthText.Text = month.ToString("yyyy년 M월", CultureInfo.GetCultureInfo("ko-KR"));
        PublicStatusText.Text = publicStatus;
        PublicStatusText.ToolTip = state.PublicCalendar is { } feed
            ? $"출처: OpenTheRank(제삼자 기록)\n{feed.Attribution}\n자료 생성: {(feed.GeneratedAt is { } generated ? $"{LocalTime.Convert(generated):yyyy-MM-dd HH:mm} {LocalTime.Label(generated)}" : "미제공")}\n마지막 수신: {LocalTime.Convert(feed.FetchedAt):MM/dd HH:mm}\n공개 기록이며 개인 계정 적용 여부와 다를 수 있습니다. 예고는 게시 시각을 표시합니다. 5분마다 확인합니다."
            : "공개 이력을 인터넷에서 불러옵니다. 확률 예측은 표시하지 않습니다.";
        DaysPanel.Children.Clear();
        var first = month.AddDays(-(int)month.DayOfWeek);
        var today = LocalTime.Convert(DateTimeOffset.UtcNow).Date;
        for (var i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            var events = EventsFor(day);
            var button = new Button { Style = (Style)FindResource("CalendarDayButton"), Tag = day };
            var content = new Grid { Height = 18 };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            var number = new TextBlock
            {
                Text = day.Day.ToString(CultureInfo.InvariantCulture), FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme(day.Month != month.Month ? "MutedBrush" : day.DayOfWeek == DayOfWeek.Sunday ? "CoralBrush" : "TextBrush"),
                Opacity = day.Month == month.Month ? 1 : .4
            };
            content.Children.Add(number);
            var markers = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(markers, 1);
            foreach (var color in new[] { "MintBrush", "VioletBrush", "SkyBrush", "CoralBrush" })
            {
                if (!events.Any(entry => entry.Color == color)) continue;
                var personal = color == "CoralBrush";
                markers.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = personal ? 4 : 3, Height = personal ? 4 : 3,
                    Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center,
                    Fill = personal ? Brushes.Transparent : Theme(color),
                    Stroke = personal ? Theme(color) : null, StrokeThickness = personal ? 1 : 0
                });
            }
            content.Children.Add(markers);
            button.Content = content;
            if (events.Count > 0)
            {
                button.Background = new SolidColorBrush(Color.FromRgb(49, 64, 100));
                button.BorderBrush = Theme(events.Any(e => e.Color == "CoralBrush") ? "CoralBrush" : events[0].Color);
                number.FontWeight = FontWeights.SemiBold;
            }
            if (day == selectedDate) { button.Background = new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)); button.BorderBrush = Theme("TextBrush"); }
            else if (day == today && events.Count == 0) button.BorderBrush = Theme("MintBrush");
            button.ToolTip = $"{day:yyyy-MM-dd}\n" + (events.Count == 0 ? "등록된 공개 이력·예정 없음" : string.Join("\n", events.Select(e => e.Text)));
            System.Windows.Automation.AutomationProperties.SetName(button, $"{day:yyyy년 M월 d일} {(day == selectedDate ? "선택됨 " : "")}{string.Join(" ", events.Select(e => e.Text))}");
            button.Click += CalendarDay_Click;
            DaysPanel.Children.Add(button);
        }
        SelectedDateText.Text = selectedDate.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR")) + " · 현지 시간";
        SelectedDateText.ToolTip = $"PC 시간대: {TimeZoneInfo.Local.DisplayName}";
        var selected = EventsFor(selectedDate);
        DetailText.Text = selected.Count == 0 ? "이 날짜에 등록된 공개 이력·예정이 없습니다." : string.Join("\n", selected.Select(e => e.Text));
        DetailText.ToolTip = string.Join("\n", selected.Select(e => e.Text + (string.IsNullOrWhiteSpace(e.Summary) ? "" : "\n" + e.Summary)))
            + "\n출처: OpenTheRank(제삼자 기록). 공개 기록은 개인 계정에 실제 적용된 시각과 다를 수 있습니다. 예고는 발표 게시 시각이며 적용 완료나 확정 일정이 아닙니다.";
    }

    private List<CalendarEntry> EventsFor(DateTime day)
    {
        var events = new List<CalendarEntry>();
        if (state.PublicCalendar is { } calendar)
        {
            foreach (var entry in calendar.Events.Where(e => e.Time is { } at
                         ? LocalTime.Convert(at).Date == day
                         : e.Date == DateOnly.FromDateTime(day)))
            {
                var time = entry.Time is { } at ? $"{LocalTime.Convert(at):HH:mm} {LocalTime.Label(at)}" : "시각 미제공 · UTC 날짜";
                var label = entry.IsAnnouncement ? entry.Kind == "banked" ? "리셋권 사전 예고" : "리셋 사전 예고"
                    : entry.IsScheduled ? "발표된 리셋 예정" : entry.Kind == "both" ? "리셋 적용 + 리셋권 지급 발표"
                    : entry.Kind == "banked" ? "리셋권 지급 발표" : "리셋 적용 기록";
                events.Add(new($"{time} · {label}", entry.IsAnnouncement || entry.IsScheduled ? "SkyBrush"
                    : entry.Kind is "banked" or "both" ? "VioletBrush" : "MintBrush", entry.SourceUrl, entry.Summary));
            }
        }
        if (snapshot is not null)
            events.AddRange(snapshot.Windows.Where(w => w.ResetsAt is { } at && LocalTime.Convert(at).Date == day)
                .Select(w => new CalendarEntry($"{LocalTime.Convert(w.ResetsAt!.Value):HH:mm} · 내 {w.Label} {(w.ResetsAt > DateTimeOffset.UtcNow ? "예정" : "확인 대기")}", "CoralBrush", null)));
        return events;
    }

    private void OpenSource_Click(object sender, RoutedEventArgs e)
    {
        var source = EventsFor(selectedDate).Select(e => e.Url).FirstOrDefault(u => u is not null) ?? "https://opentherank.com/ko/codex-reset/";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { SetStatus("출처 페이지를 열지 못했습니다", false); }
    }
}
