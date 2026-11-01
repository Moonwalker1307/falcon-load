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

    /// <summary>The errors of each request, so failures can be traced to the call that made them. Missing from results saved before error samples existed.</summary>
    public IReadOnlyList<RequestErrorCount> RequestErrors { get; init; } = [];

    /// <summary>
    /// A few failed requests per request of the plan, kept whole enough to see why they failed: the URL, headers and the
    /// start of the bodies, with secrets hidden. Missing from results saved before error samples existed.
    /// </summary>
    public IReadOnlyList<ErrorSample> ErrorSamples { get; init; } = [];

    public required IReadOnlyList<StepStats> Steps { get; init; }

    public required IReadOnlyList<TimelinePoint> Timeline { get; init; }

    /// <summary>How often each request's checks passed and failed. Missing from results saved before checks existed.</summary>
    public IReadOnlyList<CheckStats> Checks { get; init; } = [];

    /// <summary>The plan's thresholds measured against these results. Empty when the plan has none.</summary>
    public IReadOnlyList<ThresholdResult> Thresholds { get; init; } = [];

    public string? FailureReason { get; init; }

    /// <summary>How the run's OAuth2 tokens were fetched. Null when the test has no auth, and in results saved before auth existed.</summary>
    public AuthStats? Auth { get; init; }

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

/// <summary>The token requests of a run with OAuth2 auth. They are not part of the load, so they aren't counted as requests.</summary>
/// <param name="TokensFetched">Tokens received, the first one included; more than one means tokens were replaced during the run.</param>
/// <param name="FailedFetches">Token requests that failed. Requests went on with the previous token meanwhile.</param>
/// <param name="LastError">Why the last failed token request failed, with secrets masked.</param>
/// <param name="TokenLifetimeSeconds">The <c>expires_in</c> of the latest token; null when the server gave none.</param>
public sealed record AuthStats(long TokensFetched, long FailedFetches, string? LastError, double? TokenLifetimeSeconds);

public sealed record ErrorCount(string Message, long Count);

/// <summary>How often one request of the plan failed with one error.</summary>
public sealed record RequestErrorCount(int StepIndex, string Message, long Count);

/// <summary>
/// One failed request, kept as an example of an error. Secrets of the run's environment and its OAuth2 tokens are
/// hidden everywhere in it, and the values of headers that carry credentials are never kept.
/// </summary>
public sealed record ErrorSample
{
    /// <summary>The headers whose values are always hidden, whatever their value is.</summary>
    public static readonly IReadOnlySet<string> CredentialHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token", "X-Access-Token",
    };

    /// <summary>How much of each body is kept.</summary>
    public const int MaxBodyChars = 2048;

    public int StepIndex { get; init; }

    /// <summary>The method for HTTP, otherwise the protocol: GraphQL, WS or gRPC.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>The request's <see cref="Model.RequestDefinition.Route"/>.</summary>
    public string Route { get; init; } = string.Empty;

    /// <summary>The URL that was called, with its placeholders filled in.</summary>
    public string? Url { get; init; }

    /// <summary>When the request started, in seconds from the start of the run.</summary>
    public double AtSeconds { get; init; }

    public double LatencyMs { get; init; }

    /// <summary>The status code, the protocol's status such as a gRPC code, or "No response".</summary>
    public string Status { get; init; } = RequestResult.NoResponseStatus;

    public string Error { get; init; } = string.Empty;

    public IReadOnlyList<SampleHeader> RequestHeaders { get; init; } = [];

    /// <summary>The body that was sent, or null when there was none.</summary>
    public SampleBody? RequestBody { get; init; }

    public IReadOnlyList<SampleHeader> ResponseHeaders { get; init; } = [];

    /// <summary>The start of the response body, or null when no response came back or its body wasn't read.</summary>
    public SampleBody? ResponseBody { get; init; }

    /// <summary>The machine that sent the request, in the combined results of several machines.</summary>
    public string? Machine { get; init; }
}

public sealed record SampleHeader(string Name, string Value);

/// <param name="Text">The start of the body as text; null when it is binary, such as a protobuf message.</param>
/// <param name="Bytes">The size of the whole body, or of the part that was read.</param>
/// <param name="Truncated">True when <paramref name="Text"/> is only the start of the body.</param>
public sealed record SampleBody(string? Text, long Bytes, bool Truncated);

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
