using System.Net;
using System.Text;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class AssertionTests
{
    private const string OrderJson = """
        { "status": "ok", "total": 3, "price": 1.0, "items": [ { "id": 7, "name": "falcon" }, { "id": 9, "name": null } ], "odd key": true }
        """;

    private static LoadTestPlan Plan(int requests, params Assertion[] checks) => new()
    {
        Requests = [new RequestDefinition { Url = "http://api.test/orders", Assertions = [.. checks] }],
        Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    private static FakeHandler Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { { "X-Request-Id", "abc-123" } },
        }));

    private static async Task<LoadTestSnapshot> Run(LoadTestPlan plan, HttpMessageHandler handler)
    {
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        return await runner.RunAsync(cancellationToken: CancellationToken.None);
    }

    [Theory]
    [InlineData("$.status", "\"ok\"")]
    [InlineData("status", "\"ok\"")]
    [InlineData("$.items[0].id", "7")]
    [InlineData("$.items[-1].id", "9")]
    [InlineData("$.items[1].name", "null")]
    [InlineData("$['odd key']", "true")]
    [InlineData("$.items.length", "2")]
    [InlineData("$.items[0].name.length", "6")]
    [InlineData("$.items[0]", """{ "id": 7, "name": "falcon" }""")]
    public void JsonPath_FindsValues(string path, string expected)
    {
        using var document = JsonDocument.Parse(OrderJson);
        Assert.True(JsonPath.TryParse(path, out var parsed, out var error), error);

        Assert.True(parsed.TryEvaluate(document.RootElement, out var value));
        Assert.Equal(expected, value.Kind == JsonValueKind.String ? $"\"{value.Text}\"" : value.Text);
    }

    [Theory]
    [InlineData("$.missing")]
    [InlineData("$.items[2]")]
    [InlineData("$.status.inner")]
    [InlineData("$.total[0]")]
    public void JsonPath_ReportsMissingValues(string path)
    {
        using var document = JsonDocument.Parse(OrderJson);
        Assert.True(JsonPath.TryParse(path, out var parsed, out _));
        Assert.False(parsed.TryEvaluate(document.RootElement, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$.")]
    [InlineData("$.items[*]")]
    [InlineData("$.items[0")]
    [InlineData("$..id")]
    public void JsonPath_RejectsWhatItCannotFollow(string path)
    {
        Assert.False(JsonPath.TryParse(path, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Validate_ExplainsBadChecks()
    {
        var request = new RequestDefinition
        {
            Url = "http://api.test/",
            Assertions =
            [
                new Assertion { Source = AssertionSource.Header, Operator = AssertionOperator.Exists },
                new Assertion { Source = AssertionSource.JsonPath, Target = "$.items[*]", Operator = AssertionOperator.Exists },
                new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "" },
                new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Exists },
                new Assertion { Source = AssertionSource.JsonPath, Target = "$.total", Operator = AssertionOperator.LessThan, Value = "many" },
                new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Matches, Value = "([a-z" },
                new Assertion { Source = AssertionSource.Header, Operator = AssertionOperator.Exists, Enabled = false },
            ],
        };

        var problems = request.Validate().ToList();

        Assert.Equal(6, problems.Count);
        Assert.Contains(problems, p => p.Contains("header name"));
        Assert.Contains(problems, p => p.Contains("Wildcards"));
        Assert.Contains(problems, p => p.Contains("needs a value"));
        Assert.Contains(problems, p => p.Contains("can't be checked with \"exists\""));
        Assert.Contains(problems, p => p.Contains("needs a number"));
        Assert.Contains(problems, p => p.Contains("regular expression"));
    }

    [Fact]
    public async Task PassingChecks_KeepRequestsSuccessfulAndAreCounted()
    {
        var plan = Plan(
            10,
            new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "\"falcon\"" },
            new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Matches, Value = "\"total\":\\s*3" },
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.status", Operator = AssertionOperator.Equals, Value = "ok" },
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.price", Operator = AssertionOperator.Equals, Value = "1" },
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.items.length", Operator = AssertionOperator.GreaterThan, Value = "1" },
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.error", Operator = AssertionOperator.NotExists },
            new Assertion { Source = AssertionSource.Header, Target = "content-type", Operator = AssertionOperator.Contains, Value = "json" },
            new Assertion { Source = AssertionSource.Header, Target = "X-Request-Id", Operator = AssertionOperator.Matches, Value = "^[a-z]+-\\d+$" });

        var result = await Run(plan, Json(OrderJson));

        Assert.Equal(10, result.SuccessfulRequests);
        Assert.Empty(result.Errors);
        Assert.Equal(8, result.Checks.Count);
        Assert.All(result.Checks, c => Assert.Equal((10L, 0L), (c.Passed, c.Failed)));
        Assert.Equal("$.status equals \"ok\"", result.Checks[2].Name);
    }

    [Fact]
    public async Task FailingCheck_FailsTheRequestAndNamesTheCheck()
    {
        var plan = Plan(
            6,
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.status", Operator = AssertionOperator.Exists },
            new Assertion { Source = AssertionSource.JsonPath, Target = "$.total", Operator = AssertionOperator.LessThan, Value = "2" },
            new Assertion { Source = AssertionSource.Header, Target = "X-Missing", Operator = AssertionOperator.Exists });

        var result = await Run(plan, Json(OrderJson));

        Assert.Equal(6, result.FailedRequests);
        Assert.Equal(6, result.StatusCodes["200"]);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Check failed: $.total is less than 2", error.Message);
        Assert.Equal([(6L, 0L), (0L, 6L), (0L, 6L)], result.Checks.Select(c => (c.Passed, c.Failed)));
        Assert.Equal(6, result.Steps[0].Failures);
    }

    [Fact]
    public async Task UnexpectedStatus_IsReportedBeforeChecksButChecksStillCount()
    {
        var plan = Plan(4, new Assertion { Source = AssertionSource.JsonPath, Target = "$.status", Operator = AssertionOperator.Equals, Value = "ok" });

        var result = await Run(plan, Json("""{ "status": "broken" }""", HttpStatusCode.InternalServerError));

        Assert.StartsWith("Unexpected status 500", Assert.Single(result.Errors).Message);
        Assert.Equal((0L, 4L), (result.Checks[0].Passed, result.Checks[0].Failed));
    }

    [Fact]
    public async Task BodyThatIsNotJson_FailsJsonChecks()
    {
        var plan = Plan(2, new Assertion { Source = AssertionSource.JsonPath, Target = "$.status", Operator = AssertionOperator.Exists });

        var result = await Run(plan, FakeHandler.Returning(HttpStatusCode.OK, "<html>oops</html>"));

        Assert.Equal(2, result.FailedRequests);
    }

    [Fact]
    public async Task TransportErrors_AreNotCountedAsCheckResults()
    {
        var plan = Plan(3, new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "ok" });
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "boom"));

        var result = await Run(plan, handler);

        Assert.Equal(3, result.FailedRequests);
        Assert.Equal((0L, 0L), (result.Checks[0].Passed, result.Checks[0].Failed));
    }

    [Fact]
    public async Task OversizedBody_FailsBodyChecksButStillCountsItsBytes()
    {
        var size = Assertion.MaxBodyBytes + 10;
        var plan = Plan(
            1,
            new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.NotContains, Value = "error" },
            new Assertion { Source = AssertionSource.Header, Target = "X-Error", Operator = AssertionOperator.NotExists });
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[size]) }));

        var result = await Run(plan, handler);

        Assert.Equal(size, result.BytesReceived);
        Assert.Contains("too large to check", Assert.Single(result.Errors).Message);
        Assert.Equal([(0L, 1L), (1L, 0L)], result.Checks.Select(c => (c.Passed, c.Failed)));
    }

    [Fact]
    public async Task DisabledChecks_AreIgnored()
    {
        var plan = Plan(2, new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "nope", Enabled = false });

        var result = await Run(plan, FakeHandler.Returning(HttpStatusCode.OK));

        Assert.Equal(2, result.SuccessfulRequests);
        Assert.Empty(result.Checks);
    }

    [Fact]
    public async Task Probe_ShowsWhatEachCheckSaw()
    {
        var request = new RequestDefinition
        {
            Url = "http://api.test/orders",
            Assertions =
            [
                new Assertion { Source = AssertionSource.JsonPath, Target = "$.items[0].name", Operator = AssertionOperator.Equals, Value = "eagle" },
                new Assertion { Source = AssertionSource.Header, Target = "X-Request-Id", Operator = AssertionOperator.Exists },
                new Assertion { Source = AssertionSource.JsonPath, Target = "$.nothing", Operator = AssertionOperator.Exists },
            ],
        };

        var probe = await RequestProbe.SendAsync(request, new LoadProfile(), new HttpMessageInvoker(Json(OrderJson)));

        Assert.True(probe.StatusMatches);
        Assert.False(probe.Success);
        Assert.Equal(
            [(false, "\"falcon\""), (true, "abc-123"), (false, "(not found)")],
            probe.Checks.Select(c => (c.Passed, c.Actual)));
    }
}
