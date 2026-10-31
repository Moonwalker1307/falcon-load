using System.Reflection;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Telemetry;

namespace FalconLoad.Web.Services;

/// <summary>
/// Collects the live runs of this falcon-load for the metric exporters, answers Prometheus scrapes and pushes to
/// InfluxDB and the OTLP endpoint in the background.
/// </summary>
/// <remarks>
/// A test run on agents is reported here, on the controller, with one series per agent. Agents leave out the shares
/// they run for a controller, so a test is never counted twice when both are scraped.
/// </remarks>
public sealed class TelemetryService : BackgroundService
{
    /// <summary>How long a finished run stays on <c>/metrics</c>, so a scraper picks up its final numbers before its series end.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(5);

    public static readonly string Version =
        (typeof(TelemetryService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(TelemetryService).Assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];

    private readonly RunManager _runs;
    private readonly DistributedRunManager _distributed;
    private readonly TelemetryStore _store;

    public TelemetryService(RunManager runs, DistributedRunManager distributed, TelemetryStore store, IHttpClientFactory http, ILogger<TelemetryService> logger)
    {
        _runs = runs;
        _distributed = distributed;
        _store = store;
        Publisher = new TelemetryPublisher(
            store.Get,
            GetRuns,
            http.CreateClient(nameof(TelemetryService)),
            Environment.MachineName,
            Version,
            message => logger.LogWarning("Metrics export: {Message}", message));
    }

    public TelemetryPublisher Publisher { get; }

    /// <summary>The runs to report: every run in progress, and those that finished in the last <see cref="KeepFinished"/>.</summary>
    public IReadOnlyList<RunTelemetry> GetRuns()
    {
        var now = DateTimeOffset.UtcNow;
        var since = now - KeepFinished;
        var runs = new List<RunTelemetry>();
        foreach (var run in _runs.GetAll())
        {
            if (run.Controller is null && run.Snapshot.State != RunState.Pending && run.GetTelemetry() is { } telemetry && !(telemetry.EndedAt < since))
            {
                runs.Add(telemetry);
            }
        }

        foreach (var run in _distributed.GetAll())
        {
            // Agents start together a moment after the run is created; until then there is nothing to report.
            if (run.GetTelemetry() is { Machines.Count: > 0 } telemetry && telemetry.StartedAt <= now && !(telemetry.EndedAt < since))
            {
                runs.Add(telemetry);
            }
        }

        return runs;
    }

    /// <summary>The Prometheus scrape: null when the endpoint is turned off.</summary>
    public string? Scrape() => _store.Get().PrometheusEnabled ? PrometheusText.Write(TelemetryMetrics.Build(GetRuns())) : null;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Publisher.RunAsync(stoppingToken);
}
