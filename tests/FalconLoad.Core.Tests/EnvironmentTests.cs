using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class EnvironmentTests
{
    private const string Token = "s3cr3t-token-value";

    private static TestEnvironment Staging() => new()
    {
        Name = "Staging",
        Variables =
        [
            new EnvironmentVariable { Name = "baseUrl", Value = "http://staging.test" },
            new EnvironmentVariable { Name = "apiKey", Value = Token, Secret = true },
            new EnvironmentVariable { Name = "tenant", Value = "acme" },
        ],
    };

    private static LoadTestPlan Plan(int total = 3) => new()
    {
        Name = "Orders",
        Requests =
        [
            new RequestDefinition
            {
                Method = "POST",
                Url = "{{baseUrl}}/{{tenant}}/orders",
                Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer {{apiKey}}" }],
                Body = "{ \"tenant\": \"{{tenant}}\", \"id\": \"{{$guid}}\" }",
            },
        ],
        Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = total },
    };

    private static async Task<(FakeHandler Handler, LoadTestSnapshot Snapshot, LoadTestRunner Runner)> RunAsync(LoadTestPlan plan, TestEnvironment? environment, FakeHandler? handler = null)
    {
        handler ??= FakeHandler.Returning(HttpStatusCode.OK);
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), environment);
        var snapshot = await runner.RunAsync(cancellationToken: CancellationToken.None);
        return (handler, snapshot, runner);
    }

    [Fact]
    public async Task Variables_AreSubstitutedInUrlHeadersAndBody()
    {
        var (handler, _, _) = await RunAsync(Plan(), Staging());

        Assert.Equal(3, handler.Received.Count);
        foreach (var (request, body) in handler.Received)
        {
            Assert.Equal("http://staging.test/acme/orders", request.RequestUri!.ToString());
            Assert.Equal($"Bearer {Token}", request.Headers.GetValues("Authorization").Single());
            Assert.Contains("\"tenant\": \"acme\"", body);
        }

        // Dynamic values next to the variables are still rendered for every request.
        Assert.Equal(3, handler.Received.Select(r => r.Body).Distinct().Count());
    }

    [Fact]
    public async Task SameTest_RunsAgainstTheEnvironmentThatIsChosen()
    {
        var production = new TestEnvironment
        {
            Name = "Production",
            Variables = [new EnvironmentVariable { Name = "baseUrl", Value = "http://prod.test" }, new EnvironmentVariable { Name = "apiKey", Value = "other-key-1234", Secret = true }, new EnvironmentVariable { Name = "tenant", Value = "globex" }],
        };

        var (staging, _, stagingRunner) = await RunAsync(Plan(1), Staging());
        var (prod, _, prodRunner) = await RunAsync(Plan(1), production);

        Assert.Equal("staging.test", staging.Received.Single().Request.RequestUri!.Host);
        Assert.Equal("prod.test", prod.Received.Single().Request.RequestUri!.Host);
        Assert.Equal("Staging", stagingRunner.Plan.Environment);
        Assert.Equal("Production", prodRunner.Plan.Environment);
    }

    [Fact]
    public async Task ParameterList_WinsOverAnEnvironmentVariableOfTheSameName()
    {
        var plan = Plan(2);
        plan.Parameters = [new ParameterList { Name = "tenant", Values = ["from-list"] }];

        var (handler, _, _) = await RunAsync(plan, Staging());

        Assert.All(handler.Received, r => Assert.Equal("/from-list/orders", r.Request.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task VariableValues_MayUseDynamicValues()
    {
        var environment = new TestEnvironment { Name = "Dyn", Variables = [new EnvironmentVariable { Name = "baseUrl", Value = "http://api.test" }, new EnvironmentVariable { Name = "trace", Value = "t-{{$iteration}}" }] };
        var plan = new LoadTestPlan
        {
            Requests = [new RequestDefinition { Url = "{{baseUrl}}/x", Headers = [new HeaderEntry { Name = "X-Trace", Value = "{{trace}}" }] }],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 2 },
        };

        var (handler, _, _) = await RunAsync(plan, environment);

        Assert.Equal(["t-1", "t-2"], handler.Received.Select(r => r.Request.Headers.GetValues("X-Trace").Single()));
    }

    [Fact]
    public async Task ExtractedVariable_WinsOverAnEnvironmentVariableOfTheSameName()
    {
        var environment = new TestEnvironment
        {
            Name = "Dev",
            Variables = [new EnvironmentVariable { Name = "baseUrl", Value = "http://api.test" }, new EnvironmentVariable { Name = "token", Value = "from-environment", Secret = true }],
        };
        var plan = new LoadTestPlan
        {
            Requests =
            [
                new RequestDefinition
                {
                    Method = "POST",
                    Url = "{{baseUrl}}/login",
                    Extractions = [new ExtractionRule { Variable = "token", Expression = "$.token" }],
                    OncePerVirtualUser = true,
                },
                new RequestDefinition { Url = "{{baseUrl}}/orders", Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" }] },
            ],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 3 },
        };
        var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/login" ? "{ \"token\": \"from-login\" }" : "ok"),
        }));

        await RunAsync(plan, environment, handler);

        var orders = handler.Received.Where(r => r.Request.RequestUri!.AbsolutePath == "/orders").ToList();
        Assert.NotEmpty(orders);
        Assert.All(orders, r => Assert.Equal("Bearer from-login", r.Request.Headers.GetValues("Authorization").Single()));
    }

    [Fact]
    public void Saving_DoesNotNeedAnEnvironment_ButRunningDoes()
    {
        var plan = Plan();

        Assert.Empty(plan.Validate());

        var withoutEnvironment = plan.ValidateForRun(null);
        Assert.Contains(withoutEnvironment, p => p.Contains("{{baseUrl}}") && p.Contains("environment"));
        Assert.Empty(plan.ValidateForRun(Staging()));
        Assert.Throws<ArgumentException>(() => new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK))));
    }

    [Fact]
    public void Saving_StillRejectsAnUrlThatIsWrongWhateverTheEnvironment()
    {
        var plan = Plan();
        plan.Requests[0].Url = "ftp://{{baseUrl}}/orders";

        Assert.NotEmpty(plan.Validate());
    }

    [Fact]
    public async Task SecretValues_AreHiddenInErrorMessages()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException(HttpRequestError.Unknown, $"The server rejected key {Token} for the tenant"));

        var (_, snapshot, _) = await RunAsync(Plan(2), Staging(), handler);

        var error = Assert.Single(snapshot.Errors).Message;
        Assert.DoesNotContain(Token, error);
        Assert.Contains(SecretMasker.Mask, error);
        Assert.Contains("rejected key", error);
    }

    [Fact]
    public async Task Probe_UsesTheEnvironmentAndHidesSecretsTheServerEchoes()
    {
        var handler = new FakeHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"you sent {request.Headers.GetValues("Authorization").Single()}"),
            Headers = { { "X-Echo", Token } },
        }));

        var result = await RequestProbe.SendAsync(Plan().Requests[0], new LoadProfile(), new HttpMessageInvoker(handler), environment: Staging());

        Assert.True(result.Success);
        Assert.Equal($"Bearer {Token}", handler.Received.Single().Request.Headers.GetValues("Authorization").Single());
        Assert.Equal($"you sent Bearer {SecretMasker.Mask}", result.BodyPreview);
        Assert.Equal(SecretMasker.Mask, result.Headers.Single(h => h.Key == "X-Echo").Value);
    }

    [Fact]
    public async Task Probe_WithoutTheEnvironment_ExplainsWhatIsMissing()
    {
        var result = await RequestProbe.SendAsync(Plan().Requests[0], new LoadProfile(), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)));

        Assert.False(result.Success);
        Assert.Contains("{{baseUrl}}", result.Error);
    }

    [Fact]
    public void SecretMasker_HidesRawAndEscapedValues_LongestFirst()
    {
        var masker = new SecretMasker(["abc def", "abc", "ab", null, ""]);

        Assert.Equal("x ****** y", masker.Apply("x abc def y"));
        Assert.Equal("?k=****** and ******", masker.Apply("?k=abc%20def and abc"));
        Assert.Equal("a ab b", masker.Apply("a ab b"));
        Assert.Null(masker.Apply((string?)null));
        Assert.True(SecretMasker.None.IsEmpty);
    }

    [Fact]
    public void Redacted_LeavesOutSecrets_AndSaysWhichAreSet()
    {
        var environment = Staging();
        environment.Variables.Add(new EnvironmentVariable { Name = "unset", Value = "", Secret = true });

        var redacted = environment.Redacted();

        Assert.Null(redacted.Variables.Single(v => v.Name == "apiKey").Value);
        Assert.Equal(string.Empty, redacted.Variables.Single(v => v.Name == "unset").Value);
        Assert.Equal("http://staging.test", redacted.Variables.Single(v => v.Name == "baseUrl").Value);
        Assert.Equal(Token, environment.Variables.Single(v => v.Name == "apiKey").Value);
    }

    [Fact]
    public void ExportedFile_NeverContainsASecret_AndImportsWithTheSecretsEmpty()
    {
        var json = EnvironmentFile.Write([Staging()]);

        Assert.DoesNotContain(Token, json);
        Assert.Contains("\"apiKey\"", json);
        Assert.Contains("http://staging.test", json);

        var import = EnvironmentFile.Read(json);
        Assert.True(import.Succeeded);
        var imported = Assert.Single(import.Environments);
        Assert.Equal("Staging", imported.Name);
        Assert.NotEqual(Staging().Id, imported.Id);
        var key = imported.Variables.Single(v => v.Name == "apiKey");
        Assert.True(key.Secret);
        Assert.Equal(string.Empty, key.Value);
    }

    [Fact]
    public void Import_AcceptsABareEnvironmentAndAnArray_AndRejectsInvalidOnes()
    {
        const string one = """{ "name": "Dev", "variables": [{ "name": "baseUrl", "value": "http://dev.test" }] }""";

        Assert.Equal("Dev", Assert.Single(EnvironmentFile.Read(one).Environments).Name);
        Assert.Single(EnvironmentFile.Read($"[{one}]").Environments);

        var bad = EnvironmentFile.Read("""{ "name": "", "variables": [{ "name": "a b" }, { "name": "x" }, { "name": "X" }] }""");
        Assert.False(bad.Succeeded);
        Assert.Contains(bad.Problems, p => p.Contains("name"));
        Assert.Contains(bad.Problems, p => p.Contains("'a b'"));
        Assert.Contains(bad.Problems, p => p.Contains("more than one"));

        Assert.False(EnvironmentFile.Read("""{ "hello": 1 }""").Succeeded);
        Assert.False(EnvironmentFile.Read("not json").Succeeded);
        Assert.False(EnvironmentFile.Read("""{ "format": "falcon-load/environments", "version": 99, "environments": [] }""").Succeeded);
    }

    [Fact]
    public void Plans_SavedBeforeEnvironmentsExisted_StillLoad()
    {
        const string old = """{ "id": "6f7f8a64-3d47-4c5b-8d0d-2b1d9a2a8f10", "name": "Old", "requests": [{ "url": "http://api.test/" }], "profile": { "concurrency": 2 } }""";

        var plan = JsonSerializer.Deserialize<LoadTestPlan>(old, PlanFile.JsonOptions)!;

        Assert.Null(plan.Environment);
        Assert.Empty(plan.Validate());
    }

    [Fact]
    public void PlanFile_CarriesTheEnvironmentName_ButNoValues()
    {
        var plan = Plan();
        plan.Environment = "Staging";

        var json = PlanFile.Write([plan]);

        Assert.Contains("\"environment\": \"Staging\"", json);
        Assert.DoesNotContain(Token, json);
        Assert.Equal("Staging", PlanFile.Read(json).Plans.Single().Environment);
    }

    [Fact]
    public async Task Combine_WarnsWhenRunsUsedDifferentEnvironments()
    {
        async Task<RunExport> Export(TestEnvironment? environment)
        {
            var plan = Plan(4);
            var (_, snapshot, runner) = await RunAsync(plan, environment ?? Staging());
            var (finalSnapshot, histograms) = runner.GetResults();
            return new RunExport
            {
                Id = Guid.NewGuid(),
                StartedAt = DateTimeOffset.UtcNow,
                Machine = "m",
                Plan = runner.Plan,
                Results = finalSnapshot,
                Histograms = histograms,
            };
        }

        var production = new TestEnvironment { Name = "Production", Variables = Staging().Variables };
        var same = ResultCombiner.Combine([await Export(null), await Export(null)]);
        var different = ResultCombiner.Combine([await Export(null), await Export(production)]);

        Assert.DoesNotContain(same.Warnings, w => w.Contains("environments"));
        Assert.Contains(different.Warnings, w => w.Contains("Staging") && w.Contains("Production"));
    }
}
