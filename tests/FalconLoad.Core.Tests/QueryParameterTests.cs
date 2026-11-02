using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class QueryParameterTests
{
    private static LoadTestPlan Plan(RequestDefinition request, int total = 3) => new()
    {
        Requests = [request],
        Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    private static async Task<List<Uri>> RunAsync(LoadTestPlan plan, TestEnvironment? environment = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = FakeHandler.Returning(status);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        return [.. handler.Received.Select(r => r.Request.RequestUri!)];
    }

    [Fact]
    public async Task EnabledParameters_AreAddedToTheUrl_Encoded()
    {
        var request = new RequestDefinition
        {
            Url = "http://api.test/search",
            QueryParameters =
            [
                new QueryParameter { Name = "q", Value = "red shoes & socks" },
                new QueryParameter { Name = "page size", Value = "20" },
                new QueryParameter { Name = "debug", Value = "1", Enabled = false },
                new QueryParameter { Name = " ", Value = string.Empty },
            ],
        };

        var received = await RunAsync(Plan(request, total: 1));

        Assert.Equal("?q=red%20shoes%20%26%20socks&page%20size=20", received.Single().Query);
    }

    [Fact]
    public async Task Parameters_FollowAQueryAlreadyInTheUrl_AndDropTheFragment()
    {
        var request = new RequestDefinition
        {
            Url = "http://api.test/search?sort=name#top",
            QueryParameters = [new QueryParameter { Name = "q", Value = "x" }],
        };

        var received = await RunAsync(Plan(request, total: 1));

        Assert.Equal("http://api.test/search?sort=name&q=x", received.Single().AbsoluteUri);
    }

    [Fact]
    public async Task PlaceholderValues_AreEncodedAfterTheyAreFilledIn()
    {
        var request = new RequestDefinition
        {
            Url = "{{baseUrl}}/users",
            QueryParameters =
            [
                new QueryParameter { Name = "email", Value = "{{email}}" },
                new QueryParameter { Name = "tenant", Value = "{{tenant}}" },
                new QueryParameter { Name = "vu", Value = "vu-{{$vu}}" },
            ],
        };
        var plan = Plan(request, total: 2);
        plan.Parameters = [new ParameterList { Name = "email", Values = ["a+b@test.com", "c&d=e@test.com"] }];
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables =
            [
                new EnvironmentVariable { Name = "baseUrl", Value = "http://staging.test" },
                new EnvironmentVariable { Name = "tenant", Value = "acme & co" },
            ],
        };

        var received = await RunAsync(plan, environment);

        Assert.Equal(
            ["http://staging.test/users?email=a%2Bb%40test.com&tenant=acme%20%26%20co&vu=vu-1", "http://staging.test/users?email=c%26d%3De%40test.com&tenant=acme%20%26%20co&vu=vu-1"],
            received.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public async Task WebSocketRequests_ConnectWithTheParameters()
    {
        var connector = new FakeWebSocketConnector(_ => null);
        var plan = Plan(new RequestDefinition
        {
            Protocol = RequestProtocol.WebSocket,
            Url = "ws://api.test/live",
            Body = "hi",
            WebSocket = new WebSocketOptions { WaitForReply = false },
            QueryParameters = [new QueryParameter { Name = "channel", Value = "orders {{$vu}}" }],
        }, total: 1);

        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: connector);
        await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal("ws://api.test/live?channel=orders%201", connector.Connections.Single().Uri.AbsoluteUri);
    }

    [Fact]
    public void FullUrl_ShowsTheParametersWithPlaceholdersAsWritten()
    {
        var request = new RequestDefinition
        {
            Url = "https://api.test/users",
            QueryParameters = [new QueryParameter { Name = "id", Value = "{{userId}}" }, new QueryParameter { Name = "tag", Value = "a b" }],
        };

        Assert.Equal("https://api.test/users?id={{userId}}&tag=a%20b", request.FullUrl);
        Assert.Equal("GET https://api.test/users?id={{userId}}&tag=a%20b", request.DisplayName);
        Assert.Equal("/users", request.Route);
    }

    [Fact]
    public void GrpcRequests_IgnoreTheParameters()
    {
        var request = new RequestDefinition
        {
            Protocol = RequestProtocol.Grpc,
            Url = "http://api.test",
            QueryParameters = [new QueryParameter { Name = "id", Value = "1" }],
        };

        Assert.Equal("http://api.test", request.FullUrl);
    }

    [Fact]
    public void AValueWithoutAName_IsInvalid()
    {
        var request = new RequestDefinition
        {
            Url = "https://api.test/",
            QueryParameters = [new QueryParameter { Name = "", Value = "1" }],
        };

        Assert.Contains("Query parameter names cannot be empty.", request.Validate());
    }

    [Fact]
    public void PlansSavedBeforeTheTable_LoadWithNoParameters_AndNullsAreTolerated()
    {
        var import = PlanFile.Read("""
            { "plans": [ { "name": "Old", "requests": [ { "url": "https://api.test/?a=1" } ] },
                         { "name": "Nulls", "requests": [ { "url": "https://api.test/", "queryParameters": [ null, { "name": null, "value": null } ] } ] } ] }
            """);

        Assert.True(import.Succeeded, string.Join("; ", import.Problems));
        Assert.Empty(import.Plans[0].Requests[0].QueryParameters);
        Assert.Equal("https://api.test/?a=1", import.Plans[0].Requests[0].FullUrl);
        var parameter = Assert.Single(import.Plans[1].Requests[0].QueryParameters);
        Assert.Equal((string.Empty, string.Empty), (parameter.Name, parameter.Value));
    }

    [Fact]
    public void Parameters_SurviveACloneAndAnExport()
    {
        var plan = Plan(new RequestDefinition
        {
            Url = "https://api.test/",
            QueryParameters = [new QueryParameter { Name = "id", Value = "{{userId}}", Enabled = false }],
        });

        var copy = PlanFile.Read(PlanFile.Write([plan.Clone()])).Plans.Single();

        var parameter = Assert.Single(copy.Requests[0].QueryParameters);
        Assert.Equal(("id", "{{userId}}", false), (parameter.Name, parameter.Value, parameter.Enabled));
    }

    [Fact]
    public async Task ASecretInAParameter_StaysOutOfErrorSamples()
    {
        const string secret = "k3y with spaces+plus/slash";
        var plan = Plan(new RequestDefinition
        {
            Url = "http://api.test/orders",
            QueryParameters = [new QueryParameter { Name = "key", Value = "{{apiKey}}" }],
        }, total: 1);
        var environment = new TestEnvironment
        {
            Name = "Staging",
            Variables = [new EnvironmentVariable { Name = "apiKey", Value = secret, Secret = true }],
        };

        var handler = FakeHandler.Returning(HttpStatusCode.InternalServerError);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        var snapshot = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal("?key=" + Uri.EscapeDataString(secret), handler.Received.Single().Request.RequestUri!.Query);
        var sample = Assert.Single(snapshot.ErrorSamples);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("key=" + SecretMasker.Mask, sample.Url);
        Assert.DoesNotContain("k3y", json);
    }
}
