using System.Globalization;
using System.Text.RegularExpressions;

namespace WindexBar.Core.Forecasting;

public sealed record QuotaSignalImportResult(
    string SourceUrl,
    string Author,
    string Note,
    DateTimeOffset? TargetAt,
    int Reliability,
    string SourceKind);

public static partial class QuotaSignalImportParser
{
    public static QuotaSignalImportResult Parse(
        string? clipboardText,
        DateTimeOffset now,
        string fallbackKind = "网页/新闻",
        int fallbackReliability = 50)
    {
        var text = (clipboardText ?? string.Empty).Trim();
        var sourceUrl = ExtractUrl(text);
        var host = Host(sourceUrl);
        var sourceKind = Classify(host, fallbackKind);
        var author = ExtractAuthor(text, sourceUrl, host);
        var reliability = Reliability(host, author, fallbackReliability);
        var targetAt = ExtractTargetAt(text, now);
        var note = NormalizeNote(text, sourceUrl);

        return new QuotaSignalImportResult(
            sourceUrl,
            author,
            note,
            targetAt,
            reliability,
            sourceKind);
    }

    private static string ExtractUrl(string text)
    {
        var match = UrlRegex().Match(text);
        return match.Success
            ? match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '，', '。', '；', '！', '？')
            : string.Empty;
    }

    private static string Host(string sourceUrl) =>
        Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : string.Empty;

    private static string Classify(string host, string fallbackKind)
    {
        if (IsDomain(host, "openai.com"))
        {
            return host.StartsWith("status.", StringComparison.OrdinalIgnoreCase)
                ? "OpenAI 状态"
                : "OpenAI 官方";
        }

        if (host is "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com")
        {
            return "X 线索";
        }

        if (host is "github.com" or "www.github.com")
        {
            return "GitHub";
        }

        if (IsDomain(host, "reddit.com"))
        {
            return "Reddit 社区";
        }

        return string.IsNullOrWhiteSpace(fallbackKind) ? "网页/新闻" : fallbackKind.Trim();
    }

    private static string ExtractAuthor(string text, string sourceUrl, string host)
    {
        if (host is "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com")
        {
            var match = XAuthorRegex().Match(sourceUrl);
            if (match.Success)
            {
                return $"@{match.Groups[1].Value}";
            }
        }

        if (IsDomain(host, "openai.com") || IsOpenAiGitHub(sourceUrl, host))
        {
            return "@OpenAI";
        }

        var mention = MentionRegex().Match(text);
        return mention.Success ? $"@{mention.Groups[1].Value}" : string.Empty;
    }

    private static int Reliability(string host, string author, int fallback)
    {
        var baseline = host switch
        {
            "status.openai.com" => 95,
            _ when IsDomain(host, "openai.com") => 90,
            "github.com" or "www.github.com" when author.Equals("@OpenAI", StringComparison.OrdinalIgnoreCase) => 85,
            "x.com" or "www.x.com" or "twitter.com" or "www.twitter.com" => 60,
            _ when IsDomain(host, "reddit.com") => 45,
            _ => Math.Clamp(fallback, 0, 100)
        };
        return Math.Max(baseline, QuotaTrustedSources.SuggestedReliability(author, baseline));
    }

    private static DateTimeOffset? ExtractTargetAt(string text, DateTimeOffset now)
    {
        var fullDate = FullDateRegex().Match(text);
        if (fullDate.Success
            && TryNumber(fullDate, "year", out var year)
            && TryNumber(fullDate, "month", out var month)
            && TryNumber(fullDate, "day", out var day))
        {
            return CreateDate(year, month, day, fullDate, now.Offset);
        }

        var monthDay = MonthDayRegex().Match(text);
        if (monthDay.Success
            && TryNumber(monthDay, "month", out month)
            && TryNumber(monthDay, "day", out day))
        {
            var candidate = CreateDate(now.Year, month, day, monthDay, now.Offset);
            if (candidate is not null && candidate < now.AddDays(-1))
            {
                candidate = CreateDate(now.Year + 1, month, day, monthDay, now.Offset);
            }

            return candidate;
        }

        var relative = RelativeDateRegex().Match(text);
        if (!relative.Success)
        {
            return null;
        }

        var token = relative.Groups["relative"].Value.ToLowerInvariant();
        var days = token switch
        {
            "后天" => 2,
            "明天" or "tomorrow" => 1,
            _ => 0
        };
        var date = now.Date.AddDays(days);
        var (hour, minute) = Time(relative);
        return new DateTimeOffset(date.Year, date.Month, date.Day, hour, minute, 0, now.Offset);
    }

    private static DateTimeOffset? CreateDate(
        int year,
        int month,
        int day,
        Match match,
        TimeSpan offset)
    {
        try
        {
            var (hour, minute) = Time(match);
            return new DateTimeOffset(year, month, day, hour, minute, 0, offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static (int Hour, int Minute) Time(Match match)
    {
        var hour = TryNumber(match, "hour", out var parsedHour) ? parsedHour : 12;
        var minute = TryNumber(match, "minute", out var parsedMinute) ? parsedMinute : 0;
        return (Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59));
    }

    private static bool TryNumber(Match match, string group, out int value) =>
        int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static string NormalizeNote(string text, string sourceUrl)
    {
        var note = string.IsNullOrWhiteSpace(sourceUrl)
            ? text
            : text.Replace(sourceUrl, string.Empty, StringComparison.OrdinalIgnoreCase);
        note = WhitespaceRegex().Replace(note, " ").Trim(' ', '-', '—', '|');
        return note.Length <= 500 ? note : note[..500];
    }

    private static bool IsDomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{domain}", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpenAiGitHub(string sourceUrl, string host)
    {
        if (!IsDomain(host, "github.com")
            || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var owner = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(owner, "openai", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"https?://(?:www\.)?(?:x|twitter)\.com/([A-Za-z0-9_]{1,15})/status/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XAuthorRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])@([A-Za-z0-9_]{1,15})", RegexOptions.CultureInvariant)]
    private static partial Regex MentionRegex();

    [GeneratedRegex(@"(?<year>20\d{2})\s*(?:[-/.]|年)\s*(?<month>1[0-2]|0?[1-9])\s*(?:[-/.]|月)\s*(?<day>3[01]|[12]\d|0?[1-9])\s*日?(?:\s*(?<hour>[01]?\d|2[0-3])\s*(?::|点)\s*(?<minute>[0-5]?\d)?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FullDateRegex();

    [GeneratedRegex(@"(?<!\d)(?<month>1[0-2]|0?[1-9])\s*(?:[-/.]|月)\s*(?<day>3[01]|[12]\d|0?[1-9])\s*日?(?:\s*(?<hour>[01]?\d|2[0-3])\s*(?::|点)\s*(?<minute>[0-5]?\d)?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthDayRegex();

    [GeneratedRegex(@"(?<relative>后天|明天|今天|今晚|tomorrow|today)(?:\s*(?<hour>[01]?\d|2[0-3])\s*(?::|点)\s*(?<minute>[0-5]?\d)?)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeDateRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
