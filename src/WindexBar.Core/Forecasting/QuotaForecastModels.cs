using WindexBar.Core.Models;

namespace WindexBar.Core.Forecasting;

public enum QuotaSignalStatus
{
    Pending,
    Confirmed,
    Missed
}

public sealed record QuotaCommunitySignal(
    Guid Id,
    string SourceUrl,
    string Author,
    string Note,
    DateTimeOffset CapturedAt,
    DateTimeOffset? TargetAt,
    int Reliability,
    QuotaSignalStatus Status);

public sealed record QuotaCommunitySignalDraft(
    string SourceUrl,
    string Author,
    string Note,
    DateTimeOffset? TargetAt,
    int Reliability = 60);

public sealed record QuotaResetObservation(
    string EvidenceId,
    DateTimeOffset OccurredAt,
    string Kind,
    string? Description);

public sealed class QuotaForecastState
{
    public int Version { get; set; } = 1;

    public List<QuotaCommunitySignal> Signals { get; set; } = [];

    public List<QuotaResetObservation> Observations { get; set; } = [];

    public List<QuotaWindowCheckpoint> WindowCheckpoints { get; set; } = [];
}

public sealed record QuotaWindowCheckpoint(
    string WindowId,
    double UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt,
    DateTimeOffset ObservedAt);

public sealed record OfficialQuotaResetForecast(
    string WindowName,
    DateTimeOffset ResetsAt,
    double RemainingPercent);

public sealed record ExtraQuotaResetForecast(
    DateTimeOffset TargetAt,
    DateTimeOffset WindowStartsAt,
    DateTimeOffset WindowEndsAt,
    int Confidence,
    string Basis,
    int EvidenceCount,
    int DistinctAuthorCount,
    IReadOnlyList<QuotaDateProbability> DateProbabilities)
{
    public int NoExtraResetProbability => Math.Max(0, 100 - DateProbabilities.Sum(item => item.Probability));
}

public sealed record QuotaDateProbability(DateOnly Date, int Probability);

public sealed record QuotaForecastSnapshot(
    DateTimeOffset GeneratedAt,
    OfficialQuotaResetForecast? OfficialReset,
    ExtraQuotaResetForecast? ExtraReset,
    IReadOnlyList<QuotaCommunitySignal> Signals,
    IReadOnlyList<QuotaResetObservation> Observations)
{
    public int PendingSignalCount => Signals.Count(signal => signal.Status == QuotaSignalStatus.Pending);

    public int ConfirmedSignalCount => Signals.Count(signal => signal.Status == QuotaSignalStatus.Confirmed);

    public int MissedSignalCount => Signals.Count(signal => signal.Status == QuotaSignalStatus.Missed);
}

internal sealed record ForecastCandidate(
    DateTimeOffset TargetAt,
    TimeSpan HalfWindow,
    int Confidence,
    string Basis,
    int EvidenceCount,
    int DistinctAuthorCount);
