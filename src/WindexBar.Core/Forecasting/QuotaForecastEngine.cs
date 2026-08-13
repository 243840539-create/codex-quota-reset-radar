using WindexBar.Core.Models;

namespace WindexBar.Core.Forecasting;

public static class QuotaForecastEngine
{
    private static readonly TimeSpan ConfirmationTolerance = TimeSpan.FromDays(2);

    public static QuotaForecastSnapshot Build(
        UsageSnapshot? usage,
        QuotaForecastState state,
        DateTimeOffset now)
    {
        var official = FindNearestOfficialReset(usage, now);
        var community = BuildCommunityCandidate(state.Signals, now);
        var history = BuildHistoryCandidate(state.Observations, now);
        var extra = CombineCandidates(community, history);

        return new QuotaForecastSnapshot(
            now,
            official,
            extra,
            state.Signals
                .OrderByDescending(signal => signal.CapturedAt)
                .ToArray(),
            state.Observations
                .Where(observation => observation.Kind == "global-quota-reset")
                .OrderByDescending(observation => observation.OccurredAt)
                .ToArray());
    }

    public static IReadOnlyList<QuotaCommunitySignal> ReconcileSignals(
        IEnumerable<QuotaCommunitySignal> signals,
        IEnumerable<QuotaResetObservation> observations,
        DateTimeOffset now)
    {
        var events = observations
            .Where(observation => observation.Kind == "global-quota-reset")
            .Select(observation => observation.OccurredAt)
            .ToArray();
        return signals.Select(signal =>
        {
            if (signal.TargetAt is null || signal.Status != QuotaSignalStatus.Pending)
            {
                return signal;
            }

            var target = signal.TargetAt.Value;
            if (events.Any(occurredAt => (occurredAt - target).Duration() <= ConfirmationTolerance))
            {
                return signal with { Status = QuotaSignalStatus.Confirmed };
            }

            return target + ConfirmationTolerance < now
                ? signal with { Status = QuotaSignalStatus.Missed }
                : signal;
        }).ToArray();
    }

    private static OfficialQuotaResetForecast? FindNearestOfficialReset(UsageSnapshot? usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            return null;
        }

        var candidates = new List<OfficialQuotaResetForecast>();
        AddOfficial(candidates, "Current", usage.Primary, now);
        AddOfficial(candidates, "Weekly", usage.Secondary, now);
        AddOfficial(candidates, "Additional", usage.Tertiary, now);

        foreach (var model in usage.Models ?? [])
        {
            AddOfficial(candidates, $"{model.ModelName} current", model.Current, now);
            AddOfficial(candidates, $"{model.ModelName} weekly", model.Weekly, now);
        }

