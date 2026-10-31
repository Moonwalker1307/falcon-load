using System.Net;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class LoadTestRunnerTests
{
    private static LoadTestPlan Plan(Action<LoadTestPlan>? configure = null)
    {
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "http://api.test/items" }],
            Profile = new LoadProfile { Concurrency = 4, StopCondition = StopCondition.RequestCount, TotalRequests = 100 },
        };
        configure?.Invoke(plan);
        return plan;
    }

    [Fact]
    public async Task RequestCount_SendsExactlyThatManyRequests()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "hello");
        await using var runner = new LoadTestRunner(Plan(), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(100, result.TotalRequests);
        Assert.Equal(100, result.SuccessfulRequests);
        Assert.Equal(0, result.FailedRequests);
        Assert.Equal(100, handler.Received.Count);
        Assert.Equal(500, result.BytesReceived);
        Assert.Equal(100, result.StatusCodes["200"]);
    }

    [Fact]
    public async Task UnexpectedStatus_IsCountedAsFailure()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.InternalServerError);
        await using var runner = new LoadTestRunner(Plan(p => p.Profile.TotalRequests = 10), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(10, result.FailedRequests);
        Assert.Equal(1.0, result.ErrorRate);
        var error = Assert.Single(result.Errors);
        Assert.Contains("500", error.Message);
    }

    [Fact]
    public async Task ExpectedStatusCodes_OverrideDefaultSuccessRule()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.NotFound);
        var plan = Plan(p =>
        {
            p.Profile.TotalRequests = 5;
            p.Requests[0].ExpectedStatusCodes = "404";
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(5, result.SuccessfulRequests);
    }

    [Fact]
    public async Task TransportErrors_AreRecordedWithMessage()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "boom", new IOException("Connection refused")));
        await using var runner = new LoadTestRunner(Plan(p => p.Profile.TotalRequests = 3), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.FailedRequests);
        Assert.Equal(3, result.StatusCodes["No response"]);
        Assert.Equal("ConnectionError: Connection refused", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Timeout_IsReportedAsFailure()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromSeconds(10));
        var plan = Plan(p =>
        {
            p.Profile.TotalRequests = 2;
            p.Profile.Concurrency = 2;
            p.Profile.TimeoutSeconds = 1;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.FailedRequests);
        Assert.StartsWith("Timeout", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Duration_StopsAfterConfiguredTime()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(5));
        var plan = Plan(p =>
        {
            p.Profile.StopCondition = StopCondition.Duration;
            p.Profile.DurationSeconds = 1;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.InRange(result.Elapsed.TotalSeconds, 0.9, 3);
        Assert.True(result.TotalRequests > 10);
        Assert.Equal(0, result.FailedRequests);
    }

    [Fact]
    public async Task Cancellation_EndsRunAsCancelled()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(5));
        var plan = Plan(p =>
        {
            p.Profile.StopCondition = StopCondition.Duration;
            p.Profile.DurationSeconds = 60;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var result = await runner.RunAsync(cancellationToken: cts.Token);

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.FailedRequests);
    }

    [Fact]
    public async Task RateLimit_CapsThroughput()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var plan = Plan(p =>
        {
            p.Profile.TotalRequests = 20;
            p.Profile.MaxRequestsPerSecond = 20;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(20, result.TotalRequests);
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(850), $"Took {result.Elapsed}");
    }

    [Fact]
    public async Task MultipleSteps_RunInOrderAndReportPerStep()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var plan = Plan(p =>
        {
            p.Profile.Concurrency = 1;
            p.Profile.TotalRequests = 4;
            p.Requests =
            [
                new RequestDefinition { Name = "login", Method = "POST", Url = "http://api.test/login", Body = "{\"user\":\"vu{{$vu}}\"}" },
                new RequestDefinition { Name = "list", Url = "http://api.test/items?page={{$iteration}}" },
            ];
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        var requests = handler.Received.ToArray();
        Assert.Equal(["/login", "/items", "/login", "/items"], requests.Select(r => r.Request.RequestUri!.AbsolutePath));
        Assert.Equal("?page=2", requests[3].Request.RequestUri!.Query);
        Assert.Equal("{\"user\":\"vu1\"}", requests[0].Body);
        Assert.Equal("application/json", requests[0].Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal([2L, 2L], result.Steps.Select(s => s.Requests));
        Assert.Equal("login", result.Steps[0].Name);
    }

    [Fact]
    public async Task Headers_AreSentIncludingContentHeaders()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var plan = Plan(p =>
        {
            p.Profile.TotalRequests = 1;
            p.Requests[0].Method = "PUT";
            p.Requests[0].Body = "<x/>";
            p.Requests[0].Headers =
            [
                new HeaderEntry { Name = "Authorization", Value = "Bearer abc" },
                new HeaderEntry { Name = "Content-Type", Value = "application/xml" },
                new HeaderEntry { Name = "X-Disabled", Value = "no", Enabled = false },
            ];
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        var (request, _) = Assert.Single(handler.Received);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("Bearer abc", request.Headers.Authorization!.ToString());
        Assert.Equal("application/xml", request.Content!.Headers.ContentType!.MediaType);
        Assert.False(request.Headers.Contains("X-Disabled"));
    }

    [Fact]
    public async Task Progress_IsReportedAndEndsWithFinalSnapshot()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, delay: TimeSpan.FromMilliseconds(2));
        var plan = Plan(p =>
        {
            p.Profile.StopCondition = StopCondition.Duration;
            p.Profile.DurationSeconds = 1;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var snapshots = new List<LoadTestSnapshot>();

        await runner.RunAsync(s => { lock (snapshots) { snapshots.Add(s); } }, TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.True(snapshots.Count >= 3);
        Assert.True(snapshots[^1].IsFinished);
        Assert.NotEmpty(snapshots[^1].Timeline);
    }

    [Fact]
    public void InvalidPlan_IsRejected()
    {
        var plan = Plan(p => p.Requests[0].Url = "not a url");
        Assert.Throws<ArgumentException>(() => new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK))));
    }

    [Fact]
    public async Task VirtualUserOffset_NumbersUsersOnFromIt()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var plan = Plan(p =>
        {
            p.Requests[0].Url = "http://api.test/users/{{$vu}}";
            p.Profile.Concurrency = 2;
            p.Profile.TotalRequests = 20;
            p.Profile.VirtualUserOffset = 10;
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        // One user may take every request before the other gets going, so only the numbering is checked.
        var paths = handler.Received.Select(r => r.Request.RequestUri!.AbsolutePath).Distinct().ToList();
        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.Contains(p, new[] { "/users/11", "/users/12" }));
    }
}
