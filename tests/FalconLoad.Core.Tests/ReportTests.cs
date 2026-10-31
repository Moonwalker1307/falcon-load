using System.Net;
using System.Xml.Linq;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Reports;

namespace FalconLoad.Core.Tests;

public class ReportTests
{
    private const string Token = "s3cr3t-token-value";
    private const string TypedToken = "typed-in-header-token";

    private static LoadTestSnapshot Snapshot(
        RunState state = RunState.Completed,
        long total = 100,
        long failed = 2,
        IReadOnlyList<ThresholdResult>? thresholds = null,
        IReadOnlyList<ErrorCount>? errors = null,
        string? failureReason = null) => new()
    {
        State = state,
        Elapsed = TimeSpan.FromSeconds(10),
        TotalRequests = total,
        SuccessfulRequests = total - failed,
        FailedRequests = failed,
        ActiveVirtualUsers = 0,
        BytesSent = 2048,
        BytesReceived = 4_000_000,
        Latency = new LatencyStats(1, 120, 100, 250, 400, 900, 1500),
        StatusCodes = new Dictionary<string, long> { ["200"] = total - failed, ["500"] = failed },
        Errors = errors ?? [],
        Steps =
        [
            new StepStats(0, "login", total / 2, failed, new LatencyStats(1, 200, 180, 450, 600, 900, 1500)),
            new StepStats(1, "list", total / 2, 0, new LatencyStats(1, 40, 30, 60, 80, 90, 100)),
        ],
        Timeline = Enumerable.Range(0, 10).Select(s => new TimelinePoint(s, 10, s % 3 == 0 ? 1 : 0, 100 + s, 300 + s * 10)).ToList(),
        Thresholds = thresholds ?? [],
        FailureReason = failureReason,
    };

    private static RunExport Export(LoadTestSnapshot snapshot, string name = "Checkout", string? machine = "runner-1", string? environment = null) => new()
    {
        Id = Guid.NewGuid(),
        StartedAt = new DateTimeOffset(2026, 10, 31, 12, 30, 15, TimeSpan.Zero),
        Machine = machine,
        Plan = new LoadTestPlan
        {
            Name = name,
            Environment = environment,
            Requests = [new RequestDefinition { Url = "http://api.test/items" }],
            Profile = new LoadProfile { Concurrency = 5, StopCondition = StopCondition.Duration, DurationSeconds = 10 },
        },
        Results = snapshot,
    };

    private static ThresholdResult Pass() => new("p95 latency < 500 ms", "ms", 500, 400, true);

    private static ThresholdResult Fail() => new("Error rate < 1%", "%", 1, 2, false);

    private static XDocument Parse(string xml) => XDocument.Parse(xml);

    [Fact]
    public void Verdict_WithThresholds_IsJudgedByThem()
    {
        var passing = RunVerdict.Evaluate(Snapshot(failed: 5, thresholds: [Pass()]), null);
        var failing = RunVerdict.Evaluate(Snapshot(thresholds: [Pass(), Fail()]), null);

        Assert.True(passing.Passed);
        Assert.False(failing.Passed);
        Assert.Equal(["Threshold not met: Error rate < 1% (measured 2%)."], failing.Failures);
    }

    [Fact]
    public void Verdict_WithoutThresholds_AllowsNoFailedRequestsUnlessToldOtherwise()
    {
        Assert.Equal(["2% of requests failed, more than the 0% allowed."], RunVerdict.Evaluate(Snapshot(), null).Failures);
        Assert.True(RunVerdict.Evaluate(Snapshot(), 0.05).Passed);
        Assert.True(RunVerdict.Evaluate(Snapshot(failed: 0), null).Passed);
    }

    [Fact]
    public void Verdict_MaxErrorRate_AppliesOnTopOfThresholds()
    {
        var verdict = RunVerdict.Evaluate(Snapshot(thresholds: [Pass()]), 0.01);

        Assert.False(verdict.Passed);
        Assert.Contains("more than the 1% allowed", Assert.Single(verdict.Failures));
    }

