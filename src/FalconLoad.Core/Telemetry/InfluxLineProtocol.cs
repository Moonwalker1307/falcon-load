using System.Globalization;
using System.Text;
using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Telemetry;

/// <summary>
/// Turns live runs into InfluxDB line protocol, one batch per push. InfluxDB has no histogram type a dashboard can
/// add up, so each point carries ready-made numbers: running totals, and the throughput, error rate and latency
/// percentiles of the interval since the previous push, worked out from the difference between the two pushes'
/// histograms. Agents are merged before the percentiles are taken, so a distributed run's percentiles are exact.
/// </summary>
/// <remarks>
/// Measurements: <c>falcon_load</c> (the whole run), <c>falcon_load_request</c> (each request), <c>falcon_load_machine</c>
/// (each machine or agent) and <c>falcon_load_status</c> (responses per status code). Not thread safe: it remembers
/// what it last sent of each run, so one instance serves one sequence of pushes.
/// </remarks>
public sealed class InfluxLineProtocol
{
    public const string ContentType = "text/plain; charset=utf-8";

    private readonly Dictionary<(Guid Run, string Machine), Previous> _previous = new();

    public string Write(IReadOnlyList<RunTelemetry> runs, DateTimeOffset now)
    {
        var text = new StringBuilder();
        var seen = new HashSet<(Guid, string)>();
        foreach (var run in runs)
        {
            WriteRun(text, run, now, seen);
        }

        // Forget the runs that are no longer sent, so memory follows the number of live runs.
        foreach (var key in _previous.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _previous.Remove(key);
        }

        return text.ToString();
    }

    private void WriteRun(StringBuilder text, RunTelemetry run, DateTimeOffset now, HashSet<(Guid, string)> seen)
    {
        var stepCount = run.Machines.Count == 0 ? 0 : run.Machines.Max(m => m.Results.Steps.Count);
        var stepNames = new string[stepCount];
        var stepTotals = new Totals[stepCount];
        var stepWindows = new Window[stepCount];
        for (var i = 0; i < stepCount; i++)
        {
            stepWindows[i] = new Window();
        }

        var runTotals = new Totals();
        var runWindow = new Window();
        var users = 0L;
        long bytesSent = 0, bytesReceived = 0;
        var statusCodes = new SortedDictionary<string, long>(StringComparer.Ordinal);

        // The point is stamped when the run ended, if it has, so the final interval isn't stretched to now.
        var at = run.EndedAt is { } ended && ended < now ? ended : now;
        var timestamp = (at.ToUnixTimeMilliseconds()).ToString(CultureInfo.InvariantCulture);
        var runTags = Tag("run_id", run.RunId.ToString()) + Tag("test", run.Test);

        foreach (var machine in run.Machines)
        {
            var key = (run.RunId, machine.Machine);
            seen.Add(key);
            var previous = _previous.GetValueOrDefault(key);
            var since = previous?.At ?? run.StartedAt;
            var results = machine.Results;
            var machineWindow = new Window();
            var machineTotals = new Totals { Requests = results.TotalRequests, Failures = results.FailedRequests };
            var histograms = new HistogramData[results.Steps.Count];
            for (var i = 0; i < results.Steps.Count; i++)
            {
                var step = results.Steps[i];
                histograms[i] = i < machine.Steps.Count ? machine.Steps[i] : HistogramData.Empty;
                var added = LatencyHistogram.Since(histograms[i], previous is not null && i < previous.Steps.Length ? previous.Steps[i] : null);
                var failures = step.Failures - (previous is not null && i < previous.Failures.Length ? previous.Failures[i] : 0);
                stepNames[i] ??= step.Name;
                stepTotals[i] = new Totals { Requests = (stepTotals[i]?.Requests ?? 0) + step.Requests, Failures = (stepTotals[i]?.Failures ?? 0) + step.Failures };
                foreach (var window in new[] { stepWindows[i], machineWindow, runWindow })
                {
                    window.Add(added, Math.Max(0, failures), at - since);
                }
            }

            _previous[key] = new Previous(at, histograms, [.. results.Steps.Select(s => s.Failures)]);

            var machineUsers = run.IsFinished ? 0 : results.ActiveVirtualUsers;
            users += machineUsers;
            bytesSent += results.BytesSent;
            bytesReceived += results.BytesReceived;
            runTotals.Requests += results.TotalRequests;
            runTotals.Failures += results.FailedRequests;
            foreach (var (code, count) in results.StatusCodes)
            {
                var name = int.TryParse(code, out _) ? code : "none";
                statusCodes[name] = statusCodes.GetValueOrDefault(name) + count;
            }

            text.Append("falcon_load_machine").Append(runTags).Append(Tag("machine", machine.Machine)).Append(' ');
            var fields = new Fields(text);
            machineTotals.WriteTo(fields);
            fields.Int("virtual_users", machineUsers);
            machineWindow.WriteTo(fields);
            text.Append(' ').Append(timestamp).Append('\n');
        }

        text.Append("falcon_load").Append(runTags).Append(' ');
        var runFields = new Fields(text);
        runTotals.WriteTo(runFields);
        runFields.Int("virtual_users", users);
        runFields.Int("bytes_sent", bytesSent);
        runFields.Int("bytes_received", bytesReceived);
        runWindow.WriteTo(runFields);
        runFields.Bool("running", !run.IsFinished);
        if (run.Passed is { } passed)
        {
            runFields.Bool("passed", passed);
        }

        text.Append(' ').Append(timestamp).Append('\n');

        for (var i = 0; i < stepCount; i++)
        {
            text.Append("falcon_load_request").Append(runTags)
                .Append(Tag("request_index", i.ToString(CultureInfo.InvariantCulture)))
                .Append(Tag("request", stepNames[i] ?? $"Request {i + 1}")).Append(' ');
            var fields = new Fields(text);
            (stepTotals[i] ?? new Totals()).WriteTo(fields);
            stepWindows[i].WriteTo(fields);
            text.Append(' ').Append(timestamp).Append('\n');
        }

        foreach (var (code, count) in statusCodes)
        {
            text.Append("falcon_load_status").Append(runTags).Append(Tag("status_code", code)).Append(' ');
            new Fields(text).Int("count", count);
            text.Append(' ').Append(timestamp).Append('\n');
        }
    }

