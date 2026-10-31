using System.Net;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Import;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class ExternalImportTests
{
    [Fact]
    public void Parse_RecognisesEachKind()
    {
        Assert.Equal(ImportKind.Curl, ImportSource.Parse("curl https://api.test/a").Kind);
        Assert.Equal(ImportKind.Curl, ImportSource.Parse("$ curl https://api.test/a").Kind);
        Assert.Equal(ImportKind.Postman, ImportSource.Parse("""{ "info": { "name": "x" }, "item": [ { "name": "a", "request": "https://api.test/a" } ] }""").Kind);
        Assert.Equal(ImportKind.OpenApi, ImportSource.Parse("openapi: 3.0.0\ninfo:\n  title: t\npaths:\n  /a:\n    get: {}\n").Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("{ \"name\": \"just json\" }")]
    [InlineData("{ not json")]
    public void Parse_RejectsEverythingElseWithAnImportException(string text) =>
        Assert.Throws<ImportException>(() => ImportSource.Parse(text));

    [Fact]
    public void ToPlan_IsValid_AndSurvivesAnExportRoundTrip()
    {
        var plan = ImportSource.Parse("curl -k -X POST https://api.test/a -H 'X-A: 1' -d '{}' -H 'content-type: application/json'").Build().ToPlan();

        Assert.Empty(plan.Validate());
        Assert.True(plan.Profile.IgnoreTlsErrors);
        var again = Assert.Single(PlanFile.Read(PlanFile.Write([plan])).Plans);
        Assert.Equal(plan.Requests[0].Body, again.Requests[0].Body);
    }

    [Fact]
    public void AddTo_AppendsRequests_AndKeepsExistingListsWithTheSameName()
    {
        var plan = new LoadTestPlan { Parameters = [new ParameterList { Name = "token", Values = ["mine"] }] };
        var result = ImportSource.Parse("""
            { "info": { "name": "x" }, "variable": [ { "key": "token", "value": "theirs" }, { "key": "host", "value": "https://api.test" } ],
              "item": [ { "name": "a", "request": "{{host}}/a" } ] }
            """).Build();

        var notes = result.AddTo(plan);

        Assert.Equal(2, plan.Requests.Count);
        Assert.Equal(["mine"], plan.Parameters.Single(p => p.Name == "token").Values);
        Assert.Contains(plan.Parameters, p => p.Name == "host");
        Assert.Single(notes);
    }

    [Fact]
    public async Task ImportedPlan_SendsWhatTheCurlCommandSaid()
    {
        var plan = ImportSource.Parse("curl -X POST https://api.test/orders -H 'Authorization: Bearer abc' -H 'Content-Type: application/json' -d '{\"n\":1}'").Build().ToPlan();
        plan.Profile.Concurrency = 1;
        plan.Profile.StopCondition = StopCondition.RequestCount;
        plan.Profile.TotalRequests = 3;
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler));

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, handler.Received.Count);
        var (request, body) = handler.Received.First();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("Bearer abc", request.Headers.Authorization!.ToString());
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"n\":1}", body);
    }
}
