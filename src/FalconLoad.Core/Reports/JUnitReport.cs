using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FalconLoad.Core.Metrics;
using static FalconLoad.Core.Reports.ReportFormat;

namespace FalconLoad.Core.Reports;

/// <summary>
/// Renders finished runs as JUnit XML, the format CI systems such as GitHub Actions, GitLab, Jenkins and Azure Pipelines
/// show as test results. Each run is a test suite and each condition of its <see cref="RunVerdict"/> (every threshold, that
/// the run completed, that requests were sent and, where it applies, the error rate) is a test case, so a failed
/// threshold appears as a failed test with the measured value in its message.
/// </summary>
public static class JUnitReport
{
    public static string Render(RunExport export, RunVerdict verdict) => Render([(export, verdict)]);

    public static string Render(IReadOnlyList<(RunExport Export, RunVerdict Verdict)> runs)
    {
        var suites = runs.Select(r => Suite(r.Export, r.Verdict)).ToList();
        var root = new XElement(
            "testsuites",
            new XAttribute("name", "falcon-load"),
            new XAttribute("tests", suites.Sum(s => Total(s, "tests"))),
            new XAttribute("failures", suites.Sum(s => Total(s, "failures"))),
            new XAttribute("errors", suites.Sum(s => Total(s, "errors"))),
            new XAttribute("time", Seconds(TimeSpan.FromSeconds(runs.Sum(r => r.Export.Results.Elapsed.TotalSeconds)))),
            suites);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        return document.Declaration + Environment.NewLine + document + Environment.NewLine;
    }

    private static XElement Suite(RunExport export, RunVerdict verdict)
    {
        var s = export.Results;
        var name = Clean(export.Plan.Name);
        var cases = verdict.Checks.Select(check => Case(name, check, s.Elapsed, check.Kind == RunVerdict.RunKind)).ToList();

        var suite = new XElement(
            "testsuite",
            new XAttribute("name", name),
            new XAttribute("tests", cases.Count),
            new XAttribute("failures", cases.Count(c => c.Element("failure") is not null)),
            new XAttribute("errors", cases.Count(c => c.Element("error") is not null)),
            new XAttribute("skipped", 0),
            new XAttribute("time", Seconds(s.Elapsed)),
            new XAttribute("timestamp", export.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(export.Machine))
        {
            suite.SetAttributeValue("hostname", Clean(export.Machine));
        }

        var properties = new List<(string, string)>
        {
            ("state", s.State.ToString()),
            ("requests", s.TotalRequests.ToString(CultureInfo.InvariantCulture)),
            ("failedRequests", s.FailedRequests.ToString(CultureInfo.InvariantCulture)),
            ("requestsPerSecond", s.RequestsPerSecond.ToString("0.##", CultureInfo.InvariantCulture)),
            ("errorRatePercent", (s.ErrorRate * 100).ToString("0.####", CultureInfo.InvariantCulture)),
            ("latencyMeanMs", s.Latency.MeanMs.ToString("0.##", CultureInfo.InvariantCulture)),
            ("latencyP50Ms", s.Latency.P50Ms.ToString("0.##", CultureInfo.InvariantCulture)),
            ("latencyP95Ms", s.Latency.P95Ms.ToString("0.##", CultureInfo.InvariantCulture)),
            ("latencyP99Ms", s.Latency.P99Ms.ToString("0.##", CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(export.Plan.Environment))
        {
            properties.Add(("environment", export.Plan.Environment));
        }

        suite.Add(new XElement("properties", properties.Select(p =>
            new XElement("property", new XAttribute("name", p.Item1), new XAttribute("value", Clean(p.Item2))))));
        suite.Add(cases);
        suite.Add(new XElement("system-out", Clean(Output(export))));
        return suite;
    }

    private static XElement Case(string className, VerdictCheck check, TimeSpan elapsed, bool carriesTime)
    {
        var element = new XElement(
            "testcase",
            new XAttribute("classname", className),
            new XAttribute("name", Clean(check.Name)),

            // The run's duration goes on one case, so the cases add up to the suite's time.
            new XAttribute("time", Seconds(carriesTime ? elapsed : TimeSpan.Zero)));
        if (!check.Passed)
        {
            var message = Clean(check.Message ?? check.Name);
            element.Add(new XElement(
                check.IsError ? "error" : "failure",
                new XAttribute("message", message),
                new XAttribute("type", check.Kind),
                message));
        }

        return element;
    }

    /// <summary>A short summary for the CI job's log: the numbers behind the verdict, with errors already masked by the engine.</summary>
    private static string Output(RunExport export)
    {
        var s = export.Results;
        var text = new StringBuilder();
        text.AppendLine(Invariant($"{export.Plan.Name}: {s.State.ToString().ToLowerInvariant()} in {Duration(s.Elapsed)}"));
        text.AppendLine(Invariant($"Requests: {Count(s.TotalRequests)} ({Rate(s.RequestsPerSecond)}/s), {Count(s.FailedRequests)} failed ({Percent(s.ErrorRate)})"));
        text.AppendLine(Invariant($"Latency: mean {Ms(s.Latency.MeanMs)}, p50 {Ms(s.Latency.P50Ms)}, p90 {Ms(s.Latency.P90Ms)}, p95 {Ms(s.Latency.P95Ms)}, p99 {Ms(s.Latency.P99Ms)}, max {Ms(s.Latency.MaxMs)}"));
        foreach (var error in s.Errors.Take(10))
        {
            text.AppendLine(Invariant($"Error: {Count(error.Count)} x {error.Message}"));
        }

        return text.ToString();
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static int Total(XElement suite, string attribute) => (int)suite.Attribute(attribute)!;

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Replaces characters XML can't hold, which an error message copied from a server may contain.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.All(c => XmlConvert.IsXmlChar(c)))
        {
            return text ?? string.Empty;
        }

        var cleaned = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[i + 1], text[i]))
            {
                cleaned.Append(text[i]).Append(text[++i]);
            }
            else
            {
                cleaned.Append(XmlConvert.IsXmlChar(text[i]) ? text[i] : '�');
            }
        }

        return cleaned.ToString();
    }
}
