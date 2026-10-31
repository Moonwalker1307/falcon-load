namespace FalconLoad.Core.Metrics;

/// <summary>Thread-safe aggregation of <see cref="RequestResult"/>s into snapshots.</summary>
public sealed class MetricsCollector
{
    private const int MaxDistinctErrors = 50;

    private readonly Lock _gate = new();
    private readonly LatencyHistogram _overall = new();
    private readonly LatencyHistogram[] _steps;
    private readonly long[] _stepFailures;
    private readonly string[] _stepNames;
    private readonly string[][] _checkNames;
    private readonly long[][] _checkFailures;
    private readonly long[] _checkedResponses;
    private readonly Dictionary<string, long> _statusCodes = new();
    private readonly Dictionary<string, long> _errors = new();
    private readonly SortedDictionary<int, SecondBucket> _timeline = new();

    private long _successful;
    private long _failed;
    private long _bytesSent;
    private long _bytesReceived;

    /// <param name="stepChecks">The names of each request's checks, in order; null when no request has any.</param>
    public MetricsCollector(IReadOnlyList<string> stepNames, IReadOnlyList<IReadOnlyList<string>>? stepChecks = null)
    {
        _stepNames = [.. stepNames];
        _steps = new LatencyHistogram[stepNames.Count];
        for (var i = 0; i < _steps.Length; i++)
        {
            _steps[i] = new LatencyHistogram();
        }

        _stepFailures = new long[stepNames.Count];
        _checkNames = stepNames.Select((_, i) => stepChecks is null || i >= stepChecks.Count ? [] : stepChecks[i].ToArray()).ToArray();
        _checkFailures = _checkNames.Select(names => new long[names.Length]).ToArray();
        _checkedResponses = new long[stepNames.Count];
    }

    public void Record(in RequestResult result)
    {
        lock (_gate)
        {
            _overall.Record(result.Latency);
            _steps[result.StepIndex].Record(result.Latency);
            _bytesSent += result.BytesSent;
            _bytesReceived += result.BytesReceived;

            // Every response runs all of its request's checks, so one count per request gives each check's total.
            var checkFailures = _checkFailures[result.StepIndex];
            if (result.HasResponse && checkFailures.Length > 0)
            {
                _checkedResponses[result.StepIndex]++;
                for (var failed = result.FailedChecks; failed != 0; failed &= failed - 1)
                {
                    checkFailures[System.Numerics.BitOperations.TrailingZeroCount(failed)]++;
                }
            }

            var code = result.StatusCode?.ToString() ?? result.StatusLabel ?? RequestResult.NoResponseStatus;
            _statusCodes[code] = _statusCodes.GetValueOrDefault(code) + 1;

            if (result.Success)
            {
                _successful++;
            }
            else
            {
                _failed++;
                _stepFailures[result.StepIndex]++;
                var message = result.Error ?? "Unknown error";
                if (_errors.ContainsKey(message) || _errors.Count < MaxDistinctErrors)
                {
                    _errors[message] = _errors.GetValueOrDefault(message) + 1;
                }
                else
                {
                    _errors["Other errors"] = _errors.GetValueOrDefault("Other errors") + 1;
                }
            }

            // Attribute the request to the second in which it completed.
            var second = (int)(result.StartedAt + result.Latency).TotalSeconds;
            if (!_timeline.TryGetValue(second, out var bucket))
            {
                bucket = new SecondBucket();
                _timeline[second] = bucket;
            }

            bucket.Histogram.Record(result.Latency);
            if (!result.Success)
            {
                bucket.Failures++;
            }
        }
    }

    public LoadTestSnapshot Snapshot(RunState state, TimeSpan elapsed, int activeVirtualUsers, string? failureReason = null)
    {
        lock (_gate)
        {
            return SnapshotLocked(state, elapsed, activeVirtualUsers, failureReason);
        }
    }

