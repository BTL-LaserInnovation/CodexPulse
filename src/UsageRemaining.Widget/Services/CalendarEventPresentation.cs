using System.Globalization;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public static class CalendarEventPresentation
{
    public static (DateOnly Start, DateOnly EndExclusive)? Dates(GoogleCalendarEvent value)
    {
        if (value.Cancelled) return null;
        if (value.AllDay)
            return value.StartDate is { } first && value.EndDate is { } end && end > first ? (first, end) : null;
        if (value.Start is not { } start || value.End is not { } finish || finish <= start) return null;
        var localStart = DateOnly.FromDateTime(LocalTime.Convert(start).DateTime);
        var localLast = DateOnly.FromDateTime(LocalTime.Convert(finish.AddTicks(-1)).DateTime);
        return localLast == DateOnly.MaxValue ? null : (localStart, localLast.AddDays(1));
    }

    public static bool OccursOn(GoogleCalendarEvent value, DateOnly date) =>
        Dates(value) is { } range && range.Start <= date && date < range.EndExclusive;

    public static string TimeLabel(GoogleCalendarEvent value)
    {
        var dates = Dates(value);
        if (dates is null) return "시각 미제공";
        if (dates.Value.EndExclusive.DayNumber - dates.Value.Start.DayNumber > 1)
            return $"{dates.Value.Start:M/d}–{dates.Value.EndExclusive.AddDays(-1):M/d}";
        if (value.AllDay) return "종일";
        return $"{LocalTime.Convert(value.Start!.Value):HH:mm}–{LocalTime.Convert(value.End!.Value):HH:mm}";
    }

    public static string Description(GoogleCalendarEvent value)
    {
        var dates = Dates(value);
        var period = value.AllDay && dates is { } range
            ? $"{range.Start:yyyy-MM-dd} ~ {range.EndExclusive.AddDays(-1):yyyy-MM-dd} · 종일"
            : value.Start is { } start && value.End is { } end
                ? $"{LocalTime.Convert(start):yyyy-MM-dd HH:mm} ~ {LocalTime.Convert(end):yyyy-MM-dd HH:mm} {LocalTime.Label(start)}"
                : "시각 미제공";
        return string.Join("\n", new[] { value.Summary, period, value.Location,
            value.CanWrite ? "목록에서 눌러 편집" : "읽기 전용 일정" }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    public static DateTimeOffset LocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A few time zones jump at midnight. Use the first existing instant of that date.
        while (TimeZoneInfo.Local.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public static string Key(GoogleCalendarEvent value) => value.CalendarId + "\n" + value.Id;
}
