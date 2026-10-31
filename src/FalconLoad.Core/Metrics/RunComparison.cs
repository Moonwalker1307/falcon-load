using FalconLoad.Core.Model;

namespace FalconLoad.Core.Metrics;

/// <summary>
/// How much worse a run may be than its baseline before the change counts as a regression. Changes smaller than these
/// are treated as noise, and changes as large in the good direction count as improvements.
/// </summary>
public sealed record RegressionThresholds
{
    /// <summary>How much a latency (mean or a percentile) may grow, in percent of the baseline.</summary>
    public double LatencyPercent { get; init; } = 10;

    /// <summary>
    /// The smallest latency change, in milliseconds, that can count at all. Without it a fast endpoint would flag 0.4 ms
    /// going to 0.5 ms as a 25% regression.
    /// </summary>
    public double MinLatencyChangeMs { get; init; } = 1;

    /// <summary>How much throughput may drop, in percent of the baseline.</summary>
    public double ThroughputPercent { get; init; } = 10;

    /// <summary>How much the error rate may grow, in percentage points (1 means 2% to 3%).</summary>
    public double ErrorRatePoints { get; init; } = 1;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (LatencyPercent is < 0 or > 10_000 || double.IsNaN(LatencyPercent))
        {
            problems.Add("The latency threshold must be between 0 and 10,000 percent.");
        }

        if (MinLatencyChangeMs is < 0 or > 3_600_000 || double.IsNaN(MinLatencyChangeMs))
        {
            problems.Add("The smallest latency change must be between 0 ms and one hour.");
        }

        if (ThroughputPercent is < 0 or > 100 || double.IsNaN(ThroughputPercent))
        {
            problems.Add("The throughput threshold must be between 0 and 100 percent.");
        }

        if (ErrorRatePoints is < 0 or > 100 || double.IsNaN(ErrorRatePoints))
        {
            problems.Add("The error rate threshold must be between 0 and 100 percentage points.");
        }

        return problems;
    }
}

/// <summary>What a metric's change means, judged against <see cref="RegressionThresholds"/>.</summary>
public enum ChangeVerdict
{
    /// <summary>The metric is shown for context but never judged, such as the request count or the maximum latency.</summary>
    NotJudged,

    /// <summary>Within the thresholds either way.</summary>
    Unchanged,

    Improved,

    Regressed,
}

/// <summary>The unit of a compared metric, so a client can format both values and the change.</summary>
public enum MetricUnit
{
    Milliseconds,
    RequestsPerSecond,

    /// <summary>A fraction between 0 and 1, such as the error rate. Its change is reported in the same unit.</summary>
    Fraction,
    Count,
    Bytes,
}

/// <param name="Key">A stable name for scripts, such as <c>p95Ms</c>.</param>
/// <param name="Label">The name shown in the UI.</param>
public sealed record MetricChange(string Key, string Label, MetricUnit Unit, double Baseline, double Current, ChangeVerdict Verdict)
{
    public double Change => Current - Baseline;

    /// <summary>The change as a fraction of the baseline; null when the baseline is 0.</summary>
    public double? RelativeChange => Baseline == 0 ? null : Change / Baseline;
}

/// <summary>One request of the test compared between the two runs. Requests are matched by their position in the test.</summary>
public sealed record StepComparison(int StepIndex, string Name, IReadOnlyList<MetricChange> Metrics);

/// <summary>A run compared with a baseline run of the same test.</summary>
/// <param name="Warnings">Things that make the comparison less meaningful, such as a changed load profile.</param>
public sealed record RunComparison(
    Guid BaselineRunId,
    DateTimeOffset BaselineStartedAt,
    Guid RunId,
    DateTimeOffset StartedAt,
    RegressionThresholds Thresholds,
    IReadOnlyList<MetricChange> Overall,
    IReadOnlyList<StepComparison> Steps,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when any metric, overall or of one request, got worse by more than the thresholds allow.</summary>
    public bool HasRegression =>
        Overall.Any(m => m.Verdict == ChangeVerdict.Regressed) ||
        Steps.Any(s => s.Metrics.Any(m => m.Verdict == ChangeVerdict.Regressed));

    public int RegressionCount =>
        Overall.Count(m => m.Verdict == ChangeVerdict.Regressed) +
        Steps.Sum(s => s.Metrics.Count(m => m.Verdict == ChangeVerdict.Regressed));

    public int ImprovementCount =>
        Overall.Count(m => m.Verdict == ChangeVerdict.Improved) +
        Steps.Sum(s => s.Metrics.Count(m => m.Verdict == ChangeVerdict.Improved));
}

