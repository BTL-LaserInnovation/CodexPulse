using System.Text.Json.Nodes;

namespace UsageRemaining.Widget.Models;

public sealed record GoogleCalendarInfo
{
    public string Id { get; init; } = "";
    public string Summary { get; init; } = "";
    public string AccessRole { get; init; } = "";
    public string TimeZone { get; init; } = "";
    public bool Primary { get; init; }
    public bool CanWrite => AccessRole is "writer" or "owner";
}

public sealed record GoogleCalendarEvent
{
    public string CalendarId { get; init; } = "";
    public string Id { get; init; } = "";
    public string ETag { get; init; } = "";
    public string ClientGeneratedId { get; init; } = Guid.NewGuid().ToString("N");
    public string Summary { get; init; } = "";
    public string Description { get; init; } = "";
    public string Location { get; init; } = "";
    public bool AllDay { get; init; }
    public bool CanWrite { get; init; }
    public bool Cancelled { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? TimeZone { get; init; }
    public string? RecurringEventId { get; init; }
    public DateOnly? OriginalStartDate { get; init; }
    public DateTimeOffset? OriginalStart { get; init; }
    public string[] Recurrence { get; init; } = [];
    public bool UseDefaultReminders { get; init; } = true;
    public int? ReminderMinutes { get; init; }
    public bool RemindersChanged { get; init; }
    public JsonObject Raw { get; init; } = new();
}

public sealed record GoogleCalendarState
{
    public bool Connected { get; init; }
    public string AccountEmail { get; init; } = "";
    public IReadOnlyList<GoogleCalendarInfo> Calendars { get; init; } = [];
    public IReadOnlyList<GoogleCalendarEvent> Events { get; init; } = [];
    public IReadOnlySet<string> SelectedCalendarIds { get; init; } = new HashSet<string>();
    public DateTimeOffset? LastSyncedAt { get; init; }
    public string? Warning { get; init; }
    public bool IsConfigured { get; init; }
    public string ConfigurationMessage { get; init; } = "";
}
