using FalconLoad.Core.Model;

namespace FalconLoad.Core.Distributed;

/// <summary>
/// Another falcon-load that a controller can run a share of a test on. The agent's base URL is the address of its
/// web UI, and the token is the one its <c>FalconLoad:AgentToken</c> setting holds.
/// </summary>
public sealed record AgentEndpoint(string Name, Uri BaseUrl, string Token);

/// <summary>What an agent says about itself, from <c>GET /api/agent</c>.</summary>
/// <param name="Time">The agent's clock when it answered, so a controller can measure the offset between the clocks.</param>
/// <param name="RunningRuns">How many load tests the agent is running right now, for any controller.</param>
public sealed record AgentInfo(string Machine, string Version, DateTimeOffset Time, int RunningRuns);

/// <summary>A controller's request to run a share of a test, sent as <c>POST /api/agent/runs</c>.</summary>
/// <param name="StartAt">When to start, on the agent's clock, so every agent starts at the same moment. Null starts at once.</param>
/// <param name="Controller">The controller's name, shown with the run on the agent.</param>
/// <param name="Environment">
/// The environment the test runs with, values included, so the agent needs no environment of its own. The agent keeps
/// only the name with the run, like a run started on it.
/// </param>
public sealed record AgentStartRequest(LoadTestPlan Plan, DateTimeOffset? StartAt, string? Controller, TestEnvironment? Environment = null);

/// <summary>An agent's answer to <see cref="AgentStartRequest"/>: the id to poll and stop the run by.</summary>
public sealed record AgentRunStarted(Guid RunId);

/// <summary>The result of checking an agent: what it said and how far its clock is from ours.</summary>
/// <param name="RoundTrip">How long the check took.</param>
/// <param name="ClockOffset">The agent's clock minus the controller's. Positive when the agent's clock is ahead.</param>
public sealed record AgentProbe(AgentInfo Info, TimeSpan RoundTrip, TimeSpan ClockOffset);
