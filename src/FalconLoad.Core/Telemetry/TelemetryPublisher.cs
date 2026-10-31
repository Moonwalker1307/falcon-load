using System.Net.Http.Headers;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Telemetry;

/// <summary>How the last pushes to one destination went.</summary>
/// <param name="LastError">Why the last push failed, with secrets masked; null when it succeeded.</param>
public sealed record PushStatus(DateTimeOffset? LastAttempt, DateTimeOffset? LastSuccess, string? LastError, int Runs)
{
    public static PushStatus None { get; } = new(null, null, null, 0);
}

/// <summary>
/// Pushes the metrics of running tests to InfluxDB and an OTLP endpoint at the configured interval, plus one last
/// push when a run finishes so the final numbers arrive, even for a run shorter than the interval. Runs that ended
/// before the publisher was created, and weren't seen running by it, are left out. A failed push is reported in the status and retried with the
/// next one; it never affects the test.
/// </summary>
public sealed class TelemetryPublisher
{
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<TelemetrySettings> _settings;
    private readonly Func<IReadOnlyList<RunTelemetry>> _runs;
    private readonly HttpMessageInvoker _http;
    private readonly string _instance;
    private readonly string _version;
    private readonly Action<string>? _log;
    private readonly InfluxLineProtocol _influx = new();
    private readonly HashSet<Guid> _running = [];
    private readonly HashSet<Guid> _finished = [];
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="settings">Read before every push, so a change applies to the next one.</param>
    /// <param name="runs">The runs to report: those running and those that finished recently.</param>
    /// <param name="instance">The name of this machine, sent as the OTLP <c>service.instance.id</c>.</param>
    /// <param name="log">Told when a destination starts failing or recovers, not on every push.</param>
    public TelemetryPublisher(
        Func<TelemetrySettings> settings,
        Func<IReadOnlyList<RunTelemetry>> runs,
        HttpMessageInvoker http,
        string instance,
        string version,
        Action<string>? log = null)
    {
        _settings = settings;
        _runs = runs;
        _http = http;
        _instance = instance;
        _version = version;
        _log = log;
    }

    public PushStatus InfluxDbStatus { get; private set; } = PushStatus.None;

    public PushStatus OtlpStatus { get; private set; } = PushStatus.None;

    /// <summary>Pushes at the configured interval until <paramref name="cancellationToken"/> is cancelled, then once more.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await PushAsync(cancellationToken).ConfigureAwait(false);
            var interval = Math.Clamp(_settings().PushIntervalSeconds, TelemetrySettings.MinPushIntervalSeconds, TelemetrySettings.MaxPushIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Runs that finished during the last interval still get their final numbers sent.
        await PushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Sends the running tests now, and the final numbers of those that finished since the last push.</summary>
    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var all = _runs();
            var due = new List<RunTelemetry>();
            var finishing = new List<Guid>();
            foreach (var run in all)
            {
                if (!run.IsFinished)
                {
                    _running.Add(run.RunId);
                    due.Add(run);
                }
                else if (!_finished.Contains(run.RunId) && (_running.Contains(run.RunId) || run.EndedAt >= _since))
                {
                    due.Add(run);
                    finishing.Add(run.RunId);
                }
            }

            _running.IntersectWith(all.Select(r => r.RunId));
            if (due.Count == 0 || !settings.PushesAnywhere)
            {
                return;
            }

            // Each finished run is sent once more, with its final numbers; the list of those sent follows the runs given.
            _finished.UnionWith(finishing);
            _finished.IntersectWith(all.Select(r => r.RunId));

            var now = DateTimeOffset.UtcNow;
            var masker = settings.CreateMasker();
            var pushes = new List<Task>();
            if (settings.InfluxDb.Enabled)
            {
                var body = _influx.Write(due, now);
                pushes.Add(SendAsync("InfluxDB", settings.InfluxDb.WriteUri, body, InfluxLineProtocol.ContentType, request =>
                {
                    if (!string.IsNullOrWhiteSpace(settings.InfluxDb.Token))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Token " + settings.InfluxDb.Token.Trim());
                    }
                }, masker, due.Count, s => InfluxDbStatus = s, () => InfluxDbStatus, cancellationToken));
            }

            if (settings.Otlp.Enabled)
            {
                var body = OtlpJson.Write(TelemetryMetrics.Build(due), now, _instance, _version);
                pushes.Add(SendAsync("the OTLP endpoint", settings.Otlp.MetricsUri, body, OtlpJson.ContentType, request =>
                {
                    foreach (var header in settings.Otlp.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)))
                    {
                        request.Headers.TryAddWithoutValidation(header.Name.Trim(), header.Value ?? string.Empty);
                    }
                }, masker, due.Count, s => OtlpStatus = s, () => OtlpStatus, cancellationToken));
            }

            await Task.WhenAll(pushes).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(
        string destination,
        Func<Uri> uri,
        object body,
        string contentType,
        Action<HttpRequestMessage> addHeaders,
        SecretMasker masker,
        int runs,
        Action<PushStatus> setStatus,
        Func<PushStatus> getStatus,
        CancellationToken cancellationToken)
    {
        var attempt = DateTimeOffset.UtcNow;
        string? error;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri());
            request.Content = body is byte[] bytes ? new ByteArrayContent(bytes) : new StringContent((string)body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            addHeaders(request);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PushTimeout);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                error = null;
            }
            else
            {
                var answer = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                answer = answer.Length > 300 ? answer[..300] + "…" : answer;
                error = $"{Capitalized(destination)} answered {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(answer) ? "" : ": " + answer.Trim())}";
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = $"{Capitalized(destination)} didn't answer within {PushTimeout.TotalSeconds:0} seconds.";
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException or FormatException)
        {
            error = $"Couldn't reach {destination}: {ex.Message}";
        }

        error = masker.Apply(error);
        var previous = getStatus();
        setStatus(new PushStatus(attempt, error is null ? DateTimeOffset.UtcNow : previous.LastSuccess, error, runs));
        if (error is not null && error != previous.LastError)
        {
            _log?.Invoke(error);
        }
        else if (error is null && previous.LastError is not null)
        {
            _log?.Invoke($"{Capitalized(destination)} accepts metrics again.");
        }
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
