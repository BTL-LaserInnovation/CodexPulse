using System.IO;
using UsageRemaining.Widget.Models;
using UsageRemaining.Widget.Services;

namespace UsageRemaining.Checks;

internal static class OpenTheRankChecks
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    // Selected data and quotations from the fetched Korean tracker, 2026-11-04.
    // Presentation-only wrappers have been removed; the event attributes are unchanged.
    private const string Excerpt = """
        <!doctype html><html><head>
        <title>Codex 오늘 초기화될까요? Tibo의 Codex 리셋 트래커 &amp; 캘린더</title>
        <link rel="canonical" href="https://opentherank.com/ko/codex-reset/">
        </head><body><main class="cx-cal">
        <a class="cx-stamp" href="#r-1968163721034994139" data-id="1968163721034994139" data-at="2025-09-17T04:02:52.000Z" data-type="regular"></a>
        <a class="cx-stamp" href="#r-2067399435009622521" data-id="2067399435009622521" data-at="2026-06-18T00:10:10.000Z" data-type="both"></a>
        <span class="cx-stamp" data-id="observed-20260929T1845Z" data-at="2026-09-29T18:45:00.000Z" data-type="banked"></span>
        <a class="cx-stamp" href="#r-2116692529970311254" data-id="2116692529970311254" data-at="2026-11-01T00:43:20.047Z" data-type="banked"></a>
        <a class="cx-stamp" href="#r-2117035794678546764" data-id="2117035794678546764" data-at="2026-11-01T23:27:20.731Z" data-type="banked"></a>
        </main>
        <article class="cx-post" id="r-2117035794678546764" data-type="banked">
        <time class="cx-post-t" datetime="2026-11-01T23:27:20.731Z">2026년 11월 1일 (일) 23:27 UTC</time>
        <blockquote class="cx-quote" lang="en" cite="https://x.com/thsottiaux/status/2117035794678546764"><p>Day 28/ Banked 2/2</p></blockquote>
        </article>
        <article class="cx-post" id="r-2116692529970311254" data-type="banked">
        <time class="cx-post-t" datetime="2026-11-01T00:43:20.047Z">2026년 11월 1일 (일) 00:43 UTC</time>
        <blockquote class="cx-quote" lang="en" cite="https://x.com/thsottiaux/status/2116692529970311254"><p>Banked 1/2</p></blockquote>
        </article>
        <article class="cx-post" id="r-2116405414921384099" data-type="banked" data-status="announced">
        <time class="cx-post-t" datetime="2026-10-31T05:42:26.485Z">2026년 10월 31일 (토) 05:42 UTC</time>
        <blockquote class="cx-quote" lang="en" cite="https://x.com/thsottiaux/status/2116405414921384099"><p>Day 26/ You can now easily switch between multiple accounts on mobile, and coming soon to desktop too. Next two days will be resets… banked… do with this what you may 👀.</p></blockquote>
        </article>
        <article class="cx-post" id="r-2067399435009622521" data-type="both">
        <time class="cx-post-t" datetime="2026-06-18T00:10:10.000Z">2026년 6월 18일 (목) 00:10 UTC</time>
        <blockquote class="cx-quote" lang="en" cite="https://x.com/thsottiaux/status/2067399435009622521"><p>Dearest gentle codexer.</p><p>We did a sneaky double reset. Not only do you get a full reset on us. But you are also getting one into the reset bank to use at your own leisure.</p><p>Enjoy</p></blockquote>
        </article>
        <article class="cx-post" id="r-1968163721034994139" data-type="regular">
        <time class="cx-post-t" datetime="2025-09-17T04:02:52.000Z">2025년 9월 17일 (수) 04:02 UTC</time>
        <blockquote class="cx-quote" lang="en" cite="https://x.com/thsottiaux/status/1968163721034994139"><p>We have reset everyone&#39;s limits for gpt-5-codex to allow you all to benefit more from it today and as a way to make up for the slowdowns earlier while we were provisioning additional GPUs. We&#39;ll continue to add capacity throughout the week to maintain the speed.</p><p>Enjoy!</p></blockquote>
        </article>
        </body></html>
        """;

    public static void Run(Action<bool, string> check)
    {
        var calendar = PublicResetCalendar.Parse(Excerpt, Now.ToOffset(TimeSpan.FromHours(9)));
        check(calendar.Events.Count == 6 && calendar.Events.Count(e => e.IsAnnouncement) == 1,
            "OpenTheRank excerpt yields five delivered stamps and separate announcement");
        check(calendar.Source == PublicResetCalendar.ProviderId
            && PublicResetCalendar.PublicSource == "https://opentherank.com/ko/codex-reset/"
            && calendar.Attribution.Contains("OpenTheRank") && calendar.GeneratedAt is null
            && calendar.FetchedAt.EqualsExact(Now),
            "OpenTheRank provenance distinguishes fetched UTC time from unknown generation time");
        check(calendar.Events.Count(e => e.IsDelivered) == 5 && calendar.Events.All(e => !e.IsScheduled),
            "tracker prose never creates an exact future reset schedule");
        var first = calendar.Events.Single(e => e.SourceUrl?.EndsWith("/2116692529970311254") == true);
        var second = calendar.Events.Single(e => e.SourceUrl?.EndsWith("/2117035794678546764") == true);
        check(first.Time == DateTimeOffset.Parse("2026-11-01T00:43:20.047Z")
            && second.Time == DateTimeOffset.Parse("2026-11-01T23:27:20.731Z")
            && first.Date == new DateOnly(2026, 11, 1) && second.Date == first.Date
            && first.Summary.Contains("Banked 1/2") && second.Summary.Contains("Banked 2/2"),
            "multiple banked grants on one UTC date retain exact distinct instants and source posts");
        var both = calendar.Events.Single(e => e.Kind == "both");
        check(both.IsDelivered && both.Summary.Contains("reset bank") && both.Summary.Contains("Enjoy"),
            "combined regular and banked reset remains one enriched event");
        check(calendar.Events.Single(e => e.Kind == "regular").Summary.Contains("everyone's limits")
            && !calendar.Events.Single(e => e.Kind == "regular").Summary.Contains("&#39;"),
            "post quotations decode HTML entities");
        var observed = calendar.Events.Single(e => e.Time == DateTimeOffset.Parse("2026-09-29T18:45:00Z"));
        check(observed.IsDelivered && observed.Kind == "banked"
            && observed.SourceUrl == PublicResetCalendar.PublicSource + "#cal-2026-09",
            "span stamps without linked articles retain observed reset history");
        var announced = calendar.Events.Single(e => e.IsAnnouncement);
        check(!announced.IsScheduled && !announced.IsDelivered
            && announced.Time == DateTimeOffset.Parse("2026-10-31T05:42:26.485Z")
            && announced.SourceUrl?.EndsWith("/2116405414921384099") == true,
            "announcement time is publication time and is never confirmed delivery");
        check(calendar.Events.Select(e => e.Time).SequenceEqual(calendar.Events.OrderByDescending(e => e.Time).Select(e => e.Time)),
            "public history sorts newest exact instant first");
        LocalCalendarChecks(check, first.Time!.Value, second.Time!.Value);

        var variants = PublicResetCalendar.Parse(Excerpt.Replace("class=\"cx-stamp\"", "class='decorated cx-stamp selected'")
            .Replace("class=\"cx-post\"", "class='extra cx-post'"), Now);
        check(variants.Events.Count == calendar.Events.Count,
            "HTML single quotes and class tokens preserve provider events");
        var withFuture = PublicResetCalendar.Parse(Excerpt.Replace("</main>", """
            <span class="cx-stamp" data-id="future" data-at="2026-11-05T00:00:00Z" data-type="banked"></span></main>
            """), Now);
        check(withFuture.Events.Count == calendar.Events.Count, "future stamp is excluded from elapsed history");
        var duplicate = PublicResetCalendar.Parse(Excerpt.Replace("</main>", """
            <span class="cx-stamp" data-id="2117035794678546764" data-at="2026-11-01T23:27:20.731Z" data-type="banked"></span></main>
            """), Now);
        check(duplicate.Events.Count == calendar.Events.Count, "identical stamp identifiers collapse duplicate provider renderings");
        var hidden = PublicResetCalendar.Parse(Excerpt.Replace("</main>", """
            <!-- <span class="cx-stamp" data-id="comment" data-at="bad" data-type="banked"></span> -->
            <script>const sample = '<span class="cx-stamp" data-id="script" data-at="bad" data-type="banked"></span>';</script>
            </main>
            """), Now);
        check(hidden.Events.Count == calendar.Events.Count, "comments and script examples never become provider events");
        var utcOffset = PublicResetCalendar.Parse(Excerpt.Replace("2026-11-01T00:43:20.047Z", "2026-11-01T00:43:20.047+00:00"), Now);
        check(utcOffset.Events.Single(e => e.SourceUrl == first.SourceUrl).Time == first.Time,
            "explicit zero UTC offset preserves provider event instant");
        var olderAnnouncement = PublicResetCalendar.Parse(Excerpt, Now.AddYears(1)).Events.Single(e => e.IsAnnouncement);
        check(!olderAnnouncement.IsDelivered && !olderAnnouncement.IsScheduled && olderAnnouncement.Time == announced.Time,
            "aging an announcement never fabricates a delivered reset");
        var unsafeCitation = PublicResetCalendar.Parse(Excerpt.Replace("https://x.com/thsottiaux/status/2116692529970311254", "javascript:alert(1)"), Now);
        check(unsafeCitation.Events.All(e => e.SourceUrl is null || Uri.TryCreate(e.SourceUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)),
            "untrusted post citations cannot create executable source links");
        foreach (var (html, name) in new[]
        {
            ("", "empty response"),
            ("<html><title>Just a moment...</title><body>Verify you are human</body></html>", "HTTP 200 challenge"),
            ("<html><body>OpenTheRank pricing index</body></html>", "unrelated provider page"),
            ("<main class='cx-cal'></main>", "calendar without observed events"),
            ("<main class='cx-cal'><span class='cx-stamp' data-id='bad' data-at='not-a-date' data-type='banked'></span></main>", "invalid UTC event time"),
            ("<main class='cx-cal'><span class='cx-stamp' data-id='bad' data-at='2026-11-01' data-type='banked'></span></main>", "date-only event time"),
            ("<main class='cx-cal'><span class='cx-stamp-extra' data-id='bad' data-at='2026-11-01T00:00:00Z' data-type='banked'></span></main>", "class-name prefix collision"),
            (Excerpt.Replace("</main>", "<span class='cx-stamp' data-id='2117035794678546764' data-at='2026-11-01T23:28:20.731Z' data-type='banked'></span></main>"), "conflicting stamp identifier"),
            (Excerpt.Replace("</blockquote>", string.Empty).ReplaceLineEndings("\n"), "truncated quotation markup with LF line endings"),
            (Excerpt.Replace("</blockquote>", string.Empty).ReplaceLineEndings("\r\n"), "truncated quotation markup with CRLF line endings")
        })
        {
            var rejected = false;
            try { PublicResetCalendar.Parse(html, Now); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "OpenTheRank rejects " + name);
        }
        CacheChecks(check, calendar);
    }

    private static void LocalCalendarChecks(Action<bool, string> check, DateTimeOffset first, DateTimeOffset second)
    {
        var korea = TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        check(LocalTime.Convert(first, korea).DateTime == new DateTime(2026, 11, 1, 9, 43, 20, 47)
            && LocalTime.Convert(second, korea).DateTime == new DateTime(2026, 11, 2, 8, 27, 20, 731),
            "actual Nov 1 banked grants map to separate Korean calendar dates");
        check(LocalTime.Convert(first, pacific).DateTime == new DateTime(2026, 10, 31, 17, 43, 20, 47)
            && LocalTime.Convert(second, pacific).DateTime == new DateTime(2026, 11, 1, 15, 27, 20, 731)
            && LocalTime.Label(first, pacific) == "UTC-07:00" && LocalTime.Label(second, pacific) == "UTC-08:00",
            "actual grant instants cross US calendar rollover and fall daylight transition");
        check(LocalTime.Convert(second, korea) - LocalTime.Convert(first, korea) == second - first
            && LocalTime.Convert(second, pacific) - LocalTime.Convert(first, pacific) == second - first,
            "calendar timezone conversion preserves elapsed grant interval");
    }

    private static void CacheChecks(Action<bool, string> check, PublicResetCalendar current)
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "opentherank-state-checks", Guid.NewGuid().ToString("N"));
        var store = new LocalStateStore(folder);
        var snapshot = new UsageSnapshot(Now, [new("primary", 37, 300, Now.AddHours(1))]);
        var state = new WidgetState
        {
            AlwaysOnTop = false, Opacity = .7, Left = 123, Top = 456, IsCollapsed = true,
            CodexExecutable = "C:\\fixture\\codex.exe", ActiveAccount = "account-a",
            PublicCalendar = current with { Source = null, Attribution = "AgentResets" }
        };
        state.Accounts["account-a"] = new AccountHistory
        {
            Email = "fixture@example.test", Plan = "plus", LastSnapshot = snapshot,
            Resets = [new("primary", "quota", Now.AddHours(-1), Now.AddHours(1))]
        };
        state.Accounts["account-b"] = new AccountHistory();
        state.PendingResetUses["account-a"] = new("credit-one", "pending-key");
        check(store.Save(state), "legacy public cache fixture saved");
        var loaded = store.Load();
        check(loaded.PublicCalendar is null && store.Warning is null,
            "legacy unmarked AgentResets cache invalidated without corrupt-state recovery");
        check(!loaded.AlwaysOnTop && loaded.Opacity == .7 && loaded.Left == 123 && loaded.Top == 456 && loaded.IsCollapsed
            && loaded.CodexExecutable == state.CodexExecutable && loaded.ActiveAccount == "account-a"
            && loaded.Accounts.Count == 2 && loaded.Accounts["account-a"].Resets.Count == 1
            && loaded.Accounts["account-a"].Email == "fixture@example.test" && loaded.Accounts["account-a"].Plan == "plus"
            && loaded.Accounts["account-a"].LastSnapshot?.Windows.Single().UsedPercent == 37
            && loaded.PendingResetUses["account-a"] == new ResetUseOperation("credit-one", "pending-key"),
            "provider cache migration preserves settings accounts private observations and pending reset use");
        loaded.PublicCalendar = current;
        check(store.Save(loaded), "OpenTheRank public cache saved");
        var offline = store.Load();
        check(offline.PublicCalendar?.Source == PublicResetCalendar.ProviderId
            && offline.PublicCalendar.Events.Count == current.Events.Count
            && offline.PublicCalendar.Events.Single(e => e.IsAnnouncement).Time == current.Events.Single(e => e.IsAnnouncement).Time,
            "marked OpenTheRank history and announcement retained for offline cache");
        loaded.PublicCalendar = current with { Source = "unknown-provider" };
        check(store.Save(loaded) && store.Load().PublicCalendar is null,
            "unrecognized public cache provider invalidated");
    }
}