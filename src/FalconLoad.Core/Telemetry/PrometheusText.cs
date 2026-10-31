using System.Globalization;
using System.Text;

namespace FalconLoad.Core.Telemetry;

/// <summary>Writes metrics in the Prometheus text exposition format (version 0.0.4), which every Prometheus-compatible scraper reads.</summary>
public static class PrometheusText
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Write(IReadOnlyList<MetricFamily> families)
    {
        var text = new StringBuilder();
        foreach (var family in families)
        {
            var name = family.PrometheusName;
            text.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(family.Description)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(' ').Append(family.Type switch
            {
                MetricType.Counter => "counter",
                MetricType.Gauge => "gauge",
                _ => "histogram",
            }).Append('\n');

            foreach (var point in family.Points)
            {
                if (family.Type != MetricType.Histogram)
                {
                    Sample(text, name, point.Labels, null, point.Value);
                    continue;
                }

                // Prometheus buckets are cumulative: each counts the samples at or below its bound.
                long cumulative = 0;
                var buckets = point.Buckets ?? [];
                for (var i = 0; i < TelemetryMetrics.LatencyBoundsSeconds.Count; i++)
                {
                    cumulative += i < buckets.Length ? buckets[i] : 0;
                    Sample(text, name + "_bucket", point.Labels, Number(TelemetryMetrics.LatencyBoundsSeconds[i]), cumulative);
                }

                Sample(text, name + "_bucket", point.Labels, "+Inf", point.Value);
                Sample(text, name + "_sum", point.Labels, null, point.Sum);
                Sample(text, name + "_count", point.Labels, null, point.Value);
            }
        }

        return text.ToString();
    }

    private static void Sample(StringBuilder text, string name, IReadOnlyList<KeyValuePair<string, string>> labels, string? le, double value)
    {
        text.Append(name);
        if (labels.Count > 0 || le is not null)
        {
            text.Append('{');
            var first = true;
            foreach (var (key, label) in labels)
            {
                text.Append(first ? "" : ",").Append(key).Append("=\"").Append(EscapeLabel(label)).Append('"');
                first = false;
            }

            if (le is not null)
            {
                text.Append(first ? "" : ",").Append("le=\"").Append(le).Append('"');
            }

            text.Append('}');
        }

        text.Append(' ').Append(Number(value)).Append('\n');
    }

    private static string Number(double value) =>
        double.IsPositiveInfinity(value) ? "+Inf"
        : double.IsNegativeInfinity(value) ? "-Inf"
        : double.IsNaN(value) ? "NaN"
        : value.ToString("R", CultureInfo.InvariantCulture);

    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EscapeHelp(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
