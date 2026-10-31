using FalconLoad.Core.Model;

namespace FalconLoad.Core.Distributed;

/// <summary>
/// Splits one test into the shares that agents run, so that all the shares together send the load the test asks for:
/// the virtual users, the request count and the rate cap are divided by weight, while the duration, ramp-up and
/// timeout stay the same on every agent. A test never uses more agents than it has virtual users (or requests).
/// A staged test keeps its stage lengths on every agent and each stage's target is divided by weight; virtual user
/// targets are rounded up on each agent, so the agents together may run up to one user more per agent than the target.
/// </summary>
public static class LoadSplitter
{
    /// <summary>One agent's share of a test.</summary>
    /// <param name="AgentIndex">The index of the agent in the list the weights were given for.</param>
    public sealed record Share(int AgentIndex, LoadTestPlan Plan);

    /// <summary>
    /// Splits <paramref name="plan"/> over the agents whose weight is above zero. An agent with weight 2 gets twice
    /// the virtual users of one with weight 1, and every agent that is used gets at least one.
    /// </summary>
    public static IReadOnlyList<Share> Split(LoadTestPlan plan, IReadOnlyList<int> weights)
    {
        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("The load test plan is invalid: " + string.Join(" ", problems), nameof(plan));
        }

        var eligible = Enumerable.Range(0, weights.Count).Where(i => weights[i] > 0).ToList();
        if (eligible.Count == 0)
        {
            throw new ArgumentException("At least one agent with a weight above zero is needed.", nameof(weights));
        }

        var profile = plan.Profile;
        var userStages = profile.IsStaged && profile.StageMode == StageMode.VirtualUsers;
        var countsRequests = !profile.IsStaged && profile.StopCondition == StopCondition.RequestCount;
        var count = Math.Min(eligible.Count, userStages ? profile.PeakVirtualUsers : profile.Concurrency);
        if (countsRequests)
        {
            count = (int)Math.Min(count, profile.TotalRequests);
        }

        var used = eligible.Take(count).ToList();
        var shareWeights = used.Select(i => (long)weights[i]).ToList();
        var totalWeight = shareWeights.Sum();

        // Virtual user stages set the number of users themselves, so the fixed number is only divided otherwise.
        var users = userStages ? null : Allocate(profile.Concurrency, shareWeights);
        var requests = countsRequests ? Allocate(profile.TotalRequests, shareWeights) : null;

        var shares = new List<Share>(count);
        var offset = 0;
        for (var j = 0; j < count; j++)
        {
            var share = plan.Clone();
            if (users is not null)
            {
                share.Profile.Concurrency = (int)users[j];
            }

            foreach (var stage in share.Profile.Stages)
            {
                stage.Target = stage.Target * shareWeights[j] / totalWeight;
            }

            share.Profile.VirtualUserOffset = profile.VirtualUserOffset + offset;
            offset += userStages ? share.Profile.PeakVirtualUsers : (int)users![j];
            if (requests is not null)
            {
                share.Profile.TotalRequests = (int)requests[j];
            }

            if (profile.MaxRequestsPerSecond > 0)
            {
                share.Profile.MaxRequestsPerSecond = profile.MaxRequestsPerSecond * shareWeights[j] / totalWeight;
            }

            // A list handed out in order is dealt round-robin to the agents, so no two agents use the same value at
            // the same time and every value is still used once per pass. A short list is left whole.
            foreach (var list in share.Parameters.Where(l => l.Selection == ParameterSelection.Sequential && l.Values.Count >= count))
            {
                list.Values = list.Values.Where((_, k) => k % count == j).ToList();
            }

            // Rows of a data file are dealt out the same way, so two agents never send the same row at the same time.
            share.DataFiles = [.. share.DataFiles.Select(f =>
                f.Selection == ParameterSelection.Sequential && f.TryGetTable(out var table, out _) && table.Rows.Count >= count ? f.TakeEvery(count, j) : f)];

            shares.Add(new Share(used[j], share));
        }

        return shares;
    }

    /// <summary>
    /// Divides <paramref name="total"/> by weight into whole numbers that add up to it, each at least one. The parts
    /// are rounded by largest remainder, so the result is as close to proportional as whole numbers allow.
    /// </summary>
    internal static long[] Allocate(long total, IReadOnlyList<long> weights)
    {
        var n = weights.Count;
        if (total < n)
        {
            throw new ArgumentException($"Can't give {n} agents at least one each out of {total}.", nameof(total));
        }

        var totalWeight = weights.Sum();
        var remaining = total - n;
        var parts = new long[n];
        var remainders = new double[n];
        long given = 0;
        for (var i = 0; i < n; i++)
        {
            var exact = (double)remaining * weights[i] / totalWeight;
            parts[i] = 1 + (long)Math.Floor(exact);
            remainders[i] = exact - Math.Floor(exact);
            given += parts[i];
        }

        // Hand the rounding leftovers to the agents that were rounded down the most, first come first served on ties.
        foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)(total - given)))
        {
            parts[i]++;
        }

        return parts;
    }
}