    /// <summary>
    /// A snapshot and the latency histograms behind it, taken together so their counts agree. The histograms are what
    /// lets results from several machines be merged exactly.
    /// </summary>
    public (LoadTestSnapshot Snapshot, ResultHistograms Histograms) SnapshotWithHistograms(
        RunState state, TimeSpan elapsed, int activeVirtualUsers, string? failureReason = null)
    {
        lock (_gate)
        {
            return (SnapshotLocked(state, elapsed, activeVirtualUsers, failureReason), HistogramsLocked(state, elapsed));
        }
    }

    /// <summary>
    /// A snapshot and each request's latency histogram, taken together so their counts agree. Cheaper than
    /// <see cref="SnapshotWithHistograms"/>, as it leaves out the per-second histograms, so it suits frequent
    /// readers such as the metric exporters.
    /// </summary>
    public (LoadTestSnapshot Snapshot, IReadOnlyList<HistogramData> Steps) SnapshotWithStepHistograms(
        RunState state, TimeSpan elapsed, int activeVirtualUsers, string? failureReason = null)
    {
        lock (_gate)
        {
            return (SnapshotLocked(state, elapsed, activeVirtualUsers, failureReason), _steps.Select(h => h.ToData()).ToList());
        }
    }

    private LoadTestSnapshot SnapshotLocked(RunState state, TimeSpan elapsed, int activeVirtualUsers, string? failureReason)
    {
        var lastSecond = LastChartedSecond(state, elapsed);
        var timeline = new List<TimelinePoint>(Math.Max(lastSecond + 1, 0));
        for (var s = 0; s <= lastSecond; s++)
        {
            timeline.Add(_timeline.TryGetValue(s, out var b)
                ? new TimelinePoint(s, b.Histogram.Count, b.Failures, b.Histogram.MeanMs, b.Histogram.Percentile(95))
                : new TimelinePoint(s, 0, 0, 0, 0));
        }

        return new LoadTestSnapshot
        {
            State = state,
            Elapsed = elapsed,
            TotalRequests = _successful + _failed,
            SuccessfulRequests = _successful,
            FailedRequests = _failed,
            ActiveVirtualUsers = activeVirtualUsers,
            BytesSent = _bytesSent,
            BytesReceived = _bytesReceived,
            Latency = _overall.ToStats(),
            StatusCodes = new SortedDictionary<string, long>(_statusCodes),
            Errors = _errors.OrderByDescending(e => e.Value).Select(e => new ErrorCount(e.Key, e.Value)).ToList(),
            Steps = _steps.Select((h, i) => new StepStats(i, _stepNames[i], h.Count, _stepFailures[i], h.ToStats())).ToList(),
            Timeline = timeline,
            Checks = CheckStatsLocked(),
            FailureReason = failureReason,
        };
    }

    private List<CheckStats> CheckStatsLocked()
    {
        var checks = new List<CheckStats>();
        for (var step = 0; step < _checkNames.Length; step++)
        {
            for (var i = 0; i < _checkNames[step].Length; i++)
            {
                var failed = _checkFailures[step][i];
                checks.Add(new CheckStats(step, _checkNames[step][i], _checkedResponses[step] - failed, failed));
            }
        }

        return checks;
    }

    private ResultHistograms HistogramsLocked(RunState state, TimeSpan elapsed)
    {
        var lastSecond = LastChartedSecond(state, elapsed);
        return new ResultHistograms(
            _overall.ToData(),
            _steps.Select(h => h.ToData()).ToList(),
            _timeline.Where(t => t.Key <= lastSecond)
                .Select(t => new SecondHistogram(t.Key, t.Value.Failures, t.Value.Histogram.ToData()))
                .ToList());
    }

    // Only full seconds are charted, so the per-second numbers stay comparable and the chart doesn't
    // dip at the end. A run shorter than one second still shows its single (partial) second.
    private int LastChartedSecond(RunState state, TimeSpan elapsed)
    {
        var lastSecond = (int)elapsed.TotalSeconds - 1;
        if (lastSecond < 0 && state != RunState.Running && _timeline.Count > 0)
        {
            lastSecond = 0;
        }

        return lastSecond;
    }

    private sealed class SecondBucket
    {
        public LatencyHistogram Histogram { get; } = new();

        public long Failures { get; set; }
    }
}
