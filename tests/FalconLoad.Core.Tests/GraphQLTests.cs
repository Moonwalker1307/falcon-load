using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class GraphQLTests
{
    private static RequestDefinition Query(string query = "{ me { id } }", string? variables = null, string? operationName = null) => new()
    {
        Protocol = RequestProtocol.GraphQL,
        Method = "GET",
        Url = "http://api.test/graphql",
        Body = query,
        GraphQL = new GraphQLOptions { Variables = variables, OperationName = operationName },
    };

    private static LoadTestPlan Plan(RequestDefinition request, int requests = 5) => new()
    {
        Requests = [request],
        Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    [Fact]
    public async Task Query_IsPostedAsJsonWithVariablesAndOperationName()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":{"me":{"id":"1"}}}""");
        var request = Query("query Me($id: ID!) { user(id: $id) { name } }", """{ "id": "{{$vu}}-{{userIds}}" }""", "Me");
        var plan = Plan(request, requests: 1);
        plan.Parameters.Add(new ParameterList { Name = "userIds", Values = ["42"] });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.SuccessfulRequests);
        var (sent, body) = Assert.Single(handler.Received);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("query Me($id: ID!) { user(id: $id) { name } }", json.RootElement.GetProperty("query").GetString());
        Assert.Equal("1-42", json.RootElement.GetProperty("variables").GetProperty("id").GetString());
        Assert.Equal("Me", json.RootElement.GetProperty("operationName").GetString());
        Assert.Equal(body!.Length, result.BytesSent);
    }

    [Fact]
    public async Task Query_WithoutVariables_SendsOnlyTheQuery()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":{}}""");
        await using var runner = new LoadTestRunner(Plan(Query(), requests: 2), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SuccessfulRequests);
        var (_, body) = handler.Received.First();
        Assert.Equal("""{"query":"{ me { id } }"}""", body);
    }

    [Fact]
    public async Task ResponseWithErrors_CountsAsFailure()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":null,"errors":[{"message":"Not authorised","path":["me"]},{"message":"Other"}]}""");
        await using var runner = new LoadTestRunner(Plan(Query(), requests: 3), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.FailedRequests);
        Assert.Equal(3, result.StatusCodes["200"]);
        var error = Assert.Single(result.Errors);
        Assert.Equal("GraphQL error: Not authorised", error.Message);
        Assert.Equal(3, error.Count);
    }

    [Theory]
    [InlineData("""{"data":{"me":null}}""")]
    [InlineData("""{"data":{"me":null},"errors":[]}""")]
    [InlineData("""{"errors":null,"data":{}}""")]
    public async Task ResponseWithoutErrors_Succeeds(string body)
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, body);
        await using var runner = new LoadTestRunner(Plan(Query(), requests: 2), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SuccessfulRequests);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ResponseThatIsNotJson_CountsAsFailure()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "<html>oops</html>");
        await using var runner = new LoadTestRunner(Plan(Query(), requests: 1), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.FailedRequests);
        Assert.Equal("GraphQL error: the response isn't valid JSON", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task UnexpectedStatus_IsReportedBeforeGraphQLErrors()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.BadRequest, """{"errors":[{"message":"Syntax error"}]}""");
        await using var runner = new LoadTestRunner(Plan(Query(), requests: 1), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.StartsWith("Unexpected status 400", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task ChecksAndExtractions_ReadTheDataObject()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":{"login":{"token":"abc","role":"admin"}}}""");
        var login = Query("mutation { login { token role } }");
        login.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.data.login.role", Operator = AssertionOperator.Equals, Value = "admin" });
        login.Extractions.Add(new ExtractionRule { Variable = "token", Source = ExtractionSource.JsonPath, Expression = "$.data.login.token" });
        var next = Query("{ me { id } }");
        next.Headers.Add(new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" });
        var plan = Plan(login, requests: 2);
        plan.Requests.Add(next);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SuccessfulRequests);
        Assert.Equal(1, result.Checks.Single().Passed);
        var second = handler.Received.Last().Request;
        Assert.Equal("Bearer abc", second.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task FailedCheck_IsReportedWhenThereIsNoGraphQLError()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":{"me":{"id":"1"}}}""");
        var request = Query();
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.data.me.id", Operator = AssertionOperator.Equals, Value = "2" });
        await using var runner = new LoadTestRunner(Plan(request, requests: 1), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.StartsWith("Check failed", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Probe_ReportsTheGraphQLError()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"errors":[{"message":"Field \"nope\" doesn't exist"}]}""");

        var result = await RequestProbe.SendAsync(Query("{ nope }"), new LoadProfile(), new HttpMessageInvoker(handler));

        Assert.Equal(200, result.StatusCode);
        Assert.True(result.StatusMatches);
        Assert.False(result.Success);
        Assert.Equal("GraphQL error: Field \"nope\" doesn't exist", result.Error);
    }

    [Fact]
    public async Task Probe_Succeeds_WithoutErrors()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK, """{"data":{"me":{"id":"1"}}}""");

        var result = await RequestProbe.SendAsync(Query(), new LoadProfile(), new HttpMessageInvoker(handler));

        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Validation_NeedsAQuery_AndVariablesAsAnObject()
    {
        Assert.Contains(Query(query: " ").Validate(), p => p.Contains("needs a query"));
        Assert.Contains(Query(variables: "[1, 2]").Validate(), p => p.Contains("JSON object"));
        Assert.Contains(Query(variables: "{ not json").Validate(), p => p.Contains("JSON object"));
        Assert.Empty(Query(variables: """{ "id": {{userId}}, "name": "{{$guid}}" }""").Validate());
        Assert.Empty(Query(variables: "  ").Validate());
    }

    [Fact]
    public void Validation_IgnoresTheMethod()
    {
        var request = Query();
        request.Method = "BOGUS";

        Assert.Empty(request.Validate());
    }

    [Fact]
    public void Label_NamesTheProtocol()
    {
        var request = Query();

        Assert.Equal("GraphQL", request.Label);
        Assert.Equal("GraphQL http://api.test/graphql", request.DisplayName);
        Assert.Equal("GET", new RequestDefinition().Label);
    }

    [Fact]
    public void PlansSavedBeforeProtocols_LoadAsHttp()
    {
        var plan = JsonSerializer.Deserialize<LoadTestPlan>("""{"name":"old","requests":[{"method":"POST","url":"http://api.test/x","body":"{}"}]}""")!;

        var request = Assert.Single(plan.Requests);
        Assert.Equal(RequestProtocol.Http, request.Protocol);
        Assert.Null(request.GraphQL);
        Assert.Empty(plan.Validate());
    }

    [Fact]
    public void GraphQLRequest_RoundTripsThroughJson()
    {
        var plan = Plan(Query("{ me { id } }", """{"id": 1}""", "Me"));

        var clone = plan.Clone();

        var request = Assert.Single(clone.Requests);
        Assert.Equal(RequestProtocol.GraphQL, request.Protocol);
        Assert.Equal("""{"id": 1}""", request.GraphQL!.Variables);
        Assert.Equal("Me", request.GraphQL.OperationName);
    }

    [Theory]
    [InlineData("""{"errors":[{"message":"a"}]}""", "GraphQL error: a")]
    [InlineData("""{"errors":[{"extensions":{},"message":"b"}]}""", "GraphQL error: b")]
    [InlineData("""{"errors":[{"extensions":{"message":"not this"}}]}""", "GraphQL error: the response reports an error")]
    [InlineData("""{"errors":["text"]}""", "GraphQL error: the response reports an error")]
    [InlineData("""{"errors":{"message":"x"}}""", "GraphQL error: \"errors\" isn't an array")]
    [InlineData("""[1]""", "GraphQL error: the response isn't a JSON object")]
    [InlineData("""{"data":{"errors":[{"message":"nested, not top level"}]}}""", null)]
    [InlineData("""{"errors":[]}""", null)]
    public void FindError_ReadsOnlyTheTopLevelErrors(string body, string? expected)
    {
        Assert.Equal(expected, GraphQLRequest.FindError(System.Text.Encoding.UTF8.GetBytes(body)));
    }
}
