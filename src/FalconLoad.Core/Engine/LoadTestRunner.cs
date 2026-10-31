using System.Diagnostics;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>
/// Executes a <see cref="LoadTestPlan"/>: spins up the configured number of virtual users, each looping
/// over the plan's requests until the stop condition is reached, and aggregates the results.
/// A runner instance runs once.
/// </summary>
public sealed class LoadTestRunner : IAsyncDisposable
{
    private readonly LoadTestPlan _plan;
    private readonly PreparedRequest[] _requests;
    private readonly ParameterSet? _parameters;
    private readonly bool _extracts;
    private readonly bool _keepsBodies;
    private readonly bool _hasOnceRequests;
    private readonly HttpMessageInvoker _invoker;
    private readonly bool _ownsInvoker;
    private readonly MetricsCollector _metrics;
    private readonly Stopwatch _clock = new();

    private long _remainingRequests;
    private long _nextRateSlot;
    private long _rateIntervalTicks;
    private int _activeVirtualUsers;
    private int _started;
    private volatile RunState _state = RunState.Pending;
    private string? _failureReason;
    private TimeSpan? _finalElapsed;

    public LoadTestRunner(LoadTestPlan plan, HttpMessageInvoker? invoker = null)
    {
        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("The load test plan is invalid: " + string.Join(" ", problems), nameof(plan));
        }

        _plan = plan.Clone();
        _requests = _plan.Requests.Select(PreparedRequest.From).ToArray();
        _parameters = _plan.Parameters.Count > 0 ? new ParameterSet(_plan.Parameters) : null;
        _extracts = _requests.Any(r => r.Extractor is not null);
        _keepsBodies = _requests.Any(r => r.Extractor is { NeedsBody: true });
        _hasOnceRequests = _requests.Any(r => r.OncePerVirtualUser);
        _metrics = new MetricsCollector(_plan.Requests.Select(r => r.DisplayName).ToList());

