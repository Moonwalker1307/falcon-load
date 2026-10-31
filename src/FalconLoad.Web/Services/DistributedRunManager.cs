using System.Collections.Concurrent;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>A test this controller is running, or ran, on its agents.</summary>
public sealed class DistributedRun
{
    private readonly CancellationTokenSource _cancel = new();
    private DistributedRunner? _runner;
    private IReadOnlyList<RunExport> _finalExports = [];

    internal DistributedRun(DistributedRunner runner)
    {
        _runner = runner;
        Plan = runner.Plan;
        AgentNames = runner.Agents.Select(a => a.Name).ToList();
        Snapshot = runner.GetSnapshot();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public LoadTestPlan Plan { get; }

    /// <summary>When the run was created here; the agents start a moment later, at <see cref="DistributedSnapshot.StartedAt"/>.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>The name of the environment the run used, if any. Its values are not kept with the run.</summary>
    public string? EnvironmentName => Plan.Environment;

    public IReadOnlyList<string> AgentNames { get; }

    public DistributedSnapshot Snapshot { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="Snapshot"/> changes.</summary>
    public event Action<DistributedRun>? Updated;

    internal CancellationToken CancellationToken => _cancel.Token;

    /// <summary>Stops the test on every agent.</summary>
    public void Stop() => _cancel.Cancel();

    /// <summary>The merged results plus each agent's export, so the agents' runs can be combined again later.</summary>
    public DistributedRunExport ToExport()
    {
        var agents = _runner?.GetAgentExports() ?? _finalExports;
        return new DistributedRunExport
        {
            Id = Id,
            StartedAt = Snapshot.StartedAt ?? StartedAt,
            Controller = System.Environment.MachineName,
            Plan = Plan,
            Results = Snapshot.Results,
            Warnings = Snapshot.Warnings,
            FailureReason = Snapshot.FailureReason,
            Agents = agents,
        };
    }

    /// <summary>
    /// The merged results as an ordinary run export, for the reports. The controller's own state and failure reason win
    /// over the merged results', since a run whose agent was lost has to read as failed in the report.
    /// </summary>
    public RunExport ToReportExport() => new()
    {
        Id = Id,
        StartedAt = Snapshot.StartedAt ?? StartedAt,
        Machine = System.Environment.MachineName,
        Plan = Plan,
        Results = Snapshot.Results with { State = Snapshot.State, FailureReason = Snapshot.FailureReason ?? Snapshot.Results.FailureReason },
    };

    internal void Finish(DistributedRunner runner)
    {
        _finalExports = runner.GetAgentExports();
        _runner = null;
    }

    internal void Publish(DistributedSnapshot snapshot)
    {
        Snapshot = snapshot;
        Updated?.Invoke(this);
    }
}

/// <summary>A distributed run as it is exported to a file.</summary>
public sealed record DistributedRunExport
{
    public string Format { get; init; } = "falcon-load/distributed-run";

    public Guid Id { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public string? Controller { get; init; }

    public required LoadTestPlan Plan { get; init; }

    /// <summary>All agents' results merged.</summary>
    public required LoadTestSnapshot Results { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? FailureReason { get; init; }

    /// <summary>Each agent's own export, as the Combine page reads them.</summary>
    public required IReadOnlyList<RunExport> Agents { get; init; }
}

/// <summary>Starts tests on agents in the background and keeps a bounded history of them in memory.</summary>
public sealed class DistributedRunManager(AgentRegistry agents, AgentClient client, ILogger<DistributedRunManager> logger) : IDisposable
{
    private const int MaxHistory = 50;

    private readonly ConcurrentDictionary<Guid, DistributedRun> _runs = new();

    /// <summary>Raised when a run starts or finishes.</summary>
    public event Action? RunsChanged;

    public IReadOnlyList<DistributedRun> GetAll() => _runs.Values.OrderByDescending(r => r.StartedAt).ToList();

    public DistributedRun? Get(Guid id) => _runs.GetValueOrDefault(id);

    /// <summary>
    /// Splits <paramref name="plan"/> over <paramref name="selected"/>, or every enabled agent when null, and starts
    /// it with the variables of <paramref name="environment"/>, if any. Throws <see cref="ArgumentException"/> when the
    /// plan is invalid or no agent is available.
    /// </summary>
    public DistributedRun Start(LoadTestPlan plan, TestEnvironment? environment = null, IReadOnlyList<AgentDefinition>? selected = null)
    {
        var chosen = (selected ?? agents.GetEnabled()).Where(a => a.Enabled).ToList();
        if (chosen.Count == 0)
        {
            throw new ArgumentException("No agents are enabled. Add one on the Agents page first.", nameof(selected));
        }

        var invalid = chosen.SelectMany(a => a.Validate().Select(p => $"{a.Name}: {p}")).ToList();
        if (invalid.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", invalid), nameof(selected));
        }

        var runner = new DistributedRunner(
            plan,
            chosen.Select(a => a.ToEndpoint()).ToList(),
            chosen.Select(a => a.Weight).ToList(),
            client,
            new DistributedRunnerOptions { ControllerName = System.Environment.MachineName },
            environment);
        var run = new DistributedRun(runner);
        _runs[run.Id] = run;
        Trim();

        _ = Task.Run(async () =>
        {
            try
            {
                logger.LogInformation("Starting distributed load test {RunId} '{Name}' on {Agents}", run.Id, plan.Name, string.Join(", ", run.AgentNames));
                var final = await runner.RunAsync(run.Publish, run.CancellationToken);
                run.Finish(runner);
                logger.LogInformation(
                    "Distributed load test {RunId} {State}: {Requests} requests, {Rps:F1} req/s, {Errors} errors",
                    run.Id, final.State, final.Results.TotalRequests, final.Results.RequestsPerSecond, final.Results.FailedRequests);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Distributed load test {RunId} crashed", run.Id);
            }
            finally
            {
                RunsChanged?.Invoke();
            }
        });

        RunsChanged?.Invoke();
        return run;
    }

    private void Trim()
    {
        foreach (var old in _runs.Values.Where(r => r.Snapshot.IsFinished).OrderByDescending(r => r.StartedAt).Skip(MaxHistory))
        {
            _runs.TryRemove(old.Id, out _);
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs.Values)
        {
            run.Stop();
        }
    }
}
