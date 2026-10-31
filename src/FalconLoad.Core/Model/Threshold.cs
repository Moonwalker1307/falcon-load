using System.Globalization;
using System.Text.Json.Serialization;

namespace FalconLoad.Core.Model;

public enum ThresholdMetric
{
    /// <summary>Failed requests as a percentage of all requests, 0 to 100.</summary>
    ErrorRate,

    MeanLatency,
    P50Latency,
    P90Latency,
    P95Latency,
    P99Latency,
    MaxLatency,

    /// <summary>Average requests per second over the run.</summary>
    RequestsPerSecond,
}

public enum ThresholdOperator
{
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
}

/// <summary>
/// A pass/fail rule for a whole run, such as "p95 latency &lt; 500 ms" or "error rate &lt; 1%". A run with thresholds
/// passes when it completes and every enabled threshold holds; that verdict is what a CI job checks.
/// </summary>
public sealed class Threshold
{
    public ThresholdMetric Metric { get; set; } = ThresholdMetric.P95Latency;

    public ThresholdOperator Operator { get; set; } = ThresholdOperator.LessThan;

    /// <summary>The limit, in the metric's unit: milliseconds, percent or requests per second.</summary>
    public double Value { get; set; } = 500;

    /// <summary>The zero-based position of the request this applies to, or null for all requests together.</summary>
    public int? RequestIndex { get; set; }

    public bool Enabled { get; set; } = true;

    public static bool IsLatency(ThresholdMetric metric) =>
        metric is not (ThresholdMetric.ErrorRate or ThresholdMetric.RequestsPerSecond);

    public static string Unit(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.ErrorRate => "%",
        ThresholdMetric.RequestsPerSecond => "req/s",
        _ => "ms",
    };

    public static string MetricText(ThresholdMetric metric) => metric switch
    {
        ThresholdMetric.ErrorRate => "Error rate",
        ThresholdMetric.MeanLatency => "Mean latency",
        ThresholdMetric.P50Latency => "p50 latency",
        ThresholdMetric.P90Latency => "p90 latency",
        ThresholdMetric.P95Latency => "p95 latency",
        ThresholdMetric.P99Latency => "p99 latency",
        ThresholdMetric.MaxLatency => "Max latency",
        ThresholdMetric.RequestsPerSecond => "Throughput",
        _ => metric.ToString(),
    };

    public static string OperatorText(ThresholdOperator op) => op switch
    {
        ThresholdOperator.LessThan => "<",
        ThresholdOperator.LessThanOrEqual => "≤",
        ThresholdOperator.GreaterThan => ">",
        _ => "≥",
    };

    public bool Holds(double actual) => Operator switch
    {
        ThresholdOperator.LessThan => actual < Value,
        ThresholdOperator.LessThanOrEqual => actual <= Value,
        ThresholdOperator.GreaterThan => actual > Value,
        _ => actual >= Value,
    };

    /// <summary>A short readable form, e.g. "p95 latency &lt; 500 ms", without the request it applies to.</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var unit = Unit(Metric);
            var value = Value.ToString("#,0.###", CultureInfo.InvariantCulture);
            return $"{MetricText(Metric)} {OperatorText(Operator)} {value}{(unit == "%" ? "%" : " " + unit)}";
        }
    }

    public IEnumerable<string> Validate(int requestCount)
    {
        if (!Enum.IsDefined(Metric) || !Enum.IsDefined(Operator))
        {
            yield return "Unknown threshold metric or comparison.";
            yield break;
        }

        if (!double.IsFinite(Value) || Value < 0)
        {
            yield return $"\"{Description}\" needs a limit of 0 or more.";
        }
        else if (Metric == ThresholdMetric.ErrorRate && Value > 100)
        {
            yield return "An error rate limit is a percentage between 0 and 100.";
        }

        if (RequestIndex is { } index && (index < 0 || index >= requestCount))
        {
            yield return $"\"{Description}\" applies to request {index + 1}, but the test has {requestCount} {(requestCount == 1 ? "request" : "requests")}.";
        }
    }
}
