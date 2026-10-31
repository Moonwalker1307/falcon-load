using FalconLoad.Core.Distributed;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class LoadSplitterTests
{
    private static LoadTestPlan Plan(Action<LoadTestPlan>? configure = null)
    {
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "http://api.test/items" }],
            Profile = new LoadProfile { Concurrency = 10, StopCondition = StopCondition.RequestCount, TotalRequests = 1000, MaxRequestsPerSecond = 100, DurationSeconds = 45, RampUpSeconds = 5 },
        };
        configure?.Invoke(plan);
        return plan;
    }

    [Fact]
    public void Split_DividesUsersRequestsAndRateByWeight_AndKeepsTheTotals()
    {
        var shares = LoadSplitter.Split(Plan(), [1, 1, 2]);

        Assert.Equal([0, 1, 2], shares.Select(s => s.AgentIndex));
        Assert.Equal([3, 3, 4], shares.Select(s => s.Plan.Profile.Concurrency));
        Assert.Equal([250, 250, 500], shares.Select(s => s.Plan.Profile.TotalRequests));
        Assert.Equal([25, 25, 50], shares.Select(s => s.Plan.Profile.MaxRequestsPerSecond));

        // Virtual users are numbered on from one agent to the next, so {{$vu}} is unique across the whole test.
        Assert.Equal([0, 3, 6], shares.Select(s => s.Plan.Profile.VirtualUserOffset));

        // Time-based settings apply on every agent as they are.
        Assert.All(shares, s =>
        {
            Assert.Equal(45, s.Plan.Profile.DurationSeconds);
            Assert.Equal(5, s.Plan.Profile.RampUpSeconds);
            Assert.Empty(s.Plan.Validate());
        });
    }

    [Fact]
    public void Split_UsesNoMoreAgentsThanVirtualUsers()
    {
        var shares = LoadSplitter.Split(Plan(p => p.Profile.Concurrency = 2), [1, 1, 1]);

        Assert.Equal([0, 1], shares.Select(s => s.AgentIndex));
        Assert.All(shares, s => Assert.Equal(1, s.Plan.Profile.Concurrency));
        Assert.Equal(1000, shares.Sum(s => s.Plan.Profile.TotalRequests));
    }

    [Fact]
    public void Split_UsesNoMoreAgentsThanRequests()
    {
        var single = Assert.Single(LoadSplitter.Split(Plan(p => p.Profile.TotalRequests = 1), [1, 1, 1]));

        Assert.Equal(1, single.Plan.Profile.TotalRequests);
        Assert.Equal(10, single.Plan.Profile.Concurrency);
    }

    [Fact]
    public void Split_SkipsAgentsWithZeroWeight_AndLeavesDurationTestsWhole()
    {
        var plan = Plan(p => p.Profile.StopCondition = StopCondition.Duration);

        var share = Assert.Single(LoadSplitter.Split(plan, [0, 3]));

        Assert.Equal(1, share.AgentIndex);
        Assert.Equal(10, share.Plan.Profile.Concurrency);
        Assert.Equal(1000, share.Plan.Profile.TotalRequests);
        Assert.Equal(100, share.Plan.Profile.MaxRequestsPerSecond);
    }

    [Fact]
    public void Split_DealsSequentialListsRoundRobin_AndLeavesRandomAndShortListsWhole()
    {
        var plan = Plan(p => p.Parameters =
        [
            new ParameterList { Name = "ids", Values = ["a", "b", "c", "d", "e"] },
            new ParameterList { Name = "pick", Values = ["x", "y", "z"], Selection = ParameterSelection.Random },
            new ParameterList { Name = "one", Values = ["only"] },
        ]);

        var shares = LoadSplitter.Split(plan, [1, 1]);

        Assert.Equal(["a", "c", "e"], shares[0].Plan.Parameters[0].Values);
        Assert.Equal(["b", "d"], shares[1].Plan.Parameters[0].Values);
        Assert.All(shares, s =>
        {
            Assert.Equal(["x", "y", "z"], s.Plan.Parameters[1].Values);
            Assert.Equal(["only"], s.Plan.Parameters[2].Values);
        });

        // The original plan is untouched.
        Assert.Equal(5, plan.Parameters[0].Values.Count);
    }

    [Fact]
    public void Split_RejectsInvalidPlansAndEmptyAgentLists()
    {
        Assert.Throws<ArgumentException>(() => LoadSplitter.Split(Plan(p => p.Profile.Concurrency = 0), [1]));
        Assert.Throws<ArgumentException>(() => LoadSplitter.Split(Plan(), []));
        Assert.Throws<ArgumentException>(() => LoadSplitter.Split(Plan(), [0, 0]));
    }

    [Fact]
    public void Allocate_IsProportionalAndAddsUp()
    {
        Assert.Equal([25, 25, 25, 25], LoadSplitter.Allocate(100, [1, 1, 1, 1]));
        Assert.Equal([1, 1, 1], LoadSplitter.Allocate(3, [1, 1, 50]));
        Assert.Equal([34, 33, 33], LoadSplitter.Allocate(100, [1, 1, 1]));
        Assert.Equal([7, 1], LoadSplitter.Allocate(8, [100, 1]));
        Assert.Throws<ArgumentException>(() => LoadSplitter.Allocate(2, [1, 1, 1]));
    }
}
