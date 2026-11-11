using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public interface IGoogleCalendarService : IDisposable
{
    GoogleCalendarState State { get; }
    event EventHandler? Changed;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default);
    Task RefreshAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default);
    Task<GoogleCalendarEvent> SaveAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default);
    Task DeleteAsync(GoogleCalendarEvent value, CancellationToken cancellationToken = default);
    Task<GoogleCalendarEvent> GetEventAsync(string calendarId, string eventId, CancellationToken cancellationToken = default);
    Task SelectCalendarsAsync(IEnumerable<string> calendarIds, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class GoogleCalendarConflictException : Exception
{
    public GoogleCalendarConflictException() : base("Google 일정이 다른 곳에서 변경되었습니다. 최신 일정을 다시 불러온 후 수정해 주세요.") { }
}

public class GoogleCalendarException : Exception
{
    public GoogleCalendarException(string message) : base(message) { }
}

internal sealed class GoogleCalendarConnectionTimeoutException : GoogleCalendarException
{
    public GoogleCalendarConnectionTimeoutException() : base("Google 로그인 시간이 초과되었습니다. 다시 연결해 주세요.") { }
}

internal sealed class GoogleCalendarCredentialRestoreException : GoogleCalendarException
{
    public GoogleCalendarCredentialRestoreException() : base("취소한 Google 연결의 저장 정보를 복구하지 못했습니다. 앱에서 Google 연결을 다시 확인해 주세요.") { }
}
