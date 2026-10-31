using System.Collections.Concurrent;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>A load test run started from the UI or API, with its live results.</summary>
public sealed class LoadTestRun
{
    private readonly CancellationTokenSource _cancel = new();
    private LoadTestRunner? _runner;
    private (LoadTestSnapshot Snapshot, ResultHistograms Histograms)? _final;

    internal LoadTestRun(LoadTestRunner runner)
    {
        _runner = runner;
        Plan = runner.Plan;
        Snapshot = runner.GetSnapshot();
    }

    public Guid Id { get; } = Guid.NewGuid();

    public LoadTestPlan Plan { get; }

    /// <summary>The name of the environment the run used, if any. Its values are not kept with the run.</summary>
    public string? EnvironmentName => Plan.Environment;

    /// <summary>When the virtual users started. A run that waits for a shared start time is stamped when the wait ends.</summary>
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>The controller that asked this agent to run its share of a test, or null for a run started here.</summary>
    public string? Controller { get; init; }

    public LoadTestSnapshot Snapshot { get; private set; }

    /// <summary>Raised (on a background thread) whenever <see cref="Snapshot"/> changes.</summary>
    public event Action<LoadTestRun>? Updated;

    internal CancellationToken CancellationToken => _cancel.Token;

    public void Stop() => _cancel.Cancel();

    internal void MarkStarted() => StartedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// The run as an exportable result, with the latency histograms that let it be combined with runs from other
    /// machines. A finished run keeps only its compact histograms, not the runner.
    /// </summary>
    public RunExport ToExport()
    {
        // A finished run keeps its final results and their histograms; a live one is read from the runner in one go
        // so the two agree.
        var (snapshot, histograms) = _final is { } final ? final : _runner?.GetResults() ?? _final!.Value;
        return new RunExport
        {
            Id = Id,
            StartedAt = StartedAt,
            Machine = Environment.MachineName,
            Plan = Plan,
            Results = snapshot,
            Histograms = histograms,
        };
    }

    /// <summary>Keeps the final results' compact histograms and lets go of the runner and its metrics.</summary>
    /// <param name="state">The state to report when the runner never ran, e.g. a run stopped while waiting to start.</param>
    internal void Finish(LoadTestRunner runner, RunState? state = null)
    {
        var (snapshot, histograms) = runner.GetResults();
        if (state is { } s && snapshot.State != s)
        {
            snapshot = snapshot with { State = s };
        }

        _final = (snapshot, histograms);
        _runner = null;
        Snapshot = snapshot;
        Updated?.Invoke(this);
    }

    internal void Publish(LoadTestSnapshot snapshot)
    {
        Snapshot = snapshot;
        Updated?.Invoke(this);
    }
}

/// <summary>Starts load tests in the background and keeps a bounded history of recent runs in memory.</summary>
public sealed class RunManager(ILogger<RunManager> logger) : IDisposable
{
    private const int MaxHistory = 50;

    private readonly ConcurrentDictionary<Guid, LoadTestRun> _runs = new();

    /// <summary>Raised when a run starts or finishes.</summary>
    public event Action? RunsChanged;

    public IReadOnlyList<LoadTestRun> GetAll() => _runs.Values.OrderByDescending(r => r.StartedAt).ToList();

    public LoadTestRun? Get(Guid id) => _runs.GetValueOrDefault(id);

    /// <summary>How far ahead a controller may schedule a start; later than this is most likely a clock that is off.</summary>
    public static readonly TimeSpan MaxStartDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Validates and starts <paramref name="plan"/> with the variables of <paramref name="environment"/>, if any.
    /// Throws <see cref="ArgumentException"/> if it is invalid.
    /// </summary>
    /// <param name="startAt">Waits until this time before sending anything, so agents of a distributed test start together.</param>
    /// <param name="controller">The controller that asked for the run, when it is a share of a distributed test.</param>
    public LoadTestRun Start(LoadTestPlan plan, TestEnvironment? environment = null, DateTimeOffset? startAt = null, string? controller = null)
    {
        var runner = new LoadTestRunner(plan, environment: environment);
        var run = new LoadTestRun(runner) { Controller = controller };
        _runs[run.Id] = run;
        Trim();

        _ = Task.Run(async () =>
        {
            await using (runner)
            {
                try
                {
                    var wait = startAt is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.Zero;
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait > MaxStartDelay ? MaxStartDelay : wait, run.CancellationToken);
                    }

                    run.MarkStarted();
                    logger.LogInformation("Starting load test {RunId} '{Name}' (environment: {Environment})", run.Id, plan.Name, environment?.Name ?? "none");
                    var final = await runner.RunAsync(run.Publish, TimeSpan.FromMilliseconds(500), run.CancellationToken);
                    run.Finish(runner);
                    logger.LogInformation(
                        "Load test {RunId} {State}: {Requests} requests, {Rps:F1} req/s, {Errors} errors",
                        run.Id, final.State, final.TotalRequests, final.RequestsPerSecond, final.FailedRequests);
                }
                catch (OperationCanceledException) when (run.CancellationToken.IsCancellationRequested)
                {
                    // Stopped while waiting for the shared start time: nothing was sent.
                    run.Finish(runner, RunState.Cancelled);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Load test {RunId} crashed", run.Id);
                }
                finally
                {
                    RunsChanged?.Invoke();
                }
            }
        });

        RunsChanged?.Invoke();
        return run;
    }

    public bool Remove(Guid id)
    {
        if (_runs.TryGetValue(id, out var run) && run.Snapshot.IsFinished && _runs.TryRemove(id, out _))
        {
            RunsChanged?.Invoke();
            return true;
        }

        return false;
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
