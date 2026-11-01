namespace FalconLoad.Core.Metrics;

/// <summary>The outcome of a single request.</summary>
/// <param name="StepIndex">Index of the request in the plan.</param>
/// <param name="StartedAt">When the request started, relative to the start of the run.</param>
/// <param name="Latency">Time until the full response body was read.</param>
/// <param name="StatusCode">HTTP status code, or null when no response was received.</param>
/// <param name="Success">Whether the response matched the expected status codes and passed every check.</param>
/// <param name="Error">Short description of a transport error, unexpected status or failed check.</param>
/// <param name="FailedChecks">Bit <c>i</c> is set when the response failed the request's check <c>i</c>. Checks only run on responses.</param>
/// <param name="StatusLabel">
/// What stands in for the status code when the protocol has none, such as <see cref="ReplyStatus"/> for a WebSocket
/// message that was answered. Null with a status code, or when nothing came back.
/// </param>
public readonly record struct RequestResult(
    int StepIndex,
    TimeSpan StartedAt,
    TimeSpan Latency,
    int? StatusCode,
    bool Success,
    string? Error,
    long BytesSent,
    long BytesReceived,
    ulong FailedChecks = 0,
    string? StatusLabel = null)
{
    /// <summary>The status of a WebSocket request whose reply arrived.</summary>
    public const string ReplyStatus = "Reply";

    /// <summary>The status of a WebSocket request that was sent without waiting for a reply.</summary>
    public const string SentStatus = "Sent";

    /// <summary>The status recorded when no response came back at all.</summary>
    public const string NoResponseStatus = "No response";

    /// <summary>Whether a response came back, so its checks ran.</summary>
    public bool HasResponse => StatusCode is not null || StatusLabel is not null;
}
