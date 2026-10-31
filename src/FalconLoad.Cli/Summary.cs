using System.Globalization;
using System.Text;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Cli;

/// <summary>Formats results as plain text for a terminal or a CI log. Numbers always use the invariant culture.</summary>
internal static class Summary
{
    public static string Write(LoadTestPlan plan, LoadTestSnapshot s, IReadOnlyList<string> failures, double maxErrorRate)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(Invariant($"{plan.Name}: {s.State.ToString().ToLowerInvariant()} in {Duration(s.Elapsed)}"));
        text.AppendLine(Invariant($"  Requests      {s.TotalRequests:#,0} ({Rate(s.RequestsPerSecond)}/s), {s.FailedRequests:#,0} failed ({Percent(s.ErrorRate)})"));
        text.AppendLine(Invariant($"  Latency       mean {Ms(s.Latency.MeanMs)}, p50 {Ms(s.Latency.P50Ms)}, p90 {Ms(s.Latency.P90Ms)}, p95 {Ms(s.Latency.P95Ms)}, p99 {Ms(s.Latency.P99Ms)}, max {Ms(s.Latency.MaxMs)}"));
        text.AppendLine(Invariant($"  Data          {Bytes(s.BytesSent)} sent, {Bytes(s.BytesReceived)} received"));

        if (s.StatusCodes.Count > 0)
        {
            var codes = s.StatusCodes.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => Invariant($"{c.Key} x {c.Value:#,0}"));
            text.AppendLine("  Status codes  " + string.Join(", ", codes));
        }

        if (s.Steps.Count > 1)
        {
            text.AppendLine("  Per request");
            var width = Math.Min(40, s.Steps.Max(step => step.Name.Length));
            foreach (var step in s.Steps)
            {
                var name = step.Name.Length > width ? step.Name[..(width - 1)] + "…" : step.Name.PadRight(width);
                text.AppendLine(Invariant($"    {name}  {step.Requests,10:#,0} requests  {step.Failures,8:#,0} failed  p95 {Ms(step.Latency.P95Ms)}"));
            }
        }

        if (s.Errors.Count > 0)
        {
            text.AppendLine("  Errors");
            foreach (var error in s.Errors.Take(10))
            {
                text.AppendLine(Invariant($"    {error.Count,10:#,0} x {error.Message}"));
            }

            if (s.Errors.Count > 10)
            {
                text.AppendLine(Invariant($"    and {s.Errors.Count - 10} other kinds of error"));
            }
        }

        if (s.State == RunState.Cancelled)
        {
            text.AppendLine("  Result        INTERRUPTED before the stop condition was reached");
        }
        else if (failures.Count == 0)
        {
            text.AppendLine(maxErrorRate > 0
                ? $"  Result        PASS (at most {Percent(maxErrorRate)} of requests may fail)"
                : "  Result        PASS");
        }
        else
        {
            text.AppendLine("  Result        FAIL: " + failures[0]);
            foreach (var failure in failures.Skip(1))
            {
                text.AppendLine("                " + failure);
            }
        }

        return text.ToString();
    }

    public static string ProgressLine(LoadTestSnapshot s) => Invariant(
        $"  {Duration(s.Elapsed),7}  {s.TotalRequests,10:#,0} requests  {Rate(s.RequestsPerSecond),8}/s  {Percent(s.ErrorRate),6} failed  p95 {Ms(s.Latency.P95Ms),9}  {s.ActiveVirtualUsers:#,0} {(s.ActiveVirtualUsers == 1 ? "user" : "users")}");

    public static string Ms(double ms) => ms switch
    {
        0 => "0 ms",
        < 1 => Invariant($"{ms:0.00} ms"),
        < 100 => Invariant($"{ms:0.0} ms"),
        < 1000 => Invariant($"{ms:0} ms"),
        _ => Invariant($"{ms / 1000:0.00} s"),
    };

    public static string Rate(double perSecond) => perSecond < 10 ? Invariant($"{perSecond:0.##}") : Invariant($"{perSecond:#,0.#}");

    public static string Percent(double fraction) =>
        fraction == 0 ? "0%" : fraction < 0.0001 ? "<0.01%" : Invariant($"{fraction * 100:0.##}%");

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => Invariant($"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => Invariant($"{bytes / (double)(1L << 20):0.##} MB"),
        >= 1L << 10 => Invariant($"{bytes / 1024.0:0.#} KB"),
        _ => Invariant($"{bytes} B"),
    };

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? Invariant($"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s")
        : t.TotalMinutes >= 1 ? Invariant($"{t.Minutes}m {t.Seconds:00}s") : Invariant($"{t.TotalSeconds:0.0}s");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