        return candidates
            .GroupBy(candidate => candidate.ResetsAt.ToUnixTimeSeconds() / 60)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.ResetsAt)
            .FirstOrDefault();
    }

    private static void AddOfficial(
        ICollection<OfficialQuotaResetForecast> candidates,
        string name,
        RateWindow? window,
        DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } resetsAt || resetsAt <= now)
        {
            return;
        }

        candidates.Add(new OfficialQuotaResetForecast(name, resetsAt, window.RemainingPercent));
    }

    private static ForecastCandidate? BuildCommunityCandidate(
        IReadOnlyList<QuotaCommunitySignal> signals,
        DateTimeOffset now)
    {
        var usable = signals
            .Where(signal => signal.Status == QuotaSignalStatus.Pending)
            .Where(signal => signal.TargetAt is not null)
            .Where(signal => signal.TargetAt >= now.AddDays(-2) && signal.TargetAt <= now.AddDays(90))
            .Select(signal => new WeightedSignal(signal, SignalWeight(signal, signals, now)))
            .Where(item => item.Weight > 0)
            .OrderBy(item => item.Signal.TargetAt)
            .ToArray();

        if (usable.Length == 0)
        {
            return null;
        }

        var target = WeightedMedian(usable);
        var totalWeight = usable.Sum(item => item.Weight);
        var dispersionDays = usable.Sum(item =>
            item.Weight * Math.Abs((item.Signal.TargetAt!.Value - target).TotalDays)) / totalWeight;
        var halfWindow = TimeSpan.FromHours(Math.Clamp(Math.Max(12, dispersionDays * 36), 12, 24 * 7));
        var distinctAuthors = usable
            .Select(item => NormalizeAuthor(item.Signal.Author))
            .Where(author => author.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var quality = usable.Sum(item => item.Weight * item.Signal.Reliability / 100d) / totalWeight;
        var freshness = usable.Sum(item => item.Weight * Freshness(item.Signal.CapturedAt, now)) / totalWeight;
        var corroboration = 1 - Math.Exp(-Math.Max(1, distinctAuthors) / 2d);
        var agreement = Math.Exp(-dispersionDays / 3d);
        var confidence = (int)Math.Round(100 * (
            (0.35 * quality)
            + (0.25 * corroboration)
            + (0.20 * agreement)
            + (0.20 * freshness)));
        var strongestReliability = usable.Max(item => item.Signal.Reliability);
        confidence = usable.Length switch
        {
            1 => Math.Min(confidence, strongestReliability >= 90 ? 78 : 55),
            2 => Math.Min(confidence, strongestReliability >= 90 ? 86 : 72),
            _ => Math.Min(confidence, 92)
        };

        return new ForecastCandidate(
            target,
            halfWindow,
            Math.Clamp(confidence, 10, 92),
            "community",
            usable.Length,
            distinctAuthors);
    }

    private static ForecastCandidate? BuildHistoryCandidate(
        IReadOnlyList<QuotaResetObservation> observations,
        DateTimeOffset now)
    {
        var events = observations
            .Where(observation => observation.Kind == "global-quota-reset")
            .Select(observation => observation.OccurredAt)
            .Distinct()
            .OrderBy(value => value)
            .ToArray();
        if (events.Length < 3)
        {
            return null;
        }

        var intervals = events
            .Zip(events.Skip(1), (left, right) => (right - left).TotalDays)
            .Where(days => days is >= 0.25 and <= 180)
            .OrderBy(days => days)
            .ToArray();
        if (intervals.Length < 2)
        {
            return null;
        }

        var medianDays = Median(intervals);
        var deviations = intervals
            .Select(days => Math.Abs(days - medianDays))
            .OrderBy(days => days)
            .ToArray();
        var madDays = Median(deviations);
        var target = events[^1].AddDays(medianDays);
        while (target < now.AddDays(-2))
        {
            target = target.AddDays(medianDays);
        }

        var regularity = Math.Exp(-madDays / Math.Max(1, medianDays));
        var sampleScore = 1 - Math.Exp(-intervals.Length / 4d);
        var confidence = (int)Math.Round(100 * ((0.65 * regularity) + (0.35 * sampleScore)));
        var halfWindow = TimeSpan.FromHours(Math.Clamp(Math.Max(24, madDays * 36), 24, 24 * 14));
        return new ForecastCandidate(
            target,
            halfWindow,
            Math.Clamp(confidence, 15, 75),
            "history",
            events.Length,
            0);
    }

    private static ExtraQuotaResetForecast? CombineCandidates(
        ForecastCandidate? community,
        ForecastCandidate? history)
    {
        if (community is null && history is null)
        {
            return null;
        }

        var selected = community ?? history!;
        if (community is not null && history is not null)
        {
            var separation = (community.TargetAt - history.TargetAt).Duration();
            if (separation <= TimeSpan.FromDays(7))
            {
                var communityWeight = community.Confidence;
                var historyWeight = history.Confidence;
                var unixSeconds = (
                    (community.TargetAt.ToUnixTimeSeconds() * communityWeight)
                    + (history.TargetAt.ToUnixTimeSeconds() * historyWeight))
                    / (communityWeight + historyWeight);
                selected = new ForecastCandidate(
                    DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
                    community.HalfWindow > history.HalfWindow ? community.HalfWindow : history.HalfWindow,
                    Math.Min(94, Math.Max(community.Confidence, history.Confidence) + 6),
                    "community+history",
                    community.EvidenceCount + history.EvidenceCount,
                    community.DistinctAuthorCount);
            }
            else if (history.Confidence > community.Confidence)
            {
                selected = history with { Basis = "history (signals disagree)" };
            }
            else
            {
                selected = community with { Basis = "community (history disagrees)" };
            }
        }

        return new ExtraQuotaResetForecast(
            selected.TargetAt,
            selected.TargetAt - selected.HalfWindow,
            selected.TargetAt + selected.HalfWindow,
            selected.Confidence,
            selected.Basis,
            selected.EvidenceCount,
            selected.DistinctAuthorCount,
            BuildDateProbabilities(selected));
    }

    private static IReadOnlyList<QuotaDateProbability> BuildDateProbabilities(ForecastCandidate candidate)
    {
        var center = candidate.TargetAt;
        var radiusDays = Math.Clamp((int)Math.Ceiling(candidate.HalfWindow.TotalDays * 2.5), 2, 14);
        var sigmaDays = Math.Max(0.5, candidate.HalfWindow.TotalDays / 1.28);
        var centerDate = DateOnly.FromDateTime(center.Date);
        var weighted = Enumerable.Range(-radiusDays, (radiusDays * 2) + 1)
            .Select(offset =>
            {
                var date = centerDate.AddDays(offset);
                var midpoint = new DateTimeOffset(
                    date.Year,
                    date.Month,
                    date.Day,
                    12,
                    0,
                    0,
                    center.Offset);
                var distanceDays = (midpoint - center).TotalDays;
                var weight = Math.Exp(-0.5 * Math.Pow(distanceDays / sigmaDays, 2));
                return new WeightedDate(date, weight);
            })
            .ToArray();
        var totalWeight = weighted.Sum(item => item.Weight);
        var allocated = weighted
            .Select(item =>
            {
                var exact = candidate.Confidence * item.Weight / totalWeight;
                return new AllocatedDate(item.Date, (int)Math.Floor(exact), exact - Math.Floor(exact));
            })
            .ToArray();
        var remainder = candidate.Confidence - allocated.Sum(item => item.Probability);
        foreach (var item in allocated.OrderByDescending(item => item.Fraction).Take(remainder))
        {
            item.Probability++;
        }

        return allocated
            .Where(item => item.Probability > 0)
            .OrderBy(item => item.Date)
            .Select(item => new QuotaDateProbability(item.Date, item.Probability))
            .ToArray();
    }

    private static double SignalWeight(
        QuotaCommunitySignal signal,
        IReadOnlyList<QuotaCommunitySignal> allSignals,
        DateTimeOffset now)
    {
        var author = NormalizeAuthor(signal.Author);
        var authorHistory = allSignals
            .Where(candidate => NormalizeAuthor(candidate.Author) == author)
            .Where(candidate => candidate.Status != QuotaSignalStatus.Pending)
            .ToArray();
        var confirmed = authorHistory.Count(candidate => candidate.Status == QuotaSignalStatus.Confirmed);
        var accuracy = (confirmed + 1d) / (authorHistory.Length + 2d);
        var reliability = Math.Clamp(signal.Reliability, 0, 100) / 100d;
        return (0.25 + (0.75 * reliability))
            * (0.5 + (0.5 * accuracy))
            * Freshness(signal.CapturedAt, now);
    }

    private static double Freshness(DateTimeOffset capturedAt, DateTimeOffset now)
    {
        var ageDays = Math.Max(0, (now - capturedAt).TotalDays);
        return Math.Exp(-ageDays / 14d);
    }

    private static DateTimeOffset WeightedMedian(IReadOnlyList<WeightedSignal> values)
    {
        var midpoint = values.Sum(item => item.Weight) / 2d;
        var accumulated = 0d;
        foreach (var item in values)
        {
            accumulated += item.Weight;
            if (accumulated >= midpoint)
            {
                return item.Signal.TargetAt!.Value;
            }
        }

        return values[^1].Signal.TargetAt!.Value;
    }

    private static double Median(IReadOnlyList<double> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[middle - 1] + sortedValues[middle]) / 2d
            : sortedValues[middle];
    }

    private static string NormalizeAuthor(string author) =>
        author.Trim().TrimStart('@').ToLowerInvariant();

    private sealed record WeightedSignal(QuotaCommunitySignal Signal, double Weight);

    private sealed record WeightedDate(DateOnly Date, double Weight);

    private sealed class AllocatedDate(DateOnly date, int probability, double fraction)
    {
        public DateOnly Date { get; } = date;

        public int Probability { get; set; } = probability;

        public double Fraction { get; } = fraction;
    }
}