    [Theory]
    [InlineData(RunState.Failed, "The test could not run: boom")]
    [InlineData(RunState.Cancelled, "The run was interrupted before the stop condition was reached.")]
    public void Verdict_ARunThatDidNotComplete_NeverPasses(RunState state, string message)
    {
        var verdict = RunVerdict.Evaluate(Snapshot(state, failed: 0, thresholds: [Pass()], failureReason: "boom"), null);

        Assert.False(verdict.Passed);
        Assert.Equal([message], verdict.Failures);
    }

    [Fact]
    public void Verdict_ACompletedRunWithoutRequests_Fails()
    {
        Assert.Equal(["No requests completed."], RunVerdict.Evaluate(Snapshot(total: 0, failed: 0), null).Failures);
    }

    [Fact]
    public void JUnit_HasASuitePerRunAndATestCasePerCondition()
    {
        var xml = JUnitReport.Render(Export(Snapshot(thresholds: [Pass(), Fail()]), environment: "Staging"), RunVerdict.Evaluate(Snapshot(thresholds: [Pass(), Fail()]), null));

        var root = Parse(xml).Root!;
        var suite = Assert.Single(root.Elements("testsuite"));
        Assert.Equal("testsuites", root.Name.LocalName);
        Assert.Equal("Checkout", (string?)suite.Attribute("name"));
        Assert.Equal("runner-1", (string?)suite.Attribute("hostname"));
        Assert.Equal("2026-10-31T12:30:15", (string?)suite.Attribute("timestamp"));
        Assert.Equal("10", (string?)suite.Attribute("time"));
        Assert.Equal(["p95 latency < 500 ms", "Error rate < 1%", "The run completed", "Requests were sent"], suite.Elements("testcase").Select(c => (string?)c.Attribute("name")));
        Assert.Equal("4", (string?)suite.Attribute("tests"));
        Assert.Equal("1", (string?)suite.Attribute("failures"));
        Assert.Equal("0", (string?)suite.Attribute("errors"));
        Assert.Equal("4", (string?)root.Attribute("tests"));
        Assert.Equal("1", (string?)root.Attribute("failures"));

        var failure = Assert.Single(suite.Descendants("failure"));
        Assert.Equal("Error rate < 1%", (string?)failure.Parent!.Attribute("name"));
        Assert.Equal("Threshold", (string?)failure.Attribute("type"));
        Assert.Equal("Threshold not met: Error rate < 1% (measured 2%).", (string?)failure.Attribute("message"));
        Assert.Contains(suite.Descendants("property"), p => (string?)p.Attribute("name") == "environment" && (string?)p.Attribute("value") == "Staging");
        Assert.Contains(suite.Descendants("property"), p => (string?)p.Attribute("name") == "latencyP95Ms" && (string?)p.Attribute("value") == "400");
    }

    [Fact]
    public void JUnit_AFailedRun_IsAnError_AndAnInterruptedOneAFailure()
    {
        var failed = Snapshot(RunState.Failed, failed: 0, failureReason: "bad plan");
        var cancelled = Snapshot(RunState.Cancelled, failed: 0);

        var suites = Parse(JUnitReport.Render([(Export(failed, "A"), RunVerdict.Evaluate(failed, null)), (Export(cancelled, "B"), RunVerdict.Evaluate(cancelled, null))])).Root!;

        var error = Assert.Single(suites.Descendants("error"));
        Assert.Equal("The test could not run: bad plan", (string?)error.Attribute("message"));
        var interrupted = Assert.Single(suites.Elements("testsuite").Single(x => (string?)x.Attribute("name") == "B").Descendants("failure"));
        Assert.Equal("Run", (string?)interrupted.Attribute("type"));
        Assert.Equal("The run was interrupted before the stop condition was reached.", (string?)interrupted.Attribute("message"));
        Assert.Equal("6", (string?)suites.Attribute("tests"));
        Assert.Equal("1", (string?)suites.Attribute("errors"));
        Assert.Equal("1", (string?)suites.Attribute("failures"));
        Assert.Equal("20", (string?)suites.Attribute("time"));
    }

    [Fact]
    public void JUnit_CharactersXmlCannotHold_AreReplaced()
    {
        var snapshot = Snapshot(errors: [new ErrorCount("server said \u0001 and \u001b[31mred", 2)]);

        var xml = JUnitReport.Render(Export(snapshot, "Plan \u0002 <x> & \"y\""), RunVerdict.Evaluate(snapshot, null));

        var suite = Parse(xml).Root!.Element("testsuite")!;
        Assert.Equal("Plan � <x> & \"y\"", (string?)suite.Attribute("name"));
        Assert.Contains("server said � and �[31mred", suite.Element("system-out")!.Value);
    }

