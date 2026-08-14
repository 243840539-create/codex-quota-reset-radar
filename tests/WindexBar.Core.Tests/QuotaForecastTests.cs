using WindexBar.Core.Forecasting;
using WindexBar.Core.Models;

namespace WindexBar.Core.Tests;

public sealed class QuotaForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UsesCodexResetTimestampAsAuthoritativeForecast()
    {
        var reset = Now.AddHours(5);
        var usage = Usage(new RateWindow(40, 300, reset, null));

        var forecast = QuotaForecastEngine.Build(usage, new QuotaForecastState(), Now);

        Assert.NotNull(forecast.OfficialReset);
        Assert.Equal(reset, forecast.OfficialReset!.ResetsAt);
        Assert.Equal(60, forecast.OfficialReset.RemainingPercent);
        Assert.Null(forecast.ExtraReset);
    }

    [Fact]
    public void CapsSingleCommunityClueAtLowConfidence()
    {
        var target = Now.AddDays(2);
        var state = new QuotaForecastState
        {
            Signals =
            [
                Signal("@observer", target, 70, Now)
            ]
        };

        var forecast = QuotaForecastEngine.Build(null, state, Now);

        Assert.NotNull(forecast.ExtraReset);
        Assert.Equal(target, forecast.ExtraReset!.TargetAt);
        Assert.True(forecast.ExtraReset.Confidence <= 55);
        Assert.Equal("community", forecast.ExtraReset.Basis);
    }

    [Fact]
    public void AllowsAHigherProbabilityForOneTrustedInternalSource()
    {
        var target = Now.AddDays(1);
        var state = new QuotaForecastState
        {
            Signals = [Signal("@thsottiaux", target, 95, Now)]
        };

        var forecast = QuotaForecastEngine.Build(null, state, Now).ExtraReset!;

        Assert.InRange(forecast.Confidence, 56, 78);
    }

    [Fact]
    public void CorroboratingAuthorsProduceACommunityWindow()
    {
        var target = Now.AddDays(3);
        var state = new QuotaForecastState
        {
            Signals =
            [
                Signal("@one", target.AddHours(-3), 75, Now.AddHours(-4)),
                Signal("@two", target, 80, Now.AddHours(-3)),
                Signal("@three", target.AddHours(4), 70, Now.AddHours(-2))
            ]
        };

        var forecast = QuotaForecastEngine.Build(null, state, Now).ExtraReset!;

        Assert.InRange(forecast.TargetAt, target.AddHours(-3), target.AddHours(4));
        Assert.Equal(3, forecast.DistinctAuthorCount);
        Assert.True(forecast.Confidence > 55);
        Assert.True(forecast.WindowStartsAt < forecast.TargetAt);
        Assert.True(forecast.WindowEndsAt > forecast.TargetAt);
        Assert.Equal(forecast.Confidence, forecast.DateProbabilities.Sum(item => item.Probability));
        Assert.Equal(100, forecast.NoExtraResetProbability + forecast.DateProbabilities.Sum(item => item.Probability));
    }

    [Fact]
    public void HistoricalCadenceNeedsThreeObservedGrants()
    {
        var state = new QuotaForecastState
        {
            Observations =
            [
                Observation("a", Now.AddDays(-30)),
                Observation("b", Now.AddDays(-20)),
                Observation("c", Now.AddDays(-10))
            ]
        };

        var forecast = QuotaForecastEngine.Build(null, state, Now).ExtraReset!;

        Assert.Equal(Now, forecast.TargetAt);
        Assert.Equal("history", forecast.Basis);
        Assert.True(forecast.Confidence <= 75);
    }

    [Fact]
    public void ReconcilesCluesAgainstObservedGrantTimes()
    {
        var hit = Signal("@hit", Now.AddHours(-12), 80, Now.AddDays(-3));
        var miss = Signal("@miss", Now.AddDays(-4), 80, Now.AddDays(-7));
        var observations = new[] { Observation("event", Now) };

        var result = QuotaForecastEngine.ReconcileSignals([hit, miss], observations, Now);

        Assert.Equal(QuotaSignalStatus.Confirmed, result.Single(signal => signal.Author == "@hit").Status);
        Assert.Equal(QuotaSignalStatus.Missed, result.Single(signal => signal.Author == "@miss").Status);
    }

    [Fact]
    public void BankedResetCreditsDoNotCountAsGlobalQuotaResets()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"windexbar-forecast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "forecast.json");
        try
        {
            var store = new QuotaForecastStore(path);
            store.AddSignal(new QuotaCommunitySignalDraft(
                "https://x.com/example/status/1",
                "example",
                "reset tomorrow",
                Now.AddDays(1),
                70), null, Now);

            var credit = new RateLimitResetCredit(
                "credit-1",
                Now.AddDays(1),
                Now.AddDays(31),
                "codexRateLimits",
                "available",
                null,
                null);
            var usage = Usage(
                null,
                resetCredits: new RateLimitResetCreditsSnapshot(1, Now.AddDays(1), [credit]));
            var observed = store.ObserveAndForecast(usage, Now.AddDays(1));
            var reloaded = new QuotaForecastStore(path).ObserveAndForecast(usage, Now.AddDays(1));

            Assert.Empty(observed.Observations);
            Assert.Equal(QuotaSignalStatus.Pending, Assert.Single(observed.Signals).Status);
            Assert.Empty(reloaded.Observations);
            Assert.Single(reloaded.Signals);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AppliesTrustedWeightToKnownCodexSources()
    {
        Assert.Equal(95, QuotaTrustedSources.SuggestedReliability("@thsottiaux", 50));
        Assert.Equal(90, QuotaTrustedSources.SuggestedReliability("sama", 50));
        Assert.Equal(50, QuotaTrustedSources.SuggestedReliability("someone_else", 50));
    }

    [Fact]
    public void DetectsPairedEarlyWindowDropsAsAGlobalReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"windexbar-window-reset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new QuotaForecastStore(Path.Combine(directory, "forecast.json"));
            var scheduledReset = Now.AddDays(4);
            store.ObserveAndForecast(Usage(
                new RateWindow(82, 300, scheduledReset, null),
                new RateWindow(76, 10080, scheduledReset, null)), Now);

            var result = store.ObserveAndForecast(
                Usage(
                    new RateWindow(3, 300, scheduledReset.AddDays(1), null),
                    new RateWindow(4, 10080, scheduledReset.AddDays(7), null)),
                Now.AddHours(2));

            var observation = Assert.Single(result.Observations);
            Assert.Equal("global-quota-reset", observation.Kind);
            Assert.Equal(Now.AddHours(2), observation.OccurredAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ImportsKnownXAuthorAndChineseDateFromClipboard()
    {
        var imported = QuotaSignalImportParser.Parse(
            "@thsottiaux 预计 2026年8月16日 20:30 全员重置 https://x.com/thsottiaux/status/123",
            Now);

        Assert.Equal("@thsottiaux", imported.Author);
        Assert.Equal("X 线索", imported.SourceKind);
        Assert.Equal(95, imported.Reliability);
        Assert.Equal(new DateTimeOffset(2026, 8, 16, 20, 30, 0, TimeSpan.Zero), imported.TargetAt);
    }

    [Fact]
    public void ImportsOfficialStatusInformationAtHighReliability()
    {
        var imported = QuotaSignalImportParser.Parse(
            "Codex limits will reset tomorrow 18:00 https://status.openai.com/incidents/example",
            Now);

        Assert.Equal("@OpenAI", imported.Author);
        Assert.Equal("OpenAI 状态", imported.SourceKind);
        Assert.Equal(95, imported.Reliability);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 18, 0, 0, TimeSpan.Zero), imported.TargetAt);
    }

    [Fact]
    public void KeepsCommunityInformationWithoutInventingADate()
    {
        var imported = QuotaSignalImportParser.Parse(
            "Community discussion https://www.reddit.com/r/codex/comments/example",
            Now);

        Assert.Equal("Reddit 社区", imported.SourceKind);
        Assert.Equal(45, imported.Reliability);
        Assert.Null(imported.TargetAt);
    }

    [Fact]
    public void SimilarLookingDomainsDoNotReceiveOfficialTrust()
    {
        var imported = QuotaSignalImportParser.Parse(
            "Reset tomorrow https://notopenai.com/reset",
            Now,
            fallbackReliability: 50);

        Assert.Equal("网页/新闻", imported.SourceKind);
        Assert.Equal(50, imported.Reliability);
        Assert.NotEqual("@OpenAI", imported.Author);
    }

    private static QuotaCommunitySignal Signal(
        string author,
        DateTimeOffset target,
        int reliability,
        DateTimeOffset capturedAt) =>
        new(Guid.NewGuid(), string.Empty, author, string.Empty, capturedAt, target, reliability, QuotaSignalStatus.Pending);

    private static QuotaResetObservation Observation(string id, DateTimeOffset occurredAt) =>
        new(id, occurredAt, "global-quota-reset", null);

    private static UsageSnapshot Usage(
        RateWindow? primary,
        RateWindow? secondary = null,
        RateLimitResetCreditsSnapshot? resetCredits = null) =>
        new(primary, secondary, null, Now, null, RateLimitResetCredits: resetCredits);
}
