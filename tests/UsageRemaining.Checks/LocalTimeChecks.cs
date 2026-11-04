using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static class LocalTimeChecks
{
    public static void Run(Action<bool, string> check)
    {
        var korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var reset = new DateTimeOffset(2026, 11, 4, 3, 0, 0, TimeSpan.Zero);
        var now = reset.AddMinutes(-37);
        foreach (var zone in new[] { korea, pacific, eastern })
        {
            var localReset = LocalTime.Convert(reset, zone);
            check(localReset.UtcDateTime == reset.UtcDateTime, zone.Id + " preserves absolute reset instant");
            check(localReset - LocalTime.Convert(now, zone) == TimeSpan.FromMinutes(37), zone.Id + " preserves countdown duration");
        }
        check(LocalTime.Convert(reset, korea).DateTime == new DateTime(2026, 11, 4, 12, 0, 0) &&
            LocalTime.Convert(reset, pacific).DateTime == new DateTime(2026, 11, 3, 19, 0, 0) &&
            LocalTime.Convert(reset, eastern).DateTime == new DateTime(2026, 11, 3, 22, 0, 0),
            "same reset appears on previous US calendar date");

        var springBefore = new DateTimeOffset(2026, 3, 8, 9, 59, 59, TimeSpan.Zero);
        var springAfter = springBefore.AddSeconds(1);
        check(LocalTime.Convert(springBefore, pacific).DateTime == new DateTime(2026, 3, 8, 1, 59, 59) &&
            LocalTime.Convert(springAfter, pacific).DateTime == new DateTime(2026, 3, 8, 3, 0, 0),
            "Pacific spring transition skips nonexistent 02 hour");
        check(LocalTime.Label(springBefore, pacific) == "UTC-08:00" && LocalTime.Label(springAfter, pacific) == "UTC-07:00" &&
            LocalTime.Convert(springAfter, pacific) - LocalTime.Convert(springBefore, pacific) == TimeSpan.FromSeconds(1),
            "spring offset change preserves one elapsed second");
        var easternSpring = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero);
        check(LocalTime.Convert(easternSpring.AddSeconds(-1), eastern).Hour == 1 && LocalTime.Convert(easternSpring, eastern).Hour == 3 &&
            LocalTime.Label(easternSpring, eastern) == "UTC-04:00", "Eastern spring transition uses its own UTC boundary");

        var fallFirst = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.Zero);
        var fallSecond = fallFirst.AddHours(1);
        var first = LocalTime.Convert(fallFirst, pacific);
        var second = LocalTime.Convert(fallSecond, pacific);
        check(first.DateTime == new DateTime(2026, 11, 1, 1, 30, 0) && second.DateTime == first.DateTime && first != second &&
            first.Offset == TimeSpan.FromHours(-7) && second.Offset == TimeSpan.FromHours(-8),
            "Pacific repeated fall hour retains distinct instants and offsets");
        check(LocalTime.Label(fallFirst, pacific) == "UTC-07:00" && LocalTime.Label(fallSecond, pacific) == "UTC-08:00" &&
            second - first == TimeSpan.FromHours(1), "fall repeated hour labels and elapsed time remain distinct");
        var easternFall = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        check(LocalTime.Convert(easternFall, eastern).DateTime == LocalTime.Convert(easternFall.AddHours(1), eastern).DateTime &&
            LocalTime.Label(easternFall, eastern) == "UTC-04:00" && LocalTime.Label(easternFall.AddHours(1), eastern) == "UTC-05:00",
            "Eastern repeated fall hour retains both offsets");

        var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        check(LocalTime.Label(winter, pacific) == "UTC-08:00" && LocalTime.Label(summer, pacific) == "UTC-07:00" &&
            LocalTime.Label(winter, eastern) == "UTC-05:00" && LocalTime.Label(summer, eastern) == "UTC-04:00",
            "labels use each target instant's seasonal offset");
        check(LocalTime.Label(winter, korea) == "UTC+09:00" && LocalTime.Label(summer, korea) == "UTC+09:00" &&
            LocalTime.Label(summer, TimeZoneInfo.Utc) == "UTC", "Korea and UTC labels have stable offsets");

        var observations = new[]
        {
            new ResetObservation("primary", "quota", reset, reset.AddHours(5)),
            new ResetObservation("primary", "quota", reset.AddHours(6), reset.AddHours(11))
        };
        var pacificDays = observations.GroupBy(r => LocalTime.Convert(r.ObservedAt, pacific).Date).OrderBy(g => g.Key).ToList();
        var koreaDays = observations.GroupBy(r => LocalTime.Convert(r.ObservedAt, korea).Date).ToList();
        check(pacificDays.Count == 2 && pacificDays[0].Key == new DateTime(2026, 11, 3) && pacificDays[1].Key == new DateTime(2026, 11, 4) &&
            koreaDays.Count == 1 && koreaDays[0].Key == new DateTime(2026, 11, 4), "history calendar groups observed instants by selected local date");

        check(LocalTime.Convert(DateTimeOffset.MaxValue, korea).EqualsExact(DateTimeOffset.MaxValue),
            "positive-zone maximum conversion safely clamps to DateTimeOffset maximum");
        check(LocalTime.Convert(DateTimeOffset.MinValue, pacific).EqualsExact(DateTimeOffset.MinValue),
            "negative-zone minimum conversion safely clamps to DateTimeOffset minimum");
        check(LocalTime.Convert(DateTimeOffset.MinValue, korea).UtcDateTime == DateTimeOffset.MinValue.UtcDateTime &&
            LocalTime.Convert(DateTimeOffset.MaxValue, pacific).UtcDateTime == DateTimeOffset.MaxValue.UtcDateTime,
            "opposite range endpoints preserve representable instants");
        var local = TimeZoneInfo.Local;
        check(LocalTime.Convert(reset).EqualsExact(TimeZoneInfo.ConvertTime(reset, local)) &&
            LocalTime.Label(reset) == LocalTime.Label(reset, local), "default helpers use current PC local timezone");
    }
}
