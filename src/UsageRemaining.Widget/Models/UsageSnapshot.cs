using System.Text.Json;

namespace UsageRemaining.Widget.Models;

public sealed record RateWindow(string Key, double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string CompactLabel => DurationMinutes switch
    {
        10080 => "주간",
        1440 => "일일",
        >= 60 when DurationMinutes % 60 == 0 => $"{DurationMinutes / 60}시간",
        > 0 => $"{DurationMinutes}분",
        _ => "사용 한도"
    };
    public string Label => DurationMinutes switch
    {
        10080 => "주간 한도",
        1440 => "일일 한도",
        >= 60 when DurationMinutes % 60 == 0 => $"{DurationMinutes / 60}시간 한도",
        > 0 => $"{DurationMinutes}분 한도",
        _ => "사용 한도"
    };
}

public sealed record UsageSnapshot(DateTimeOffset CapturedAt, List<RateWindow> Windows);

public static class UsageParser
{
    public static UsageSnapshot Parse(JsonElement result, DateTimeOffset capturedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) return new(capturedAt, []);
        JsonElement bucket;
        if (result.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex) &&
            codex.ValueKind == JsonValueKind.Object)
            bucket = codex;
        else if (result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object &&
                 (!legacy.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null ||
                  (id.ValueKind == JsonValueKind.String && id.GetString() == "codex")))
            bucket = legacy;
        else
            return new(capturedAt, []);

        var windows = new List<RateWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!bucket.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object ||
                !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percent) ||
                !double.IsFinite(percent))
                continue;
            int? duration = window.TryGetProperty("windowDurationMins", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var mins) && mins > 0 ? mins : null;
            DateTimeOffset? reset = null;
            if (window.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var unix))
            {
                try
                {
                    reset = DateTimeOffset.FromUnixTimeSeconds(unix);
                }
                catch (ArgumentOutOfRangeException) { /* An invalid provider timestamp is displayed as unknown. */ }
            }
            windows.Add(new(key, Math.Clamp(percent, 0, 100), duration, reset));
        }
        return new(capturedAt, windows);
    }
}
