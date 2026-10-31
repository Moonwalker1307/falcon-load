using System.Net;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class ThresholdTests
{
    private static LoadTestSnapshot Snapshot(RunState state = RunState.Completed, long total = 100, long failed = 2) => new()
    {
        State = state,
        Elapsed = TimeSpan.FromSeconds(10),
        TotalRequests = total,
        SuccessfulRequests = total - failed,
        FailedRequests = failed,
        ActiveVirtualUsers = 0,
        BytesSent = 0,
        BytesReceived = 0,
        Latency = new LatencyStats(1, 120, 100, 250, 400, 900, 1500),
        StatusCodes = new Dictionary<string, long>(),
        Errors = [],
        Steps =
        [
            new StepStats(0, "login", total / 2, failed, new LatencyStats(1, 200, 180, 450, 600, 900, 1500)),
            new StepStats(1, "list", total / 2, 0, new LatencyStats(1, 40, 30, 60, 80, 90, 100)),
        ],
        Timeline = [],
    };

    [Theory]
    [InlineData(ThresholdMetric.P95Latency, ThresholdOperator.LessThan, 500, true)]
    [InlineData(ThresholdMetric.P95Latency, ThresholdOperator.LessThan, 400, false)]
    [InlineData(ThresholdMetric.P95Latency, ThresholdOperator.LessThanOrEqual, 400, true)]
    [InlineData(ThresholdMetric.P99Latency, ThresholdOperator.LessThan, 800, false)]
    [InlineData(ThresholdMetric.MaxLatency, ThresholdOperator.LessThan, 2000, true)]
    [InlineData(ThresholdMetric.ErrorRate, ThresholdOperator.LessThan, 1, false)]
    [InlineData(ThresholdMetric.ErrorRate, ThresholdOperator.LessThanOrEqual, 2, true)]
    [InlineData(ThresholdMetric.RequestsPerSecond, ThresholdOperator.GreaterThanOrEqual, 10, true)]
    [InlineData(ThresholdMetric.RequestsPerSecond, ThresholdOperator.GreaterThan, 10, false)]
    public void Thresholds_CompareTheRunWithTheirLimit(ThresholdMetric metric, ThresholdOperator op, double limit, bool passes)
    {
        var threshold = new Threshold { Metric = metric, Operator = op, Value = limit };

        var result = Assert.Single(ThresholdEvaluator.Evaluate([threshold], Snapshot()));

        Assert.Equal(passes, result.Passed);
        Assert.Equal(threshold.Description, result.Name);
    }

    [Fact]
    public void Threshold_CanApplyToOneRequest()
    {
        Threshold[] thresholds =
        [
            new() { Metric = ThresholdMetric.P95Latency, Value = 500, RequestIndex = 0 },
            new() { Metric = ThresholdMetric.P95Latency, Value = 500, RequestIndex = 1 },
            new() { Metric = ThresholdMetric.ErrorRate, Value = 5, RequestIndex = 0 },
        ];

        var results = ThresholdEvaluator.Evaluate(thresholds, Snapshot());

        Assert.Equal([false, true, true], results.Select(r => r.Passed));
        Assert.Equal([600, 80, 4], results.Select(r => r.Actual!.Value));
        Assert.Equal("Request 1 (login): p95 latency < 500 ms", results[0].Name);
    }

    [Fact]
    public void Verdict_NeedsThresholdsAndACompletedRun()
    {
        var holds = new Threshold { Metric = ThresholdMetric.ErrorRate, Value = 5 };

        Assert.Null(Snapshot().Passed);
        Assert.True(ThresholdEvaluator.Apply([holds], Snapshot()).Passed);
        Assert.True(ThresholdEvaluator.Apply([holds], Snapshot(RunState.Running)).Passed);
        Assert.False(ThresholdEvaluator.Apply([holds], Snapshot(RunState.Cancelled)).Passed);
        Assert.False(ThresholdEvaluator.Apply([holds], Snapshot(RunState.Failed)).Passed);
        Assert.Null(ThresholdEvaluator.Apply([new Threshold { Enabled = false }], Snapshot()).Passed);
    }

    [Fact]
    public void Threshold_FailsWithoutRequestsToMeasure()
    {
        var result = Assert.Single(ThresholdEvaluator.Evaluate([new Threshold { Value = 500 }], Snapshot(total: 0, failed: 0)));

        Assert.Null(result.Actual);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Validate_ChecksLimitsAndRequestIndex()
    {
        var plan = new LoadTestPlan
        {
            Thresholds =
            [
                new Threshold { Metric = ThresholdMetric.ErrorRate, Value = 150 },
                new Threshold { Metric = ThresholdMetric.P95Latency, Value = -1 },
                new Threshold { Metric = ThresholdMetric.P95Latency, Value = 500, RequestIndex = 3 },
                new Threshold { Metric = ThresholdMetric.P95Latency, Value = double.NaN, Enabled = false },
            ],
        };

        var problems = plan.Validate();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("between 0 and 100"));
        Assert.Contains(problems, p => p.Contains("0 or more"));
        Assert.Contains(problems, p => p.Contains("applies to request 4"));
    }

    [Fact]
    public async Task Runner_ReportsVerdictFromChecksAndThresholds()
    {
        // Every other response says "error", so half the requests fail their check and the error rate is 50%.
        var count = 0;
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Interlocked.Increment(ref count) % 2 == 0 ? "error" : "fine"),
        }));
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "http://api.test/", Assertions = [new Assertion { Operator = AssertionOperator.NotContains, Value = "error" }] }],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 20 },
            Thresholds =
            [
                new Threshold { Metric = ThresholdMetric.ErrorRate, Operator = ThresholdOperator.LessThan, Value = 1 },
                new Threshold { Metric = ThresholdMetric.MaxLatency, Operator = ThresholdOperator.LessThan, Value = 60_000 },
            ],
        };
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(10, result.FailedRequests);
        Assert.Equal([false, true], result.Thresholds.Select(t => t.Passed));
        Assert.Equal(50, result.Thresholds[0].Actual);
        Assert.False(result.Passed);
        Assert.Equal(result.Thresholds, runner.GetResults().Snapshot.Thresholds);
    }

    [Fact]
    public void OldPlans_WithoutChecksOrThresholds_StillLoad()
    {
        // A plan as saved before checks and thresholds existed.
        const string json = """
            [{ "id": "6f1c1d1e-0000-4000-8000-000000000001", "name": "Old", "requests": [{ "method": "GET", "url": "http://api.test/", "expectedStatusCodes": "200" }],
               "profile": { "concurrency": 2, "stopCondition": "Duration", "durationSeconds": 5 } }]
            """;

        var import = PlanFile.Read(json);

        Assert.True(import.Succeeded, string.Join(" ", import.Problems));
        var plan = Assert.Single(import.Plans);
        Assert.Empty(plan.Thresholds);
        Assert.Empty(plan.Requests[0].Assertions);
    }

    [Fact]
    public void ChecksAndThresholds_SurviveAnExportAndImport()
    {
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "http://api.test/", Assertions = [new Assertion { Source = AssertionSource.JsonPath, Target = "$.ok", Operator = AssertionOperator.Equals, Value = "true" }] }],
            Thresholds = [new Threshold { Metric = ThresholdMetric.P99Latency, Operator = ThresholdOperator.LessThanOrEqual, Value = 800, RequestIndex = 0 }],
        };

        var copy = Assert.Single(PlanFile.Read(PlanFile.Write([plan])).Plans);

        Assert.Equal("$.ok equals \"true\"", Assert.Single(copy.Requests[0].Assertions).Description);
        var threshold = Assert.Single(copy.Thresholds);
        Assert.Equal((ThresholdMetric.P99Latency, ThresholdOperator.LessThanOrEqual, 800d, (int?)0), (threshold.Metric, threshold.Operator, threshold.Value, threshold.RequestIndex));
    }
}
