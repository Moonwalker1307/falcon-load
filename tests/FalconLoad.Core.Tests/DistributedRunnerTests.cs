using System.Net;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class DistributedRunnerTests
{
    private static readonly DistributedRunnerOptions Fast = new()
    {
        StartDelay = TimeSpan.FromMilliseconds(100),
        PollInterval = TimeSpan.FromMilliseconds(50),
        LostAfter = TimeSpan.FromSeconds(2),
        StopGrace = TimeSpan.FromSeconds(5),
        ControllerName = "controller",
    };

    private static LoadTestPlan Plan(Action<LoadTestPlan>? configure = null)
    {
        var plan = new LoadTestPlan
        {
            Name = "Products",
            Requests = [new RequestDefinition { Url = "{{base}}/items/{{$vu}}" }],
            Profile = new LoadProfile { Concurrency = 4, StopCondition = StopCondition.RequestCount, TotalRequests = 100 },
        };
        configure?.Invoke(plan);
        return plan;
    }

    // The URL comes from an environment, so the tests also show the environment travelling to the agents.
    private static readonly TestEnvironment Staging = new()
    {
        Name = "Staging",
        Variables = [new EnvironmentVariable { Name = "base", Value = "http://api.test", Secret = true }],
    };

    private static DistributedRunner Runner(LoadTestPlan plan, params FakeAgent[] agents) =>
        new(plan, agents.Select(a => a.Endpoint()).ToList(), agents.Select(_ => 1).ToList(), new AgentClient(new HttpClient(new FakeAgentNetwork(agents))), Fast, Staging);

    [Fact]
    public async Task RunAsync_SplitsTheTest_StartsTheAgentsTogether_AndMergesTheirResults()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2") { ClockSkew = TimeSpan.FromSeconds(7) };
        var runner = Runner(Plan(), one, two);
        var snapshots = new List<DistributedSnapshot>();

        var result = await runner.RunAsync(snapshots.Add, CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Null(result.FailureReason);
        Assert.Equal(100, result.Results.TotalRequests);
        Assert.Equal(100, result.Results.SuccessfulRequests);
        Assert.Equal(50, one.Target.Received.Count);
        Assert.Equal(50, two.Target.Received.Count);
        Assert.All(result.Agents, a => Assert.Equal(AgentRunPhase.Finished, a.Phase));
        Assert.Equal([2, 2], result.Agents.Select(a => a.Share.Concurrency));

        // The virtual users are numbered 1..4 across both agents, so no two of them collide: the first agent's
        // users are 1 and 2, the second's 3 and 4.
        Assert.All(one.Target.Received, r => Assert.Contains(r.Request.RequestUri!.AbsolutePath, new[] { "/items/1", "/items/2" }));
        Assert.All(two.Target.Received, r => Assert.Contains(r.Request.RequestUri!.AbsolutePath, new[] { "/items/3", "/items/4" }));

        // Each agent was told to start at the same moment, expressed on its own clock, and told who asked.
        var starts = new[] { one, two }.Select(a => a.Runs.Values.Single().Request).ToList();
        Assert.All(starts, s => Assert.Equal("controller", s.Controller));
        Assert.All(starts, s => Assert.Equal("http://api.test", s.Environment!.Variables.Single().Value));
        Assert.Equal("Staging", result.Combined!.Machines[0].Results.Steps.Count > 0 ? runner.Plan.Environment : null);
        Assert.InRange((starts[1].StartAt!.Value - starts[0].StartAt!.Value).TotalSeconds, 6.5, 7.5);

        // The second agent's clock is seven seconds ahead; corrected, both runs line up at the start of the timeline.
        var combined = result.Combined!;
        Assert.Equal(["agent-1", "agent-2"], combined.Machines.Select(m => m.Name));
        Assert.All(combined.Machines, m => Assert.Equal(0, m.OffsetSeconds));
        Assert.Contains(result.Warnings, w => w.Contains("agent-2's clock is 7") && w.Contains("ahead"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("overlapped"));
        Assert.Equal(100, combined.Combined.TotalRequests);
        Assert.Equal(100, combined.Combined.Timeline.Sum(t => t.Requests));

        // Progress was reported while the agents ran, and every agent's export is there for combining later.
        Assert.Contains(snapshots, s => s.State == RunState.Running);
        Assert.Equal(2, runner.GetAgentExports().Count);
        Assert.All(runner.GetAgentExports(), e => Assert.NotNull(e.Histograms));
    }

    [Fact]
    public async Task RunAsync_StopsEveryAgentWhenCancelled()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2");
        var plan = Plan(p =>
        {
            p.Profile.StopCondition = StopCondition.Duration;
            p.Profile.DurationSeconds = 600;
        });
        var runner = Runner(plan, one, two);
        using var stop = new CancellationTokenSource();

        // Stop as soon as both agents are running.
        var result = await runner.RunAsync(
            s =>
            {
                if (s.Agents.All(a => a.Phase == AgentRunPhase.Running) && s.Results.TotalRequests > 0)
                {
                    stop.Cancel();
                }
            },
            stop.Token);

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.Null(result.FailureReason);
        Assert.All(result.Agents, a => Assert.Equal(AgentRunPhase.Finished, a.Phase));
        Assert.All(result.Agents, a => Assert.Equal(RunState.Cancelled, a.Results!.State));
        Assert.True(result.Results.TotalRequests > 0);

        foreach (var agent in new[] { one, two })
        {
            var run = agent.Runs.Values.Single();
            await run.Task;
            Assert.Equal(RunState.Cancelled, run.State);
            Assert.Contains(agent.Received, r => r.RequestUri!.AbsolutePath.EndsWith("/stop"));
        }
    }

    [Fact]
    public async Task RunAsync_FailsAndStopsTheOthers_WhenAnAgentRefusesItsShare()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2") { RefusesStarts = true };
        var runner = Runner(Plan(p => { p.Profile.StopCondition = StopCondition.Duration; p.Profile.DurationSeconds = 600; }), one, two);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Contains("Not every agent accepted its share", result.FailureReason);
        Assert.Contains("Concurrency must be between 1 and 10,000.", result.FailureReason);
        Assert.Equal(AgentRunPhase.Failed, result.Agents[1].Phase);

        // The agent that did accept was told to stop again, so it doesn't run the test on its own.
        var started = one.Runs.Values.Single();
        await started.Task;
        Assert.Equal(RunState.Cancelled, started.State);
        Assert.Empty(two.Runs);
    }

    [Fact]
    public async Task RunAsync_DoesNotStart_WhenAnAgentCantBeReached()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2") { Offline = true };
        var runner = Runner(Plan(), one, two);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Contains("not every agent could be reached", result.FailureReason);
        Assert.Contains("Can't reach agent-2", result.FailureReason);
        Assert.Equal(AgentRunPhase.Failed, result.Agents[1].Phase);
        Assert.Empty(one.Runs);
        Assert.Empty(one.Target.Received);
        Assert.Null(result.Combined);
    }

    [Fact]
    public async Task RunAsync_RejectsWrongTokens_BeforeAnythingStarts()
    {
        var one = new FakeAgent("agent-1", "t1");
        var endpoint = one.Endpoint("wrong");
        var runner = new DistributedRunner(Plan(), [endpoint], [1], new AgentClient(new HttpClient(new FakeAgentNetwork(one))), Fast, Staging);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Contains("rejected the token", result.FailureReason);
        Assert.Empty(one.Runs);
    }

    [Fact]
    public async Task RunAsync_CountsALostAgentsResultsSoFar_AndReportsIt()
    {
        var one = new FakeAgent("agent-1", "t1");
        var two = new FakeAgent("agent-2", "t2");
        var plan = Plan(p => { p.Profile.StopCondition = StopCondition.Duration; p.Profile.DurationSeconds = 600; });
        var runner = Runner(plan, one, two);
        using var stop = new CancellationTokenSource();
        var cutOff = false;

        var result = await runner.RunAsync(
            s =>
            {
                // Once both report results, agent-2 goes dark; when the controller has given up on it, stop the test.
                if (!cutOff && s.Agents.All(a => a.Results?.TotalRequests > 0))
                {
                    cutOff = true;
                    two.Offline = true;
                }

                if (s.Agents[1].Phase == AgentRunPhase.Lost)
                {
                    stop.Cancel();
                }
            },
            stop.Token);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Contains("agent-2 hasn't answered", result.FailureReason);
        Assert.Equal(AgentRunPhase.Finished, result.Agents[0].Phase);
        Assert.Equal(AgentRunPhase.Lost, result.Agents[1].Phase);
        Assert.True(result.Agents[1].Results!.TotalRequests > 0);
        Assert.Equal(2, result.Combined!.Machines.Count);

        // The lost agent's run is still going on its own machine; only the reachable one was stopped.
        var reachable = one.Runs.Values.Single();
        await reachable.Task;
        Assert.Equal(RunState.Cancelled, reachable.State);
        two.Runs.Values.Single().Stop.Cancel();
    }
}
