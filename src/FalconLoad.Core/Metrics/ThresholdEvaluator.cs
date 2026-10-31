using FalconLoad.Core.Model;

namespace FalconLoad.Core.Metrics;

/// <summary>Measures a plan's <see cref="Threshold"/>s against a run's results.</summary>
public static class ThresholdEvaluator
{
    /// <summary>
    /// Returns <paramref name="snapshot"/> with its <see cref="LoadTestSnapshot.Thresholds"/> filled in from the enabled
    /// thresholds. A threshold over requests that haven't sent anything yet fails, since there is nothing to show it holds.
    /// </summary>
    public static LoadTestSnapshot Apply(IReadOnlyList<Threshold> thresholds, LoadTestSnapshot snapshot) =>
        thresholds.Any(t => t.Enabled) ? snapshot with { Thresholds = Evaluate(thresholds, snapshot) } : snapshot;

    public static IReadOnlyList<ThresholdResult> Evaluate(IReadOnlyList<Threshold> thresholds, LoadTestSnapshot snapshot)
    {
        var results = new List<ThresholdResult>();
        foreach (var threshold in thresholds.Where(t => t.Enabled))
        {
            var name = threshold.Description;
            long requests;
            long failures;
            LatencyStats latency;
            if (threshold.RequestIndex is { } index)
            {
                var step = snapshot.Steps.FirstOrDefault(s => s.StepIndex == index);
                name = $"{(step is null ? $"Request {index + 1}" : $"Request {index + 1} ({step.Name})")}: {name}";
                requests = step?.Requests ?? 0;
                failures = step?.Failures ?? 0;
                latency = step?.Latency ?? LatencyStats.Empty;
            }
            else
            {
                requests = snapshot.TotalRequests;
                failures = snapshot.FailedRequests;
                latency = snapshot.Latency;
            }

            double? actual = requests == 0 ? null : threshold.Metric switch
            {
                ThresholdMetric.ErrorRate => 100.0 * failures / requests,
                ThresholdMetric.MeanLatency => latency.MeanMs,
                ThresholdMetric.P50Latency => latency.P50Ms,
                ThresholdMetric.P90Latency => latency.P90Ms,
                ThresholdMetric.P95Latency => latency.P95Ms,
                ThresholdMetric.P99Latency => latency.P99Ms,
                ThresholdMetric.MaxLatency => latency.MaxMs,
                _ => snapshot.Elapsed.TotalSeconds <= 0 ? 0 : requests / snapshot.Elapsed.TotalSeconds,
            };

            results.Add(new ThresholdResult(name, Threshold.Unit(threshold.Metric), threshold.Value, actual, actual is { } a && threshold.Holds(a)));
        }

        return results;
    }
}
