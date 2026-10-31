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

    public string? FailureReason { get; init; }

    /// <summary>Average throughput over the whole run.</summary>
    public double RequestsPerSecond => Elapsed.TotalSeconds <= 0 ? 0 : TotalRequests / Elapsed.TotalSeconds;

    public double ErrorRate => TotalRequests == 0 ? 0 : (double)FailedRequests / TotalRequests;

    public bool IsFinished => State is RunState.Completed or RunState.Cancelled or RunState.Failed;
}

public sealed record ErrorCount(string Message, long Count);

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
