using System.Reflection;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Reports;
using FalconLoad.Core.Telemetry;

namespace FalconLoad.Cli;

/// <summary>Where the CLI reads and writes. Tests pass string readers and writers instead of the console.</summary>
/// <param name="ErrorIsTerminal">When true, live progress rewrites one line; otherwise it prints a line now and then.</param>
/// <param name="Secrets">
/// Secret variables from the process environment (<c>FALCON_LOAD_TOKEN</c> supplies <c>{{token}}</c>), by variable name.
/// </param>
/// <param name="EnvironmentVariables">Other settings from the process environment, such as <c>INFLUX_TOKEN</c>; null reads none.</param>
internal sealed record CliConsole(
    TextReader In,
    TextWriter Out,
    TextWriter Error,
    bool ErrorIsTerminal,
    IReadOnlyDictionary<string, string>? Secrets = null,
    Func<string, string?>? EnvironmentVariables = null)
{
    public const string InfluxTokenVariable = "INFLUX_TOKEN";
    public const string OtlpHeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>Environment variables with this prefix supply secret values, so a CI job can pass tokens without a file.</summary>
    public const string SecretPrefix = "FALCON_LOAD_";
}

/// <summary>Runs the tests in an exported plan file without the web UI, for CI pipelines and containers.</summary>
internal static class CliApp
{
    public const int Passed = 0;
    public const int TestFailed = 1;
    public const int InvalidInput = 2;
    public const int Interrupted = 130;

