namespace FalconLoad.Core.Metrics;

/// <summary>The outcome of a single request.</summary>
/// <param name="StepIndex">Index of the request in the plan.</param>
/// <param name="StartedAt">When the request started, relative to the start of the run.</param>
/// <param name="Latency">Time until the full response body was read.</param>
/// <param name="StatusCode">HTTP status code, or null when no response was received.</param>
/// <param name="Success">Whether the response matched the expected status codes.</param>
/// <param name="Error">Short description of a transport error or unexpected status.</param>
public readonly record struct RequestResult(
    int StepIndex,
    TimeSpan StartedAt,
    TimeSpan Latency,
    int? StatusCode,
    bool Success,
    string? Error,
    long BytesSent,
    long BytesReceived);
