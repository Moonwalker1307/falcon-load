using System.Text;
using FalconLoad.Core.Metrics;
using static FalconLoad.Core.Reports.ReportFormat;

namespace FalconLoad.Core.Reports;

/// <summary>
/// Renders finished runs as a short Markdown summary for a pull request comment or a CI job's summary page: the verdict,
/// the headline numbers and the conditions that decided it. Like the other reports it shows results only, never the
/// requests' headers or bodies or an environment's values. Every text that comes from a test or a server is escaped, so an
/// error message can't add links, markup or @mentions to the comment.
/// </summary>
public static class MarkdownReport
{
    private const int MaxStepRows = 20;
    private const int MaxErrorRows = 10;
    private const int MaxTextLength = 300;

    public static string Render(RunExport export, RunVerdict verdict) => Render([(export, verdict)]);

    public static string Render(IReadOnlyList<(RunExport Export, RunVerdict Verdict)> runs)
    {
        var md = new StringBuilder(4 * 1024);
        var failed = runs.Count(r => !r.Verdict.Passed);
        var title = runs.Count == 1
            ? $"falcon-load: {Text(runs[0].Export.Plan.Name)} {(failed == 0 ? "passed" : Result(runs[0].Export.Results, runs[0].Verdict).ToLowerInvariant())}"
            : failed == 0 ? $"falcon-load: all {runs.Count} tests passed" : $"falcon-load: {failed} of {runs.Count} tests failed";
        md.Append($"## {Icon(failed == 0)} {title}\n\n");

        if (runs.Count > 1)
        {
            md.Append("| Test | Result | Requests | Throughput | Error rate | p95 latency |\n");
            md.Append("|:--|:--|--:|--:|--:|--:|\n");
            foreach (var (export, verdict) in runs)
            {
                var s = export.Results;
                md.Append($"| {Text(export.Plan.Name)} | {Icon(verdict.Passed)} {Result(s, verdict)} | {Count(s.TotalRequests)} | {Rate(s.RequestsPerSecond)} req/s | {Percent(s.ErrorRate)} | {Ms(s.Latency.P95Ms)} |\n");
            }

            md.Append('\n');
        }

        foreach (var (export, verdict) in runs)
        {
            AppendRun(md, export, verdict, heading: runs.Count > 1);
        }

        return md.ToString();
    }

    private static void AppendRun(StringBuilder md, RunExport export, RunVerdict verdict, bool heading)
    {
        var s = export.Results;
        if (heading)
        {
            md.Append($"### {Icon(verdict.Passed)} {Text(export.Plan.Name)}\n\n");
        }

        md.Append(string.Join(" · ", Meta(export))).Append("\n\n");
        if (s.FailureReason is not null)
        {
            md.Append($"> The run failed: {Text(s.FailureReason)}\n\n");
        }

        md.Append("| Requests | Throughput | Error rate | p50 | p95 | p99 |\n");
        md.Append("|--:|--:|--:|--:|--:|--:|\n");
        md.Append($"| {Count(s.TotalRequests)} | {Rate(s.RequestsPerSecond)} req/s | {Percent(s.ErrorRate)} ({Count(s.FailedRequests)} failed) | {Ms(s.Latency.P50Ms)} | {Ms(s.Latency.P95Ms)} | {Ms(s.Latency.P99Ms)} |\n\n");

        md.Append("| | Condition | Detail |\n");
        md.Append("|:-:|:--|:--|\n");
        foreach (var check in verdict.Checks)
        {
            var detail = check.Kind == RunVerdict.ThresholdKind ? Threshold(s, check) : check.Message ?? string.Empty;
            md.Append($"| {Icon(check.Passed)} | {Text(check.Name)} | {Text(detail)} |\n");
        }

        md.Append('\n');
        if (s.Thresholds.Count == 0)
        {
            md.Append("This test has no thresholds, so it passes when the run completes and no request fails.\n\n");
        }

        AppendSteps(md, s);
        AppendErrors(md, s);
    }

    private static IEnumerable<string> Meta(RunExport export)
    {
        var s = export.Results;
        yield return Text(DescribeProfile(export.Plan.Profile));
        yield return $"{(s.State == RunState.Completed ? "ran" : "stopped after")} {Duration(s.Elapsed)}";
        if (!string.IsNullOrWhiteSpace(export.Plan.Environment))
        {
            yield return $"environment {Text(export.Plan.Environment)}";
        }

        if (!string.IsNullOrWhiteSpace(export.Machine))
        {
            yield return $"machine {Text(export.Machine)}";
        }
    }

    private static void AppendSteps(StringBuilder md, LoadTestSnapshot s)
    {
        if (s.Steps.Count < 2)
        {
            return;
        }

        md.Append("<details><summary>Requests</summary>\n\n");
        md.Append("| Request | Count | Failed | Mean | p95 | p99 |\n");
        md.Append("|:--|--:|--:|--:|--:|--:|\n");
        foreach (var step in s.Steps.Take(MaxStepRows))
        {
            md.Append($"| {Text(step.Name)} | {Count(step.Requests)} | {Count(step.Failures)} | {Ms(step.Latency.MeanMs)} | {Ms(step.Latency.P95Ms)} | {Ms(step.Latency.P99Ms)} |\n");
        }

        if (s.Steps.Count > MaxStepRows)
        {
            md.Append($"\n{s.Steps.Count - MaxStepRows} more in the full report.\n");
        }

        md.Append("\n</details>\n\n");
    }

    private static void AppendErrors(StringBuilder md, LoadTestSnapshot s)
    {
        if (s.Errors.Count == 0)
        {
            return;
        }

        md.Append("<details><summary>Errors</summary>\n\n");
        md.Append("| Count | Error |\n");
        md.Append("|--:|:--|\n");
        foreach (var error in s.Errors.Take(MaxErrorRows))
        {
            md.Append($"| {Count(error.Count)} | {Text(error.Message)} |\n");
        }

        if (s.Errors.Count > MaxErrorRows)
        {
            md.Append($"\n{s.Errors.Count - MaxErrorRows} more in the full report.\n");
        }

        md.Append("\n</details>\n\n");
    }

    private static string Threshold(LoadTestSnapshot s, VerdictCheck check)
    {
        var threshold = s.Thresholds.FirstOrDefault(t => t.Name == check.Name);
        return threshold is null ? string.Empty
            : threshold.Actual is { } actual ? $"measured {Value(actual, threshold.Unit)}"
            : "nothing was measured";
    }

    private static string Result(LoadTestSnapshot s, RunVerdict verdict) =>
        verdict.Passed ? "Passed" : s.State == RunState.Cancelled ? "Interrupted" : "Failed";

    private static string Icon(bool passed) => passed ? "✅" : "❌";

    /// <summary>
    /// Makes text safe for one table cell: it stays on one line, is cut to a readable length, and Markdown punctuation is
    /// escaped (and angle brackets written as entities) so it shows as typed and can't open an HTML tag. A word joiner after @ and # stops GitHub from turning the text into a mention or a link
    /// to an issue, which would notify people who have nothing to do with the test.
    /// </summary>
    internal static string Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength] + "…";
        }

        var escaped = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\r' or '\n' or '\t':
                    escaped.Append(' ');
                    break;
                case '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '(' or ')' or '!' or '|' or '~' or '-' or '+' or '=':
                    escaped.Append('\\').Append(c);
                    break;
                case '@' or '#':
                    escaped.Append(c).Append('⁠');
                    break;
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                default:
                    escaped.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }

        return escaped.ToString();
    }
}
