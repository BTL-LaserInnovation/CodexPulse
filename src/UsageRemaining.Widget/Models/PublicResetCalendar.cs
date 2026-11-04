using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace UsageRemaining.Widget.Models;

public sealed record PublicResetEvent(DateTimeOffset? Time, DateOnly Date, string Kind, string Summary,
    string? SourceUrl, bool IsScheduled = false, bool IsDelivered = false, bool IsAnnouncement = false);

public sealed record PublicResetCalendar(DateTimeOffset FetchedAt, DateTimeOffset? GeneratedAt,
    List<PublicResetEvent> Events, string Attribution, string? Source = null)
{
    public const string PublicSource = "https://opentherank.com/ko/codex-reset/";
    public const string ProviderId = "opentherank-codex-reset-v1";
    private const int MaximumHtmlCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan ParseTimeout = TimeSpan.FromSeconds(2);
    private const RegexOptions HtmlOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex NonContent = new(@"<!--[\s\S]*?-->|<(?:script|style)\b[^>]*>[\s\S]*?</(?:script|style)\s*>", HtmlOptions, ParseTimeout);
    private static readonly Regex Tags = new("<(?<closing>/)?(?<name>[a-z][a-z0-9:-]*)\\b(?<attributes>(?:\"[^\"]*\"|'[^']*'|[^'\"<>])*)>", HtmlOptions, ParseTimeout);
    private static readonly Regex Attributes = new("(?<name>[a-z_:][a-z0-9_:.-]*)(?:\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s'\"=<>`]+)))?", HtmlOptions, ParseTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, ParseTimeout);

    public static PublicResetCalendar Parse(string html, DateTimeOffset fetchedAt)
    {
        if (string.IsNullOrWhiteSpace(html) || html.Length > MaximumHtmlCharacters)
            throw new InvalidDataException("Unsupported public reset calendar page.");
        try
        {
            return ParsePage(NonContent.Replace(html, string.Empty), fetchedAt.ToUniversalTime());
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new InvalidDataException("Public reset calendar page could not be parsed within the time limit.", exception);
        }
    }

    private static PublicResetCalendar ParsePage(string html, DateTimeOffset fetchedAt)
    {
        var stamps = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        var articles = new Dictionary<string, Article>(StringComparer.Ordinal);
        bool hasCalendar = false;
        Tag? openArticle = null;
        var articleTags = new List<Tag>();
        foreach (Match match in Tags.Matches(html))
        {
            var tag = ReadTag(match);
            if (!tag.Closing && (HasClass(tag, "cx-cal") || tag.Attributes.ContainsKey("data-cx-cal"))) hasCalendar = true;
            if (!tag.Closing && HasClass(tag, "cx-stamp"))
            {
                var id = Required(tag, "data-id");
                var stamp = new Stamp(UtcTimestamp(Required(tag, "data-at")), Kind(Required(tag, "data-type")));
                if (stamps.TryGetValue(id, out var existing) && existing != stamp)
                    throw new InvalidDataException("Conflicting public reset calendar records.");
                stamps[id] = stamp;
            }
            if (tag.Name.Equals("article", StringComparison.OrdinalIgnoreCase))
            {
                if (!tag.Closing && HasClass(tag, "cx-post"))
                {
                    if (openArticle is not null) throw new InvalidDataException("Nested public reset articles are unsupported.");
                    openArticle = tag;
                    articleTags.Clear();
                }
                else if (tag.Closing && openArticle is { } opening)
                {
                    var id = Required(opening, "id");
                    if (!id.StartsWith("r-", StringComparison.Ordinal) || id.Length == 2)
                        throw new InvalidDataException("Invalid public reset article identifier.");
                    var article = ReadArticle(html, opening, articleTags);
                    if (!articles.TryAdd(id[2..], article))
                        throw new InvalidDataException("Duplicate public reset articles.");
                    openArticle = null;
                }
            }
            else if (openArticle is not null) articleTags.Add(tag);
        }
        // A changed/error page must not replace the last successfully fetched calendar with an empty one.
        if (!hasCalendar || stamps.Count == 0 || openArticle is not null)
            throw new InvalidDataException("Unsupported public reset calendar layout.");

        var events = new List<PublicResetEvent>();
        foreach (var (id, stamp) in stamps)
        {
            if (stamp.Time > fetchedAt) continue;
            articles.TryGetValue(id, out var article);
            events.Add(new(stamp.Time, DateOnly.FromDateTime(stamp.Time.UtcDateTime), stamp.Kind,
                article?.Summary ?? string.Empty, article?.SourceUrl ?? (article is null
                    ? PublicSource + "#cal-" + stamp.Time.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                    : RecordUrl(id)), IsDelivered: true));
        }
        if (events.Count == 0) throw new InvalidDataException("Public reset calendar contains no elapsed reset records.");
        foreach (var (id, article) in articles)
        {
            // Publication time describes the announcement, never the reset it discusses. Aging an
            // announcement cannot make it delivered, and relative prose supplies no schedule.
            if (!article.IsAnnouncement || article.Time > fetchedAt || stamps.ContainsKey(id)) continue;
            events.Add(new(article.Time, DateOnly.FromDateTime(article.Time.UtcDateTime), article.Kind,
                article.Summary, article.SourceUrl ?? RecordUrl(id), IsAnnouncement: true));
        }
        return new(fetchedAt, null, events.OrderByDescending(e => e.Date).ThenByDescending(e => e.Time).ToList(),
            "OpenTheRank · " + PublicSource, ProviderId);
    }

    private static Article ReadArticle(string html, Tag opening, List<Tag> tags)
    {
        var time = tags.FirstOrDefault(t => !t.Closing && t.Name.Equals("time", StringComparison.OrdinalIgnoreCase));
        if (time is null) throw new InvalidDataException("Public reset article publication time is missing.");
        var publicationTime = UtcTimestamp(Required(time, "datetime"));
        var quote = tags.FirstOrDefault(t => !t.Closing && t.Name.Equals("blockquote", StringComparison.OrdinalIgnoreCase));
        var closeQuote = quote is null ? null : tags.FirstOrDefault(t => t.Closing
            && t.Name.Equals("blockquote", StringComparison.OrdinalIgnoreCase) && t.Start > quote.Start);
        if (quote is null || closeQuote is null)
            throw new InvalidDataException("Public reset article quotation is missing.");
        var summaryHtml = html[quote.End..closeQuote.Start];
        var summary = Whitespace.Replace(WebUtility.HtmlDecode(Tags.Replace(summaryHtml, " ")), " ").Trim();
        var type = opening.Attributes.GetValueOrDefault("data-type");
        return new(publicationTime, type is null ? "regular" : Kind(type), summary,
            HttpsUrl(quote.Attributes.GetValueOrDefault("cite")),
            opening.Attributes.GetValueOrDefault("data-status") == "announced");
    }

    private static Tag ReadTag(Match match)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in Attributes.Matches(match.Groups["attributes"].Value))
        {
            var value = attribute.Groups["double"].Success ? attribute.Groups["double"].Value
                : attribute.Groups["single"].Success ? attribute.Groups["single"].Value : attribute.Groups["bare"].Value;
            attributes[attribute.Groups["name"].Value] = WebUtility.HtmlDecode(value);
        }
        return new(match.Groups["name"].Value, match.Groups["closing"].Success, attributes, match.Index, match.Index + match.Length);
    }

    private static bool HasClass(Tag tag, string name) => tag.Attributes.TryGetValue("class", out var classes)
        && classes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(name, StringComparer.Ordinal);

    private static string Required(Tag tag, string name) =>
        tag.Attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidDataException("Public reset calendar attribute is missing: " + name);

    private static string Kind(string value) => value is "regular" or "banked" or "both" ? value
        : throw new InvalidDataException("Unknown public reset calendar event type.");

    private static DateTimeOffset UtcTimestamp(string text)
    {
        if (!text.Contains('T') || !(text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            || !DateTimeOffset.TryParseExact(text,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
            throw new InvalidDataException("Public reset calendar timestamp must be an exact UTC time.");
        return timestamp.ToUniversalTime();
    }

    private static string RecordUrl(string id) => PublicSource + "#r-" + Uri.EscapeDataString(id);

    private static string? HttpsUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(url.UserInfo) ? url.AbsoluteUri : null;

    private sealed record Tag(string Name, bool Closing, Dictionary<string, string> Attributes, int Start, int End);
    private sealed record Stamp(DateTimeOffset Time, string Kind);
    private sealed record Article(DateTimeOffset Time, string Kind, string Summary, string? SourceUrl, bool IsAnnouncement);
}