    /// <param name="createInvoker">Builds the HTTP invoker for a plan; null uses the engine's own handler. Tests pass a fake.</param>
    /// <param name="telemetryInvoker">Sends the metric pushes of --influx and --otlp; null uses a plain HTTP client.</param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliConsole console,
        Func<LoadTestPlan, HttpMessageInvoker>? createInvoker = null,
        TimeSpan? progressInterval = null,
        CancellationToken cancellationToken = default,
        HttpMessageInvoker? telemetryInvoker = null)
    {
        var options = CliOptions.Parse(args, out var error);
        if (options is null)
        {
            await console.Error.WriteLineAsync($"falcon-load: {error}");
            await console.Error.WriteLineAsync("Run 'falcon-load --help' for usage.");
            return InvalidInput;
        }

        switch (options.Command)
        {
            case CliCommand.Help:
                await console.Out.WriteLineAsync(CliOptions.Usage);
                return Passed;
            case CliCommand.Version:
                await console.Out.WriteLineAsync($"falcon-load {Version}");
                return Passed;
        }

        var telemetry = TelemetryFrom(options, console);
        if (telemetry?.Validate() is { Count: > 0 } telemetryProblems)
        {
            await WriteProblemsAsync(console, "The metrics export options are invalid:", telemetryProblems);
            return InvalidInput;
        }

        var plans = await LoadPlansAsync(options, console);
        if (plans is null)
        {
            return InvalidInput;
        }

        var outputs = OutputPaths(options.Out, plans.Select(p => p.Plan).ToList(), ".json");
        var htmlOutputs = OutputPaths(options.Html, plans.Select(p => p.Plan).ToList(), ".html");
        var junit = new List<(RunExport, RunVerdict)>();
        var machine = string.IsNullOrWhiteSpace(options.Machine) ? Environment.MachineName : options.Machine;

        // With --influx or --otlp, the test that is running is pushed in the background, and each test once more when it ends.
        Func<RunTelemetry?> current = () => null;
        using var ownedHttp = telemetry is not null && telemetryInvoker is null ? new HttpClient() : null;
        var publisher = telemetry is null ? null : new TelemetryPublisher(
            () => telemetry,
            () => current() is { } run ? [run] : [],
            telemetryInvoker ?? ownedHttp!,
            machine,
            Version,
            message => console.Error.WriteLine($"Metrics export: {message}"));
        using var stopPublishing = new CancellationTokenSource();
        var publishing = publisher?.RunAsync(stopPublishing.Token) ?? Task.CompletedTask;

        var exitCode = Passed;
        var written = true;
        for (var i = 0; i < plans.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var (plan, environment) = plans[i];
            var invoker = createInvoker?.Invoke(plan);
            var runId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow;
            LoadTestSnapshot snapshot;
            ResultHistograms histograms;
            LoadTestPlan ran;
            await using (var runner = new LoadTestRunner(plan, invoker, environment))
            {
                if (plans.Count > 1 || !options.Quiet)
                {
                    await console.Error.WriteLineAsync($"Running '{plan.Name}': {ReportFormat.DescribeProfile(plan.Profile)}");
                }

                var progress = options.Quiet ? null : new ProgressPrinter(console);
                current = () => RunTelemetry.OnOneMachine(runId, plan.Name, startedAt, runner.GetTelemetry(machine));
                await runner.RunAsync(progress is null ? null : progress.Report, progressInterval ?? TimeSpan.FromSeconds(1), cancellationToken);
                progress?.Finish();
                (snapshot, histograms) = runner.GetResults();
                var final = RunTelemetry.OnOneMachine(runId, plan.Name, startedAt, MachineTelemetry.From(machine, snapshot, histograms));
                current = () => final;

                // The runner's copy records the environment's name (never its values) for the export.
                ran = runner.Plan;
            }

            invoker?.Dispose();
            if (publisher is not null)
            {
                await publisher.PushAsync(CancellationToken.None);
            }

            var verdict = RunVerdict.Evaluate(snapshot, options.MaxErrorRate);
            var failures = verdict.Failures;
            await console.Out.WriteAsync(Summary.Write(plan, snapshot, failures, options.MaxErrorRate));

            var export = new RunExport
            {
                Id = runId,
                StartedAt = startedAt,
                Machine = machine,
                Tags = options.Tags,
                Note = options.Note,
                Plan = ran,
                Results = snapshot,
                Histograms = histograms,
            };

            if (outputs is not null)
            {
                written &= await WriteAsync(outputs[i], JsonSerializer.Serialize(export, PlanFile.JsonOptions), console, "Results");
            }

            if (htmlOutputs is not null)
            {
                written &= await WriteAsync(htmlOutputs[i], HtmlReport.Render(export, verdict), console, "Report");
            }

            junit.Add((export, verdict));

            if (snapshot.State == RunState.Cancelled)
            {
                exitCode = Interrupted;
            }
            else if (failures.Count > 0 && exitCode == Passed)
            {
                exitCode = TestFailed;
            }
        }

        // Written after the loop so one file covers every test, also when an interrupt skipped the last ones.
        if (options.Junit is { } junitPath && junit.Count > 0)
        {
            written &= await WriteAsync(junitPath, JUnitReport.Render(junit), console, "JUnit report");
        }

        await stopPublishing.CancelAsync();
        await publishing;

        if (cancellationToken.IsCancellationRequested)
        {
            await console.Error.WriteLineAsync(plans.Count > 1 ? "Interrupted; tests that had not started were skipped." : "Interrupted.");
            return Interrupted;
        }

        // A CI job that asked for a report has to notice when there is none, even if every test passed.
        return written || exitCode != Passed ? exitCode : TestFailed;
    }

    private static async Task<IReadOnlyList<(LoadTestPlan Plan, TestEnvironment? Environment)>?> LoadPlansAsync(CliOptions options, CliConsole console)
    {
        IReadOnlyList<TestEnvironment> environments = [];
        if (options.EnvironmentFile is { } environmentFile)
        {
            string environmentJson;
            try
            {
                environmentJson = await File.ReadAllTextAsync(environmentFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await console.Error.WriteLineAsync($"falcon-load: Can't read '{environmentFile}': {ex.Message}");
                return null;
            }

            var read = EnvironmentFile.Read(environmentJson);
            if (!read.Succeeded)
            {
                await WriteProblemsAsync(console, $"'{environmentFile}' can't be used:", read.Problems);
                return null;
            }

            environments = read.Environments;
        }

        string json;
        try
        {
            json = options.File == "-" ? await console.In.ReadToEndAsync() : await File.ReadAllTextAsync(options.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await console.Error.WriteLineAsync($"falcon-load: Can't read '{options.File}': {ex.Message}");
            return null;
        }

        var import = PlanFile.Read(json);
        if (!import.Succeeded)
        {
            await WriteProblemsAsync(console, $"'{options.File}' can't be run:", import.Problems);
            return null;
        }

        var plans = import.Plans.ToList();
        if (options.Test is { } name)
        {
            plans = plans.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (plans.Count == 0)
            {
                await WriteProblemsAsync(console, $"There is no test named '{name}' in the file. It holds:", import.Plans.Select(p => p.Name));
                return null;
            }
        }

        var problems = new List<string>();
        if (!await ApplyDataFilesAsync(plans, options, console, problems))
        {
            return null;
        }

        var runs = new List<(LoadTestPlan, TestEnvironment?)>();
        foreach (var plan in plans)
        {
            ApplyOverrides(plan.Profile, options);
            if (StagedOverrideProblem(plan, options) is { } staged)
            {
                problems.Add(plans.Count == 1 ? staged : $"{plan.Name}: {staged}");
                continue;
            }

            var environment = PickEnvironment(plan, environments, options, console.Secrets, out var problem);
            if (problem is not null)
            {
                problems.Add(problem);
                continue;
            }

            problems.AddRange(plan.ValidateForRun(environment).Select(p => plans.Count == 1 ? p : $"{plan.Name}: {p}"));
            runs.Add((plan, environment));
        }

        if (problems.Count > 0)
        {
            await WriteProblemsAsync(console, "The test can't run:", problems);
            if (problems.Any(p => p.Contains("No value for", StringComparison.Ordinal)))
            {
                await console.Error.WriteLineAsync(
                    $"Variables come from --env-file, --var name=value, or {CliConsole.SecretPrefix}<name> environment variables for secrets.");
            }

            return null;
        }

        return runs;
    }

    /// <summary>
    /// Replaces the rows of the data files named with --data by the contents of the given files. A name that no test
    /// uses is a mistake, such as a misspelling, so it is reported rather than ignored.
    /// </summary>
    private static async Task<bool> ApplyDataFilesAsync(List<LoadTestPlan> plans, CliOptions options, CliConsole console, List<string> problems)
    {
        foreach (var (name, path) in options.DataFiles)
        {
            var targets = plans.SelectMany(p => p.DataFiles).Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (targets.Count == 0)
            {
                var known = plans.SelectMany(p => p.DataFiles).Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                problems.Add(known.Count == 0
                    ? $"--data {name}: no test in the file has a data file to replace."
                    : $"--data {name}: no test has a data file with that name. The data files are {string.Join(", ", known.Select(k => $"'{k}'"))}.");
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                await console.Error.WriteLineAsync($"falcon-load: Can't read '{path}': {ex.Message}");
                return false;
            }

            foreach (var file in targets)
            {
                file.Content = content;
                file.Format = DataFile.DetectFormat(path, content);
                file.OmittedRows = 0;
            }
        }

        return true;
    }

    /// <summary>
    /// The environment a plan runs with: the one named by --env or the plan, from the environments file, with secrets from
    /// the process environment and --var values laid over it. Returns null when nothing supplies any variable.
    /// </summary>
    internal static TestEnvironment? PickEnvironment(
        LoadTestPlan plan,
        IReadOnlyList<TestEnvironment> environments,
        CliOptions options,
        IReadOnlyDictionary<string, string>? secrets,
        out string? problem)
    {
        problem = null;
        var name = options.Environment ?? plan.Environment;
        TestEnvironment? environment = null;
        if (name is not null)
        {
            environment = environments.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))?.Clone();
            if (environment is null && (options.Environment is not null || environments.Count > 0))
            {
                problem = environments.Count == 0
                    ? $"{plan.Name}: the environment '{name}' needs an environments file; pass --env-file."
                    : $"{plan.Name}: there is no environment named '{name}' in the environments file. It holds: {string.Join(", ", environments.Select(e => e.Name))}.";
                return null;
            }
        }
        else if (environments.Count == 1)
        {
            // With a single environment in the file there is nothing to choose between.
            environment = environments[0].Clone();
        }

        if ((secrets is null || secrets.Count == 0) && options.Variables.Count == 0)
        {
            return environment;
        }

        environment ??= new TestEnvironment { Name = name ?? "command line" };
        foreach (var (key, value) in secrets ?? new Dictionary<string, string>())
        {
            Set(environment, key, value, secret: true);
        }

        foreach (var (key, value) in options.Variables)
        {
            Set(environment, key, value, secret: false);
        }

        return environment;

        static void Set(TestEnvironment environment, string name, string value, bool secret)
        {
            var variable = environment.Variables.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (variable is null)
            {
                environment.Variables.Add(new EnvironmentVariable { Name = name, Value = value, Secret = secret });
            }
            else
            {
                variable.Value = value;

                // A value that was a secret in the file stays masked, whichever way it is overridden.
                variable.Secret |= secret;
            }
        }
    }

    /// <summary>
    /// A staged test lasts as long as its stages and sets its own load, so an option that would change either is refused
    /// rather than ignored: a run that quietly differs from what was asked for is worse than an error.
    /// </summary>
    private static string? StagedOverrideProblem(LoadTestPlan plan, CliOptions options)
    {
        var profile = plan.Profile;
        if (!profile.IsStaged)
        {
            return null;
        }

        if (options.DurationSeconds is not null || options.TotalRequests is not null)
        {
            return "This test uses load stages, which set how long it runs, so --duration and --requests don't apply.";
        }

        return options.Concurrency is not null && profile.StageMode == StageMode.VirtualUsers
            ? "This test uses load stages that set the number of virtual users, so --concurrency doesn't apply."
            : null;
    }

    private static void ApplyOverrides(LoadProfile profile, CliOptions options)
    {
        if (options.Concurrency is { } concurrency)
        {
            profile.Concurrency = concurrency;
        }

        if (options.DurationSeconds is { } duration)
        {
            profile.StopCondition = StopCondition.Duration;
            profile.DurationSeconds = duration;
        }

        if (options.TotalRequests is { } requests)
        {
            profile.StopCondition = StopCondition.RequestCount;
            profile.TotalRequests = requests;
        }
    }

    /// <summary>Writes an output file; returns false, having said why, when it can't be written.</summary>
    private static async Task<bool> WriteAsync(string path, string content, CliConsole console, string what)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, content, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await console.Error.WriteLineAsync($"falcon-load: {what} couldn't be written to '{path}': {ex.Message}");
            return false;
        }

        await console.Error.WriteLineAsync($"{what} written to {path}");
        return true;
    }

    /// <summary>One file per test: the given path for a single test, otherwise files named after the tests in that directory.</summary>
    internal static IReadOnlyList<string>? OutputPaths(string? output, IReadOnlyList<LoadTestPlan> plans, string extension)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        if (plans.Count == 1)
        {
            return [output];
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var plan in plans)
        {
            var slug = Slug(plan.Name);
            var candidate = slug;
            for (var n = 2; !used.Add(candidate); n++)
            {
                candidate = $"{slug}-{n}";
            }

            paths.Add(Path.Combine(output, candidate + extension));
        }

        return paths;
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "test" : slug;
    }

    private static async Task WriteProblemsAsync(CliConsole console, string heading, IEnumerable<string> problems)
    {
        await console.Error.WriteLineAsync($"falcon-load: {heading}");
        foreach (var problem in problems)
        {
            await console.Error.WriteLineAsync($"  - {problem}");
        }
    }

    /// <summary>The metrics export asked for with --influx and --otlp, or null for none.</summary>
    internal static TelemetrySettings? TelemetryFrom(CliOptions options, CliConsole console)
    {
        if (options.InfluxUrl is null && options.OtlpEndpoint is null)
        {
            return null;
        }

        var settings = new TelemetrySettings { PrometheusEnabled = false, PushIntervalSeconds = options.MetricsIntervalSeconds ?? 5 };
        if (options.InfluxUrl is not null)
        {
            settings.InfluxDb = new InfluxDbSettings
            {
                Enabled = true,
                Url = options.InfluxUrl,
                Bucket = options.InfluxBucket ?? string.Empty,
                Organization = options.InfluxOrganization,
                Token = console.EnvironmentVariables?.Invoke(CliConsole.InfluxTokenVariable) ?? string.Empty,
            };
        }

        if (options.OtlpEndpoint is not null)
        {
            settings.Otlp = new OtlpSettings
            {
                Enabled = true,
                Endpoint = options.OtlpEndpoint,
                Headers = ParseOtlpHeaders(console.EnvironmentVariables?.Invoke(CliConsole.OtlpHeadersVariable)),
            };
        }

        return settings;
    }

    /// <summary>Reads headers in the OpenTelemetry format, <c>name=value,name=value</c> with percent-encoded values.</summary>
    internal static List<TelemetryHeader> ParseOtlpHeaders(string? text)
    {
        var headers = new List<TelemetryHeader>();
        foreach (var pair in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = pair.IndexOf('=');
            headers.Add(equals <= 0
                ? new TelemetryHeader { Name = pair }
                : new TelemetryHeader { Name = pair[..equals].Trim(), Value = Uri.UnescapeDataString(pair[(equals + 1)..].Trim()) });
        }

        return headers;
    }

    private static string Version =>
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    /// <summary>
    /// Prints live progress to standard error, so standard output carries only the summary. On a terminal it rewrites
    /// one line; in a log it prints a line every few reports so the log stays short.
    /// </summary>
    private sealed class ProgressPrinter(CliConsole console)
    {
        private const int ReportsPerLogLine = 5;

        private int _reports;
        private int _width;

        public void Report(LoadTestSnapshot snapshot)
        {
            if (snapshot.IsFinished)
            {
                return;
            }

            var line = Summary.ProgressLine(snapshot);
            if (console.ErrorIsTerminal)
            {
                console.Error.Write("\r" + line.PadRight(_width));
                _width = line.Length;
            }
            else if (++_reports % ReportsPerLogLine == 0)
            {
                console.Error.WriteLine(line);
            }
        }

        public void Finish()
        {
            if (console.ErrorIsTerminal && _width > 0)
            {
                console.Error.Write("\r" + new string(' ', _width) + "\r");
            }
        }
    }
}
