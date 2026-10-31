using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Telemetry;

/// <summary>
/// One run's results as the metric exporters see them. A run on one machine has one <see cref="Machines"/> entry; a
/// test run on agents has one per agent, so a dashboard can show each agent and add them up exactly.
/// </summary>
/// <param name="Test">The test's name.</param>
/// <param name="Passed">The run's verdict against its thresholds, as in <see cref="LoadTestSnapshot.Passed"/>.</param>
public sealed record RunTelemetry(
    Guid RunId,
    string Test,
    DateTimeOffset StartedAt,
    RunState State,
    bool? Passed,
    IReadOnlyList<MachineTelemetry> Machines)
{
    public bool IsFinished => State is RunState.Completed or RunState.Cancelled or RunState.Failed;

    /// <summary>A run on one machine.</summary>
    public static RunTelemetry OnOneMachine(Guid runId, string test, DateTimeOffset startedAt, MachineTelemetry machine) =>
        new(runId, test, startedAt, machine.Results.State, machine.Results.Passed, [machine]);

    /// <summary>When the last machine stopped sending, or null while the run goes on.</summary>
    public DateTimeOffset? EndedAt => IsFinished
        ? StartedAt + (Machines.Count == 0 ? TimeSpan.Zero : Machines.Max(m => m.Results.Elapsed))
        : null;
}

/// <summary>What one machine has measured so far, with a latency histogram per request.</summary>
/// <param name="Steps">The latency histogram of each request, in the order of <see cref="LoadTestSnapshot.Steps"/>.</param>
public sealed record MachineTelemetry(string Machine, LoadTestSnapshot Results, IReadOnlyList<HistogramData> Steps)
{
    /// <summary>The telemetry in an export, or null for an export without histograms.</summary>
    public static MachineTelemetry? From(RunExport export) =>
        export.Histograms is { } histograms
            ? new MachineTelemetry(string.IsNullOrWhiteSpace(export.Machine) ? "unknown" : export.Machine, export.Results, histograms.Steps)
            : null;

    public static MachineTelemetry From(string machine, LoadTestSnapshot results, ResultHistograms histograms) =>
        new(machine, results, histograms.Steps);
}
