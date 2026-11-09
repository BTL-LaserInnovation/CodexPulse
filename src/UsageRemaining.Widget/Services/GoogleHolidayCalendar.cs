using System.Net;
using System.Text.RegularExpressions;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public static class GoogleHolidayCalendar
{
    private const string HolidaySuffix = "#holiday@group.v.calendar.google.com";

    public static bool IsHolidayCalendar(GoogleCalendarInfo calendar) => IsHolidayCalendar(calendar.Id);

    public static bool IsHolidayCalendar(string? calendarId)
    {
        if (string.IsNullOrEmpty(calendarId) || !calendarId.EndsWith(HolidaySuffix, StringComparison.OrdinalIgnoreCase)) return false;
        var source = calendarId[..^HolidaySuffix.Length];
        // Google holiday sources use a locale and region, such as ko.south_korea or en.usa.official.
        var parts = source.Split('.');
        return parts.Length >= 2 && parts.All(part => part.Length > 0 &&
            part.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'));
    }

    public static bool IsPublicHoliday(GoogleCalendarEvent value)
    {
        if (!IsHolidayCalendar(value.CalendarId) || !value.AllDay || value.Cancelled) return false;
        var category = DescriptionCategory(value.Description);
        // Google's public Korean holiday feed labels these 공휴일 and 기념일 followed by explanatory text.
        // Inspect the leading category, since public-holiday boilerplate can itself mention observances.
        if (IsCategory(category, "Observance") || IsCategory(category, "Observances") ||
            IsCategory(category, "Other observance") || IsCategory(category, "Other observances") ||
            IsCategory(category, "기념일") || IsCategory(category, "기타 기념일")) return false;
        if (IsCategory(category, "Public holiday") || IsCategory(category, "공휴일")) return true;

        // The public-only .official source is a source convention, not an Events API eventType.
        // Mixed holiday calendars without an explicit supported category stay unclassified.
        return value.CalendarId[..^HolidaySuffix.Length].EndsWith(".official", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescriptionCategory(string description)
    {
        // Calendar descriptions may contain HTML paragraphs or line breaks as well as plain text.
        var plain = Regex.Replace(description, @"<br\s*/?>|</(?:p|div)\s*>", "\n", RegexOptions.IgnoreCase);
        plain = WebUtility.HtmlDecode(Regex.Replace(plain, @"<[^>]*>", ""));
        return plain.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
    }

    private static bool IsCategory(string category, string label) =>
        category.Equals(label, StringComparison.OrdinalIgnoreCase) ||
        category.StartsWith(label + ".", StringComparison.OrdinalIgnoreCase) ||
        category.StartsWith(label + ":", StringComparison.OrdinalIgnoreCase) ||
        category.StartsWith(label + " (", StringComparison.OrdinalIgnoreCase);
}