    // Tag values can't be empty or hold a line break; commas, equals signs and spaces are escaped.
    private static string Tag(string key, string value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('\n', ' ').Replace('\r', ' ');
        return "," + key + "=" + clean
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace(" ", "\\ ", StringComparison.Ordinal);
    }

    private sealed record Previous(DateTimeOffset At, HistogramData[] Steps, long[] Failures);

    private sealed class Totals
    {
        public long Requests { get; set; }

        public long Failures { get; set; }

        public void WriteTo(Fields fields)
        {
            fields.Int("requests", Requests);
            fields.Int("failures", Failures);
        }
    }

    /// <summary>What happened in the interval since the previous push.</summary>
    private sealed class Window
    {
        private readonly LatencyHistogram _latency = new();
        private long _failures;
        private TimeSpan _length;

        public void Add(LatencyHistogram latency, long failures, TimeSpan length)
        {
            _latency.Add(latency);
            _failures += failures;

            // Machines and requests of one run share the interval; the longest is the run's.
            _length = length > _length ? length : _length;
        }

        public void WriteTo(Fields fields)
        {
            var seconds = _length.TotalSeconds;
            fields.Float("rps", seconds > 0 ? _latency.Count / seconds : 0);
            fields.Int("interval_requests", _latency.Count);
            fields.Int("interval_failures", _failures);
            if (_latency.Count == 0)
            {
                return;
            }

            var stats = _latency.ToStats();
            fields.Float("error_percent", 100.0 * _failures / _latency.Count);
            fields.Float("latency_mean_ms", stats.MeanMs);
            fields.Float("latency_p50_ms", stats.P50Ms);
            fields.Float("latency_p90_ms", stats.P90Ms);
            fields.Float("latency_p95_ms", stats.P95Ms);
            fields.Float("latency_p99_ms", stats.P99Ms);
            fields.Float("latency_max_ms", stats.MaxMs);
        }
    }

    private sealed class Fields(StringBuilder text)
    {
        private bool _first = true;

        public void Int(string name, long value) => Append(name, value.ToString(CultureInfo.InvariantCulture) + "i");

        public void Float(string name, double value) =>
            Append(name, double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "0");

        public void Bool(string name, bool value) => Append(name, value ? "true" : "false");

        private void Append(string name, string value)
        {
            text.Append(_first ? "" : ",").Append(name).Append('=').Append(value);
            _first = false;
        }
    }
}
