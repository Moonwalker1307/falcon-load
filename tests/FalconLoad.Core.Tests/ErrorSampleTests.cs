using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Reports;

namespace FalconLoad.Core.Tests;

public class ErrorSampleTests
{
    private const string Secret = "k3y-very-secret-0042";
    private const string Token = "hard-coded-bearer-9911";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static LoadTestPlan Plan(int total = 20, params RequestDefinition[] requests) => new()
    {
        Name = "Orders",
        Requests = requests.Length > 0 ? [.. requests] : [new RequestDefinition { Url = "http://api.test/orders/{{$vu}}?page=1" }],
        Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    private static async Task<(LoadTestSnapshot Snapshot, LoadTestRunner Runner)> RunAsync(LoadTestPlan plan, HttpMessageHandler handler, TestEnvironment? environment = null)
    {
        var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        return (await runner.RunAsync(cancellationToken: CancellationToken.None), runner);
    }

    [Fact]
    public async Task UnexpectedStatus_IsSampledWithRouteUrlAndResponse()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":"database unavailable"}""", System.Text.Encoding.UTF8, "application/json"),
        }));
        var (s, runner) = await RunAsync(Plan(), handler);
        await runner.DisposeAsync();

        Assert.Equal(20, s.FailedRequests);
        // Only one kind of error, so only as many samples as one error may keep.
        Assert.Equal(ErrorSampler.MaxPerError, s.ErrorSamples.Count);
        var sample = s.ErrorSamples[0];
        Assert.Equal(0, sample.StepIndex);
        Assert.Equal("GET", sample.Method);
        Assert.Equal("/orders/{{$vu}}", sample.Route);
        Assert.Matches(@"^http://api\.test/orders/\d\?page=1$", sample.Url);
        Assert.Equal("500", sample.Status);
        Assert.StartsWith("Unexpected status 500", sample.Error);
        Assert.Equal("""{"error":"database unavailable"}""", sample.ResponseBody!.Text);
        Assert.False(sample.ResponseBody.Truncated);
        Assert.Contains(sample.ResponseHeaders, h => h.Name == "Content-Type" && h.Value.StartsWith("application/json"));

        var counted = Assert.Single(s.RequestErrors);
        Assert.Equal((0, 20L), (counted.StepIndex, counted.Count));
    }

    [Fact]
    public async Task Samples_AreBoundedPerRequestAndKeepEachKindOfError()
    {
        // Every call fails with a different status, so each would be a new kind of error.
        var calls = 0;
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)(400 + Interlocked.Increment(ref calls) % 50))));
        var plan = Plan(200, new RequestDefinition { Name = "List", Url = "http://api.test/a" }, new RequestDefinition { Name = "Get", Method = "POST", Url = "http://api.test/b" });
        var (s, runner) = await RunAsync(plan, handler);
        await runner.DisposeAsync();

        Assert.Equal(200, s.FailedRequests);
        Assert.Equal(ErrorSampler.MaxPerRequest, s.ErrorSamples.Count(e => e.StepIndex == 0));
        Assert.Equal(ErrorSampler.MaxPerRequest, s.ErrorSamples.Count(e => e.StepIndex == 1));
        Assert.All(s.ErrorSamples.Where(e => e.StepIndex == 1), e => Assert.Equal(("POST", "/b"), (e.Method, e.Route)));
        Assert.All(s.ErrorSamples.GroupBy(e => (e.StepIndex, e.Error)), g => Assert.True(g.Count() <= ErrorSampler.MaxPerError));

        // The errors of each request are counted apart, and add up to its failures. Two users share the request count,
        // so the split between the requests can be 101 and 99 rather than 100 each.
        Assert.Equal(s.Steps[0].Failures, s.RequestErrors.Where(e => e.StepIndex == 0).Sum(e => e.Count));
        Assert.Equal(s.Steps[1].Failures, s.RequestErrors.Where(e => e.StepIndex == 1).Sum(e => e.Count));
    }

    [Fact]
    public async Task TransportError_IsSampledWithTheRequestThatWasSent()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "boom", new IOException("Connection refused")));
        var request = new RequestDefinition
        {
            Method = "POST",
            Url = "http://api.test/orders",
            Body = """{"sku":"A-1"}""",
            Headers = [new HeaderEntry { Name = "X-Trace", Value = "trace-1" }],
        };
        var (s, runner) = await RunAsync(Plan(3, request), handler);
        await runner.DisposeAsync();

        var sample = s.ErrorSamples[0];
        Assert.Equal(RequestResult.NoResponseStatus, sample.Status);
        Assert.Equal("ConnectionError: Connection refused", sample.Error);
        Assert.Equal("http://api.test/orders", sample.Url);
        Assert.Contains(new SampleHeader("X-Trace", "trace-1"), sample.RequestHeaders);
        Assert.Equal("""{"sku":"A-1"}""", sample.RequestBody!.Text);
        Assert.Null(sample.ResponseBody);
        Assert.Empty(sample.ResponseHeaders);
    }

    [Fact]
    public async Task Samples_NeverContainSecretsOrCredentialHeaders()
    {
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables = [new EnvironmentVariable { Name = "apiKey", Value = Secret, Secret = true }],
        };
        var request = new RequestDefinition
        {
            Method = "POST",
            Url = "http://api.test/orders?key={{apiKey}}",
            Body = """{"key":"{{apiKey}}"}""",
            Headers =
            [
                new HeaderEntry { Name = "Authorization", Value = $"Bearer {Token}" },
                new HeaderEntry { Name = "X-Echo", Value = "{{apiKey}}" },
            ],
        };

        // The server echoes the key back in its body and in a cookie.
        var handler = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent($"key {Secret} is not allowed") };
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"session={Token}");
            response.Headers.TryAddWithoutValidation("X-Key", Secret);
            return Task.FromResult(response);
        });
        var (s, runner) = await RunAsync(Plan(4, request), handler, environment);
        var (results, histograms) = runner.GetResults();
        await runner.DisposeAsync();

        Assert.NotEmpty(s.ErrorSamples);
        var sample = s.ErrorSamples[0];
        Assert.Equal($"key {SecretMasker.Mask} is not allowed", sample.ResponseBody!.Text);
        Assert.Contains(new SampleHeader("Authorization", SecretMasker.Mask), sample.RequestHeaders);
        Assert.Contains(new SampleHeader("Set-Cookie", SecretMasker.Mask), sample.ResponseHeaders);

        // Nothing that leaves the run holds either value: the snapshot as the API returns it, or the export.
        var export = new RunExport { Plan = runner.Plan, Results = results, Histograms = histograms };
        var json = JsonSerializer.Serialize(s, Json) + JsonSerializer.Serialize(export.Results, Json);
        Assert.DoesNotContain(Secret, json);
        Assert.DoesNotContain(Uri.EscapeDataString(Secret), json);
        Assert.DoesNotContain(Token, json);
    }

    [Fact]
    public async Task OAuth2Token_IsNotInSamples()
    {
        const string accessToken = "oauth-access-token-7731";
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/token"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"access_token":"{{accessToken}}","expires_in":3600}""", System.Text.Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($"token {accessToken} was rejected") }));
        var plan = Plan(3);
        plan.Auth = new AuthSettings { Type = AuthType.OAuth2ClientCredentials, TokenUrl = "http://auth.test/token", ClientId = "app", ClientSecret = "client-secret-1234" };
        var (s, runner) = await RunAsync(plan, handler);
        await runner.DisposeAsync();

        var json = JsonSerializer.Serialize(s, Json);
        Assert.NotEmpty(s.ErrorSamples);
        Assert.DoesNotContain(accessToken, json);
        Assert.DoesNotContain("client-secret-1234", json);
    }

    [Fact]
    public async Task GraphQLError_IsSampledWithOperationInTheRoute()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"errors":[{"message":"Not allowed"}]}""");
        var request = new RequestDefinition
        {
            Protocol = RequestProtocol.GraphQL,
            Url = "http://api.test/graphql",
            Body = "query Me { me { id } }",
            GraphQL = new GraphQLOptions { OperationName = "Me" },
        };
        var (s, runner) = await RunAsync(Plan(3, request), handler);
        await runner.DisposeAsync();

        var sample = s.ErrorSamples[0];
        Assert.Equal(("GraphQL", "/graphql Me", "200"), (sample.Method, sample.Route, sample.Status));
        Assert.Contains("Not allowed", sample.ResponseBody!.Text);
        Assert.Contains("query Me", sample.RequestBody!.Text);
    }

    [Fact]
    public async Task WebSocketFailure_IsSampledWithTheMessageSent()
    {
        var sockets = new FakeWebSocketConnector(m => """{"ok":false}""");
        var request = new RequestDefinition
        {
            Protocol = RequestProtocol.WebSocket,
            Url = "ws://api.test/socket?room=1",
            Body = """{"op":"ping"}""",
            Assertions = [new Assertion { Source = AssertionSource.JsonPath, Target = "$.ok", Operator = AssertionOperator.Equals, Value = "true" }],
        };
        await using var runner = new LoadTestRunner(Plan(3, request), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);
        var s = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, s.FailedRequests);
        var sample = s.ErrorSamples[0];
        Assert.Equal(("WS", "/socket", RequestResult.ReplyStatus), (sample.Method, sample.Route, sample.Status));
        Assert.Equal("ws://api.test/socket?room=1", sample.Url);
        Assert.Equal("""{"op":"ping"}""", sample.RequestBody!.Text);
        Assert.Equal("""{"ok":false}""", sample.ResponseBody!.Text);
    }

    [Fact]
    public async Task Combine_KeepsSamplesOfEachMachineAtTheirCombinedPosition()
    {
        var exports = new List<RunExport>();
        foreach (var machine in new[] { "agent-a", "agent-b" })
        {
            var handler = FakeHandler.Returning(machine == "agent-a" ? HttpStatusCode.BadGateway : HttpStatusCode.ServiceUnavailable);
            await using var runner = new LoadTestRunner(Plan(6), new HttpMessageInvoker(handler));
            await runner.RunAsync(cancellationToken: CancellationToken.None);
            var (results, histograms) = runner.GetResults();
            exports.Add(new RunExport { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UnixEpoch, Machine = machine, Plan = runner.Plan, Results = results, Histograms = histograms });
        }

        // The samples travel through JSON, as they do from an agent to its controller.
        var read = exports.Select(e => JsonSerializer.Deserialize<RunExport>(JsonSerializer.Serialize(e, Json), JsonInput.Apply(new JsonSerializerOptions(Json)))!).ToList();
        var combined = ResultCombiner.Combine(read).Combined;

        Assert.Equal(2 * ErrorSampler.MaxPerError, combined.ErrorSamples.Count);
        Assert.Equal(["agent-a", "agent-b"], combined.ErrorSamples.Select(e => e.Machine).Distinct());
        Assert.All(combined.ErrorSamples, e => Assert.Equal("/orders/{{$vu}}", e.Route));
        Assert.Equal(12, combined.RequestErrors.Sum(e => e.Count));
        Assert.Equal(2, combined.RequestErrors.Count);
    }

    [Fact]
    public void ResultsWithoutSamples_StillLoad()
    {
        var snapshot = JsonSerializer.Deserialize<LoadTestSnapshot>(
            """{"state":"Completed","elapsed":"00:00:01","totalRequests":1,"successfulRequests":0,"failedRequests":1,"activeVirtualUsers":0,"bytesSent":0,"bytesReceived":0,"latency":{"count":1,"minMs":1,"meanMs":1,"p50Ms":1,"p90Ms":1,"p95Ms":1,"p99Ms":1,"maxMs":1},"statusCodes":{},"errors":[{"message":"x","count":1}],"steps":[],"timeline":[]}""",
            JsonInput.Apply(new JsonSerializerOptions(Json)))!;

        Assert.Empty(snapshot.ErrorSamples);
        Assert.Empty(snapshot.RequestErrors);
    }

    [Fact]
    public async Task HtmlReport_ShowsErrorsByRouteButNoRequestDetails()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.InternalServerError, "body-detail-xyz");
        var request = new RequestDefinition { Url = "http://api.test/orders/{{$vu}}", Headers = [new HeaderEntry { Name = "X-Trace", Value = "trace-header-xyz" }] };
        await using var runner = new LoadTestRunner(Plan(4, request), new HttpMessageInvoker(handler));
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        var (results, histograms) = runner.GetResults();
        var export = new RunExport { Plan = runner.Plan, Results = results, Histograms = histograms };

        var html = HtmlReport.Render(export, RunVerdict.Evaluate(results, null));
        var junit = JUnitReport.Render([(export, RunVerdict.Evaluate(results, null))]);

        Assert.Contains("Errors by request", html);
        Assert.Contains("GET /orders/{{$vu}}", html);
        Assert.Contains("Sampled failures", html);
        Assert.Contains("GET /orders/{{$vu}}: Unexpected status 500", junit);
        foreach (var report in new[] { html, junit })
        {
            Assert.DoesNotContain("body-detail-xyz", report);
            Assert.DoesNotContain("trace-header-xyz", report);
            Assert.DoesNotContain("api.test", report);
        }
    }

    [Theory]
    [InlineData("https://api.test/users/{{id}}?expand=1", "/users/{{id}}")]
    [InlineData("{{baseUrl}}/orders#top", "/orders")]
    [InlineData("https://api.test", "/")]
    [InlineData("https://api.test?next=/a", "/")]
    [InlineData("{{baseUrl}}", "/")]
    public void Route_IsThePathOfTheUrlAsWritten(string url, string route) =>
        Assert.Equal(route, new RequestDefinition { Url = url }.Route);

    [Fact]
    public void Route_OfAGrpcRequestIsItsMethod() =>
        Assert.Equal("/shop.v1.Catalog/GetProduct", new RequestDefinition { Protocol = RequestProtocol.Grpc, Url = "http://api.test:5000", Grpc = new GrpcOptions { Method = "shop.v1.Catalog/GetProduct" } }.Route);

    [Fact]
    public void Body_KeepsTextCutsLongBodiesAndHidesBinary()
    {
        var text = ErrorSampleBuilder.Body("héllo"u8, 6, false, t => t);
        Assert.Equal(("héllo", false), (text.Text, text.Truncated));

        var longBody = new string('a', ErrorSample.MaxBodyChars + 10);
        var cut = ErrorSampleBuilder.Body(System.Text.Encoding.UTF8.GetBytes(longBody), longBody.Length, false, t => t);
        Assert.Equal((ErrorSample.MaxBodyChars, true), (cut.Text!.Length, cut.Truncated));

        // A body cut in the middle of a character drops it rather than failing as invalid UTF-8.
        var halfChar = ErrorSampleBuilder.Body([.. "ab"u8, 0xC3], 10, true, t => t);
        Assert.Equal("ab", halfChar.Text);

        Assert.Null(ErrorSampleBuilder.Body([0x00, 0x01, 0xFF, 0x10], 4, false, t => t).Text);
    }
}