    [Fact]
    public void Html_ShowsTheVerdictThresholdsAndNumbers()
    {
        var snapshot = Snapshot(thresholds: [Pass(), Fail()]);

        var html = HtmlReport.Render(Export(snapshot, environment: "Staging"), RunVerdict.Evaluate(snapshot, null));

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<title>Checkout: Failed", html);
        Assert.Contains("badge-fail\">Failed", html);
        Assert.Contains("Started 2026-10-31 12:30:15 UTC", html);
        Assert.Contains("Environment <strong>Staging</strong>", html);
        Assert.Contains("Machine runner-1", html);
        Assert.Contains("p95 latency &lt; 500 ms", html);
        Assert.Contains("measured 2%", html);
        Assert.Contains("<polyline", html);
        Assert.Contains("200", html);
        Assert.Contains("login", html);
        Assert.Contains("#101116", html);
    }

    [Fact]
    public void Html_APassingRun_SaysPassed_AndExplainsARunWithoutThresholds()
    {
        var snapshot = Snapshot(failed: 0);

        var html = HtmlReport.Render(Export(snapshot), RunVerdict.Evaluate(snapshot, null));

        Assert.Contains("badge-pass\">Passed", html);
        Assert.Contains("has no thresholds", html);
    }

    [Fact]
    public void Html_AnInterruptedRun_SaysInterrupted()
    {
        var snapshot = Snapshot(RunState.Cancelled, failed: 0);

        Assert.Contains("badge-fail\">Interrupted", HtmlReport.Render(Export(snapshot), RunVerdict.Evaluate(snapshot, null)));
    }

    [Fact]
    public void Html_EscapesWhatUsersAndServersCanWrite_AndNeedsNoScriptOrOtherSite()
    {
        var snapshot = Snapshot(errors: [new ErrorCount("<img src=x onerror=alert(1)>", 1)]);

        var html = HtmlReport.Render(Export(snapshot, "<script>alert(1)</script>", machine: "m\"><b>"), RunVerdict.Evaluate(snapshot, null));

        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void ARunWithoutATimeline_StillRenders()
    {
        var snapshot = Snapshot() with { Timeline = [] };

        var html = HtmlReport.Render(Export(snapshot), RunVerdict.Evaluate(snapshot, null));

        Assert.DoesNotContain("<svg", html);
        Assert.Contains("Latency", html);
    }

    [Fact]
    public async Task Reports_NeverContainASecret_OrAHeaderTypedIntoTheTest()
    {
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables =
            [
                new EnvironmentVariable { Name = "baseUrl", Value = "http://staging.test" },
                new EnvironmentVariable { Name = "apiKey", Value = Token, Secret = true },
            ],
        };
        var plan = new LoadTestPlan
        {
            Name = "Orders",
            Environment = "Staging",
            Requests =
            [
                new RequestDefinition
                {
                    Url = "{{baseUrl}}/orders",
                    Headers =
                    [
                        new HeaderEntry { Name = "X-Api-Key", Value = "{{apiKey}}" },
                        new HeaderEntry { Name = "Authorization", Value = $"Bearer {TypedToken}" },
                    ],
                    Body = $"{{\"token\":\"{TypedToken}\"}}",
                },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 3 },
        };
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.Unknown, $"The server rejected key {Token}"));
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        var (snapshot, histograms) = runner.GetResults();
        var export = new RunExport { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Machine = "m", Plan = runner.Plan, Results = snapshot, Histograms = histograms };
        var verdict = RunVerdict.Evaluate(snapshot, null);

        var html = HtmlReport.Render(export, verdict);
        var xml = JUnitReport.Render(export, verdict);

        Assert.Contains("rejected key", html);
        Assert.Contains("rejected key", xml);
        Assert.DoesNotContain(Token, html);
        Assert.DoesNotContain(Token, xml);
        Assert.DoesNotContain(TypedToken, html);
        Assert.DoesNotContain(TypedToken, xml);
    }
}