        if (invoker is null)
        {
            _invoker = new HttpMessageInvoker(HttpHandlerFactory.Create(_plan.Profile), disposeHandler: true);
            _ownsInvoker = true;
        }
        else
        {
            _invoker = invoker;
        }
    }

    public LoadTestPlan Plan => _plan;

    public RunState State => _state;

    public LoadTestSnapshot GetSnapshot() =>
        _metrics.Snapshot(_state, _finalElapsed ?? _clock.Elapsed, Volatile.Read(ref _activeVirtualUsers), _failureReason);

    /// <summary>A snapshot together with the latency histograms behind it, for an exported result.</summary>
    public (LoadTestSnapshot Snapshot, ResultHistograms Histograms) GetResults() =>
        _metrics.SnapshotWithHistograms(_state, _finalElapsed ?? _clock.Elapsed, Volatile.Read(ref _activeVirtualUsers), _failureReason);

    /// <summary>Runs the load test to completion. Cancelling <paramref name="cancellationToken"/> stops it early.</summary>
    /// <param name="onProgress">Invoked with a fresh snapshot every <paramref name="reportInterval"/> while running.</param>
    public async Task<LoadTestSnapshot> RunAsync(
        Action<LoadTestSnapshot>? onProgress = null,
        TimeSpan? reportInterval = null,
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("A LoadTestRunner can only run once.");
        }

        var profile = _plan.Profile;
        _remainingRequests = profile.StopCondition == StopCondition.RequestCount ? profile.TotalRequests : long.MaxValue;
        _rateIntervalTicks = profile.MaxRequestsPerSecond > 0
            ? Math.Max(1, (long)(Stopwatch.Frequency / profile.MaxRequestsPerSecond))
            : 0;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (profile.StopCondition == StopCondition.Duration)
        {
            stop.CancelAfter(TimeSpan.FromSeconds(profile.DurationSeconds));
        }

        _state = RunState.Running;
        _clock.Start();
        _nextRateSlot = _clock.ElapsedTicks;

        var workers = Enumerable.Range(0, profile.Concurrency)
            .Select(i => Task.Run(() => VirtualUserAsync(i, stop.Token), CancellationToken.None))
            .ToArray();
        var all = Task.WhenAll(workers);

        using var reporterStop = new CancellationTokenSource();
        var reporter = onProgress is null
            ? Task.CompletedTask
            : ReportAsync(onProgress, reportInterval ?? TimeSpan.FromMilliseconds(500), reporterStop.Token);

        try
        {
            await all.ConfigureAwait(false);
            _state = cancellationToken.IsCancellationRequested ? RunState.Cancelled : RunState.Completed;
        }
        catch (Exception ex)
        {
            _failureReason = ex.Message;
            _state = RunState.Failed;
        }
        finally
        {
            _clock.Stop();
            _finalElapsed = _clock.Elapsed;
            await reporterStop.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }

        var final = GetSnapshot();
        onProgress?.Invoke(final);
        return final;
    }

    private async Task ReportAsync(Action<LoadTestSnapshot> onProgress, TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                onProgress(GetSnapshot());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task VirtualUserAsync(int index, CancellationToken stop)
    {
        var profile = _plan.Profile;
        if (profile.RampUpSeconds > 0 && index > 0)
        {
            var delay = TimeSpan.FromSeconds((double)profile.RampUpSeconds * index / profile.Concurrency);
            try
            {
                await Task.Delay(delay, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Interlocked.Increment(ref _activeVirtualUsers);
        var buffer = new byte[16 * 1024];

        // Each virtual user refills its own row of parameter values per iteration, so lists cost no allocation per request.
        var row = _parameters is null ? null : new string[_parameters.Count];

        // Extracted variables belong to one virtual user and last across its iterations, so a token from a login
        // request sent once is reused by every later request. Each user also reuses one buffer for the bodies it keeps.
        var variables = _extracts ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
        using var kept = _keepsBodies ? new MemoryStream() : null;
        var done = _hasOnceRequests ? new bool[_requests.Length] : null;
        try
        {
            for (long iteration = 1; ; iteration++)
            {
                var context = new TemplateContext(index + 1, iteration, new ParameterValues(_parameters, row), variables);
                var picked = false;
                for (var step = 0; step < _requests.Length; step++)
                {
                    if (done is not null && done[step])
                    {
                        continue;
                    }

                    if (stop.IsCancellationRequested || Interlocked.Decrement(ref _remainingRequests) < 0)
                    {
                        return;
                    }

                    if (_rateIntervalTicks > 0 && !await WaitForRateSlotAsync(stop).ConfigureAwait(false))
                    {
                        return;
                    }

                    // Values are picked only once the iteration's first request may go, so with a request count the sequence
                    // isn't advanced by iterations that never send anything.
                    if (!picked && row is not null)
                    {
                        _parameters!.Next(row);
                        picked = true;
                    }

                    var result = await SendAsync(step, context, buffer, variables, kept, stop).ConfigureAwait(false);
                    if (result is { } r)
                    {
                        _metrics.Record(r);
                        if (done is not null && r.Success && _requests[step].OncePerVirtualUser)
                        {
                            done[step] = true;
                        }
                    }
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeVirtualUsers);
        }
    }

    /// <summary>Simple global pacing: each request claims the next time slot and waits for it.</summary>
    private async Task<bool> WaitForRateSlotAsync(CancellationToken stop)
    {
        var slot = Interlocked.Add(ref _nextRateSlot, _rateIntervalTicks) - _rateIntervalTicks;
        var waitTicks = slot - _clock.ElapsedTicks;
        if (waitTicks <= 0)
        {
            return true;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds((double)waitTicks / Stopwatch.Frequency), stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Sends one request. Returns null when the run was stopped while the request was in flight.</summary>
    /// <param name="variables">The virtual user's extracted variables, the same ones <paramref name="context"/> reads.</param>
    /// <param name="kept">The virtual user's buffer for bodies that extraction rules read, or null when none do.</param>
    private async Task<RequestResult?> SendAsync(
        int step,
        TemplateContext context,
        byte[] buffer,
        Dictionary<string, string>? variables,
        MemoryStream? kept,
        CancellationToken stop)
    {
        var prepared = _requests[step];
        var extractor = prepared.Extractor;
        var keep = extractor is { NeedsBody: true } ? kept : null;
        keep?.SetLength(0);
        var startedAt = _clock.Elapsed;
        var start = Stopwatch.GetTimestamp();
        long bytesSent = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(_plan.Profile.TimeoutSeconds));

        try
        {
            using var request = prepared.Build(context, out bytesSent);
            using var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);

            long received = 0;
            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            {
                int read;
                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (keep is not null && keep.Length < ResponseExtractor.MaxBodyBytes)
                    {
                        keep.Write(buffer, 0, (int)Math.Min(read, ResponseExtractor.MaxBodyBytes - keep.Length));
                    }
                }
            }

            // Extraction happens after the clock stops, so parsing a body doesn't add to the measured latency.
            var latency = Stopwatch.GetElapsedTime(start);
            var code = (int)response.StatusCode;
            var success = prepared.Matcher.IsMatch(code);
            var error = success ? null : $"Unexpected status {code} {response.StatusCode}";

            // A response that matches the status codes but lacks a value later requests need counts as an error, since
            // those requests would otherwise fail in less obvious ways. Failed responses aren't searched at all.
            if (success && extractor is not null)
            {
                var bodyBytes = keep is null ? default : keep.GetBuffer().AsSpan(0, (int)keep.Length);
                error = extractor.Apply(response, bodyBytes, variables!);
                success = error is null;
            }

            return new RequestResult(step, startedAt, latency, code, success, error, bytesSent, received);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The run is over; in-flight requests are discarded rather than counted as failures.
            return null;
        }
        catch (OperationCanceledException)
        {
            return Failure($"Timeout after {_plan.Profile.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return Failure(DescribeTransportError(ex));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UriFormatException or FormatException)
        {
            return Failure($"{ex.GetType().Name}: {ex.Message}");
        }

        RequestResult Failure(string error) =>
            new(step, startedAt, Stopwatch.GetElapsedTime(start), null, false, error, bytesSent, 0);
    }

    private static string DescribeTransportError(HttpRequestException ex)
    {
        if (ex.HttpRequestError != HttpRequestError.Unknown)
        {
            var inner = ex.InnerException?.Message;
            return inner is null ? ex.HttpRequestError.ToString() : $"{ex.HttpRequestError}: {inner}";
        }

        return ex.InnerException?.Message ?? ex.Message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsInvoker)
        {
            _invoker.Dispose();
        }

        await ValueTask.CompletedTask;
    }
}
