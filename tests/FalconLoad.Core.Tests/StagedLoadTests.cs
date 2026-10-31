using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class StagedLoadTests
{
    private static LoadStage Hold(double target, int seconds) => new() { Target = target, DurationSeconds = seconds };

    private static LoadStage Ramp(double target, int seconds) => new() { Target = target, DurationSeconds = seconds, Ramp = true };

    private static LoadTestPlan Plan(StageMode mode, int concurrency, params LoadStage[] stages) => new()
    {
        Requests = [new RequestDefinition { Url = "http://api.test/items" }],
        Profile = new LoadProfile { Concurrency = concurrency, StageMode = mode, Stages = [.. stages] },
    };

    [Fact]
    public void TargetAt_HoldsAndRampsFromTheStageBefore()
    {
        var schedule = new StageSchedule([Ramp(100, 10), Hold(100, 10), Ramp(20, 10), Hold(60, 10)]);

        Assert.Equal(40, schedule.TotalSeconds);
        Assert.Equal(100, schedule.Peak);
        Assert.Equal(0, schedule.TargetAt(0));
        Assert.Equal(50, schedule.TargetAt(5));
        Assert.Equal(100, schedule.TargetAt(15));
        Assert.Equal(60, schedule.TargetAt(25));
        Assert.Equal(60, schedule.TargetAt(35));
        Assert.Equal(60, schedule.TargetAt(99), 6);
    }

    [Fact]
    public void StageIndexAt_ReportsTheRunningStage()
    {
        var schedule = new StageSchedule([Hold(1, 10), Hold(2, 10), Hold(3, 10)]);

        Assert.Equal([0, 0, 1, 2, 2], new[] { 0.0, 9.9, 10.0, 25.0, 500.0 }.Select(schedule.StageIndexAt));
    }

    [Fact]
    public void DueSecondOfRequest_InvertsRequestsDueBy()
    {
        // A ramp from nothing, a hold, a ramp down, a pause and a ramp that starts from the pause.
        var schedule = new StageSchedule([Ramp(50, 8), Hold(50, 4), Ramp(10, 4), Hold(0, 5), Ramp(30, 6)]);

        for (var n = 0; n < schedule.TotalRequests - 1; n += 7)
        {
            var due = schedule.DueSecondOfRequest(n);
            Assert.NotNull(due);
            Assert.Equal(n, schedule.RequestsDueBy(due.Value), 6);
        }

        Assert.Null(schedule.DueSecondOfRequest(schedule.TotalRequests));
        Assert.Equal(0, schedule.DueSecondOfRequest(0));
    }

    [Fact]
    public void DueSecondOfRequest_SpacesAConstantRateEvenly()
    {
        var schedule = new StageSchedule([Hold(4, 10)]);

        Assert.Equal(40, schedule.TotalRequests);
        Assert.Equal(0.25, schedule.DueSecondOfRequest(1));
        Assert.Equal(5, schedule.DueSecondOfRequest(20));
    }

    [Fact]
    public void Outline_JumpsStraightUpAtAHoldAndSlopesAtARamp()
    {
        var outline = new StageSchedule([Hold(10, 5), Hold(40, 5), Ramp(20, 5)]).Outline();

        Assert.Equal(
            [(0, 0), (0, 10), (5, 10), (5, 10), (5, 40), (10, 40), (10, 40), (15, 20)],
            outline.Select(p => (p.Seconds, p.Target)));
    }

    [Fact]
    public void Presets_BuildTheUsualShapes()
    {
        var step = StagePresets.Step(100, steps: 4, secondsPerStep: 30);
        Assert.Equal([25, 50, 75, 100], step.Select(s => s.Target));
        Assert.All(step, s => Assert.False(s.Ramp));
        Assert.All(step, s => Assert.Equal(30, s.DurationSeconds));

        var spike = StagePresets.Spike(200);
        Assert.Equal([20, 200, 20], spike.Select(s => s.Target));

        var soak = StagePresets.Soak(50, rampSeconds: 60, holdSeconds: 3600);
        Assert.True(soak[0].Ramp);
        Assert.Equal([50, 50], soak.Select(s => s.Target));
        Assert.Equal(3660, new StageSchedule(soak).TotalSeconds);

        Assert.Equal([1], StagePresets.Spike(1).Select(s => s.Target).Distinct());
        Assert.All(new[] { step, spike, soak }, stages => Assert.Empty(new LoadProfile { Stages = stages }.Validate()));
    }

    [Fact]
    public void Validate_ChecksTheStages()
    {
        var profile = new LoadProfile { Stages = [Hold(5, 0), Hold(-1, 10), Hold(20_000, 10)] };

        var problems = profile.Validate().ToList();

        Assert.Contains(problems, p => p.StartsWith("Stage 1") && p.Contains("duration"));
        Assert.Contains(problems, p => p.StartsWith("Stage 2") && p.Contains("target"));
        Assert.Contains(problems, p => p.StartsWith("Stage 3") && p.Contains("10,000 virtual users"));

        Assert.Contains("At least one stage needs a target above 0.", new LoadProfile { Stages = [Hold(0, 10)] }.Validate());
        Assert.Contains(new LoadProfile { Stages = [Hold(1, 86_400), Hold(1, 1)] }.Validate(), p => p.Contains("24 hours"));
        Assert.Contains(new LoadProfile { Stages = [.. Enumerable.Range(0, 101).Select(_ => Hold(1, 1))] }.Validate(), p => p.Contains("at most 100"));
    }

    [Fact]
    public void Validate_StagesReplaceTheConstantLoadSettings()
    {
        // The settings of a constant load are not used once there are stages, so they can't make the profile invalid.
        var users = new LoadProfile { Concurrency = 0, DurationSeconds = 0, RampUpSeconds = -1, Stages = [Hold(5, 10)] };
        Assert.Empty(users.Validate());

        // A request rate still needs users to send it.
        var rate = new LoadProfile { Concurrency = 0, StageMode = StageMode.RequestRate, Stages = [Hold(5, 10)] };
        Assert.Contains("Virtual users must be between 1 and 10,000.", rate.Validate());

        // Without stages nothing changes.
        Assert.Contains("Concurrency must be between 1 and 10,000.", new LoadProfile { Concurrency = 0 }.Validate());
    }

    [Fact]
    public void PlansSavedBeforeStagesExistStillLoadAsConstantLoad()
    {
        const string json = """
            { "name": "Old", "requests": [{ "method": "GET", "url": "http://api.test" }],
              "profile": { "concurrency": 5, "stopCondition": "Duration", "durationSeconds": 10, "rampUpSeconds": 2 } }
            """;

        var plan = JsonSerializer.Deserialize<LoadTestPlan>(json, PlanFile.JsonOptions)!;

        Assert.False(plan.Profile.IsStaged);
        Assert.Empty(plan.Profile.Stages);
        Assert.Equal(StageMode.VirtualUsers, plan.Profile.StageMode);
        Assert.Empty(plan.Validate());
        Assert.Equal(5, plan.Profile.PeakVirtualUsers);
    }

    [Fact]
    public void StagesSurviveCloneAndExport_AndStayOutOfTheJsonWhenComputed()
    {
        var plan = Plan(StageMode.RequestRate, 8, Ramp(30, 10), Hold(30, 20));

        var clone = plan.Clone();
        var imported = Assert.Single(PlanFile.Read(PlanFile.Write([plan])).Plans);

        foreach (var copy in new[] { clone, imported })
        {
            Assert.Equal(StageMode.RequestRate, copy.Profile.StageMode);
            Assert.Equal([(10, 30.0, true), (20, 30.0, false)], copy.Profile.Stages.Select(s => (s.DurationSeconds, s.Target, s.Ramp)));
        }

        Assert.DoesNotContain("isStaged", PlanFile.Write([plan]));
    }

    [Fact]
    public void Import_TreatsNullStagesAsNone()
    {
        const string json = """{ "name": "Hand written", "requests": [{ "url": "http://api.test" }], "profile": { "stages": null } }""";

        var import = PlanFile.Read(json);

        Assert.True(import.Succeeded);
        Assert.False(Assert.Single(import.Plans).Profile.IsStaged);
    }

    [Fact]
    public async Task VirtualUserStages_FollowTheTargetUpAndDown()
    {
        // Four stages of 1.5 s: one user, four users, a pause, two users. Requests take long enough to overlap.
        var clock = Stopwatch.StartNew();
        var inFlight = 0;
        var arrivals = new List<(double At, int InFlight)>();
        var handler = new FakeHandler(async (_, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (arrivals)
            {
                arrivals.Add((clock.Elapsed.TotalSeconds, now));
            }

            try
            {
                await Task.Delay(20, ct);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var plan = Plan(StageMode.VirtualUsers, 1, Hold(1, 2), Hold(4, 2), Hold(0, 2), Hold(2, 2));
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        clock.Restart();
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.InRange(result.Elapsed.TotalSeconds, 7.5, 12);
        // The windows stay clear of the stage edges so a slow machine doesn't blur them.
        var first = arrivals.Where(a => a.At is > 0.3 and < 1.7).ToList();
        var second = arrivals.Where(a => a.At is > 2.3 and < 3.7).ToList();
        var pause = arrivals.Where(a => a.At is > 4.3 and < 5.7).ToList();
        var last = arrivals.Where(a => a.At is > 6.3 and < 7.7).ToList();
        Assert.NotEmpty(first);
        Assert.Equal(1, first.Max(a => a.InFlight));
        Assert.Equal(4, second.Max(a => a.InFlight));
        Assert.Empty(pause);
        Assert.Equal(2, last.Max(a => a.InFlight));
    }

    [Fact]
    public async Task VirtualUserStages_IgnoreTheRequestCount()
    {
        var plan = Plan(StageMode.VirtualUsers, 1, Hold(2, 1));
        plan.Profile.StopCondition = StopCondition.RequestCount;
        plan.Profile.TotalRequests = 3;
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.True(result.TotalRequests > 3);
        Assert.InRange(result.Elapsed.TotalSeconds, 0.9, 5);
    }

    [Fact]
    public async Task VirtualUserStages_KeepCountingIterationsWhenAUserComesBack()
    {
        // The single user stops during the pause and starts again for the last stage; its iteration numbers go on.
        var plan = Plan(StageMode.VirtualUsers, 1, Hold(1, 1), Hold(0, 1), Hold(1, 1));
        plan.Requests[0].Url = "http://api.test/items/{{$iteration}}";
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(100));
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        var iterations = handler.Received.Select(r => int.Parse(r.Request.RequestUri!.Segments[^1])).ToList();
        Assert.Equal(Enumerable.Range(1, iterations.Count), iterations);
    }

    [Fact]
    public async Task RateStages_NeverSendMoreThanTheTarget()
    {
        // 20 requests per second for 2 seconds is 40 requests; with instant responses and four users it should get close.
        var plan = Plan(StageMode.RequestRate, 4, Hold(20, 2));
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.InRange(result.TotalRequests, 30, 41);
        Assert.Equal(4, plan.Profile.Concurrency);
    }

    [Fact]
    public async Task RateStages_SpreadARampFromNothing()
    {
        // 0 to 40 requests per second over 2 seconds is 40 requests, more of them late than early.
        var clock = new Stopwatch();
        var times = new List<double>();
        var handler = new FakeHandler((_, _) =>
        {
            lock (times)
            {
                times.Add(clock.Elapsed.TotalSeconds);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var plan = Plan(StageMode.RequestRate, 4, Ramp(40, 2));
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        clock.Start();
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.InRange(result.TotalRequests, 25, 41);
        Assert.True(times.Count(t => t < 1) < times.Count(t => t >= 1), "the second half of a ramp up should hold most of the requests");
    }

    [Fact]
    public async Task RateStages_SlowRequestsLeaveAShortfallInsteadOfABurst()
    {
        // One user taking 250 ms per request can send about 4 requests per second, whatever the stage asks for.
        var plan = Plan(StageMode.RequestRate, 1, Hold(50, 2));
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(250));
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.InRange(result.TotalRequests, 1, 12);
    }
}
