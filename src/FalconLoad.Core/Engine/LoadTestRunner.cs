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
    /// <summary>How often the staged runner compares the number of virtual users with the target.</summary>
    private static readonly TimeSpan StageTick = TimeSpan.FromMilliseconds(50);

    private readonly LoadTestPlan _plan;
    private readonly PreparedRequest[] _requests;
    private readonly ParameterSet? _parameters;
    private readonly HttpMessageInvoker _invoker;
    private readonly bool _ownsInvoker;
    private readonly MetricsCollector _metrics;
    private readonly Stopwatch _clock = new();

    private readonly StageSchedule? _schedule;
    private readonly bool _stagedRate;
    private readonly long[] _slotIterations;

    private long _remainingRequests;
    private long _nextRateSlot;
    private long _rateIntervalTicks;
    private long _nextStagedRequest;
    private int _desiredUsers = int.MaxValue;
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
        _schedule = _plan.Profile.IsStaged ? new StageSchedule(_plan.Profile.Stages) : null;
        _stagedRate = _schedule is not null && _plan.Profile.StageMode == StageMode.RequestRate;
        _slotIterations = new long[_plan.Profile.PeakVirtualUsers];
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
        var staged = _schedule is not null;

        // Stages set how long the test lasts, so a request count doesn't apply to them.
        _remainingRequests = !staged && profile.StopCondition == StopCondition.RequestCount ? profile.TotalRequests : long.MaxValue;
        _rateIntervalTicks = profile.MaxRequestsPerSecond > 0 && !_stagedRate
            ? Math.Max(1, (long)(Stopwatch.Frequency / profile.MaxRequestsPerSecond))
            : 0;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (staged)
        {
            stop.CancelAfter(TimeSpan.FromSeconds(_schedule!.TotalSeconds));
        }
        else if (profile.StopCondition == StopCondition.Duration)
        {
            stop.CancelAfter(TimeSpan.FromSeconds(profile.DurationSeconds));
        }

        _state = RunState.Running;
        _clock.Start();
        _nextRateSlot = _clock.ElapsedTicks;

        var all = staged && profile.StageMode == StageMode.VirtualUsers
            ? Task.Run(() => RunStagedUsersAsync(stop.Token), CancellationToken.None)
            : Task.WhenAll(Enumerable.Range(0, profile.Concurrency)
                .Select(i => Task.Run(() => VirtualUserAsync(i, stop.Token), CancellationToken.None)));

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

    /// <summary>
    /// Follows the stages by starting virtual users when the target rises. When it falls, users above the target finish
    /// the iteration they are in and stop, so a scenario is never cut off halfway. A stopped slot is started again when the
    /// target climbs back over it.
    /// </summary>
    private async Task RunStagedUsersAsync(CancellationToken stop)
    {
        var slots = new Task?[_slotIterations.Length];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var desired = Math.Min(slots.Length, (int)Math.Ceiling(_schedule!.TargetAt(_clock.Elapsed.TotalSeconds)));
                Volatile.Write(ref _desiredUsers, desired);
                for (var i = 0; i < desired; i++)
                {
                    if (slots[i] is { IsFaulted: true } faulted)
                    {
                        await faulted.ConfigureAwait(false);
                    }

                    if (slots[i] is not { IsCompleted: false })
                    {
                        var index = i;
                        slots[i] = Task.Run(() => VirtualUserAsync(index, stop), CancellationToken.None);
                    }
                }

                try
                {
                    await Task.Delay(StageTick, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            Volatile.Write(ref _desiredUsers, 0);
            await Task.WhenAll(slots.OfType<Task>()).ConfigureAwait(false);
        }
    }

    private async Task VirtualUserAsync(int index, CancellationToken stop)
    {
        var profile = _plan.Profile;
        if (profile.RampUpSeconds > 0 && index > 0 && _schedule is null)
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
        // A user that was stopped by a falling stage target carries on counting when its slot starts again.
        var iteration = _slotIterations[index];
        try
        {
            while (index < Volatile.Read(ref _desiredUsers))
            {
                iteration++;
                var context = new TemplateContext(index + 1, iteration, new ParameterValues(_parameters, row));
                var picked = false;
                for (var step = 0; step < _requests.Length; step++)
                {
                    if (stop.IsCancellationRequested || Interlocked.Decrement(ref _remainingRequests) < 0)
                    {
                        return;
                    }

                    if (_stagedRate)
                    {
                        if (!await WaitForStagedRequestAsync(stop).ConfigureAwait(false))
                        {
                            return;
                        }
                    }
                    else if (_rateIntervalTicks > 0 && !await WaitForRateSlotAsync(stop).ConfigureAwait(false))
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

                    var result = await SendAsync(step, context, buffer, stop).ConfigureAwait(false);
                    if (result is { } r)
                    {
                        _metrics.Record(r);
                    }
                }
            }
        }
        finally
        {
            _slotIterations[index] = iteration;
            Interlocked.Decrement(ref _activeVirtualUsers);
        }
    }

    /// <summary>
    /// Pacing for stages that set a request rate. Each request takes the next number in a shared sequence and waits until
    /// the schedule says that request is due. Requests the virtual users were too slow to send are skipped over rather than
    /// sent later in a burst, so the target is a ceiling and a slow server shows up as a shortfall.
    /// </summary>
    private async Task<bool> WaitForStagedRequestAsync(CancellationToken stop)
    {
        double? due;
        while (true)
        {
            var taken = Interlocked.Read(ref _nextStagedRequest);
            var now = _clock.Elapsed.TotalSeconds;
            var next = Math.Max(taken, (long)Math.Floor(_schedule!.RequestsDueBy(now)));
            if (Interlocked.CompareExchange(ref _nextStagedRequest, next + 1, taken) == taken)
            {
                due = _schedule.DueSecondOfRequest(next);
                break;
            }
        }

        if (due is null)
        {
            // The stages call for no more requests; the run ends when their time is up.
            due = double.PositiveInfinity;
        }

        var wait = due.Value - _clock.Elapsed.TotalSeconds;
        if (wait <= 0)
        {
            return true;
        }

        try
        {
            await Task.Delay(double.IsPositiveInfinity(wait) ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(wait), stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
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
    private async Task<RequestResult?> SendAsync(int step, TemplateContext context, byte[] buffer, CancellationToken stop)
    {
        var prepared = _requests[step];
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
                }
            }

            var latency = Stopwatch.GetElapsedTime(start);
            var code = (int)response.StatusCode;
            var success = prepared.Matcher.IsMatch(code);
            return new RequestResult(
                step,
                startedAt,
                latency,
                code,
                success,
                success ? null : $"Unexpected status {code} {response.StatusCode}",
                bytesSent,
                received);
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
