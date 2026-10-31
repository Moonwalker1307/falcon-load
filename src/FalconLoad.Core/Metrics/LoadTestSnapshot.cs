namespace FalconLoad.Core.Metrics;

public enum RunState
{
    Pending,
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>An immutable point-in-time view of a load test's results.</summary>
public sealed record LoadTestSnapshot
{
    public required RunState State { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required long TotalRequests { get; init; }

    public required long SuccessfulRequests { get; init; }

    public required long FailedRequests { get; init; }

    public required int ActiveVirtualUsers { get; init; }

    public required long BytesSent { get; init; }

    public required long BytesReceived { get; init; }

    public required LatencyStats Latency { get; init; }

    public required IReadOnlyDictionary<string, long> StatusCodes { get; init; }

    public required IReadOnlyList<ErrorCount> Errors { get; init; }

    public required IReadOnlyList<StepStats> Steps { get; init; }

    public required IReadOnlyList<TimelinePoint> Timeline { get; init; }

    /// <summary>How often each request's checks passed and failed. Missing from results saved before checks existed.</summary>
    public IReadOnlyList<CheckStats> Checks { get; init; } = [];

    /// <summary>The plan's thresholds measured against these results. Empty when the plan has none.</summary>
    public IReadOnlyList<ThresholdResult> Thresholds { get; init; } = [];

    public string? FailureReason { get; init; }

    /// <summary>
    /// The verdict a CI job checks: null when the plan has no thresholds; otherwise true when every threshold holds and
    /// the run completed. A run that was stopped early or failed doesn't pass. While a test runs it says whether the
    /// thresholds hold so far.
    /// </summary>
    public bool? Passed => Thresholds.Count == 0 ? null
        : State is RunState.Cancelled or RunState.Failed ? false
        : Thresholds.All(t => t.Passed);

    /// <summary>Average throughput over the whole run.</summary>
    public double RequestsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : TotalRequests / Elapsed.TotalSeconds;

    public double ErrorRate => TotalRequests == 0 ? 0 : (double)FailedRequests / TotalRequests;

    public bool IsFinished => State is RunState.Completed or RunState.Cancelled or RunState.Failed;
}

public sealed record ErrorCount(string Message, long Count);

/// <summary>How often one check on a request passed and failed. Only responses are checked, not transport errors.</summary>
public sealed record CheckStats(int StepIndex, string Name, long Passed, long Failed);

/// <summary>One threshold measured against a run.</summary>
/// <param name="Name">The threshold in words, with the request it applies to, e.g. "p95 latency &lt; 500 ms".</param>
/// <param name="Limit">The threshold's limit, in the metric's unit.</param>
/// <param name="Actual">The measured value in the same unit, or null when there were no requests to measure.</param>
public sealed record ThresholdResult(string Name, string Unit, double Limit, double? Actual, bool Passed);

public sealed record StepStats(int StepIndex, string Name, long Requests, long Failures, LatencyStats Latency);

/// <summary>Results for one second of the run.</summary>
public sealed record TimelinePoint(int Second, long Requests, long Failures, double MeanLatencyMs, double P95LatencyMs);

/// <summary>
/// The histograms behind a finished run's results, kept apart from <see cref="LoadTestSnapshot"/> so live snapshots
/// stay small. They go into an exported result so that runs from several machines can be combined.
/// </summary>
public sealed record ResultHistograms(
    HistogramData Overall,
    IReadOnlyList<HistogramData> Steps,
    IReadOnlyList<SecondHistogram> Timeline);

/// <summary>Latencies of the requests that completed in one second of the run. Seconds without requests are left out.</summary>
public sealed record SecondHistogram(int Second, long Failures, HistogramData Latency);
