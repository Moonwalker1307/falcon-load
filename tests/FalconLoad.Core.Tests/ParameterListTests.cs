using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class ParameterListTests
{
    private static LoadTestPlan Plan(ParameterSelection selection = ParameterSelection.Sequential, int concurrency = 1, int total = 6) => new()
    {
        Requests =
        [
            new RequestDefinition
            {
                Method = "POST",
                Url = "http://api.test/users/{{userIds}}",
                Headers = [new HeaderEntry { Name = "X-Region", Value = "{{ region }}" }],
                Body = "{ \"user\": \"{{userIds}}\" }",
            },
        ],
        Parameters =
        [
            new ParameterList { Name = "userIds", Values = ["17", "42", "99"], Selection = selection },
            new ParameterList { Name = "region", Values = ["eu"] },
        ],
        Profile = new LoadProfile { Concurrency = concurrency, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    private static async Task<List<(HttpRequestMessage Request, string? Body)>> RunAsync(LoadTestPlan plan)
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        return [.. handler.Received];
    }

    [Fact]
    public async Task Sequential_HandsOutValuesInOrderAndWraps()
    {
        var received = await RunAsync(Plan());

        Assert.Equal(
            ["/users/17", "/users/42", "/users/99", "/users/17", "/users/42", "/users/99"],
            received.Select(r => r.Request.RequestUri!.AbsolutePath));
        Assert.All(received, r => Assert.Equal("eu", r.Request.Headers.GetValues("X-Region").Single()));
        Assert.All(received, r => Assert.Contains(r.Request.RequestUri!.Segments[^1], r.Body));
    }

    [Fact]
    public async Task Sequential_IsSharedByAllVirtualUsers()
    {
        var received = await RunAsync(Plan(concurrency: 4, total: 300));

        var counts = received.GroupBy(r => r.Request.RequestUri!.AbsolutePath).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(100, counts["/users/17"]);
        Assert.Equal(100, counts["/users/42"]);
        Assert.Equal(100, counts["/users/99"]);
    }

    [Fact]
    public async Task Random_OnlyUsesValuesFromTheList()
    {
        var received = await RunAsync(Plan(ParameterSelection.Random, total: 50));

        Assert.Equal(50, received.Count);
        Assert.All(received, r => Assert.Contains(r.Request.RequestUri!.AbsolutePath, new[] { "/users/17", "/users/42", "/users/99" }));
    }

    [Fact]
    public async Task AllRequestsOfAnIteration_ShareTheSameValue()
    {
        var plan = Plan(total: 4);
        plan.Requests.Add(new RequestDefinition { Url = "http://api.test/users/{{userIds}}/orders" });

        var received = await RunAsync(plan);

        Assert.Equal(
            ["/users/17", "/users/17/orders", "/users/42", "/users/42/orders"],
            received.Select(r => r.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public void UnknownNames_AreSentAsWritten()
    {
        var values = new ParameterSet([new ParameterList { Name = "known", Values = ["1"] }]).First();

        var text = TemplateRenderer.Render("{{known}} {{unknown}} {{known:1:2}}", new TemplateContext(1, 1, values));

        Assert.Equal("1 {{unknown}} {{known:1:2}}", text);
    }

    [Fact]
    public void ListNames_DoNotShadowBuiltInValues()
    {
        var values = new ParameterSet([new ParameterList { Name = "vu", Values = ["list"] }]).First();

        Assert.Equal("7 list", TemplateRenderer.Render("{{$vu}} {{vu}}", new TemplateContext(7, 1, values)));
    }

    [Theory]
    [InlineData("", "needs a name")]
    [InlineData("user ids", "letters, digits and underscores")]
    [InlineData("1st", "letters, digits and underscores")]
    public void Validate_RejectsBadNames(string name, string expected)
    {
        var plan = Plan();
        plan.Parameters[0].Name = name;

        Assert.Contains(plan.Validate(), p => p.Contains(expected));
    }

    [Fact]
    public void Validate_RejectsEmptyAndDuplicateLists()
    {
        var plan = Plan();
        plan.Parameters[1].Values = [];
        plan.Parameters.Add(new ParameterList { Name = "USERIDS", Values = ["1"] });

        var problems = plan.Validate();

        Assert.Contains(problems, p => p.Contains("'region' needs at least one value"));
        Assert.Contains(problems, p => p.Contains("more than one parameter list named 'USERIDS'"));
    }

    [Fact]
    public void Validate_ChecksUrlsWithTheFirstValue()
    {
        var plan = Plan();
        plan.Requests[0].Url = "{{baseUrl}}/users";
        plan.Parameters.Add(new ParameterList { Name = "baseUrl", Values = ["https://api.test"] });
        Assert.Empty(plan.Validate());

        plan.Parameters[^1].Values = ["not a url"];
        Assert.Contains(plan.Validate(), p => p.Contains("absolute http://"));
    }

    [Fact]
    public async Task Probe_UsesTheFirstValue()
    {
        var plan = Plan();
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var result = await RequestProbe.SendAsync(plan.Requests[0], plan.Profile, new HttpMessageInvoker(handler), parameters: plan.Parameters);

        Assert.True(result.Success);
        var sent = Assert.Single(handler.Received);
        Assert.Equal("/users/17", sent.Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void PlanFile_RoundTripsListsAndReadsNumbersAsText()
    {
        var import = PlanFile.Read(PlanFile.Write([Plan(ParameterSelection.Random)]));
        var list = Assert.Single(import.Plans).Parameters[0];
        Assert.Equal("userIds", list.Name);
        Assert.Equal(["17", "42", "99"], list.Values);
        Assert.Equal(ParameterSelection.Random, list.Selection);

        var handWritten = """
            { "requests": [{ "url": "http://api.test/users/{{ids}}" }],
              "parameters": [{ "name": "ids", "values": [17, 4.5, true, "x"] }] }
            """;
        var read = PlanFile.Read(handWritten);
        Assert.True(read.Succeeded, string.Join(" ", read.Problems));
        var ids = Assert.Single(Assert.Single(read.Plans).Parameters);
        Assert.Equal(["17", "4.5", "true", "x"], ids.Values);
        Assert.Equal(ParameterSelection.Sequential, ids.Selection);
    }

    [Fact]
    public void PlansSavedBeforeLists_LoadWithNone()
    {
        var old = """[{ "id": "7b1c1d3e-0000-0000-0000-000000000001", "name": "Old", "requests": [{ "url": "http://api.test/" }], "profile": {} }]""";

        var plans = JsonSerializer.Deserialize<List<LoadTestPlan>>(old, PlanFile.JsonOptions)!;

        Assert.Empty(Assert.Single(plans).Parameters);
        Assert.Empty(plans[0].Validate());
    }
}