/// <summary>
/// Compares a run with a baseline run, metric by metric. Latency, throughput and error rate are judged against
/// <see cref="RegressionThresholds"/>; counts and the maximum latency are shown for context only, because one slow
/// request moves the maximum and the request count follows from the load profile.
/// </summary>
public static class RunComparer
{
    /// <summary>Compares two exported runs, warning when the test changed between them.</summary>
    public static RunComparison Compare(RunExport baseline, RunExport current, RegressionThresholds? thresholds = null)
    {
        thresholds ??= new RegressionThresholds();
        var comparison = Compare(baseline.Results, current.Results, thresholds);
        var warnings = new List<string>(PlanWarnings(baseline.Plan, current.Plan));
        warnings.AddRange(comparison.Warnings);
        return comparison with
        {
            BaselineRunId = baseline.Id,
            BaselineStartedAt = baseline.StartedAt,
            RunId = current.Id,
            StartedAt = current.StartedAt,
            Warnings = warnings,
        };
    }

    /// <summary>Compares the results of two runs. The ids and start times of the result are left empty.</summary>
    public static RunComparison Compare(LoadTestSnapshot baseline, LoadTestSnapshot current, RegressionThresholds? thresholds = null)
    {
        thresholds ??= new RegressionThresholds();
        var warnings = new List<string>();
        AddStateWarning(warnings, "The baseline", baseline);
        AddStateWarning(warnings, "This run", current);

        var bothHaveRequests = baseline.TotalRequests > 0 && current.TotalRequests > 0;
        var overall = new List<MetricChange>
        {
            Throughput("requestsPerSecond", "Throughput", baseline.RequestsPerSecond, current.RequestsPerSecond, bothHaveRequests, thresholds),
            ErrorRate("errorRate", "Error rate", baseline.ErrorRate, current.ErrorRate, bothHaveRequests, thresholds),
        };
        overall.AddRange(Latencies(baseline.Latency, current.Latency, bothHaveRequests, thresholds, includeMax: true));
        overall.Add(Info("totalRequests", "Requests", MetricUnit.Count, baseline.TotalRequests, current.TotalRequests));
        overall.Add(Info("bytesReceived", "Received", MetricUnit.Bytes, baseline.BytesReceived, current.BytesReceived));

        var steps = new List<StepComparison>();
        var common = Math.Min(baseline.Steps.Count, current.Steps.Count);
        if (baseline.Steps.Count != current.Steps.Count)
        {
            warnings.Add($"The baseline has {Plural(baseline.Steps.Count, "request")} and this run has {current.Steps.Count}; only the first {common} are compared, by position.");
        }

        for (var i = 0; i < common; i++)
        {
            var before = baseline.Steps[i];
            var after = current.Steps[i];
            var judged = before.Requests > 0 && after.Requests > 0;
            var metrics = new List<MetricChange>
            {
                Info("requests", "Requests", MetricUnit.Count, before.Requests, after.Requests),
                ErrorRate("errorRate", "Error rate", Rate(before.Failures, before.Requests), Rate(after.Failures, after.Requests), judged, thresholds),
            };
            metrics.AddRange(Latencies(before.Latency, after.Latency, judged, thresholds, includeMax: false));
            steps.Add(new StepComparison(after.StepIndex, after.Name, metrics));
        }

        return new RunComparison(Guid.Empty, default, Guid.Empty, default, thresholds, overall, steps, warnings);
    }

    private static IEnumerable<MetricChange> Latencies(LatencyStats before, LatencyStats after, bool judged, RegressionThresholds t, bool includeMax)
    {
        yield return Latency("meanMs", "Mean latency", before.MeanMs, after.MeanMs, judged, t);
        yield return Latency("p50Ms", "p50 latency", before.P50Ms, after.P50Ms, judged, t);
        yield return Latency("p90Ms", "p90 latency", before.P90Ms, after.P90Ms, judged, t);
        yield return Latency("p95Ms", "p95 latency", before.P95Ms, after.P95Ms, judged, t);
        yield return Latency("p99Ms", "p99 latency", before.P99Ms, after.P99Ms, judged, t);
        if (includeMax)
        {
            yield return Info("maxMs", "Max latency", MetricUnit.Milliseconds, before.MaxMs, after.MaxMs);
        }
    }

