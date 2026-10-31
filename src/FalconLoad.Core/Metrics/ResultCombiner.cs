namespace FalconLoad.Core.Metrics;

/// <summary>The results of several machines that ran a test together, combined into one view.</summary>
/// <param name="Combined">All machines' requests as if one machine had sent them.</param>
/// <param name="StartedAt">When the first machine started.</param>
/// <param name="Machines">Each machine's own results, in the order they were given.</param>
/// <param name="Warnings">Things that make the combined numbers harder to read, such as runs that didn't overlap.</param>
public sealed record CombinedResult(
    LoadTestSnapshot Combined,
    DateTimeOffset StartedAt,
    IReadOnlyList<MachineResult> Machines,
    IReadOnlyList<string> Warnings);

/// <param name="Name">The machine name from the export, made unique.</param>
/// <param name="OffsetSeconds">How many seconds after the first machine this one started; its timeline starts there.</param>
public sealed record MachineResult(
    string Name,
    Guid RunId,
    DateTimeOffset StartedAt,
    int OffsetSeconds,
    string PlanName,
    int Concurrency,
    LoadTestSnapshot Results);

/// <summary>
/// Merges exported runs from several machines. Counts are added up and latency histograms are merged bucket by
/// bucket, so the combined percentiles are exactly those of one histogram holding every request. Timelines are
/// lined up by each run's start time, so the machines' clocks should be in sync.
/// </summary>
public static class ResultCombiner
{
    /// <summary>Returns the reasons <paramref name="runs"/> can't be combined; empty when they can.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<RunExport> runs)
    {
        var problems = new List<string>();
        if (runs.Count == 0)
        {
            problems.Add("Add at least one exported result.");
            return problems;
        }

        var names = Names(runs);
        var seen = new HashSet<Guid>();
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.Id != Guid.Empty && !seen.Add(run.Id))
            {
                problems.Add($"{names[i]}: the same run was added twice.");
            }

            if (run.Histograms is not { } histograms)
            {
                problems.Add($"{names[i]}: the file has no latency histograms, so its percentiles can't be merged. Export the run again with this version of falcon-load.");
                continue;
            }

            if (histograms.Steps.Count != run.Results.Steps.Count)
            {
                problems.Add($"{names[i]}: the file has histograms for {histograms.Steps.Count} requests but results for {run.Results.Steps.Count}.");
            }

