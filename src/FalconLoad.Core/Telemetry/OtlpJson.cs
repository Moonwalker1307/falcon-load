using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FalconLoad.Core.Telemetry;

/// <summary>
/// Writes metrics as an OTLP <c>ExportMetricsServiceRequest</c> in its JSON encoding, for OTLP/HTTP. Counters and
/// histograms are cumulative from the start of their run, as Prometheus expects them. 64-bit integers are written as
/// strings, as the protobuf JSON mapping asks.
/// </summary>
public static class OtlpJson
{
    public const string ContentType = "application/json";

    private const int CumulativeTemporality = 2;

    /// <param name="instance">The machine sending the metrics, reported as <c>service.instance.id</c>.</param>
    /// <param name="version">falcon-load's version, reported as <c>service.version</c>.</param>
    public static byte[] Write(IReadOnlyList<MetricFamily> families, DateTimeOffset now, string instance, string version)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("resourceMetrics");
            json.WriteStartObject();

            json.WriteStartObject("resource");
            WriteAttributes(json, [new("service.name", "falcon-load"), new("service.instance.id", instance), new("service.version", version)]);
            json.WriteEndObject();

            json.WriteStartArray("scopeMetrics");
            json.WriteStartObject();
            json.WriteStartObject("scope");
            json.WriteString("name", "falcon-load");
            json.WriteString("version", version);
            json.WriteEndObject();

            json.WriteStartArray("metrics");
            foreach (var family in families)
            {
                WriteMetric(json, family, now);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteMetric(Utf8JsonWriter json, MetricFamily family, DateTimeOffset now)
    {
        json.WriteStartObject();
        json.WriteString("name", family.Name);
        json.WriteString("description", family.Description);
        json.WriteString("unit", family.Unit);
        json.WriteStartObject(family.Type switch
        {
            MetricType.Counter => "sum",
            MetricType.Gauge => "gauge",
            _ => "histogram",
        });

        if (family.Type != MetricType.Gauge)
        {
            json.WriteNumber("aggregationTemporality", CumulativeTemporality);
        }

        if (family.Type == MetricType.Counter)
        {
            json.WriteBoolean("isMonotonic", true);
        }

        json.WriteStartArray("dataPoints");
        foreach (var point in family.Points)
        {
            json.WriteStartObject();
            WriteAttributes(json, point.Labels);
            if (family.Type != MetricType.Gauge)
            {
                // A start after the point itself is invalid, so a run scheduled to start a moment from now starts now.
                json.WriteString("startTimeUnixNano", Nanos(point.Start > now ? now : point.Start));
            }

            json.WriteString("timeUnixNano", Nanos(now));
            switch (family.Type)
            {
                case MetricType.Counter:
                    json.WriteString("asInt", ((long)point.Value).ToString(CultureInfo.InvariantCulture));
                    break;
                case MetricType.Gauge:
                    json.WriteNumber("asDouble", point.Value);
                    break;
                default:
                    json.WriteString("count", ((long)point.Value).ToString(CultureInfo.InvariantCulture));
                    json.WriteNumber("sum", point.Sum);
                    json.WriteStartArray("bucketCounts");
                    var buckets = point.Buckets ?? new long[TelemetryMetrics.LatencyBoundsSeconds.Count + 1];
                    foreach (var count in buckets)
                    {
                        json.WriteStringValue(count.ToString(CultureInfo.InvariantCulture));
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("explicitBounds");
                    foreach (var bound in TelemetryMetrics.LatencyBoundsSeconds)
                    {
                        json.WriteNumberValue(bound);
                    }

                    json.WriteEndArray();
                    break;
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter json, IReadOnlyList<KeyValuePair<string, string>> attributes)
    {
        json.WriteStartArray("attributes");
        foreach (var (key, value) in attributes)
        {
            json.WriteStartObject();
            json.WriteString("key", key);
            json.WriteStartObject("value");
            json.WriteString("stringValue", value);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static string Nanos(DateTimeOffset time) =>
        ((time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>The JSON as text, for tests and diagnostics.</summary>
    internal static string WriteString(IReadOnlyList<MetricFamily> families, DateTimeOffset now, string instance, string version) =>
        Encoding.UTF8.GetString(Write(families, now, instance, version));
}
