using System.Globalization;

namespace UsageRemaining.Widget.Services;

public static class LocalTime
{
    public static DateTimeOffset Convert(DateTimeOffset instant, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Local);

    public static string Label(DateTimeOffset instant, TimeZoneInfo? zone = null)
    {
        var offset = (zone ?? TimeZoneInfo.Local).GetUtcOffset(instant);
        if (offset == TimeSpan.Zero) return "UTC";
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }
}
