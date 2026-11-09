using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Widget;

public partial class MainWindow
{
    private bool CanDeleteGoogleEvent(GoogleCalendarEvent value) => value.CanWrite && !value.Cancelled
        && !string.IsNullOrWhiteSpace(value.Id) && !string.IsNullOrWhiteSpace(value.ETag)
        && (value.RecurringEventId is not null || value.Recurrence.Length == 0)
        && !GoogleHolidayCalendar.IsHolidayCalendar(value.CalendarId)
        && googleCalendar.State.Calendars.Any(calendar => calendar.Id == value.CalendarId && calendar.CanWrite);

    private async Task DeleteGoogleEventAsync(GoogleCalendarEvent value)
    {
        if (closing || updateApplying || googleDeleting || googleRefreshing || googleConnecting || googleDialog is not null
            || !googleCalendar.State.Connected || !CanDeleteGoogleEvent(value)) return;
        var account = googleCalendar.State.AccountEmail;
        var key = CalendarEventPresentation.Key(value);
        googleDeleting = true;
        RenderGoogleConnection();
        RenderCalendar();
        try
        {
            var title = string.IsNullOrWhiteSpace(value.Summary) ? "(제목 없음)" : value.Summary;
            var period = value.AllDay && CalendarEventPresentation.Dates(value) is { } range
                ? $"{range.Start:yyyy-MM-dd} ~ {range.EndExclusive.AddDays(-1):yyyy-MM-dd} · 종일"
                : value.Start is { } start && value.End is { } end
                    ? $"{LocalTime.Convert(start):yyyy-MM-dd HH:mm} ~ {LocalTime.Convert(end):yyyy-MM-dd HH:mm} {LocalTime.Label(start)}"
                    : "시각 미제공";
            var scope = value.RecurringEventId is not null ? "반복 일정 중 이 일정만 삭제합니다.\n" : "";
            var confirm = new GoogleActionConfirmDialog(this, "이 일정을 삭제할까요?",
                $"{title}\n{period}\n{scope}Google 캘린더에서도 삭제됩니다.", "삭제") { Topmost = Topmost };
            googleDialog = confirm;
            bool confirmed;
            try { confirmed = confirm.ShowDialog() == true; }
            finally { googleDialog = null; }
            if (!confirmed || closing || lifetime.IsCancellationRequested) return;
            if (!googleCalendar.State.Connected || googleCalendar.State.AccountEmail != account)
                throw new InvalidOperationException("Google 계정이 변경되었습니다. 일정을 다시 선택하세요.");
            var current = googleCalendar.State.Events.FirstOrDefault(item => CalendarEventPresentation.Key(item) == key);
            if (current is null || current.ETag != value.ETag || !CanDeleteGoogleEvent(current))
                throw new InvalidOperationException("일정 또는 삭제 권한이 변경되었습니다. 새로 고침 후 다시 선택하세요.");
            // Keep the selected instance ID and ETag; whole-series deletion stays in the editor.
            await googleCalendar.DeleteAsync(value, lifetime.Token);
            if (selectedGoogleEvent == key) selectedGoogleEvent = null;
            googleError = null;
        }
        catch (OperationCanceledException) when (closing || lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            googleError = "일정 삭제 실패: " + ex.Message;
            nextGoogleRefresh = Environment.TickCount64 + 60_000;
        }
        finally
        {
            googleDeleting = false;
            if (!closing) { RenderGoogleConnection(); RenderCalendar(); }
        }
    }
}
