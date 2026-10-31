using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class ExtractionTests
{
    private const string Json = """
        { "data": { "token": "abc", "count": 3, "ok": true, "none": null, "items": [ { "id": 7 }, { "id": 9 } ] }, "odd key": "x" }
        """;

    [Theory]
    [InlineData("$.data.token", "abc")]
    [InlineData("data.token", "abc")]
    [InlineData("$['data']['token']", "abc")]
    [InlineData("$.data.count", "3")]
    [InlineData("$.data.ok", "true")]
    [InlineData("$.data.items[1].id", "9")]
    [InlineData("$.data.items[-1].id", "9")]
    [InlineData("$.data.items[0]", "{ \"id\": 7 }")]
    [InlineData("$[\"odd key\"]", "x")]
    public void JsonPath_FindsValues(string expression, string expected)
    {
        Assert.True(JsonPath.TryParse(expression, out var path, out var error), error);
        using var document = JsonDocument.Parse(Json);

        Assert.True(path.TryEvaluate(document.RootElement, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("$.data.missing")]
    [InlineData("$.data.none")]
    [InlineData("$.data.items[2]")]
    [InlineData("$.data.token.length")]
    [InlineData("$.Data.token")]
    public void JsonPath_FindsNothing(string expression)
    {
        Assert.True(JsonPath.TryParse(expression, out var path, out _));
        using var document = JsonDocument.Parse(Json);

        Assert.False(path.TryEvaluate(document.RootElement, out _));
    }

    [Theory]
    [InlineData("$.data.")]
    [InlineData("$.items[0")]
    [InlineData("$.items[x]")]
    [InlineData("$data")]
    public void JsonPath_RejectsInvalidPaths(string expression)
    {
        Assert.False(JsonPath.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task LoginOncePerVirtualUser_TokenIsReusedByLaterRequests()
    {
        var issued = 0;
        var handler = new FakeHandler((request, _) =>
        {
            var body = request.RequestUri!.AbsolutePath == "/login"
                ? $"{{ \"token\": \"token-{Interlocked.Increment(ref issued)}\" }}"
                : "ok";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition
                {
                    Method = "POST",
                    Url = "http://api.test/login",
                    OncePerVirtualUser = true,
                    Extractions = [new ExtractionRule { Variable = "token", Expression = "$.token" }],
                },
                new RequestDefinition
                {
                    Url = "http://api.test/items",
                    Headers =
                    [
                        new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" },
                        new HeaderEntry { Name = "X-User", Value = "{{$vu}}" },
                    ],
                },
            ],
            Profile = new LoadProfile { Concurrency = 3, StopCondition = StopCondition.RequestCount, TotalRequests = 60 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        var received = handler.Received.ToList();
        var logins = received.Count(r => r.Request.RequestUri!.AbsolutePath == "/login");
        var items = received.Where(r => r.Request.RequestUri!.AbsolutePath == "/items")
            .Select(r => (User: r.Request.Headers.GetValues("X-User").Single(), Token: r.Request.Headers.Authorization!.ToString()))
            .ToList();

        // A virtual user that starts late may find the shared request count used up, so up to three log in.
        Assert.InRange(logins, 1, 3);
        Assert.Equal(issued, logins);
        Assert.Equal(60, received.Count);
        Assert.Equal(60 - logins, items.Count);
        Assert.All(items, i => Assert.Matches(@"^Bearer token-[1-3]$", i.Token));

        // Every virtual user keeps its own token, and no two share one.
        var tokenByUser = items.GroupBy(i => i.User).ToDictionary(g => g.Key, g => Assert.Single(g.Select(i => i.Token).Distinct()));
        Assert.Equal(tokenByUser.Count, tokenByUser.Values.Distinct().Count());
        Assert.Equal(60, result.SuccessfulRequests);
    }

    [Fact]
    public async Task HeaderAndRegexExtractions_FeedLaterUrlsAndBodies()
    {
        var handler = new FakeHandler((request, _) =>
        {
            var response = new HttpResponseMessage(request.RequestUri!.AbsolutePath == "/orders" ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = new StringContent("<order id=\"o-42\" status=\"new\" />"),
            };
            response.Headers.Location = new Uri("http://api.test/orders/o-42");
            response.Headers.Add("X-Request-Id", "req-1");
            return Task.FromResult(response);
        });
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition
                {
                    Method = "POST",
                    Url = "http://api.test/orders",
                    ExpectedStatusCodes = "201",
                    Extractions =
                    [
                        new ExtractionRule { Variable = "orderUrl", Source = ExtractionSource.Header, Expression = "location" },
                        new ExtractionRule { Variable = "requestId", Source = ExtractionSource.Header, Expression = "X-Request-Id" },
                        new ExtractionRule { Variable = "orderId", Source = ExtractionSource.Regex, Expression = "id=\"([^\"]+)\"" },
                    ],
                },
                new RequestDefinition { Method = "PUT", Url = "{{orderUrl}}", Body = "{ \"id\": \"{{orderId}}\", \"after\": \"{{RequestId}}\" }" },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 2 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        var second = handler.Received.ToList()[1];
        Assert.Equal("http://api.test/orders/o-42", second.Request.RequestUri!.ToString());
        Assert.Equal("{ \"id\": \"o-42\", \"after\": \"req-1\" }", second.Body);
        Assert.Equal(2, result.SuccessfulRequests);
    }

    [Fact]
    public async Task ValueNotFound_CountsAsAnErrorAndThePlaceholderIsSentAsWritten()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "{ \"other\": 1 }");
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition { Url = "http://api.test/login", Extractions = [new ExtractionRule { Variable = "token", Expression = "$.token" }] },
                new RequestDefinition { Url = "http://api.test/items", Headers = [new HeaderEntry { Name = "X-Token", Value = "{{token}}" }] },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 4 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.FailedRequests);
        Assert.Equal(4, result.StatusCodes["200"]);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Couldn't extract 'token': JSON path $.token found nothing", error.Message);
        Assert.Equal(2, error.Count);
        Assert.All(handler.Received.Where(r => r.Request.RequestUri!.AbsolutePath == "/items"),
            r => Assert.Equal("{{token}}", r.Request.Headers.GetValues("X-Token").Single()));
    }

    [Fact]
    public async Task NonJsonBody_ReportsThatTheResponseIsNotJson()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "<html>login page</html>");
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "http://api.test/login", Extractions = [new ExtractionRule { Variable = "token", Expression = "token" }] }],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 1 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal("Couldn't extract 'token': the response isn't JSON", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task UnexpectedStatus_IsNotSearchedAndKeepsItsOwnError()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.Unauthorized, "{ \"token\": \"nope\" }");
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition { Url = "http://api.test/login", Extractions = [new ExtractionRule { Variable = "token", Expression = "$.token" }] },
                new RequestDefinition { Url = "http://api.test/items?t={{token}}" },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 2 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.All(result.Errors, e => Assert.StartsWith("Unexpected status 401", e.Message));
        Assert.DoesNotContain("nope", handler.Received.ToList()[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task OncePerVirtualUser_IsRetriedUntilItSucceeds()
    {
        var attempts = 0;
        var handler = new FakeHandler((request, _) =>
        {
            var code = request.RequestUri!.AbsolutePath == "/login" && Interlocked.Increment(ref attempts) == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent("ok") });
        });
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition { Url = "http://api.test/login", OncePerVirtualUser = true },
                new RequestDefinition { Url = "http://api.test/items" },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 6 },
        };

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(
            ["/login", "/items", "/login", "/items", "/items", "/items"],
            handler.Received.Select(r => r.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task Probe_ReportsExtractionsAndPassesThemToTheNextProbe()
    {
        var handler = new FakeHandler((request, _) =>
        {
            var body = request.RequestUri!.AbsolutePath == "/login" ? "{ \"token\": \"t-1\" }" : "ok";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        var invoker = new HttpMessageInvoker(handler);
        var login = new RequestDefinition
        {
            Url = "http://api.test/login",
            Extractions =
            [
                new ExtractionRule { Variable = "token", Expression = "$.token" },
                new ExtractionRule { Variable = "user", Expression = "$.user" },
            ],
        };
        var items = new RequestDefinition { Url = "http://api.test/items?token={{token}}" };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var first = await RequestProbe.SendAsync(login, new LoadProfile(), invoker, variables: variables);
        var second = await RequestProbe.SendAsync(items, new LoadProfile(), invoker, variables: variables);

        Assert.True(first.Success);
        Assert.Equal(
            [new ExtractedValue("token", "t-1", null), new ExtractedValue("user", null, "Couldn't extract 'user': JSON path $.user found nothing")],
            first.Extractions!);
        Assert.Null(second.Extractions);
        Assert.Equal("http://api.test/items?token=t-1", handler.Received.ToList()[1].Request.RequestUri!.ToString());
    }

    [Fact]
    public void Validate_ReportsBadRules()
    {
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition
                {
                    Url = "http://api.test/login",
                    Extractions =
                    [
                        new ExtractionRule { Variable = "", Expression = "$.a" },
                        new ExtractionRule { Variable = "1st", Expression = "$.a" },
                        new ExtractionRule { Variable = "a", Expression = " " },
                        new ExtractionRule { Variable = "b", Expression = "$.items[" },
                        new ExtractionRule { Variable = "c", Source = ExtractionSource.Regex, Expression = "(unclosed" },
                        new ExtractionRule { Variable = "userIds", Source = ExtractionSource.Header, Expression = "X-User" },
                    ],
                },
            ],
            Parameters = [new ParameterList { Name = "userIds", Values = ["1"] }],
        };

        var problems = plan.Validate();

        Assert.Contains(problems, p => p.EndsWith("Every extraction needs a variable name."));
        Assert.Contains(problems, p => p.Contains("Variable name '1st'"));
        Assert.Contains(problems, p => p.EndsWith("Extraction 'a' needs a JSON path."));
        Assert.Contains(problems, p => p.Contains("The JSON path for 'b' isn't valid"));
        Assert.Contains(problems, p => p.Contains("The regular expression for 'c' isn't valid"));
        Assert.Contains("'userIds' is both a parameter list and an extracted variable; rename one of them.", problems);
        Assert.Equal(6, problems.Count);
    }

    [Fact]
    public void Validate_NeedsOneRequestOnEveryIteration()
    {
        var plan = new LoadTestPlan { Requests = [new RequestDefinition { Url = "http://api.test/login", OncePerVirtualUser = true }] };

        Assert.Contains(plan.Validate(), p => p.StartsWith("At least one request has to run on every iteration"));
    }

    [Fact]
    public void PlanFile_KeepsRulesAndReadsOlderPlansWithoutThem()
    {
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition
                {
                    Url = "http://api.test/login",
                    OncePerVirtualUser = true,
                    Extractions = [new ExtractionRule { Variable = "token", Source = ExtractionSource.Header, Expression = "X-Token" }],
                },
                new RequestDefinition { Url = "http://api.test/items" },
            ],
        };

        var json = PlanFile.Write([plan]);
        var read = Assert.Single(PlanFile.Read(json).Plans);

        Assert.Contains("\"source\": \"Header\"", json);
        Assert.True(read.Requests[0].OncePerVirtualUser);
        Assert.Equal("X-Token", Assert.Single(read.Requests[0].Extractions).Expression);

        var old = PlanFile.Read("""{ "name": "old", "requests": [ { "url": "http://api.test/", "extractions": null } ] }""");
        Assert.True(old.Succeeded);
        Assert.Empty(old.Plans[0].Requests[0].Extractions);
        Assert.False(old.Plans[0].Requests[0].OncePerVirtualUser);
    }
}
