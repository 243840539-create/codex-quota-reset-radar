using System.Text.Json;
using WindexBar.Core.Models;

namespace WindexBar.Core.Forecasting;

public sealed class QuotaForecastStore
{
    private readonly object _sync = new();
    private readonly string _filePath;
    private QuotaForecastState _state;

    public QuotaForecastStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultPath();
        _state = Load();
    }

    public QuotaForecastSnapshot ObserveAndForecast(UsageSnapshot? usage, DateTimeOffset now)
    {
        lock (_sync)
        {
            var changed = ObserveUsageWindows(usage, now);
            changed |= ReconcileSignals(now);
            if (changed)
            {
                Save();
            }

            return QuotaForecastEngine.Build(usage, _state, now);
        }
    }

    public QuotaForecastSnapshot AddSignal(
        QuotaCommunitySignalDraft draft,
        UsageSnapshot? usage,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            var signal = new QuotaCommunitySignal(
                Guid.NewGuid(),
                draft.SourceUrl.Trim(),
                NormalizeAuthorDisplay(draft.Author),
                draft.Note.Trim(),
                now,
                draft.TargetAt,
                QuotaTrustedSources.SuggestedReliability(draft.Author, draft.Reliability),
                QuotaSignalStatus.Pending);
            _state.Signals.Add(signal);
            ObserveUsageWindows(usage, now);
            ReconcileSignals(now);
            Save();
            return QuotaForecastEngine.Build(usage, _state, now);
        }
    }

    public QuotaForecastSnapshot AddSignals(
        IEnumerable<QuotaCommunitySignalDraft> drafts,
        UsageSnapshot? usage,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            var knownUrls = _state.Signals
                .Where(signal => !string.IsNullOrWhiteSpace(signal.SourceUrl))
                .Select(signal => signal.SourceUrl)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var draft in drafts)
            {
                var sourceUrl = draft.SourceUrl.Trim();
                if (sourceUrl.Length == 0 || !knownUrls.Add(sourceUrl))
                {
                    continue;
                }

                _state.Signals.Add(new QuotaCommunitySignal(
                    Guid.NewGuid(),
                    sourceUrl,
                    NormalizeAuthorDisplay(draft.Author),
                    draft.Note.Trim(),
                    now,
                    draft.TargetAt,
                    QuotaTrustedSources.SuggestedReliability(draft.Author, draft.Reliability),
                    QuotaSignalStatus.Pending));
                changed = true;
            }

            if (_state.Signals.Count > 200)
            {
                _state.Signals = _state.Signals
                    .OrderByDescending(signal => signal.CapturedAt)
                    .Take(200)
                    .ToList();
                changed = true;
            }

            if (changed)
            {
                ObserveUsageWindows(usage, now);
                ReconcileSignals(now);
                Save();
            }

            return QuotaForecastEngine.Build(usage, _state, now);
        }
    }

    public QuotaForecastSnapshot ClearSignals(UsageSnapshot? usage, DateTimeOffset now)
    {
        lock (_sync)
        {
            _state.Signals.Clear();
            Save();
            return QuotaForecastEngine.Build(usage, _state, now);
        }
    }

    private bool ObserveUsageWindows(UsageSnapshot? usage, DateTimeOffset now)
    {
        if (usage is null)
        {
            return false;
        }

        var evaluations = new[]
        {
            EvaluateWindow("primary", usage.Primary, now),
            EvaluateWindow("secondary", usage.Secondary, now)
        }.Where(evaluation => evaluation is not null).Cast<WindowEvaluation>().ToArray();

        var changed = false;
        foreach (var evaluation in evaluations)
        {
            _state.WindowCheckpoints.RemoveAll(checkpoint =>
                string.Equals(checkpoint.WindowId, evaluation.Current.WindowId, StringComparison.Ordinal));
            _state.WindowCheckpoints.Add(evaluation.Current);
            changed |= evaluation.ShouldPersistCheckpoint;
        }

        var unexpected = evaluations.Where(evaluation => evaluation.IsUnexpectedReset).ToArray();
        if (unexpected.Length >= 2)
        {
            var evidenceId = $"global-quota-reset:{now.ToUnixTimeSeconds() / 60}";
            if (_state.Observations.All(observation => observation.EvidenceId != evidenceId))
            {
                var description = string.Join(
                    "; ",
                    unexpected.Select(evaluation =>
                        $"{evaluation.Current.WindowId} {evaluation.Previous!.UsedPercent:0.#}% -> {evaluation.Current.UsedPercent:0.#}%"));
                _state.Observations.Add(new QuotaResetObservation(
                    evidenceId,
                    now,
                    "global-quota-reset",
                    description));
                changed = true;
            }
        }

        if (_state.Observations.Count > 100)
        {
            _state.Observations = _state.Observations
                .Where(observation => observation.Kind == "global-quota-reset")
                .OrderByDescending(observation => observation.OccurredAt)
                .Take(100)
                .ToList();
            changed = true;
        }

        return changed;
    }

    private WindowEvaluation? EvaluateWindow(string windowId, RateWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            return null;
        }

        var previous = _state.WindowCheckpoints
            .FirstOrDefault(checkpoint => string.Equals(checkpoint.WindowId, windowId, StringComparison.Ordinal));
        var significantDrop = previous is not null
            && previous.UsedPercent - window.UsedPercent >= 15
            && previous.UsedPercent >= 20;
        var comparableWindow = previous is not null
            && (previous.WindowMinutes is null
                || window.WindowMinutes is null
                || previous.WindowMinutes == window.WindowMinutes);
        var beforeScheduledReset = previous?.ResetsAt is null
            || now < previous.ResetsAt.Value.AddMinutes(-15);
        var isUnexpectedReset = significantDrop && comparableWindow && beforeScheduledReset;

        var current = new QuotaWindowCheckpoint(
            windowId,
            window.UsedPercent,
            window.WindowMinutes,
            window.ResetsAt,
            now);
        var shouldPersist = previous is null
            || isUnexpectedReset
            || previous.ResetsAt != window.ResetsAt
            || previous.WindowMinutes != window.WindowMinutes;
        return new WindowEvaluation(previous, current, isUnexpectedReset, shouldPersist);
    }

    private bool ReconcileSignals(DateTimeOffset now)
    {
        var globalResets = _state.Observations
            .Where(observation => observation.Kind == "global-quota-reset")
            .ToArray();
        var reconciled = QuotaForecastEngine.ReconcileSignals(_state.Signals, globalResets, now);
        if (reconciled.SequenceEqual(_state.Signals))
        {
            return false;
        }

        _state.Signals = reconciled.ToList();
        return true;
    }

    private QuotaForecastState Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new QuotaForecastState();
            }

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize(json, WindexBarJsonContext.Default.QuotaForecastState)
                ?? new QuotaForecastState();
            state.Signals ??= [];
            state.Observations ??= [];
            state.WindowCheckpoints ??= [];
            state.Observations = state.Observations
                .Where(observation => observation.Kind == "global-quota-reset")
                .ToList();
            return state;
        }
        catch
        {
            return new QuotaForecastState();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_state, WindexBarJsonContext.Default.QuotaForecastState);
            File.WriteAllText(_filePath, json);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "WindexBar", "quota-forecast.json");
    }

    private static string NormalizeAuthorDisplay(string author)
    {
        var value = author.Trim();
        return value.Length == 0 || value.StartsWith('@') ? value : $"@{value}";
    }

    private sealed record WindowEvaluation(
        QuotaWindowCheckpoint? Previous,
        QuotaWindowCheckpoint Current,
        bool IsUnexpectedReset,
        bool ShouldPersistCheckpoint);
}
