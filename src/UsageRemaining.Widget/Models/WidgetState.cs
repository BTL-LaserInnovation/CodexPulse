namespace UsageRemaining.Widget.Models;

public sealed class WidgetState
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool IsCollapsed { get; set; }
    public bool IsCalendarCollapsed { get; set; }
    public PublicResetCalendar? PublicCalendar { get; set; }
    public string? CodexExecutable { get; set; }
    public string? ActiveAccount { get; set; }
    public Dictionary<string, AccountHistory> Accounts { get; set; } = [];
    public Dictionary<string, ResetUseOperation> PendingResetUses { get; set; } = [];
}

public sealed record ResetUseOperation(string CreditId, string IdempotencyKey);

public sealed class AccountHistory
{
    public string? Email { get; set; }
    public string? Plan { get; set; }
    public UsageSnapshot? LastSnapshot { get; set; }
    public List<ResetObservation> Resets { get; set; } = [];

    public void Observe(UsageSnapshot next)
    {
        if (LastSnapshot is { } previous)
        {
            foreach (var current in next.Windows)
            {
                var old = previous.Windows.Find(w => w.Key == current.Key);
                // A changed schedule alone is not evidence of a reset. Record the time we observed a decrease.
                if (old is not null && old.DurationMinutes == current.DurationMinutes &&
                    old.UsedPercent - current.UsedPercent >= 1 && old.ResetsAt is not null &&
                    current.ResetsAt is not null && current.ResetsAt != old.ResetsAt &&
                    !Resets.Any(e => e.WindowKey == current.Key && e.NewResetAt == current.ResetsAt))
                    Resets.Add(new(current.Key, current.Label, next.CapturedAt, current.ResetsAt.Value));
            }
        }
        Resets = Resets.OrderByDescending(r => r.ObservedAt).Take(300).ToList();
        LastSnapshot = next;
    }
}

public sealed record ResetObservation(string WindowKey, string Label, DateTimeOffset ObservedAt, DateTimeOffset NewResetAt);
