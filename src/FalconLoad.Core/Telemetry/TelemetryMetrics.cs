using System.Globalization;
using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Telemetry;

public enum MetricType
{
    /// <summary>A total that only grows during a run, such as the number of requests.</summary>
    Counter,

    /// <summary>A value that goes up and down, such as the active virtual users.</summary>
    Gauge,

    Histogram,
}

/// <summary>
/// One metric with all its series. <see cref="Name"/> and <see cref="Unit"/> follow OpenTelemetry;
/// <see cref="PrometheusName"/> adds the suffixes Prometheus gives the same metric when it receives it over OTLP, so
/// a dashboard works the same whether Prometheus scrapes falcon-load or gets its metrics pushed.
/// </summary>
public sealed record MetricFamily(string Name, string Unit, MetricType Type, string Description, IReadOnlyList<MetricPoint> Points)
{
    public string PrometheusName
    {
        get
        {
            var name = Unit switch
            {
                "s" => Name + "_seconds",
                "By" => Name + "_bytes",
                _ => Name,
            };
            return Type == MetricType.Counter ? name + "_total" : name;
        }
    }
}

/// <param name="Value">The count, total or gauge value; for a histogram, the number of samples.</param>
/// <param name="Buckets">For a histogram, the samples per bucket of <see cref="TelemetryMetrics.LatencyBoundsSeconds"/>, not cumulative, plus one for those above the last bound.</param>
/// <param name="Sum">For a histogram, the sum of the samples.</param>
/// <param name="Start">When the series started counting: the start of its run.</param>
public sealed record MetricPoint(
    IReadOnlyList<KeyValuePair<string, string>> Labels,
    double Value,
    DateTimeOffset Start,
    long[]? Buckets = null,
    double Sum = 0);

/// <summary>
/// Turns live runs into the metrics that the Prometheus endpoint and the OTLP push report. Every series is labelled
/// with its run, test and machine, so the agents of a distributed test are separate series that add up to the whole
/// test, and the per-request series add up to the run.
/// </summary>
public static class TelemetryMetrics
{
    public const string Prefix = "falcon_load_";

    /// <summary>The upper bounds of the latency histogram's buckets, in seconds, from 1 ms to a minute.</summary>
    public static IReadOnlyList<double> LatencyBoundsSeconds { get; } =
        [0.001, 0.0025, 0.005, 0.0075, 0.01, 0.015, 0.02, 0.03, 0.05, 0.075, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.75, 1, 1.5, 2, 3, 5, 7.5, 10, 15, 30, 60];

    private static readonly double[] LatencyBoundsMs = [.. LatencyBoundsSeconds.Select(s => s * 1000)];

    public static IReadOnlyList<MetricFamily> Build(IReadOnlyList<RunTelemetry> runs)
    {
        var requests = new List<MetricPoint>();
        var duration = new List<MetricPoint>();
        var responses = new List<MetricPoint>();
        var checks = new List<MetricPoint>();
        var users = new List<MetricPoint>();
        var sent = new List<MetricPoint>();
        var received = new List<MetricPoint>();
        var active = new List<MetricPoint>();
        var passed = new List<MetricPoint>();

        foreach (var run in runs)
        {
            var runLabels = new List<KeyValuePair<string, string>> { new("run_id", run.RunId.ToString()), new("test", run.Test) };
            active.Add(new MetricPoint(runLabels, run.IsFinished ? 0 : 1, run.StartedAt));
            if (run.Passed is { } verdict)
            {
                passed.Add(new MetricPoint(runLabels, verdict ? 1 : 0, run.StartedAt));
            }

            foreach (var machine in run.Machines)
            {
                var labels = new List<KeyValuePair<string, string>>(runLabels) { new("machine", machine.Machine) };
                var results = machine.Results;
                users.Add(new MetricPoint(labels, run.IsFinished ? 0 : results.ActiveVirtualUsers, run.StartedAt));
                sent.Add(new MetricPoint(labels, results.BytesSent, run.StartedAt));
                received.Add(new MetricPoint(labels, results.BytesReceived, run.StartedAt));
                foreach (var (code, count) in results.StatusCodes)
                {
                    responses.Add(new MetricPoint([.. labels, new("status_code", int.TryParse(code, out _) ? code : "none")], count, run.StartedAt));
                }

                foreach (var step in results.Steps)
                {
                    List<KeyValuePair<string, string>> stepLabels = [.. labels, new("request_index", step.StepIndex.ToString(CultureInfo.InvariantCulture)), new("request", step.Name)];
                    requests.Add(new MetricPoint([.. stepLabels, new("outcome", "success")], step.Requests - step.Failures, run.StartedAt));
                    requests.Add(new MetricPoint([.. stepLabels, new("outcome", "failure")], step.Failures, run.StartedAt));

                    var histogram = step.StepIndex < machine.Steps.Count ? machine.Steps[step.StepIndex] : HistogramData.Empty;
                    duration.Add(new MetricPoint(
                        stepLabels,
                        histogram.Count,
                        run.StartedAt,
                        LatencyHistogram.Rebucket(histogram, LatencyBoundsMs),
                        histogram.SumMs / 1000));
                }

                foreach (var check in results.Checks)
                {
                    var name = check.StepIndex < results.Steps.Count ? results.Steps[check.StepIndex].Name : string.Empty;
                    List<KeyValuePair<string, string>> checkLabels = [.. labels, new("request_index", check.StepIndex.ToString(CultureInfo.InvariantCulture)), new("request", name), new("check", check.Name)];
                    checks.Add(new MetricPoint([.. checkLabels, new("outcome", "success")], check.Passed, run.StartedAt));
                    checks.Add(new MetricPoint([.. checkLabels, new("outcome", "failure")], check.Failed, run.StartedAt));
                }
            }
        }

        var families = new List<MetricFamily>
        {
            new(Prefix + "requests", "{request}", MetricType.Counter, "Requests sent, by request and outcome. A failure is a transport error, an unexpected status or a failed check.", requests),
            new(Prefix + "request_duration", "s", MetricType.Histogram, "Request latency until the full response body was read, by request.", duration),
            new(Prefix + "responses", "{response}", MetricType.Counter, "Requests by HTTP status code; \"none\" when no response arrived.", responses),
            new(Prefix + "checks", "{check}", MetricType.Counter, "Response checks that passed and failed.", checks),
            new(Prefix + "virtual_users", "{user}", MetricType.Gauge, "Virtual users sending requests.", users),
            new(Prefix + "sent", "By", MetricType.Counter, "Request bytes sent.", sent),
            new(Prefix + "received", "By", MetricType.Counter, "Response bytes received.", received),
            new(Prefix + "run_active", "{run}", MetricType.Gauge, "1 while the run is going, 0 once it has finished.", active),
            new(Prefix + "run_passed", "{run}", MetricType.Gauge, "1 while the run's thresholds hold, 0 when one fails or the run was stopped. Only for tests with thresholds.", passed),
        };

        return families.Where(f => f.Points.Count > 0).ToList();
    }
}
