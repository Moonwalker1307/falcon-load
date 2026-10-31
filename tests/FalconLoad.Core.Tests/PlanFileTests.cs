using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class PlanFileTests
{
    private static LoadTestPlan SamplePlan() => new()
    {
        Name = "Orders",
        Requests =
        [
            new RequestDefinition
            {
                Name = "Create order",
                Method = "POST",
                Url = "https://localhost:5001/api/orders?page={{$randomInt:1:20}}",
                Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer abc" }, new HeaderEntry { Name = "X-Off", Value = "1", Enabled = false }],
                Body = "{ \"id\": \"{{$guid}}\" }",
                ExpectedStatusCodes = "201",
            },
        ],
        Profile = new LoadProfile { Concurrency = 25, StopCondition = StopCondition.RequestCount, TotalRequests = 500, RampUpSeconds = 5 },
    };

    [Fact]
    public void WriteThenRead_KeepsEverythingButTheId()
    {
        var original = SamplePlan();

        var import = PlanFile.Read(PlanFile.Write([original]));

        Assert.True(import.Succeeded);
        var plan = Assert.Single(import.Plans);
        Assert.NotEqual(original.Id, plan.Id);
        Assert.Equal("Orders", plan.Name);
        var request = Assert.Single(plan.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(original.Requests[0].Url, request.Url);
        Assert.Equal(original.Requests[0].Body, request.Body);
        Assert.Equal("201", request.ExpectedStatusCodes);
        Assert.Equal(2, request.Headers.Count);
        Assert.False(request.Headers[1].Enabled);
        Assert.Equal(StopCondition.RequestCount, plan.Profile.StopCondition);
        Assert.Equal(500, plan.Profile.TotalRequests);
        Assert.Equal(25, plan.Profile.Concurrency);
    }

    [Fact]
    public void Write_UsesTheVersionedEnvelope()
    {
        var json = PlanFile.Write([SamplePlan()]);

        Assert.Contains("\"format\": \"falcon-load/plans\"", json);
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"stopCondition\": \"RequestCount\"", json);
    }

    [Fact]
    public void Read_AcceptsABarePlanAndABareArray()
    {
        const string plan = """{ "name": "Health", "requests": [ { "url": "https://localhost:5001/health" } ] }""";

        var single = PlanFile.Read(plan);
        var array = PlanFile.Read($"[{plan}, {plan}]");

        Assert.Equal("Health", Assert.Single(single.Plans).Name);
        Assert.Equal(2, array.Plans.Count);
        Assert.NotEqual(array.Plans[0].Id, array.Plans[1].Id);
    }

    [Fact]
    public void Read_FillsExplicitNullsWithDefaults()
    {
        var import = PlanFile.Read("""{ "name": null, "profile": null, "requests": [ { "method": null, "url": "https://localhost/", "headers": null } ] }""");

        Assert.True(import.Succeeded, string.Join("; ", import.Problems));
        var plan = Assert.Single(import.Plans);
        Assert.Equal("Imported test", plan.Name);
        Assert.Equal("GET", plan.Requests[0].Method);
        Assert.Empty(plan.Requests[0].Headers);
        Assert.Equal(new LoadProfile().Concurrency, plan.Profile.Concurrency);
    }

    [Fact]
    public void Read_RejectsTheWholeFileWhenOnePlanIsInvalid()
    {
        var good = SamplePlan();
        var bad = SamplePlan();
        bad.Name = "Broken";
        bad.Requests[0].Url = "ftp://example";

        var import = PlanFile.Read(PlanFile.Write([good, bad]));

        Assert.False(import.Succeeded);
        Assert.Empty(import.Plans);
        Assert.Contains(import.Problems, p => p.StartsWith("Broken: "));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "hello": "world" }""")]
    [InlineData("""{ "format": "falcon-load/plans", "version": 1, "plans": [] }""")]
    [InlineData("""{ "format": "falcon-load/plans", "version": 99, "plans": [] }""")]
    [InlineData("42")]
    public void Read_ExplainsFilesItCannotImport(string json)
    {
        var import = PlanFile.Read(json);

        Assert.False(import.Succeeded);
        Assert.NotEmpty(Assert.Single(import.Problems));
    }
}
