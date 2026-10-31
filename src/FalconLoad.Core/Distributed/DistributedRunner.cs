using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Distributed;

public sealed class DistributedRunnerOptions
{
    /// <summary>How far ahead the shared start time is set, so every agent has heard about it before it arrives.</summary>
    public TimeSpan StartDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often each agent's results are fetched and merged.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>An agent that hasn't answered for this long is given up on; the test goes on without it.</summary>
    public TimeSpan LostAfter { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>After the controller stops the test, how long to wait for the agents' final results.</summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Shown with the run on each agent, so people there know where it came from.</summary>
    public string? ControllerName { get; set; }
}

/// <summary>
/// Runs one test on several agents at once: splits it with <see cref="LoadSplitter"/>, starts every share at the same
/// moment, fetches the agents' results while they run and merges them with <see cref="ResultCombiner"/>, and stops
/// them together. A runner instance runs once.
/// </summary>
public sealed class DistributedRunner
{
    private readonly LoadTestPlan _plan;
    private readonly AgentClient _client;
    private readonly DistributedRunnerOptions _options;
    private readonly AgentState[] _agents;
    private readonly TestEnvironment? _environment;
    private readonly Lock _gate = new();

    private int _started;
    private RunState _state = RunState.Pending;
    private DateTimeOffset? _startAt;
    private TimeSpan? _finalElapsed;
    private string? _failureReason;
    private volatile bool _stopRequested;
    private CombinedResult? _combined;
    private List<string> _combineWarnings = [];

    /// <param name="environment">The environment the test runs with; it is sent to every agent along with its share.</param>
    public DistributedRunner(LoadTestPlan plan, IReadOnlyList<AgentEndpoint> agents, IReadOnlyList<int> weights, AgentClient client, DistributedRunnerOptions? options = null, TestEnvironment? environment = null)
    {
        if (agents.Count != weights.Count)
        {
            throw new ArgumentException("Every agent needs a weight.", nameof(weights));
        }

        var problems = plan.ValidateForRun(environment);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The load test plan is invalid: " + string.Join(" ", problems), nameof(plan));
        }

        _plan = plan.Clone();
        _plan.Environment = environment?.Name;
        _environment = environment;
        _client = client;
        _options = options ?? new DistributedRunnerOptions();
        _agents = LoadSplitter.Split(_plan, weights).Select(s => new AgentState(agents[s.AgentIndex], s.Plan)).ToArray();
    }

    public LoadTestPlan Plan => _plan;

    /// <summary>Each agent's results as last fetched, with the histograms that let them be combined again later.</summary>
    public IReadOnlyList<RunExport> GetAgentExports()
    {
        lock (_gate)
        {
            return _agents.Select(a => a.Results).OfType<RunExport>().ToList();
        }
    }

    /// <summary>The agents the test was split over. A test with fewer virtual users than agents leaves some out.</summary>
    public IReadOnlyList<AgentEndpoint> Agents => _agents.Select(a => a.Endpoint).ToList();

