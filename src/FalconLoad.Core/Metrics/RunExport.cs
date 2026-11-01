using FalconLoad.Core.Model;

namespace FalconLoad.Core.Metrics;

/// <summary>
/// One run's results as they are exported to a file. Exports from several machines that ran the same test can be
/// combined with <see cref="ResultCombiner"/>; the histograms are what make the combined percentiles exact.
/// </summary>
public sealed record RunExport
{
    public Guid Id { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The name of the machine that ran the test, shown in the combined view.</summary>
    public string? Machine { get; init; }

    /// <summary>What the run tested, such as a build number or commit, so history can be filtered by it. Missing from older files.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>A free-text note about the run.</summary>
    public string? Note { get; init; }

    public required LoadTestPlan Plan { get; init; }

    public required LoadTestSnapshot Results { get; init; }

    /// <summary>Missing from files exported before results could be combined.</summary>
    public ResultHistograms? Histograms { get; init; }
}
