using System.Net;
using System.Text.Json;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Tests;

namespace FalconLoad.Cli.Tests;

public sealed class CliAppTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "falcon-load-cli-" + Guid.NewGuid().ToString("N"));
    private readonly StringWriter _out = new();
    private readonly StringWriter _error = new();

    public CliAppTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static LoadTestPlan Plan(string name = "Smoke", int requests = 20) => new()
    {
        Name = name,
        Requests = [new RequestDefinition { Url = "http://api.test/items" }],
        Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    private string WriteFile(params LoadTestPlan[] plans)
    {
        var path = Path.Combine(_directory, "tests.json");
        File.WriteAllText(path, PlanFile.Write(plans));
        return path;
    }

    private Task<int> Run(FakeHandler handler, params string[] args) => RunWithInput(handler, string.Empty, args);

    private Task<int> RunWithInput(FakeHandler handler, string input, params string[] args) =>
        CliApp.RunAsync(
            args,
            new CliConsole(new StringReader(input), _out, _error, ErrorIsTerminal: false),
            _ => new HttpMessageInvoker(handler, disposeHandler: false));

    [Fact]
    public async Task PassingTest_ExitsWithZeroAndPrintsSummary()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var code = await Run(handler, "run", WriteFile(Plan()));

        Assert.Equal(CliApp.Passed, code);
        Assert.Equal(20, handler.Received.Count);
        var summary = _out.ToString();
        Assert.Contains("Smoke: completed", summary);
        Assert.Contains("Requests      20 (", summary);
        Assert.Contains("200 x 20", summary);
        Assert.Contains("Result        PASS", summary);
    }

    [Fact]
    public async Task FailedRequests_ExitWithOne()
    {
        var code = await Run(FakeHandler.Returning(HttpStatusCode.InternalServerError), "run", WriteFile(Plan()));

        Assert.Equal(CliApp.TestFailed, code);
        Assert.Contains("FAIL: 100% of requests failed, more than the 0% allowed.", _out.ToString());
    }

    [Fact]
    public async Task MaxErrorRate_AllowsSomeFailures()
    {
        // Every fourth request fails, so a quarter of them are errors.
        var count = 0;
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(
            Interlocked.Increment(ref count) % 4 == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)));
        var file = WriteFile(Plan());

        Assert.Equal(CliApp.Passed, await Run(handler, "run", file, "--max-error-rate", "30"));
        Assert.Equal(CliApp.TestFailed, await Run(handler, "run", file, "--max-error-rate=20%"));
    }

    [Fact]
    public async Task Out_WritesAnExportThatCanBeCombined()
    {
        var output = Path.Combine(_directory, "results", "smoke.json");

        var code = await Run(FakeHandler.Returning(HttpStatusCode.OK), "run", WriteFile(Plan()), "--out", output, "--machine", "runner-1");

        Assert.Equal(CliApp.Passed, code);
        var export = JsonSerializer.Deserialize<RunExport>(File.ReadAllText(output), PlanFile.JsonOptions)!;
        Assert.Equal("runner-1", export.Machine);
        Assert.Equal("Smoke", export.Plan.Name);
        Assert.Equal(20, export.Results.TotalRequests);
        Assert.Equal(RunState.Completed, export.Results.State);
        Assert.NotNull(export.Histograms);

        // A second machine's export of the same test is a distinct run with its own id.
        var combined = ResultCombiner.Combine([export, export with { Id = Guid.NewGuid(), Machine = "runner-2" }]);
        Assert.Equal(40, combined.Combined.TotalRequests);
    }

    [Fact]
    public async Task SeveralTests_RunInOrderAndWriteOneFileEach()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var output = Path.Combine(_directory, "results");

        var code = await Run(handler, "run", WriteFile(Plan("Browse", 5), Plan("Check out", 7)), "--out", output);

        Assert.Equal(CliApp.Passed, code);
        Assert.Equal(12, handler.Received.Count);
        Assert.True(File.Exists(Path.Combine(output, "browse.json")));
        Assert.True(File.Exists(Path.Combine(output, "check-out.json")));
        Assert.True(_out.ToString().IndexOf("Browse:", StringComparison.Ordinal) < _out.ToString().IndexOf("Check out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestOption_RunsOnlyTheNamedTest()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var code = await Run(handler, "run", WriteFile(Plan("Browse", 5), Plan("Check out", 7)), "--test", "check out");

        Assert.Equal(CliApp.Passed, code);
        Assert.Equal(7, handler.Received.Count);
        Assert.DoesNotContain("Browse:", _out.ToString());
    }

    [Fact]
    public async Task UnknownTestName_ListsTheTestsInTheFile()
    {
        var code = await Run(FakeHandler.Returning(HttpStatusCode.OK), "run", WriteFile(Plan("Browse")), "--test", "Nope");

        Assert.Equal(CliApp.InvalidInput, code);
        Assert.Contains("no test named 'Nope'", _error.ToString());
        Assert.Contains("- Browse", _error.ToString());
    }

    [Fact]
    public async Task Overrides_ReplaceTheFilesLoadProfile()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var output = Path.Combine(_directory, "out.json");

        var code = await Run(handler, "run", WriteFile(Plan()), "--requests", "3", "--concurrency", "1", "--out", output);

        Assert.Equal(CliApp.Passed, code);
        Assert.Equal(3, handler.Received.Count);
        var export = JsonSerializer.Deserialize<RunExport>(File.ReadAllText(output), PlanFile.JsonOptions)!;
        Assert.Equal(1, export.Plan.Profile.Concurrency);
    }

    [Fact]
    public async Task Stdin_IsReadWhenTheFileIsADash()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var code = await RunWithInput(handler, PlanFile.Write([Plan(requests: 4)]), "run", "-");

        Assert.Equal(CliApp.Passed, code);
        Assert.Equal(4, handler.Received.Count);
    }

    [Fact]
    public async Task InvalidPlan_ExitsWithTwoAndSendsNothing()
    {
        var plan = Plan();
        plan.Requests[0].Url = "not a url";
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var code = await Run(handler, "run", WriteFile(plan));

        Assert.Equal(CliApp.InvalidInput, code);
        Assert.Empty(handler.Received);
        Assert.Contains("can't be run", _error.ToString());
    }

    [Fact]
    public async Task MissingFile_ExitsWithTwo()
    {
        var code = await Run(FakeHandler.Returning(HttpStatusCode.OK), "run", Path.Combine(_directory, "missing.json"));

        Assert.Equal(CliApp.InvalidInput, code);
        Assert.Contains("Can't read", _error.ToString());
    }

    [Fact]
    public async Task Cancelling_StopsTheTestAndStillWritesResults()
    {
        using var stop = new CancellationTokenSource();
        var sent = 0;
        var handler = new FakeHandler((_, _) =>
        {
            if (Interlocked.Increment(ref sent) == 10)
            {
                stop.Cancel();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var plan = Plan();
        plan.Profile.StopCondition = StopCondition.Duration;
        plan.Profile.DurationSeconds = 600;
        var output = Path.Combine(_directory, "out.json");

        var code = await CliApp.RunAsync(
            ["run", WriteFile(plan), "--out", output],
            new CliConsole(new StringReader(string.Empty), _out, _error, ErrorIsTerminal: false),
            _ => new HttpMessageInvoker(handler, disposeHandler: false),
            cancellationToken: stop.Token);

        Assert.Equal(CliApp.Interrupted, code);
        Assert.Contains("INTERRUPTED", _out.ToString());
        var export = JsonSerializer.Deserialize<RunExport>(File.ReadAllText(output), PlanFile.JsonOptions)!;
        Assert.Equal(RunState.Cancelled, export.Results.State);
    }

    private string WriteEnvironments(params TestEnvironment[] environments)
    {
        var path = Path.Combine(_directory, "environments.json");

        // Written by hand rather than with EnvironmentFile.Write, which leaves secret values out as an export does.
        File.WriteAllText(path, JsonSerializer.Serialize(new EnvironmentFile { Environments = [.. environments] }, PlanFile.JsonOptions));
        return path;
    }

    private static TestEnvironment Environment(string name, params (string Name, string Value)[] variables) => new()
    {
        Name = name,
        Variables = [.. variables.Select(v => new EnvironmentVariable { Name = v.Name, Value = v.Value })],
    };

    private static LoadTestPlan PlanWithVariables(string? environment = null)
    {
        var plan = Plan(requests: 3);
        plan.Environment = environment;
        plan.Requests[0].Url = "{{baseUrl}}/items";
        plan.Requests[0].Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" }];
        return plan;
    }

    [Fact]
    public async Task EnvironmentFile_SuppliesTheTestsEnvironment()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var environments = WriteEnvironments(
            Environment("Local", ("baseUrl", "http://local.test"), ("token", "l")),
            Environment("Staging", ("baseUrl", "http://staging.test"), ("token", "s")));
        var output = Path.Combine(_directory, "out.json");

        var code = await Run(handler, "run", WriteFile(PlanWithVariables("Staging")), "--env-file", environments, "--out", output);

        Assert.Equal(CliApp.Passed, code);
        Assert.All(handler.Received, r => Assert.Equal("http://staging.test/items", r.Request.RequestUri!.ToString()));
        var export = JsonSerializer.Deserialize<RunExport>(File.ReadAllText(output), PlanFile.JsonOptions)!;
        Assert.Equal("Staging", export.Plan.Environment);
    }

    [Fact]
    public async Task EnvOption_PicksAnotherEnvironment()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var environments = WriteEnvironments(
            Environment("Local", ("baseUrl", "http://local.test"), ("token", "l")),
            Environment("Staging", ("baseUrl", "http://staging.test"), ("token", "s")));

        var code = await Run(handler, "run", WriteFile(PlanWithVariables("Staging")), "--env-file", environments, "--env", "local");

        Assert.Equal(CliApp.Passed, code);
        Assert.All(handler.Received, r => Assert.Equal("local.test", r.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task SecretsAndVars_FillVariablesAndSecretsStayOutOfTheResults()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);
        var output = Path.Combine(_directory, "out.json");
        var secrets = new Dictionary<string, string> { ["TOKEN"] = "s3cret-value" };

        var code = await CliApp.RunAsync(
            ["run", WriteFile(PlanWithVariables()), "--var", "baseUrl=http://vars.test", "--out", output],
            new CliConsole(new StringReader(string.Empty), _out, _error, ErrorIsTerminal: false, secrets),
            _ => new HttpMessageInvoker(handler, disposeHandler: false));

        Assert.Equal(CliApp.Passed, code);
        Assert.All(handler.Received, r =>
        {
            Assert.Equal("vars.test", r.Request.RequestUri!.Host);
            Assert.Equal("Bearer s3cret-value", r.Request.Headers.Authorization!.ToString());
        });
        Assert.DoesNotContain("s3cret-value", File.ReadAllText(output));
        Assert.DoesNotContain("s3cret-value", _out.ToString());
    }

    [Fact]
    public async Task MissingVariable_ExitsWithTwoAndSaysWhereVariablesComeFrom()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.OK);

        var code = await Run(handler, "run", WriteFile(PlanWithVariables()));

        Assert.Equal(CliApp.InvalidInput, code);
        Assert.Empty(handler.Received);
        Assert.Contains("baseUrl", _error.ToString());
        Assert.Contains("--env-file", _error.ToString());
    }

    [Fact]
    public async Task UnknownEnvironment_ExitsWithTwo()
    {
        var environments = WriteEnvironments(Environment("Local", ("baseUrl", "http://local.test")));

        var code = await Run(FakeHandler.Returning(HttpStatusCode.OK), "run", WriteFile(PlanWithVariables("Production")), "--env-file", environments);

        Assert.Equal(CliApp.InvalidInput, code);
        Assert.Contains("no environment named 'Production'", _error.ToString());
    }

    [Theory]
    [InlineData(new string[0], "Missing command.")]
    [InlineData(new[] { "go" }, "Unknown command 'go'.")]
    [InlineData(new[] { "run" }, "Missing the test file to run.")]
    [InlineData(new[] { "run", "a.json", "b.json" }, "Only one test file can be given.")]
    [InlineData(new[] { "run", "a.json", "--concurrency", "0" }, "--concurrency must be a whole number greater than 0.")]
    [InlineData(new[] { "run", "a.json", "--duration", "10", "--requests", "5" }, "Use either --duration or --requests, not both.")]
    [InlineData(new[] { "run", "a.json", "--max-error-rate", "150" }, "--max-error-rate must be a percentage between 0 and 100.")]
    [InlineData(new[] { "run", "a.json", "--out" }, "--out needs a value.")]
    [InlineData(new[] { "run", "a.json", "--fast" }, "Unknown option '--fast'.")]
    [InlineData(new[] { "run", "a.json", "--var", "novalue" }, "--var needs a name and a value")]
    public void Parse_RejectsInvalidArguments(string[] args, string expected)
    {
        Assert.Null(CliOptions.Parse(args, out var error));
        Assert.Contains(expected, error);
    }

    [Fact]
    public async Task Help_PrintsUsage()
    {
        var code = await Run(FakeHandler.Returning(HttpStatusCode.OK), "--help");

        Assert.Equal(CliApp.Passed, code);
        Assert.Contains("Usage: falcon-load run <file>", _out.ToString());
    }
}
