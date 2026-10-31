using System.Text.Json.Serialization;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Telemetry;

/// <summary>
/// Where the live metrics of load tests go, so they can be watched in Grafana and similar tools: a Prometheus scrape
/// endpoint, and pushes to InfluxDB and to an OpenTelemetry (OTLP) endpoint. The web app keeps one in
/// <c>telemetry.json</c>; the CLI builds one from its options.
/// </summary>
public sealed class TelemetrySettings
{
    public const int MinPushIntervalSeconds = 1;
    public const int MaxPushIntervalSeconds = 300;

    /// <summary>Whether the web app answers Prometheus scrapes on <c>/metrics</c>. On by default, as the data is no more than the JSON API shows.</summary>
    public bool PrometheusEnabled { get; set; } = true;

    public InfluxDbSettings InfluxDb { get; set; } = new();

    public OtlpSettings Otlp { get; set; } = new();

    /// <summary>How often metrics are pushed to InfluxDB and the OTLP endpoint while a test runs.</summary>
    public int PushIntervalSeconds { get; set; } = 5;

    [JsonIgnore]
    public bool PushesAnywhere => InfluxDb.Enabled || Otlp.Enabled;

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (PushIntervalSeconds is < MinPushIntervalSeconds or > MaxPushIntervalSeconds)
        {
            problems.Add($"The push interval must be between {MinPushIntervalSeconds} and {MaxPushIntervalSeconds} seconds.");
        }

        if (InfluxDb.Enabled)
        {
            if (!IsHttpUrl(InfluxDb.Url))
            {
                problems.Add("The InfluxDB URL must be an absolute http:// or https:// address, such as http://localhost:8086.");
            }

            if (string.IsNullOrWhiteSpace(InfluxDb.Bucket))
            {
                problems.Add("Give the InfluxDB bucket to write to (on InfluxDB 1.8, the database; on InfluxDB 3, the database name).");
            }
        }

        if (Otlp.Enabled && !IsHttpUrl(Otlp.Endpoint))
        {
            problems.Add("The OTLP endpoint must be an absolute http:// or https:// address, such as http://localhost:4318.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in Otlp.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name) || header.Name.Any(c => c <= ' ' || c >= 127 || c == ':'))
            {
                problems.Add($"'{header.Name}' isn't a valid header name.");
            }
            else if (!names.Add(header.Name.Trim()))
            {
                problems.Add($"The header {header.Name} is given twice.");
            }
            else if (header.Value is { } value && value.Any(c => c is '\r' or '\n'))
            {
                problems.Add($"The value of the header {header.Name} can't contain a line break.");
            }
        }

        return problems;
    }

    public TelemetrySettings Clone() => new()
    {
        PrometheusEnabled = PrometheusEnabled,
        PushIntervalSeconds = PushIntervalSeconds,
        InfluxDb = new InfluxDbSettings
        {
            Enabled = InfluxDb.Enabled,
            Url = InfluxDb.Url,
            Bucket = InfluxDb.Bucket,
            Organization = InfluxDb.Organization,
            Token = InfluxDb.Token,
        },
        Otlp = new OtlpSettings
        {
            Enabled = Otlp.Enabled,
            Endpoint = Otlp.Endpoint,
            Headers = [.. Otlp.Headers.Select(h => new TelemetryHeader { Name = h.Name, Value = h.Value })],
        },
    };

    /// <summary>
    /// A copy without the secrets, for the UI and the API: the InfluxDB token and the OTLP header values (which usually
    /// carry credentials) are <c>null</c> when set and empty when not, like the secrets of an environment.
    /// </summary>
    public TelemetrySettings Redacted()
    {
        var copy = Clone();
        copy.InfluxDb.Token = string.IsNullOrEmpty(InfluxDb.Token) ? string.Empty : null;
        foreach (var header in copy.Otlp.Headers)
        {
            header.Value = string.IsNullOrEmpty(header.Value) ? string.Empty : null;
        }

        return copy;
    }

    /// <summary>
    /// Fills the secrets left <c>null</c> (shown as set but hidden) with the values in <paramref name="stored"/>, so a
    /// form or API call that never saw a secret keeps it. A header keeps its value when its name stays the same.
    /// </summary>
    public TelemetrySettings KeepSecretsFrom(TelemetrySettings stored)
    {
        var copy = Clone();
        copy.InfluxDb.Token ??= stored.InfluxDb.Token ?? string.Empty;
        foreach (var header in copy.Otlp.Headers.Where(h => h.Value is null))
        {
            header.Value = stored.Otlp.Headers.FirstOrDefault(h => string.Equals(h.Name.Trim(), header.Name.Trim(), StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        }

        return copy;
    }

    /// <summary>Hides the token and header values in the errors a push reports, in case a server echoes them back.</summary>
    public SecretMasker CreateMasker() => new([InfluxDb.Token, .. Otlp.Headers.Select(h => h.Value)]);

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>
/// Writes to InfluxDB through its <c>/api/v2/write</c> endpoint, which InfluxDB 2 and 3 have, and 1.8 too, with the
/// database as the bucket and <c>user:password</c> as the token.
/// </summary>
public sealed class InfluxDbSettings
{
    public bool Enabled { get; set; }

    /// <summary>The server's address, such as <c>http://localhost:8086</c>.</summary>
    public string Url { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    /// <summary>Needed by InfluxDB 2; InfluxDB 1.8 and 3 ignore it.</summary>
    public string? Organization { get; set; }

    /// <summary>The API token. <c>null</c> in a redacted copy when one is set.</summary>
    public string? Token { get; set; } = string.Empty;

    public Uri WriteUri()
    {
        var query = $"bucket={Uri.EscapeDataString(Bucket.Trim())}&precision=ms";
        if (!string.IsNullOrWhiteSpace(Organization))
        {
            query += $"&org={Uri.EscapeDataString(Organization.Trim())}";
        }

        return new Uri($"{Url.Trim().TrimEnd('/')}/api/v2/write?{query}");
    }
}

/// <summary>Sends metrics over OTLP/HTTP as JSON, which the OpenTelemetry Collector, Grafana Alloy and Prometheus 3 accept.</summary>
public sealed class OtlpSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The OTLP/HTTP base address, such as <c>http://localhost:4318</c>, to which <c>/v1/metrics</c> is added, or the full
    /// address of the metrics endpoint when it already ends with <c>/v1/metrics</c>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Sent with every push, e.g. <c>Authorization</c> for a hosted service. Values are kept secret.</summary>
    public List<TelemetryHeader> Headers { get; set; } = [];

    public Uri MetricsUri()
    {
        var endpoint = Endpoint.Trim();
        var uri = new Uri(endpoint);
        return uri.AbsolutePath.TrimEnd('/').EndsWith("/v1/metrics", StringComparison.Ordinal)
            ? uri
            : new Uri(endpoint.TrimEnd('/') + "/v1/metrics");
    }
}

public sealed class TelemetryHeader
{
    public string Name { get; set; } = string.Empty;

    /// <summary><c>null</c> in a redacted copy when a value is set.</summary>
    public string? Value { get; set; } = string.Empty;
}
