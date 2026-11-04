using System.Text.Json;

namespace UsageRemaining.Widget.Models;

public sealed record ResetCredit(string Id, string ResetType, string Status, DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt, string? Title, string? Description)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? "Codex 한도 초기화권" : Title;

    public bool IsAvailable(DateTimeOffset now) => !string.IsNullOrWhiteSpace(Id)
        && ResetType == "codexRateLimits" && Status == "available"
        && (GrantedAt is null || GrantedAt <= now) && (ExpiresAt is null || ExpiresAt > now);
}

public sealed record ResetCreditBalance(int? AvailableCount, List<ResetCredit> Credits, bool DetailsKnown)
{
    public static ResetCreditBalance Parse(JsonElement accountRateLimitsResult)
    {
        if (accountRateLimitsResult.ValueKind != JsonValueKind.Object
            || !accountRateLimitsResult.TryGetProperty("rateLimitResetCredits", out var balance)
            || balance.ValueKind != JsonValueKind.Object)
            return new(null, [], false);

        int? count = balance.TryGetProperty("availableCount", out var countField)
            && countField.ValueKind == JsonValueKind.Number && countField.TryGetInt32(out var availableCount)
            && availableCount >= 0 ? availableCount : null;
        if (!balance.TryGetProperty("credits", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new(count, [], false);

        var credits = new Dictionary<string, ResetCredit>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Text(entry, "id") is not { } id
                || string.IsNullOrWhiteSpace(id)) continue;
            var status = Text(entry, "status") ?? string.Empty;
            var expires = Timestamp(entry, "expiresAt");
            var granted = Timestamp(entry, "grantedAt");
            // A malformed explicit expiry must not turn an expired credit into a permanent one.
            if (entry.TryGetProperty("expiresAt", out var expiration)
                && expiration.ValueKind != JsonValueKind.Null && expires is null)
                status = "unknown";
            if (entry.TryGetProperty("grantedAt", out var grant)
                && grant.ValueKind != JsonValueKind.Null && granted is null)
                status = "unknown";
            var credit = new ResetCredit(id, Text(entry, "resetType") ?? string.Empty, status,
                granted, expires, Text(entry, "title"), Text(entry, "description"));
            if (!credits.TryGetValue(id, out var previous))
                credits.Add(id, credit);
            else if (previous != credit)
                // Conflicting duplicate identifiers cannot safely identify a consumable credit.
                credits[id] = previous with { Status = "unknown" };
        }

        return new(count, credits.Values.OrderBy(c => c.ExpiresAt is null).ThenBy(c => c.ExpiresAt)
            .ThenBy(c => c.Id, StringComparer.Ordinal).ToList(), true);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static DateTimeOffset? Timestamp(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Number
            || !field.TryGetDouble(out var seconds) || !double.IsFinite(seconds)) return null;
        try
        {
            return DateTimeOffset.UnixEpoch.AddSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