    private static MetricChange Latency(string key, string label, double before, double after, bool judged, RegressionThresholds t)
    {
        // A change only counts when it beats both the relative and the absolute threshold.
        var allowed = Math.Max(before * t.LatencyPercent / 100, t.MinLatencyChangeMs);
        var change = after - before;
        var verdict = !judged ? ChangeVerdict.NotJudged
            : change > allowed ? ChangeVerdict.Regressed
            : change < -allowed ? ChangeVerdict.Improved
            : ChangeVerdict.Unchanged;
        return new MetricChange(key, label, MetricUnit.Milliseconds, before, after, verdict);
    }

    private static MetricChange Throughput(string key, string label, double before, double after, bool judged, RegressionThresholds t)
    {
        var allowed = before * t.ThroughputPercent / 100;
        var change = after - before;
        var verdict = !judged ? ChangeVerdict.NotJudged
            : change < -allowed && change < 0 ? ChangeVerdict.Regressed
            : change > allowed && change > 0 ? ChangeVerdict.Improved
            : ChangeVerdict.Unchanged;
        return new MetricChange(key, label, MetricUnit.RequestsPerSecond, before, after, verdict);
    }

    private static MetricChange ErrorRate(string key, string label, double before, double after, bool judged, RegressionThresholds t)
    {
        var allowed = t.ErrorRatePoints / 100;
        var change = after - before;
        var verdict = !judged ? ChangeVerdict.NotJudged
            : change > allowed && change > 0 ? ChangeVerdict.Regressed
            : change < -allowed && change < 0 ? ChangeVerdict.Improved
            : ChangeVerdict.Unchanged;
        return new MetricChange(key, label, MetricUnit.Fraction, before, after, verdict);
    }

    private static MetricChange Info(string key, string label, MetricUnit unit, double before, double after) =>
        new(key, label, unit, before, after, ChangeVerdict.NotJudged);

    private static double Rate(long failures, long requests) => requests == 0 ? 0 : (double)failures / requests;

    private static void AddStateWarning(List<string> warnings, string which, LoadTestSnapshot results)
    {
        if (results.State == RunState.Cancelled)
        {
            warnings.Add($"{which} was stopped early, so its numbers cover only part of the test.");
        }
        else if (results.State == RunState.Failed)
        {
            warnings.Add($"{which} failed{(results.FailureReason is { } reason ? $": {reason}" : ".")}");
        }
        else if (!results.IsFinished)
        {
            warnings.Add($"{which} is still running, so its numbers will change.");
        }

        if (results.TotalRequests == 0)
        {
            warnings.Add($"{which} has no completed requests, so nothing is judged.");
        }
    }

    /// <summary>Names the load profile settings that differ, since they change what the numbers mean.</summary>
    private static IEnumerable<string> PlanWarnings(LoadTestPlan baseline, LoadTestPlan current)
    {
        if (baseline.Id != current.Id)
        {
            yield return $"These are runs of different tests: \"{baseline.Name}\" and \"{current.Name}\".";
        }

        var a = baseline.Profile;
        var b = current.Profile;
        if (a.Concurrency != b.Concurrency)
        {
            yield return $"Virtual users changed from {a.Concurrency:#,0} to {b.Concurrency:#,0}.";
        }

        if (a.StopCondition != b.StopCondition)
        {
            yield return "The stop condition changed between the runs.";
        }
        else if (a.StopCondition == StopCondition.Duration && a.DurationSeconds != b.DurationSeconds)
        {
            yield return $"The duration changed from {a.DurationSeconds:#,0}s to {b.DurationSeconds:#,0}s.";
        }
        else if (a.StopCondition == StopCondition.RequestCount && a.TotalRequests != b.TotalRequests)
        {
            yield return $"The request count changed from {a.TotalRequests:#,0} to {b.TotalRequests:#,0}.";
        }

        if (a.RampUpSeconds != b.RampUpSeconds)
        {
            yield return $"The ramp-up changed from {a.RampUpSeconds:#,0}s to {b.RampUpSeconds:#,0}s.";
        }

        if (a.MaxRequestsPerSecond != b.MaxRequestsPerSecond)
        {
            yield return $"The rate cap changed from {Cap(a.MaxRequestsPerSecond)} to {Cap(b.MaxRequestsPerSecond)}.";
        }

        var common = Math.Min(baseline.Requests.Count, current.Requests.Count);
        for (var i = 0; i < common; i++)
        {
            var before = baseline.Requests[i];
            var after = current.Requests[i];
            if (!string.Equals(before.Method, after.Method, StringComparison.OrdinalIgnoreCase) || before.Url != after.Url)
            {
                yield return $"Request {i + 1} changed from {before.Method} {before.Url} to {after.Method} {after.Url}.";
            }
        }
    }

    private static string Cap(double perSecond) => perSecond > 0 ? $"{perSecond:#,0.##} req/s" : "no cap";

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