    public DistributedSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotLocked();
        }
    }

    /// <summary>Runs the test to its end. Cancelling <paramref name="cancellationToken"/> stops every agent and waits for their final results.</summary>
    public async Task<DistributedSnapshot> RunAsync(Action<DistributedSnapshot>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("A DistributedRunner can only run once.");
        }

        try
        {
            if (await ProbeAllAsync(cancellationToken).ConfigureAwait(false) && await StartAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollUntilDoneAsync(onProgress, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped while the agents were being contacted. Any that did start are told to stop; their results aren't waited for.
            await StopAllAsync().ConfigureAwait(false);
            Finish(RunState.Cancelled);
        }

        var final = GetSnapshot();
        onProgress?.Invoke(final);
        return final;
    }

    private async Task<bool> ProbeAllAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_agents.Select(async agent =>
        {
            try
            {
                var probe = await _client.ProbeAsync(agent.Endpoint, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    agent.Probe = probe;
                    agent.LastSeen = DateTimeOffset.UtcNow;
                }
            }
            catch (AgentException ex)
            {
                lock (_gate)
                {
                    agent.Fail(ex.Message);
                }
            }
        })).ConfigureAwait(false);

        if (_agents.Any(a => a.Phase == AgentRunPhase.Failed))
        {
            Finish(RunState.Failed, "The test wasn't started because not every agent could be reached. " + FailureList());
            return false;
        }

        return true;
    }

    private async Task<bool> StartAllAsync(CancellationToken cancellationToken)
    {
        // Agents are told to start at a time that is still a little ahead for the slowest of them, each on its own clock.
        var slowest = _agents.Max(a => a.Probe!.RoundTrip);
        var startAt = DateTimeOffset.UtcNow + TimeSpan.FromTicks(Math.Max(_options.StartDelay.Ticks, slowest.Ticks * 3));
        lock (_gate)
        {
            _startAt = startAt;
            _state = RunState.Running;
        }

        await Task.WhenAll(_agents.Select(async agent =>
        {
            var request = new AgentStartRequest(agent.Share, startAt + agent.Probe!.ClockOffset, _options.ControllerName, _environment);
            try
            {
                var started = await _client.StartAsync(agent.Endpoint, request, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    agent.RunId = started.RunId;
                    agent.Phase = AgentRunPhase.Starting;
                    agent.LastSeen = DateTimeOffset.UtcNow;
                }
            }
            catch (AgentException ex)
            {
                lock (_gate)
                {
                    agent.Fail(ex.Message);
                }
            }
        })).ConfigureAwait(false);

        if (_agents.Any(a => a.Phase == AgentRunPhase.Failed))
        {
            // One agent alone would send only part of the load, so the agents that did start are stopped again.
            await StopAllAsync().ConfigureAwait(false);
            Finish(RunState.Failed, "Not every agent accepted its share, so the test was stopped. " + FailureList());
            return false;
        }

        return true;
    }

    private async Task PollUntilDoneAsync(Action<DistributedSnapshot>? onProgress, CancellationToken cancellationToken)
    {
        DateTimeOffset? giveUpAt = null;
        while (true)
        {
            if (!_stopRequested && cancellationToken.IsCancellationRequested)
            {
                _stopRequested = true;
                giveUpAt = DateTimeOffset.UtcNow + _options.StopGrace;
                await StopAllAsync().ConfigureAwait(false);
            }

            await Task.WhenAll(_agents.Where(a => a.IsActive).Select(FetchAsync)).ConfigureAwait(false);
            lock (_gate)
            {
                Recombine();
            }

            var done = _agents.All(a => !a.IsActive);
            if (done || (giveUpAt is { } deadline && DateTimeOffset.UtcNow >= deadline))
            {
                break;
            }

            onProgress?.Invoke(GetSnapshot());
            await DelayAsync(_options.PollInterval, _stopRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            foreach (var agent in _agents.Where(a => a.IsActive))
            {
                agent.Phase = AgentRunPhase.Lost;
                agent.Error = $"{agent.Endpoint.Name} didn't report its final results after the stop.";
            }

            Finish(FinalState());
            Recombine();
        }
    }

    private async Task FetchAsync(AgentState agent)
    {
        using var timeout = new CancellationTokenSource(_options.PollInterval * 3);
        try
        {
            var export = await _client.GetResultsAsync(agent.Endpoint, agent.RunId!.Value, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                agent.LastSeen = DateTimeOffset.UtcNow;
                agent.Error = null;

                // Results are stamped with the agent's clock; corrected to ours they line up on the shared timeline.
                agent.Results = export with { Machine = agent.Endpoint.Name, StartedAt = export.StartedAt - agent.Probe!.ClockOffset };
                if (export.Results.State == RunState.Failed)
                {
                    agent.Fail(export.Results.FailureReason ?? "The run failed on the agent.");
                }
                else if (export.Results.IsFinished)
                {
                    agent.Phase = AgentRunPhase.Finished;
                }
                else if (export.Results.State == RunState.Running)
                {
                    agent.Phase = AgentRunPhase.Running;
                }
            }
        }
        catch (Exception ex) when (ex is AgentException or OperationCanceledException)
        {
            lock (_gate)
            {
                var message = ex is AgentException ? ex.Message : $"{agent.Endpoint.Name} didn't answer in time.";
                var missingFor = DateTimeOffset.UtcNow - agent.LastSeen;
                if (ex is AgentException { StatusCode: System.Net.HttpStatusCode.NotFound } || missingFor > _options.LostAfter)
                {
                    agent.Phase = AgentRunPhase.Lost;
                    agent.Error = ex is AgentException { StatusCode: System.Net.HttpStatusCode.NotFound }
                        ? $"{agent.Endpoint.Name} no longer knows the run; it may have been restarted. Its results up to then are counted."
                        : $"{agent.Endpoint.Name} hasn't answered for {missingFor.TotalSeconds:0} seconds. Its results up to then are counted. {message}";
                }
                else
                {
                    agent.Error = message;
                }
            }
        }
    }

    private async Task StopAllAsync()
    {
        await Task.WhenAll(_agents.Where(a => a.RunId is not null && a.IsActive).Select(async agent =>
        {
            using var timeout = new CancellationTokenSource(_options.StopGrace);
            try
            {
                await _client.StopAsync(agent.Endpoint, agent.RunId!.Value, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AgentException or OperationCanceledException)
            {
                lock (_gate)
                {
                    agent.Error = $"Couldn't stop {agent.Endpoint.Name}: {(ex is AgentException ? ex.Message : "no answer")}";
                }
            }
        })).ConfigureAwait(false);
    }

    private void Recombine()
    {
        var exports = _agents.Select(a => a.Results).OfType<RunExport>().ToList();
        if (exports.Count == 0)
        {
            return;
        }

        try
        {
            _combined = ResultCombiner.Combine(exports, live: _state == RunState.Running);
            _combineWarnings = [];
        }
        catch (ArgumentException ex)
        {
            _combineWarnings = [$"The agents' results couldn't be merged: {ex.Message}"];
        }
    }

    private RunState FinalState()
    {
        if (_agents.Any(a => a.Phase is AgentRunPhase.Failed or AgentRunPhase.Lost))
        {
            _failureReason = FailureList();
            return RunState.Failed;
        }

        if (_stopRequested || _agents.Any(a => a.Results?.Results.State == RunState.Cancelled))
        {
            return RunState.Cancelled;
        }

        return RunState.Completed;
    }

    private string FailureList() =>
        string.Join(" ", _agents.Where(a => a.Phase is AgentRunPhase.Failed or AgentRunPhase.Lost).Select(a => a.Error ?? a.Endpoint.Name));

    private void Finish(RunState state, string? reason = null)
    {
        lock (_gate)
        {
            _state = state;
            _failureReason ??= reason;
            _finalElapsed = ElapsedLocked();
        }
    }

    private TimeSpan ElapsedLocked()
    {
        if (_finalElapsed is { } final)
        {
            return final;
        }

        var elapsed = _startAt is { } start ? DateTimeOffset.UtcNow - start : TimeSpan.Zero;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private DistributedSnapshot SnapshotLocked()
    {
        var warnings = new List<string>(_combineWarnings);
        if (_combined is { } combined)
        {
            warnings.AddRange(combined.Warnings);
        }

        foreach (var agent in _agents)
        {
            if (agent.Probe is { } probe && Math.Abs(probe.ClockOffset.TotalSeconds) >= 1)
            {
                warnings.Add($"{agent.Endpoint.Name}'s clock is {Math.Abs(probe.ClockOffset.TotalSeconds):0.#}s {(probe.ClockOffset > TimeSpan.Zero ? "ahead" : "behind")}. Its start time and timeline were corrected for it.");
            }

            if (agent.Phase is AgentRunPhase.Lost || (agent.Error is not null && agent.Phase is AgentRunPhase.Running or AgentRunPhase.Starting))
            {
                warnings.Add(agent.Error!);
            }

            if (agent.Results?.Results.State == RunState.Cancelled && !_stopRequested)
            {
                warnings.Add($"The run was stopped on {agent.Endpoint.Name} itself.");
            }
        }

        return new DistributedSnapshot
        {
            State = _state,
            Elapsed = ElapsedLocked(),
            StartedAt = _startAt,
            Agents = _agents.Select(a => a.ToStatus()).ToList(),
            Combined = _combined,
            Warnings = warnings,
            FailureReason = _failureReason,
        };
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A stop request ends the wait early; the loop then stops the agents.
        }
    }

    private sealed class AgentState(AgentEndpoint endpoint, LoadTestPlan share)
    {
        public AgentEndpoint Endpoint { get; } = endpoint;

        public LoadTestPlan Share { get; } = share;

        public AgentRunPhase Phase { get; set; } = AgentRunPhase.Connecting;

        public AgentProbe? Probe { get; set; }

        public Guid? RunId { get; set; }

        public RunExport? Results { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Whether the controller still expects results from this agent.</summary>
        public bool IsActive => RunId is not null && Phase is AgentRunPhase.Starting or AgentRunPhase.Running;

        public void Fail(string error)
        {
            Phase = AgentRunPhase.Failed;
            Error = error;
        }

        public AgentRunStatus ToStatus() => new(
            Endpoint.Name,
            Endpoint.BaseUrl,
            Share.Profile,
            Phase,
            RunId,
            Probe?.Info.Machine,
            Probe?.ClockOffset,
            Results?.Results,
            Error,
            Probe is null ? null : LastSeen);
    }
}
