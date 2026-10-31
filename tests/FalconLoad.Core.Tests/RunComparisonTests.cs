using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class RunComparisonTests
{
    /// <summary>
    /// Results of a run of <paramref name="requests"/> requests over 10 seconds, each taking <paramref name="latencyMs"/>,
    /// of which <paramref name="failures"/> failed, spread over the given steps in turn.
    /// </summary>
    private static LoadTestSnapshot Results(int requests, double latencyMs, int failures = 0, int steps = 1, RunState state = RunState.Completed)
    {
        var collector = new MetricsCollector(Enumerable.Range(1, steps).Select(i => $"step {i}").ToList());
        for (var i = 0; i < requests; i++)
        {
            var failed = i < failures;
            collector.Record(new RequestResult(
                i % steps, TimeSpan.FromMilliseconds(i), TimeSpan.FromMilliseconds(latencyMs),
                failed ? 500 : 200, !failed, failed ? "Unexpected status 500" : null, 10, 100));
        }

        return collector.Snapshot(state, TimeSpan.FromSeconds(10), 0);
    }

    private static MetricChange Metric(RunComparison c, string key) => c.Overall.Single(m => m.Key == key);

    [Fact]
    public void LatencyGrowthBeyondTheThresholdIsARegression()
    {
        var c = RunComparer.Compare(Results(1000, 100), Results(1000, 120));

        Assert.Equal(ChangeVerdict.Regressed, Metric(c, "p95Ms").Verdict);
        Assert.Equal(ChangeVerdict.Regressed, Metric(c, "meanMs").Verdict);
        Assert.True(c.HasRegression);
        Assert.Equal(0.2, Metric(c, "meanMs").RelativeChange!.Value, 2);
    }

    [Fact]
    public void LatencyWithinTheThresholdIsUnchanged()
    {
        var c = RunComparer.Compare(Results(1000, 100), Results(1000, 105));

        Assert.Equal(ChangeVerdict.Unchanged, Metric(c, "p95Ms").Verdict);
        Assert.False(c.HasRegression);
    }

    [Fact]
    public void FasterLatencyIsAnImprovement()
    {
        var c = RunComparer.Compare(Results(1000, 100), Results(1000, 70));

        Assert.Equal(ChangeVerdict.Improved, Metric(c, "p95Ms").Verdict);
        Assert.False(c.HasRegression);
        Assert.True(c.ImprovementCount > 0);
    }

    [Fact]
    public void TinyLatencyChangesOnFastEndpointsAreNotRegressions()
    {
        // 0.4 ms to 0.6 ms is +50%, but below the 1 ms minimum change.
        var c = RunComparer.Compare(Results(1000, 0.4), Results(1000, 0.6));

        Assert.Equal(ChangeVerdict.Unchanged, Metric(c, "p95Ms").Verdict);
        Assert.False(c.HasRegression);
    }

    [Fact]
    public void ThroughputDropBeyondTheThresholdIsARegression()
    {
        // Both runs last 10 seconds, so 1000 to 850 requests is 100 to 85 req/s.
        var c = RunComparer.Compare(Results(1000, 100), Results(850, 100));

        Assert.Equal(ChangeVerdict.Regressed, Metric(c, "requestsPerSecond").Verdict);
        Assert.Equal(ChangeVerdict.Unchanged, RunComparer.Compare(Results(1000, 100), Results(950, 100)).Overall.Single(m => m.Key == "requestsPerSecond").Verdict);
        Assert.Equal(ChangeVerdict.Improved, RunComparer.Compare(Results(1000, 100), Results(1200, 100)).Overall.Single(m => m.Key == "requestsPerSecond").Verdict);
    }

    [Fact]
    public void ErrorRateIsJudgedInPercentagePoints()
    {
        // 1% to 1.5% is half a point, within the default of one; 1% to 3% is two points worse.
        Assert.Equal(ChangeVerdict.Unchanged, Metric(RunComparer.Compare(Results(1000, 100, 10), Results(1000, 100, 15)), "errorRate").Verdict);
        Assert.Equal(ChangeVerdict.Regressed, Metric(RunComparer.Compare(Results(1000, 100, 10), Results(1000, 100, 30)), "errorRate").Verdict);
        Assert.Equal(ChangeVerdict.Improved, Metric(RunComparer.Compare(Results(1000, 100, 30), Results(1000, 100, 0)), "errorRate").Verdict);
    }

    [Fact]
    public void CountsAndMaxLatencyAreShownButNotJudged()
    {
        var c = RunComparer.Compare(Results(1000, 100), Results(500, 100));

        Assert.Equal(ChangeVerdict.NotJudged, Metric(c, "totalRequests").Verdict);
        Assert.Equal(ChangeVerdict.NotJudged, Metric(c, "maxMs").Verdict);
        Assert.Equal(1000, Metric(c, "totalRequests").Baseline);
        Assert.Equal(500, Metric(c, "totalRequests").Current);
    }

    [Fact]
    public void ThresholdsCanBeTightened()
    {
        var strict = new RegressionThresholds { LatencyPercent = 2, MinLatencyChangeMs = 0 };

        Assert.Equal(ChangeVerdict.Regressed, Metric(RunComparer.Compare(Results(1000, 100), Results(1000, 105), strict), "p95Ms").Verdict);
    }

    [Fact]
    public void RequestsAreComparedByPositionAndAMismatchIsWarnedAbout()
    {
        var c = RunComparer.Compare(Results(900, 100, steps: 3), Results(1000, 150, steps: 2));

        Assert.Equal(2, c.Steps.Count);
        Assert.Equal(ChangeVerdict.Regressed, c.Steps[0].Metrics.Single(m => m.Key == "p95Ms").Verdict);
        Assert.Contains(c.Warnings, w => w.Contains("only the first 2"));
    }

    [Fact]
    public void ARunWithoutRequestsIsNotJudged()
    {
        var c = RunComparer.Compare(Results(1000, 100), Results(0, 0, state: RunState.Cancelled));

        Assert.All(c.Overall, m => Assert.Equal(ChangeVerdict.NotJudged, m.Verdict));
        Assert.False(c.HasRegression);
        Assert.Contains(c.Warnings, w => w.Contains("stopped early"));
        Assert.Contains(c.Warnings, w => w.Contains("no completed requests"));
    }

    [Fact]
    public void ChangedLoadProfileIsWarnedAbout()
    {
        var before = new LoadTestPlan { Name = "MyApp" };
        var after = before.Clone();
        after.Profile.Concurrency = 50;
        after.Requests[0].Url = "https://localhost:5001/other";

        var c = RunComparer.Compare(Export(before, Results(1000, 100)), Export(after, Results(1000, 100)));

        Assert.Contains(c.Warnings, w => w.Contains("Virtual users changed from 10 to 50"));
        Assert.Contains(c.Warnings, w => w.StartsWith("Request 1 changed"));
        Assert.DoesNotContain(c.Warnings, w => w.Contains("different tests"));
    }

    [Fact]
    public void ExportComparisonCarriesTheRunIds()
    {
        var plan = new LoadTestPlan();
        var before = Export(plan, Results(1000, 100));
        var after = Export(plan, Results(1000, 100));

        var c = RunComparer.Compare(before, after);

        Assert.Equal(before.Id, c.BaselineRunId);
        Assert.Equal(after.Id, c.RunId);
        Assert.Empty(c.Warnings);
    }

    [Theory]
    [InlineData(-1, 1, 10, 1)]
    [InlineData(10, -1, 10, 1)]
    [InlineData(10, 1, 101, 1)]
    [InlineData(10, 1, 10, double.NaN)]
    public void InvalidThresholdsAreRejected(double latency, double minMs, double throughput, double errorPoints)
    {
        var thresholds = new RegressionThresholds
        {
            LatencyPercent = latency,
            MinLatencyChangeMs = minMs,
            ThroughputPercent = throughput,
            ErrorRatePoints = errorPoints,
        };

        Assert.NotEmpty(thresholds.Validate());
        Assert.Empty(new RegressionThresholds().Validate());
    }

    private static RunExport Export(LoadTestPlan plan, LoadTestSnapshot results) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = DateTimeOffset.UtcNow,
        Plan = plan,
        Results = results,
    };
}
