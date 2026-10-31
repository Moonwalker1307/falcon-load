using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Tests;

/// <summary>
/// Stands in for the network between a controller and its agents: requests are routed by host name to a
/// <see cref="FakeAgent"/>, which behaves like a falcon-load in agent mode.
/// </summary>
internal sealed class FakeAgentNetwork(params FakeAgent[] agents) : HttpMessageHandler
{
    private readonly Dictionary<string, FakeAgent> _agents = agents.ToDictionary(a => a.Host);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!_agents.TryGetValue(request.RequestUri!.Host, out var agent))
        {
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host", new IOException("Name or service not known"));
        }

        return agent.HandleAsync(request, cancellationToken);
    }
}

/// <summary>
/// An in-memory agent: it checks the bearer token, starts real <see cref="LoadTestRunner"/>s against its own
/// <see cref="FakeHandler"/> target, and serves their exports, so a controller can be tested end to end without sockets.
/// </summary>
internal sealed class FakeAgent(string host, string token)
{
    public string Host { get; } = host;

    public AgentEndpoint Endpoint(string? token = null) => new(Host, new Uri($"http://{Host}:5299"), token ?? Token);

    public string Token { get; } = token;

    /// <summary>How far this agent's clock is ahead of the real one.</summary>
    public TimeSpan ClockSkew { get; set; }

    /// <summary>The API the agent's virtual users hit; counts every request they send.</summary>
    // A little latency per request, so a long-running share doesn't spin a CPU core and starve the other tests.
    public FakeHandler Target { get; set; } = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(5));

    /// <summary>Simulates the machine being down: every request fails to connect.</summary>
    public bool Offline { get; set; }

    /// <summary>Simulates an agent that answers but refuses to start anything.</summary>
    public bool RefusesStarts { get; set; }

    public ConcurrentDictionary<Guid, FakeAgentRun> Runs { get; } = new();

    public ConcurrentQueue<HttpRequestMessage> Received { get; } = new();

    private DateTimeOffset Now => DateTimeOffset.UtcNow + ClockSkew;

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Received.Enqueue(request);
        if (Offline)
        {
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused", new IOException("Connection refused"));
        }

        if (request.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter != Token)
        {
            return Problem(HttpStatusCode.Unauthorized, "The agent token is missing or wrong");
        }

        var path = request.RequestUri!.AbsolutePath.TrimEnd('/');
        var method = request.Method;
        if (method == HttpMethod.Get && path == "/api/agent")
        {
            return Json(new AgentInfo(Host, "test", Now, Runs.Values.Count(r => !r.Runner.GetSnapshot().IsFinished)));
        }

        if (method == HttpMethod.Post && path == "/api/agent/runs")
        {
            if (RefusesStarts)
            {
                return Problem(HttpStatusCode.BadRequest, "One or more validation errors occurred.", "Concurrency must be between 1 and 10,000.");
            }

            var start = (await request.Content!.ReadFromJsonAsync<AgentStartRequest>(AgentClient.JsonOptions, cancellationToken))!;
            var run = new FakeAgentRun(new LoadTestRunner(start.Plan, new HttpMessageInvoker(Target), start.Environment), start);
            Runs[run.Id] = run;
            run.Task = Task.Run(async () =>
            {
                var wait = start.StartAt is { } at ? at - Now : TimeSpan.Zero;
                if (wait > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(wait, run.Stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stopped before the shared start time, like the real agent: nothing is sent.
                        run.StoppedBeforeStart = true;
                        return;
                    }
                }

                run.StartedAt = Now;
                await run.Runner.RunAsync(cancellationToken: run.Stop.Token);
            });
            return Json(new AgentRunStarted(run.Id), HttpStatusCode.Accepted);
        }

        if (path.StartsWith("/api/agent/runs/", StringComparison.Ordinal))
        {
            var rest = path["/api/agent/runs/".Length..];
            var stop = rest.EndsWith("/stop", StringComparison.Ordinal);
            if (!Guid.TryParse(stop ? rest[..^"/stop".Length] : rest, out var id) || !Runs.TryGetValue(id, out var run))
            {
                return Problem(HttpStatusCode.NotFound, "Not Found");
            }

            if (stop && method == HttpMethod.Post)
            {
                run.Stop.Cancel();
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            if (!stop && method == HttpMethod.Get)
            {
                var (snapshot, histograms) = run.Runner.GetResults();
                return Json(new RunExport
                {
                    Id = run.Id,
                    StartedAt = run.StartedAt ?? Now,
                    Machine = Host,
                    Plan = run.Runner.Plan,
                    Results = snapshot,
                    Histograms = histograms,
                });
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value, options: AgentClient.JsonOptions) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string title, string? error = null)
    {
        var errors = error is null ? "" : $", \"errors\": {{ \"plan\": [\"{error}\"] }}";
        return new HttpResponseMessage(status)
        {
            Content = new StringContent($"{{ \"title\": \"{title}\", \"status\": {(int)status}{errors} }}", Encoding.UTF8, "application/problem+json"),
        };
    }
}

internal sealed class FakeAgentRun(LoadTestRunner runner, AgentStartRequest request)
{
    public Guid Id { get; } = Guid.NewGuid();

    public LoadTestRunner Runner { get; } = runner;

    public AgentStartRequest Request { get; } = request;

    public CancellationTokenSource Stop { get; } = new();

    public DateTimeOffset? StartedAt { get; set; }

    public bool StoppedBeforeStart { get; set; }

    /// <summary>What the agent would report: a run stopped while waiting to start counts as cancelled.</summary>
    public RunState State => StoppedBeforeStart ? RunState.Cancelled : Runner.State;

    public Task Task { get; set; } = Task.CompletedTask;
}
