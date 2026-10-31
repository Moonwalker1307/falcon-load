using System.Reflection;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Cli;

/// <summary>Where the CLI reads and writes. Tests pass string readers and writers instead of the console.</summary>
/// <param name="ErrorIsTerminal">When true, live progress rewrites one line; otherwise it prints a line now and then.</param>
/// <param name="Secrets">
/// Secret variables from the process environment (<c>FALCON_LOAD_TOKEN</c> supplies <c>{{token}}</c>), by variable name.
/// </param>
internal sealed record CliConsole(
    TextReader In,
    TextWriter Out,
    TextWriter Error,
    bool ErrorIsTerminal,
    IReadOnlyDictionary<string, string>? Secrets = null)
{
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
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliConsole console,
        Func<LoadTestPlan, HttpMessageInvoker>? createInvoker = null,
        TimeSpan? progressInterval = null,
        CancellationToken cancellationToken = default)
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

        var plans = await LoadPlansAsync(options, console);
        if (plans is null)
        {
            return InvalidInput;
        }

        var outputs = OutputPaths(options.Out, plans.Select(p => p.Plan).ToList());
        var machine = string.IsNullOrWhiteSpace(options.Machine) ? Environment.MachineName : options.Machine;
        var exitCode = Passed;
        for (var i = 0; i < plans.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var (plan, environment) = plans[i];
            var invoker = createInvoker?.Invoke(plan);
            var startedAt = DateTimeOffset.UtcNow;
            LoadTestSnapshot snapshot;
            ResultHistograms histograms;
            LoadTestPlan ran;
            await using (var runner = new LoadTestRunner(plan, invoker, environment))
            {
                if (plans.Count > 1 || !options.Quiet)
                {
                    await console.Error.WriteLineAsync($"Running '{plan.Name}': {Describe(plan.Profile)}");
                }

                var progress = options.Quiet ? null : new ProgressPrinter(console);
                await runner.RunAsync(progress is null ? null : progress.Report, progressInterval ?? TimeSpan.FromSeconds(1), cancellationToken);
                progress?.Finish();
                (snapshot, histograms) = runner.GetResults();

                // The runner's copy records the environment's name (never its values) for the export.
                ran = runner.Plan;
            }

            invoker?.Dispose();

            var failures = Checks(snapshot, options.MaxErrorRate);
            await console.Out.WriteAsync(Summary.Write(plan, snapshot, failures, options.MaxErrorRate));

            if (outputs is not null)
            {
                var export = new RunExport
                {
                    Id = Guid.NewGuid(),
                    StartedAt = startedAt,
                    Machine = machine,
                    Plan = ran,
                    Results = snapshot,
                    Histograms = histograms,
                };

                var path = outputs[i];
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, PlanFile.JsonOptions), CancellationToken.None);
                await console.Error.WriteLineAsync($"Results written to {path}");
            }

            if (snapshot.State == RunState.Cancelled)
            {
                exitCode = Interrupted;
            }
            else if (failures.Count > 0 && exitCode == Passed)
            {
                exitCode = TestFailed;
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await console.Error.WriteLineAsync(plans.Count > 1 ? "Interrupted; tests that had not started were skipped." : "Interrupted.");
            return Interrupted;
        }

        return exitCode;
    }

    /// <summary>Why a finished test failed; empty when it passed.</summary>
    /// <param name="maxErrorRate">From --max-error-rate. Without it, a test without thresholds allows no failed requests.</param>
    internal static IReadOnlyList<string> Checks(LoadTestSnapshot snapshot, double? maxErrorRate)
    {
        var failures = new List<string>();

        // A test with thresholds is judged by them, the same verdict the web UI and the API show; --max-error-rate only
        // adds to them when it is given.
        foreach (var threshold in snapshot.Thresholds.Where(t => !t.Passed))
        {
            failures.Add(threshold.Actual is { } actual
                ? $"Threshold not met: {threshold.Name} (measured {Summary.Value(actual, threshold.Unit)})."
                : $"Threshold not met: {threshold.Name} (nothing was measured).");
        }

        var allowed = maxErrorRate ?? (snapshot.Thresholds.Count > 0 ? (double?)null : 0);
        if (snapshot.State == RunState.Failed)
        {
            failures.Add($"The test could not run: {snapshot.FailureReason}");
        }

        if (snapshot.TotalRequests == 0 && snapshot.State == RunState.Completed)
        {
            failures.Add("No requests completed.");
        }

        if (allowed is { } limit && snapshot.ErrorRate > limit)
        {
            failures.Add($"{Summary.Percent(snapshot.ErrorRate)} of requests failed, more than the {Summary.Percent(limit)} allowed.");
        }

        return failures;
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        var runs = new List<(LoadTestPlan, TestEnvironment?)>();
        foreach (var plan in plans)
        {
            ApplyOverrides(plan.Profile, options);
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

    /// <summary>One file per test: the given path for a single test, otherwise files named after the tests in that directory.</summary>
    internal static IReadOnlyList<string>? OutputPaths(string? output, IReadOnlyList<LoadTestPlan> plans)
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

            paths.Add(Path.Combine(output, candidate + ".json"));
        }

        return paths;
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "test" : slug;
    }

    private static string Describe(LoadProfile profile)
    {
        var stop = profile.StopCondition == StopCondition.Duration
            ? $"for {profile.DurationSeconds}s"
            : $"{profile.TotalRequests:#,0} requests";
        var users = profile.Concurrency == 1 ? "1 virtual user" : $"{profile.Concurrency:#,0} virtual users";
        return $"{users}, {stop}";
    }

    private static async Task WriteProblemsAsync(CliConsole console, string heading, IEnumerable<string> problems)
    {
        await console.Error.WriteLineAsync($"falcon-load: {heading}");
        foreach (var problem in problems)
        {
            await console.Error.WriteLineAsync($"  - {problem}");
        }
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
