using System.Text;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using static FalconLoad.Core.Reports.ReportFormat;

namespace FalconLoad.Cli;

/// <summary>Formats results as plain text for a terminal or a CI log. Numbers always use the invariant culture.</summary>
internal static class Summary
{
    public static string Write(LoadTestPlan plan, LoadTestSnapshot s, IReadOnlyList<string> failures, double? maxErrorRate)
    {
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(Invariant($"{plan.Name}: {s.State.ToString().ToLowerInvariant()} in {Duration(s.Elapsed)}"));
        text.AppendLine(Invariant($"  Requests      {s.TotalRequests:#,0} ({Rate(s.RequestsPerSecond)}/s), {s.FailedRequests:#,0} failed ({Percent(s.ErrorRate)})"));
        text.AppendLine(Invariant($"  Latency       mean {Ms(s.Latency.MeanMs)}, p50 {Ms(s.Latency.P50Ms)}, p90 {Ms(s.Latency.P90Ms)}, p95 {Ms(s.Latency.P95Ms)}, p99 {Ms(s.Latency.P99Ms)}, max {Ms(s.Latency.MaxMs)}"));
        text.AppendLine(Invariant($"  Data          {Bytes(s.BytesSent)} sent, {Bytes(s.BytesReceived)} received"));
        if (s.Auth is { } auth)
        {
            var failed = auth.FailedFetches == 0 ? string.Empty : Invariant($", {auth.FailedFetches:#,0} token requests failed (last: {auth.LastError})");
            text.AppendLine(Invariant($"  OAuth2        {auth.TokensFetched:#,0} {(auth.TokensFetched == 1 ? "token" : "tokens")} fetched{failed}"));
        }

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

        if (s.Checks.Count > 0)
        {
            text.AppendLine("  Checks");
            foreach (var check in s.Checks)
            {
                text.AppendLine(Invariant($"    {check.Passed,10:#,0} passed  {check.Failed,8:#,0} failed  {check.Name}"));
            }
        }

        if (s.Thresholds.Count > 0)
        {
            text.AppendLine("  Thresholds");
            foreach (var threshold in s.Thresholds)
            {
                var measured = threshold.Actual is { } actual ? Value(actual, threshold.Unit) : "nothing measured";
                text.AppendLine($"    {(threshold.Passed ? "pass" : "FAIL")}  {threshold.Name}, measured {measured}");
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
                ? $"  Result        PASS (at most {Percent(maxErrorRate.Value)} of requests may fail)"
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

    private static string Invariant(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
