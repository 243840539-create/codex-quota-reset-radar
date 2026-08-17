using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindexBar.Core.Forecasting;

public sealed record QuotaAutoSignalCollection(
    IReadOnlyList<QuotaCommunitySignalDraft> Signals,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Errors)
{
    public string StatusLabel => Errors.Count == 0
        ? $"已自动检查 {Sources.Count} 个来源，发现 {Signals.Count} 条新信息"
        : $"已检查 {Sources.Count} 个来源，发现 {Signals.Count} 条信息；{Errors.Count} 个来源暂不可用";
}

public sealed partial class QuotaAutoSignalCollector
{
    private static readonly Uri OpenAiStatusUri = new("https://status.openai.com/api/v2/incidents.json");
    private static readonly Uri GitHubUri = new("https://api.github.com/search/issues?q=repo%3Aopenai%2Fcodex%20(quota%20OR%20%22rate%20limit%22%20OR%20reset)&sort=updated&order=desc&per_page=10");
    private static readonly Uri RedditUri = new("https://www.reddit.com/search.json?q=Codex%20quota%20reset%20OpenAI&sort=new&limit=10");
    private static readonly Uri WebSearchUri = new("https://www.bing.com/search?q=site%3Ax.com%20Codex%20(quota%20reset%20OR%20%22rate%20limit%22%20OR%20usage%20reset)%20OpenAI&count=10");

    private readonly HttpClient _http;

    public QuotaAutoSignalCollector(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CodexQuotaResetRadar", "0.2"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<QuotaAutoSignalCollection> CollectAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var jobs = new[]
        {
            CollectOpenAiStatusAsync(now, cancellationToken),
            CollectGitHubAsync(now, cancellationToken),
            CollectRedditAsync(now, cancellationToken),
            CollectWebSearchAsync(now, cancellationToken)
        };
        var results = await Task.WhenAll(jobs).ConfigureAwait(false);
        var signals = results.SelectMany(result => result.Signals)
            .Where(signal => !string.IsNullOrWhiteSpace(signal.SourceUrl))
            .GroupBy(signal => signal.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var sources = results.SelectMany(result => result.Sources).Distinct(StringComparer.Ordinal).ToArray();
        var errors = results.SelectMany(result => result.Errors).ToArray();
        return new QuotaAutoSignalCollection(signals, sources, errors);
    }

    private async Task<PartialCollection> CollectOpenAiStatusAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await GetStringAsync(OpenAiStatusUri, cancellationToken).ConfigureAwait(false));
            var signals = new List<QuotaCommunitySignalDraft>();
            if (document.RootElement.TryGetProperty("incidents", out var incidents))
            {
                foreach (var incident in incidents.EnumerateArray())
                {
                    var name = Property(incident, "name");
                    var status = Property(incident, "status");
                    var link = Property(incident, "shortlink");
                    var body = IncidentBody(incident);
                    var text = $"{name} {status} {body} {link}";
                    if (!ContainsSignalKeyword(text))
                    {
                        continue;
                    }

                    var imported = QuotaSignalImportParser.Parse(text, now, "OpenAI 状态", 95);
                    signals.Add(ToDraft(imported, "OpenAI 状态"));
                    if (signals.Count >= 10)
                    {
                        break;
                    }
                }
            }

            return new PartialCollection(signals, ["OpenAI 状态"], []);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed("OpenAI 状态", error);
        }
    }

    private async Task<PartialCollection> CollectGitHubAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await GetStringAsync(GitHubUri, cancellationToken).ConfigureAwait(false));
            var signals = new List<QuotaCommunitySignalDraft>();
            if (document.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var title = Property(item, "title");
                    var body = Property(item, "body");
                    var link = Property(item, "html_url");
                    var author = item.TryGetProperty("user", out var user) ? Property(user, "login") : "OpenAI";
                    var imported = QuotaSignalImportParser.Parse(
                        $"{title} {body} {link} @{author}",
                        now,
                        "GitHub",
                        85);
                    signals.Add(ToDraft(imported, "GitHub"));
                }
            }

            return new PartialCollection(signals, ["GitHub"], []);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed("GitHub", error);
        }
    }

    private async Task<PartialCollection> CollectRedditAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await GetStringAsync(RedditUri, cancellationToken).ConfigureAwait(false));
            var signals = new List<QuotaCommunitySignalDraft>();
            if (document.RootElement.TryGetProperty("data", out var rootData)
                && rootData.TryGetProperty("children", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("data", out var data))
                    {
                        continue;
                    }

                    var title = Property(data, "title");
                    var body = Property(data, "selftext");
                    var link = Property(data, "url");
                    var author = Property(data, "author");
                    var imported = QuotaSignalImportParser.Parse(
                        $"{title} {body} {link} @{author}",
                        now,
                        "Reddit 社区",
                        40);
                    signals.Add(ToDraft(imported, "Reddit 社区"));
                }
            }

            return new PartialCollection(signals, ["Reddit 社区"], []);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Failed("Reddit 社区", error);
        }
    }

    private async Task<PartialCollection> CollectWebSearchAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = await GetStringAsync(WebSearchUri, cancellationToken).ConfigureAwait(false);
            var signals = new List<QuotaCommunitySignalDraft>();
            foreach (Match match in SearchResultRegex().Matches(html))
            {
                var title = WebUtility.HtmlDecode(StripHtml(match.Groups["title"].Value));
                var link = WebUtility.HtmlDecode(match.Groups["url"].Value);
                var snippet = WebUtility.HtmlDecode(StripHtml(match.Groups["snippet"].Value));
                var imported = QuotaSignalImportParser.Parse(
                    $"{title} {snippet} {link}",
                    now,
                    "X 线索",
                    60);
                signals.Add(ToDraft(imported, "X 线索"));
                if (signals.Count >= 10)
                {
                    break;
                }
            }

            return new PartialCollection(signals, ["网页搜索"], []);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return Failed("网页搜索", error);
        }
    }

    private async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static QuotaCommunitySignalDraft ToDraft(QuotaSignalImportResult imported, string fallbackKind)
    {
        var kind = string.IsNullOrWhiteSpace(imported.SourceKind) ? fallbackKind : imported.SourceKind;
        var note = string.IsNullOrWhiteSpace(imported.Note) ? $"[{kind}] 自动采集的信息" : $"[{kind}] {imported.Note}";
        return new QuotaCommunitySignalDraft(
            imported.SourceUrl,
            string.IsNullOrWhiteSpace(imported.Author) ? "@自动采集" : imported.Author,
            note,
            imported.TargetAt,
            imported.Reliability);
    }

    private static string IncidentBody(JsonElement incident)
    {
        if (!incident.TryGetProperty("incident_updates", out var updates))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            updates.EnumerateArray().Take(3).Select(update => Property(update, "body")));
    }

    private static string Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool ContainsSignalKeyword(string text) =>
        text.Contains("codex", StringComparison.OrdinalIgnoreCase)
        || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || text.Contains("usage limit", StringComparison.OrdinalIgnoreCase);

    private static string StripHtml(string value) => Regex.Replace(value, "<[^>]+>", " ");

    private static PartialCollection Failed(string source, Exception error) =>
        new([], [], [$"{source}: {error.Message}"]);

    [GeneratedRegex(@"<h2><a[^>]+href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a></h2>.*?<p>(?<snippet>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SearchResultRegex();

    private sealed record PartialCollection(
        IReadOnlyList<QuotaCommunitySignalDraft> Signals,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Errors);
}
