using System.Text.Json.Nodes;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static partial class GoogleCalendarChecks
{
    private static void HolidayMapping(Action<bool, string> check)
    {
        const string mixed = "ko.south_korea#holiday@group.v.calendar.google.com";
        const string official = "en.south_korea.official#holiday@group.v.calendar.google.com";
        var calendar = new GoogleCalendarInfo { Id = mixed, Summary = "대한민국의 휴일", AccessRole = "reader" };
        GoogleCalendarEvent Parse(string description, string id = mixed, bool allDay = true) =>
            GoogleCalendarService.ParseEvent(calendar with { Id = id }, new JsonObject
            {
                ["id"] = "holiday-fixture", ["summary"] = "Synthetic holiday fixture", ["description"] = description,
                ["start"] = allDay ? new JsonObject { ["date"] = "2026-11-09" } : new JsonObject { ["dateTime"] = "2026-11-09T12:00:00+09:00" },
                ["end"] = allDay ? new JsonObject { ["date"] = "2026-11-12" } : new JsonObject { ["dateTime"] = "2026-11-09T13:00:00+09:00" }
            });
        check(new[] { mixed, official, "en.usa#holiday@group.v.calendar.google.com" }.All(GoogleHolidayCalendar.IsHolidayCalendar) &&
            new[] { "holiday", "personal@example.invalid", mixed + ".evil", "#holiday@group.v.calendar.google.com", "en..usa#holiday@group.v.calendar.google.com" }
                .All(id => !GoogleHolidayCalendar.IsHolidayCalendar(id)),
            "Google holiday source identity accepts supported sources and rejects ordinary or spoofed IDs");
        foreach (var description in new[] { "공휴일", "Public holiday", "Public holiday. To hide observances, open settings.", "<p>공휴일</p><p>설명</p>", "Public holiday (regional)" })
            check(GoogleHolidayCalendar.IsPublicHoliday(Parse(description)), "Google parsed public holiday category: " + description);
        foreach (var description in new[] { "기념일\n기념일을 숨기려면 Google Calendar 설정으로 이동하세요.", "기타 기념일", "Observance", "Other observance", "Observances", "Other observances" })
            check(!GoogleHolidayCalendar.IsPublicHoliday(Parse(description)) && !GoogleHolidayCalendar.IsPublicHoliday(Parse(description, official)),
                "Google observance category never becomes a public holiday: " + description.Split('\n')[0]);
        check(!GoogleHolidayCalendar.IsPublicHoliday(Parse("")) && GoogleHolidayCalendar.IsPublicHoliday(Parse("", official)),
            "Google unlabeled mixed holiday source stays neutral while public-only official source is inferred");
        check(!GoogleHolidayCalendar.IsPublicHoliday(Parse("공휴일", "readonly-calendar")) &&
            !GoogleHolidayCalendar.IsPublicHoliday(Parse("Public holiday", allDay: false)) &&
            !GoogleHolidayCalendar.IsPublicHoliday(Parse("Public holiday") with { Cancelled = true }),
            "Google personal read-only all-day events timed events and cancelled holidays do not turn dates red");
        var span = Parse("공휴일");
        check(!span.CanWrite && span.Description == "공휴일" &&
            CalendarEventPresentation.OccursOn(span, new(2026, 11, 11)) && !CalendarEventPresentation.OccursOn(span, new(2026, 11, 12)),
            "Google holiday parser preserves category read-only rights and exclusive multi-day end");
    }
}
