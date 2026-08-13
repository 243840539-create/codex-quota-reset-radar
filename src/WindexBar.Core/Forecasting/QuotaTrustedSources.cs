namespace WindexBar.Core.Forecasting;

public static class QuotaTrustedSources
{
    private static readonly IReadOnlyDictionary<string, int> ReliabilityByAuthor =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["thsottiaux"] = 95,
            ["sama"] = 90,
            ["openaidevs"] = 90,
            ["openai"] = 90
        };

    public static int SuggestedReliability(string? author, int fallback)
    {
        var normalized = (author ?? string.Empty).Trim().TrimStart('@');
        return ReliabilityByAuthor.TryGetValue(normalized, out var reliability)
            ? reliability
            : Math.Clamp(fallback, 0, 100);
    }

    public static bool IsTrusted(string? author) =>
        SuggestedReliability(author, 0) > 0;
}
