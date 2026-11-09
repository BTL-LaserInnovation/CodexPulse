using System.Globalization;
using System.Text.Json.Nodes;
using UsageRemaining.Widget.Models;

namespace UsageRemaining.Widget.Services;

public sealed partial class GoogleCalendarService
{
    public static GoogleCalendarEvent ParseEvent(GoogleCalendarInfo calendar, JsonObject raw, string accountEmail = "")
    {
        var start = raw["start"] as JsonObject;
        var end = raw["end"] as JsonObject;
        var original = raw["originalStartTime"] as JsonObject;
        var organizer = raw["organizer"] as JsonObject;
        var reminders = raw["reminders"] as JsonObject;
        var organizerEmail = Text(organizer, "email");
        var organizerSelf = Flag(organizer, "self") || organizerEmail == calendar.Id ||
            (!string.IsNullOrEmpty(accountEmail) && organizerEmail.Equals(accountEmail, StringComparison.OrdinalIgnoreCase));
        var allowed = organizerSelf || Flag(raw, "guestsCanModify");
        var popup = (reminders?["overrides"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => Text(r, "method") == "popup");
        return new()
        {
            CalendarId = calendar.Id, Id = Text(raw, "id"), ETag = Text(raw, "etag"), Summary = Text(raw, "summary"),
            Description = Text(raw, "description"), Location = Text(raw, "location"),
            AllDay = !string.IsNullOrEmpty(Text(start, "date")), StartDate = Date(start, "date"), EndDate = Date(end, "date"),
            Start = Instant(start, "dateTime"), End = Instant(end, "dateTime"),
            TimeZone = OptionalText(start, "timeZone") ?? OptionalText(end, "timeZone") ?? (string.IsNullOrEmpty(calendar.TimeZone) ? null : calendar.TimeZone),
            Cancelled = Text(raw, "status") == "cancelled", CanWrite = calendar.CanWrite && allowed && Text(raw, "status") != "cancelled" && Text(raw, "eventType") is not ("birthday" or "fromGmail"),
            RecurringEventId = OptionalText(raw, "recurringEventId"), OriginalStartDate = Date(original, "date"), OriginalStart = Instant(original, "dateTime"),
            Recurrence = (raw["recurrence"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToArray() ?? [],
            UseDefaultReminders = reminders is null || Flag(reminders, "useDefault"), ReminderMinutes = popup?["minutes"]?.GetValue<int>(),
            Raw = (JsonObject)raw.DeepClone()
        };
    }

    public static void ValidateEvent(GoogleCalendarEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value.CalendarId)) throw new ArgumentException("캘린더를 선택해 주세요.");
        if (string.IsNullOrEmpty(value.Id) && (value.ClientGeneratedId.Length != 32 || value.ClientGeneratedId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new ArgumentException("새 일정 식별자가 올바르지 않습니다. 일정 창을 다시 열어 주세요.");
        if (string.IsNullOrWhiteSpace(value.Summary)) throw new ArgumentException("일정 제목을 입력해 주세요.");
        if (value.Cancelled) throw new ArgumentException("삭제된 일정은 수정할 수 없습니다.");
        if (value.AllDay)
        {
            if (value.StartDate is null || value.EndDate is null || value.EndDate <= value.StartDate)
                throw new ArgumentException("종일 일정의 종료 날짜는 시작 날짜보다 뒤여야 합니다.");
        }
        else if (value.Start is null || value.End is null || value.End <= value.Start)
            throw new ArgumentException("일정 종료 시간은 시작 시간보다 늦어야 합니다.");
        if (value.ReminderMinutes is < 0 or > 40320) throw new ArgumentException("알림은 일정 시작 0~40320분 전으로 설정해 주세요.");
        if (value.Recurrence.Any(r => string.IsNullOrWhiteSpace(r) || r.Contains('\r') || r.Contains('\n')))
            throw new ArgumentException("반복 규칙 형식을 확인해 주세요.");
        if (!string.IsNullOrWhiteSpace(value.RecurringEventId) && value.Recurrence.Length > 0)
            throw new ArgumentException("반복 일정의 개별 항목에는 전체 반복 규칙을 설정할 수 없습니다.");
    }

    public static JsonObject BuildEventPatch(GoogleCalendarEvent value, string? calendarTimeZone = null)
    {
        ValidateEvent(value);
        var patch = new JsonObject();
        var creating = string.IsNullOrEmpty(value.Id);
        var previous = value.Raw.Count > 0 ? ParseEvent(new GoogleCalendarInfo { Id = value.CalendarId, TimeZone = calendarTimeZone ?? value.TimeZone ?? "" }, value.Raw) : null;
        void Set(string name, JsonNode? content, bool changed)
        {
            if (creating || changed) patch[name] = content;
        }
        Set("summary", JsonValue.Create(value.Summary), previous?.Summary != value.Summary);
        Set("description", JsonValue.Create(value.Description), previous?.Description != value.Description);
        Set("location", JsonValue.Create(value.Location), previous?.Location != value.Location);
        var timeChanged = previous is null || previous.AllDay != value.AllDay || previous.StartDate != value.StartDate ||
            previous.EndDate != value.EndDate || previous.Start != value.Start || previous.End != value.End || previous.TimeZone != value.TimeZone;
        if (creating || timeChanged)
        {
            if (value.AllDay)
            {
                patch["start"] = new JsonObject { ["date"] = value.StartDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
                patch["end"] = new JsonObject { ["date"] = value.EndDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            }
            else
            {
                var start = new JsonObject { ["dateTime"] = value.Start!.Value.ToString("o", CultureInfo.InvariantCulture) };
                var end = new JsonObject { ["dateTime"] = value.End!.Value.ToString("o", CultureInfo.InvariantCulture) };
                if (!string.IsNullOrWhiteSpace(value.TimeZone)) { start["timeZone"] = value.TimeZone; end["timeZone"] = value.TimeZone; }
                patch["start"] = start;
                patch["end"] = end;
            }
        }
        if (creating || previous is null || !previous.Recurrence.SequenceEqual(value.Recurrence))
        {
            // Instance edits leave recurringEventId/originalStartTime untouched; empty recurrence removes a series rule.
            if (value.Recurrence.Length > 0 || !creating) patch["recurrence"] = new JsonArray(value.Recurrence.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        }
        if (creating || previous is null || value.RemindersChanged || previous.UseDefaultReminders != value.UseDefaultReminders || previous.ReminderMinutes != value.ReminderMinutes)
        {
            var reminders = new JsonObject { ["useDefault"] = value.UseDefaultReminders };
            if (!value.UseDefaultReminders)
                reminders["overrides"] = value.ReminderMinutes is int minutes
                    ? new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = minutes }) : new JsonArray();
            patch["reminders"] = reminders;
        }
        return patch;
    }

    private static IReadOnlyList<GoogleCalendarEvent> VisibleEvents(Dictionary<string, CalendarCache> cache, DateTimeOffset start, DateTimeOffset end) =>
        cache.Values.SelectMany(c => c.Events.Values).Where(e => !e.Cancelled && Overlaps(e, start, end))
            .OrderBy(e => e.AllDay ? e.StartDate?.ToDateTime(TimeOnly.MinValue) : e.Start?.LocalDateTime)
            .ThenBy(e => e.Summary, StringComparer.CurrentCulture).ToArray();

    private static bool Overlaps(GoogleCalendarEvent value, DateTimeOffset start, DateTimeOffset end)
    {
        if (value.AllDay)
        {
            // Day values belong to the calendar's zone, not the computer's current zone.
            TimeZoneInfo zone;
            try { zone = string.IsNullOrEmpty(value.TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(value.TimeZone); }
            catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Local; }
            catch (InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, zone).DateTime);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(end.AddTicks(-1), zone).DateTime);
            return value.StartDate <= lastDay && value.EndDate > firstDay;
        }
        return value.Start < end && value.End > start;
    }

    private void UpdateCachedEvent(GoogleCalendarEvent value)
    {
        if (_cache.TryGetValue(value.CalendarId, out var cache))
        {
            cache.Events[value.Id] = value;
            Publish(_state with { Events = VisibleEvents(_cache, cache.Start, cache.End), Warning = null });
        }
        else Publish(_state with { Warning = null });
    }

    private static string Text(JsonObject? node, string property) => node?[property]?.GetValue<string>() ?? "";
    private static string? OptionalText(JsonObject? node, string property) => node?[property]?.GetValue<string>();
    private static bool Flag(JsonObject? node, string property) => node?[property]?.GetValue<bool>() == true;
    private static DateOnly? Date(JsonObject? node, string property) => DateOnly.TryParseExact(Text(node, property), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;
    private static DateTimeOffset? Instant(JsonObject? node, string property) => DateTimeOffset.TryParse(Text(node, property), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
