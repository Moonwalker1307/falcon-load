using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Distributed;

/// <summary>Where one agent is with its share of a distributed test.</summary>
public enum AgentRunPhase
{
    /// <summary>The controller is checking the agent before the start.</summary>
    Connecting,

    /// <summary>The agent accepted its share and is waiting for the shared start time.</summary>
    Starting,

    Running,

    /// <summary>The agent's run finished, whatever its outcome; <see cref="AgentRunStatus.Results"/> says how it went.</summary>
    Finished,

    /// <summary>The agent stopped answering. Its last results are still counted.</summary>
    Lost,

    /// <summary>The agent refused to start, or its run failed.</summary>
    Failed,
}

/// <param name="Share">The load profile this agent was given.</param>
/// <param name="ClockOffset">The agent's clock minus the controller's, measured before the start.</param>
/// <param name="Results">The agent's results as last fetched; null until the first fetch.</param>
/// <param name="Error">Why the agent is <see cref="AgentRunPhase.Lost"/> or <see cref="AgentRunPhase.Failed"/>, or the last fetch error.</param>
public sealed record AgentRunStatus(
    string Name,
    Uri BaseUrl,
    LoadProfile Share,
    AgentRunPhase Phase,
    Guid? RunId,
    string? Machine,
    TimeSpan? ClockOffset,
    LoadTestSnapshot? Results,
    string? Error,
    DateTimeOffset? LastSeen);

/// <summary>An immutable view of a distributed test: every agent's state plus their results merged into one.</summary>
public sealed record DistributedSnapshot
{
    public required RunState State { get; init; }

    /// <summary>Time since the shared start, on the controller's clock. Zero until the agents have started.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>When the agents were told to start, on the controller's clock.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    public required IReadOnlyList<AgentRunStatus> Agents { get; init; }

    /// <summary>All agents' results as one, exactly as the Combine page would merge their exports. Null until the first results arrive.</summary>
    public CombinedResult? Combined { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public string? FailureReason { get; init; }

    public bool IsFinished => State is RunState.Completed or RunState.Cancelled or RunState.Failed;

    /// <summary>The merged results, or an empty snapshot before any agent has reported.</summary>
    public LoadTestSnapshot Results => Combined?.Combined ?? Empty(State, Elapsed);

    public int Concurrency => Agents.Sum(a => a.Share.PeakVirtualUsers);

    private static LoadTestSnapshot Empty(RunState state, TimeSpan elapsed) => new()
    {
        State = state,
        Elapsed = elapsed,
        TotalRequests = 0,
        SuccessfulRequests = 0,
        FailedRequests = 0,
        ActiveVirtualUsers = 0,
        BytesSent = 0,
        BytesReceived = 0,
        Latency = LatencyStats.Empty,
        StatusCodes = new Dictionary<string, long>(),
        Errors = [],
        Steps = [],
        Timeline = [],
    };
}