            try
            {
                var overall = LatencyHistogram.FromData(histograms.Overall);
                if (overall.Count != run.Results.TotalRequests)
                {
                    problems.Add($"{names[i]}: the latency histogram holds {overall.Count} requests but the results count {run.Results.TotalRequests}.");
                }

                foreach (var step in histograms.Steps)
                {
                    LatencyHistogram.FromData(step);
                }

                foreach (var second in histograms.Timeline)
                {
                    LatencyHistogram.FromData(second.Latency);
                }
            }
            catch (FormatException ex)
            {
                problems.Add($"{names[i]}: the latency histograms are damaged. {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>Combines <paramref name="runs"/>. Throws <see cref="ArgumentException"/> if <see cref="Validate"/> finds problems.</summary>
    public static CombinedResult Combine(IReadOnlyList<RunExport> runs)
    {
        var problems = Validate(runs);
        if (problems.Count > 0)
        {
            throw new ArgumentException("The results can't be combined: " + string.Join(" ", problems), nameof(runs));
        }

        var names = Names(runs);
        var start = runs.Min(r => r.StartedAt);
        var end = runs.Max(r => r.StartedAt + r.Results.Elapsed);

        var overall = new LatencyHistogram();
        var statusCodes = new SortedDictionary<string, long>();
        var errors = new Dictionary<string, long>();
        var steps = new List<(int Index, string Name, long Failures, LatencyHistogram Latency)>();
        var timeline = new SortedDictionary<int, (long Failures, LatencyHistogram Latency)>();
        var machines = new List<MachineResult>(runs.Count);
        var timelineLength = 0;

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            var results = run.Results;
            var histograms = run.Histograms!;
            var offset = (int)Math.Round((run.StartedAt - start).TotalSeconds);
            machines.Add(new MachineResult(names[i], run.Id, run.StartedAt, offset, run.Plan.Name, run.Plan.Profile.PeakVirtualUsers, results));

            overall.Add(LatencyHistogram.FromData(histograms.Overall));
            foreach (var (code, count) in results.StatusCodes)
            {
                statusCodes[code] = statusCodes.GetValueOrDefault(code) + count;
            }

            foreach (var error in results.Errors)
            {
                errors[error.Message] = errors.GetValueOrDefault(error.Message) + error.Count;
            }

            // Requests are matched by position and name, so machines running the same test line up and a
            // request that only one machine sent still gets its own row.
            for (var s = 0; s < results.Steps.Count; s++)
            {
                var step = results.Steps[s];
                var at = steps.FindIndex(x => x.Index == step.StepIndex && x.Name == step.Name);
                if (at < 0)
                {
                    steps.Add((step.StepIndex, step.Name, 0, new LatencyHistogram()));
                    at = steps.Count - 1;
                }

                var existing = steps[at];
                existing.Latency.Add(LatencyHistogram.FromData(histograms.Steps[s]));
                steps[at] = existing with { Failures = existing.Failures + step.Failures };
            }

            foreach (var second in histograms.Timeline)
            {
                var key = second.Second + offset;
                if (!timeline.TryGetValue(key, out var bucket))
                {
                    bucket = (0, new LatencyHistogram());
                }

                bucket.Latency.Add(LatencyHistogram.FromData(second.Latency));
                timeline[key] = (bucket.Failures + second.Failures, bucket.Latency);
            }

            timelineLength = Math.Max(timelineLength, offset + results.Timeline.Count);
        }

        var points = new List<TimelinePoint>(timelineLength);
        for (var s = 0; s < timelineLength; s++)
        {
            points.Add(timeline.TryGetValue(s, out var b)
                ? new TimelinePoint(s, b.Latency.Count, b.Failures, b.Latency.MeanMs, b.Latency.Percentile(95))
                : new TimelinePoint(s, 0, 0, 0, 0));
        }

        var failures = runs
            .Select((r, i) => r.Results.FailureReason is { } reason ? $"{names[i]}: {reason}" : null)
            .OfType<string>()
            .ToList();

        var combined = new LoadTestSnapshot
        {
            State = CombinedState(runs),
            Elapsed = end - start,
            TotalRequests = runs.Sum(r => r.Results.TotalRequests),
            SuccessfulRequests = runs.Sum(r => r.Results.SuccessfulRequests),
            FailedRequests = runs.Sum(r => r.Results.FailedRequests),
            ActiveVirtualUsers = runs.Sum(r => r.Results.ActiveVirtualUsers),
            BytesSent = runs.Sum(r => r.Results.BytesSent),
            BytesReceived = runs.Sum(r => r.Results.BytesReceived),
            Latency = overall.ToStats(),
            StatusCodes = statusCodes,
            Errors = errors.OrderByDescending(e => e.Value).Select(e => new ErrorCount(e.Key, e.Value)).ToList(),
            Steps = steps.Select((s, i) => new StepStats(i, s.Name, s.Latency.Count, s.Failures, s.Latency.ToStats())).ToList(),
            Timeline = points,
            FailureReason = failures.Count == 0 ? null : string.Join(" ", failures),
        };

        return new CombinedResult(combined, start, machines, Warnings(runs, names, start));
    }

    private static List<string> Warnings(IReadOnlyList<RunExport> runs, IReadOnlyList<string> names, DateTimeOffset start)
    {
        var warnings = new List<string>();
        var tests = runs.Select(r => r.Plan.Name).Distinct().ToList();
        if (tests.Count > 1)
        {
            warnings.Add($"The results come from different tests ({string.Join(", ", tests)}), so the combined numbers mix them.");
        }

        var environments = runs.Select(r => string.IsNullOrWhiteSpace(r.Plan.Environment) ? "no environment" : r.Plan.Environment).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (environments.Count > 1)
        {
            warnings.Add($"The runs used different environments ({string.Join(", ", environments)}), so they may not have hit the same system.");
        }

        var running = runs.Select((r, i) => r.Results.IsFinished ? null : names[i]).OfType<string>().ToList();
        if (running.Count > 0)
        {
            warnings.Add($"{string.Join(", ", running)} hadn't finished when the result was exported.");
        }

        // Throughput is averaged from the first start to the last end, which only means "the load the API took"
        // when the machines sent their requests at the same time.
        var lastStart = runs.Max(r => r.StartedAt);
        var firstEnd = runs.Min(r => r.StartedAt + r.Results.Elapsed);
        if (runs.Count > 1 && lastStart >= firstEnd)
        {
            warnings.Add($"Not all runs overlapped in time (the last started {Seconds(lastStart - start)} after the first). Throughput is averaged over the whole span, so it is lower than the load at any moment.");
        }

        return warnings;
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds < 120 ? $"{t.TotalSeconds:0}s" : $"{t.TotalMinutes:0} min";

    private static RunState CombinedState(IReadOnlyList<RunExport> runs)
    {
        var states = runs.Select(r => r.Results.State).ToHashSet();
        return states.Contains(RunState.Running) || states.Contains(RunState.Pending) ? RunState.Running
            : states.Contains(RunState.Failed) ? RunState.Failed
            : states.Contains(RunState.Cancelled) ? RunState.Cancelled
            : RunState.Completed;
    }

    /// <summary>Labels each run by its machine, numbering machines that appear more than once.</summary>
    private static List<string> Names(IReadOnlyList<RunExport> runs)
    {
        var baseNames = runs.Select((r, i) => string.IsNullOrWhiteSpace(r.Machine) ? $"Result {i + 1}" : r.Machine.Trim()).ToList();
        var totals = baseNames.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return baseNames.Select(n =>
        {
            if (totals[n] == 1)
            {
                return n;
            }

            counters[n] = counters.GetValueOrDefault(n) + 1;
            return $"{n} #{counters[n]}";
        }).ToList();
    }
}
