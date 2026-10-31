using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Reports;

/// <summary>One condition a run must meet to pass, such as a threshold or "the run completed".</summary>
/// <param name="Name">What is checked, e.g. "p95 latency &lt; 500 ms".</param>
/// <param name="Kind">The sort of condition; the JUnit report uses it as the failure type.</param>
/// <param name="Message">Why it failed; null when it passed.</param>
/// <param name="IsError">True when the run couldn't be judged at all, as opposed to a measured value missing its limit.</param>
public sealed record VerdictCheck(string Name, string Kind, bool Passed, string? Message = null, bool IsError = false);

/// <summary>
/// Whether a finished run passed, and which conditions decided it. This is the one verdict behind the command line's exit
/// code and the HTML and JUnit reports, so a report never disagrees with the build that produced it.
/// </summary>
public sealed class RunVerdict
{
    public const string ThresholdKind = "Threshold";
    public const string RunKind = "Run";
    public const string RequestsKind = "Requests";
    public const string ErrorRateKind = "ErrorRate";

    public required IReadOnlyList<VerdictCheck> Checks { get; init; }

    public bool Passed => Checks.All(c => c.Passed);

    /// <summary>The messages of the conditions that failed, in order.</summary>
    public IReadOnlyList<string> Failures => Checks.Where(c => !c.Passed).Select(c => c.Message ?? c.Name).ToList();

    /// <summary>
    /// A test with thresholds is judged by them. One without allows no failed requests, unless
    /// <paramref name="maxErrorRate"/> (from 0 to 1) says how many may fail; when it is given it applies on top of the
    /// thresholds as well. A run that didn't complete never passes.
    /// </summary>
    public static RunVerdict Evaluate(LoadTestSnapshot snapshot, double? maxErrorRate)
    {
        var checks = new List<VerdictCheck>();
        foreach (var threshold in snapshot.Thresholds)
        {
            var message = threshold.Passed ? null
                : threshold.Actual is { } actual
                    ? $"Threshold not met: {threshold.Name} (measured {ReportFormat.Value(actual, threshold.Unit)})."
                    : $"Threshold not met: {threshold.Name} (nothing was measured).";
            checks.Add(new VerdictCheck(threshold.Name, ThresholdKind, threshold.Passed, message));
        }

        checks.Add(snapshot.State switch
        {
            RunState.Failed => new VerdictCheck("The run completed", RunKind, false, $"The test could not run: {snapshot.FailureReason}", IsError: true),
            RunState.Cancelled => new VerdictCheck("The run completed", RunKind, false, "The run was interrupted before the stop condition was reached."),
            _ => new VerdictCheck("The run completed", RunKind, true),
        });

        var sentNothing = snapshot.TotalRequests == 0 && snapshot.State == RunState.Completed;
        checks.Add(new VerdictCheck("Requests were sent", RequestsKind, !sentNothing, sentNothing ? "No requests completed." : null));

        var allowed = maxErrorRate ?? (snapshot.Thresholds.Count > 0 ? (double?)null : 0);
        if (allowed is { } limit)
        {
            var exceeded = snapshot.ErrorRate > limit;
            checks.Add(new VerdictCheck(
                $"Error rate ≤ {ReportFormat.Percent(limit)}",
                ErrorRateKind,
                !exceeded,
                exceeded ? $"{ReportFormat.Percent(snapshot.ErrorRate)} of requests failed, more than the {ReportFormat.Percent(limit)} allowed." : null));
        }

        return new RunVerdict { Checks = checks };
    }
}
