using System.Globalization;

namespace FalconLoad.Cli;

internal enum CliCommand
{
    Run,
    Help,
    Version,
}

/// <summary>The parsed command line. Parsing is done by hand so the CLI needs no package.</summary>
internal sealed record CliOptions
{
    public const string Usage = """
        Usage: falcon-load run <file> [options]

        Runs the tests in an exported falcon-load file (or "-" to read it from standard input), prints a summary
        and exits with a non-zero code when a test fails.

        Options:
          --test <name>               Run only the test with this name. By default every test in the file runs, in order.
          --out <path>                Write the results as JSON, in the same format as an export from the web UI.
                                      When several tests run, <path> is a directory that gets one file per test.
          --html <path>               Write an HTML report of each test. When several tests run, <path> is a directory
                                      that gets one file per test.
          --junit <path>              Write the results as JUnit XML for a CI system's test report: one file, with a
                                      test suite per test and a test case per threshold.
          --concurrency <n>           Use this many virtual users instead of the number in the file.
          --duration <seconds>        Run each test for this long instead of the file's stop condition.
          --requests <n>              Send this many requests per test instead of the file's stop condition.
          --max-error-rate <percent>  Fail a test when more than this percentage of its requests fail. By default a
                                      test with thresholds passes or fails on them, and one without fails on any error.
          --env-file <path>           An environments file exported from the web UI, for tests that use {{variables}}.
          --env <name>                The environment to use. Default: the test's own environment, or the only one in the file.
          --var <name>=<value>        Set a variable, overriding the environment. Can be repeated.
          --data <name>=<path>        Use the CSV or JSON file at <path> instead of the rows saved in the test's data
                                      file called <name>, such as --data users.csv=./users.csv. Can be repeated.
          --machine <name>            The machine name recorded in the results. Default: this machine's name.
          --influx <url>              Write live metrics to InfluxDB at this address, e.g. http://localhost:8086. The token
                                      comes from the INFLUX_TOKEN environment variable.
          --influx-bucket <name>      The InfluxDB bucket (the database on InfluxDB 1.8 and 3). Needed with --influx.
          --influx-org <name>         The InfluxDB organization, for InfluxDB 2.
          --otlp <endpoint>           Send live metrics over OTLP/HTTP, e.g. http://localhost:4318. Headers such as
                                      Authorization come from OTEL_EXPORTER_OTLP_HEADERS (name=value,name=value).
          --metrics-interval <s>      Seconds between metric pushes. Default: 5.
          --quiet                     Print only the summary, without live progress.
          -h, --help                  Show this help.
          --version                   Show the version.

        Secrets: an environment variable FALCON_LOAD_<name> sets the secret variable <name>, such as FALCON_LOAD_token
        for {{token}}. Its value is masked in the output, and exports never contain it.

        Exit codes: 0 every test passed, 1 a test failed its thresholds or had too many errors, 2 the command line or the file is invalid,
        130 the run was interrupted.
        """;

    public CliCommand Command { get; init; } = CliCommand.Run;

    public string File { get; init; } = string.Empty;

    public string? Test { get; init; }

    public string? Out { get; init; }

    public string? Html { get; init; }

    public string? Junit { get; init; }

    public int? Concurrency { get; init; }

    public int? DurationSeconds { get; init; }

    public int? TotalRequests { get; init; }

    /// <summary>
    /// The allowed share of failed requests, from 0 to 1, or null when --max-error-rate wasn't given: then a test with
    /// thresholds is judged by them alone, and one without allows no failures.
    /// </summary>
    public double? MaxErrorRate { get; init; }

    public string? Machine { get; init; }

    /// <summary>The environments file given with --env-file.</summary>
    public string? EnvironmentFile { get; init; }

    /// <summary>The environment named with --env.</summary>
    public string? Environment { get; init; }

    /// <summary>Variables set with --var, in the order given; a later one wins.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Variables { get; init; } = [];

    /// <summary>Data files given with --data: the name of a data file in the test and the path of the file to use instead.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> DataFiles { get; init; } = [];

    public bool Quiet { get; init; }

    /// <summary>The InfluxDB address given with --influx.</summary>
    public string? InfluxUrl { get; init; }

    public string? InfluxBucket { get; init; }

    public string? InfluxOrganization { get; init; }

    /// <summary>The OTLP/HTTP endpoint given with --otlp.</summary>
    public string? OtlpEndpoint { get; init; }

    public int? MetricsIntervalSeconds { get; init; }

    /// <summary>Parses <paramref name="args"/>. Returns null and sets <paramref name="error"/> when they are invalid.</summary>
    public static CliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count == 0)
        {
            error = "Missing command.";
            return null;
        }

        switch (args[0])
        {
            case "-h" or "--help" or "help":
                return new CliOptions { Command = CliCommand.Help };
            case "--version":
                return new CliOptions { Command = CliCommand.Version };
            case "run":
                break;
            default:
                error = $"Unknown command '{args[0]}'.";
                return null;
        }

        var options = new CliOptions();
        string? file = null;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                return new CliOptions { Command = CliCommand.Help };
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "-")
            {
                if (file is not null)
                {
                    error = $"Unexpected argument '{arg}'. Only one test file can be given.";
                    return null;
                }

                file = arg;
                continue;
            }

            // Accept both "--name value" and "--name=value".
            var name = arg;
            string? inline = null;
            var equals = arg.IndexOf('=');
            if (equals > 0)
            {
                name = arg[..equals];
                inline = arg[(equals + 1)..];
            }

            if (name == "--quiet")
            {
                options = options with { Quiet = true };
                continue;
            }

            if (!ValueOptions.Contains(name))
            {
                error = $"Unknown option '{name}'.";
                return null;
            }

            string? value = inline;
            if (value is null)
            {
                if (i + 1 >= args.Count)
                {
                    error = $"{name} needs a value.";
                    return null;
                }

                value = args[++i];
            }

            switch (name)
            {
                case "--test":
                    options = options with { Test = value };
                    break;
                case "--out":
                    options = options with { Out = value };
                    break;
                case "--html":
                    options = options with { Html = value };
                    break;
                case "--junit":
                    options = options with { Junit = value };
                    break;
                case "--machine":
                    options = options with { Machine = value };
                    break;
                case "--env-file":
                    options = options with { EnvironmentFile = value };
                    break;
                case "--env":
                    options = options with { Environment = value };
                    break;
                case "--var":
                    var split = value.IndexOf('=');
                    if (split <= 0)
                    {
                        error = "--var needs a name and a value, such as --var baseUrl=http://localhost:5000.";
                        return null;
                    }

                    options = options with { Variables = [.. options.Variables, new(value[..split], value[(split + 1)..])] };
                    break;
                case "--data":
                    var separator = value.IndexOf('=');
                    if (separator <= 0 || separator == value.Length - 1)
                    {
                        error = "--data needs the name of a data file in the test and a path, such as --data users.csv=./users.csv.";
                        return null;
                    }

                    options = options with { DataFiles = [.. options.DataFiles, new(value[..separator], value[(separator + 1)..])] };
                    break;
                case "--influx":
                    options = options with { InfluxUrl = value };
                    break;
                case "--influx-bucket":
                    options = options with { InfluxBucket = value };
                    break;
                case "--influx-org":
                    options = options with { InfluxOrganization = value };
                    break;
                case "--otlp":
                    options = options with { OtlpEndpoint = value };
                    break;
                case "--metrics-interval":
                    if (!TryPositive(name, value, out var interval, out error))
                    {
                        return null;
                    }

                    options = options with { MetricsIntervalSeconds = interval };
                    break;
                case "--concurrency":
                    if (!TryPositive(name, value, out var concurrency, out error))
                    {
                        return null;
                    }

                    options = options with { Concurrency = concurrency };
                    break;
                case "--duration":
                    if (!TryPositive(name, value, out var duration, out error))
                    {
                        return null;
                    }

                    options = options with { DurationSeconds = duration };
                    break;
                case "--requests":
                    if (!TryPositive(name, value, out var requests, out error))
                    {
                        return null;
                    }

                    options = options with { TotalRequests = requests };
                    break;
                case "--max-error-rate":
                    var percent = value.TrimEnd('%');
                    if (!double.TryParse(percent, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) || rate is < 0 or > 100)
                    {
                        error = "--max-error-rate must be a percentage between 0 and 100.";
                        return null;
                    }

                    options = options with { MaxErrorRate = rate / 100 };
                    break;
                default:
                    error = $"Unknown option '{name}'.";
                    return null;
            }
        }

        if (file is null)
        {
            error = "Missing the test file to run.";
            return null;
        }

        if (options.DurationSeconds is not null && options.TotalRequests is not null)
        {
            error = "Use either --duration or --requests, not both.";
            return null;
        }

        if (options.InfluxUrl is null && (options.InfluxBucket ?? options.InfluxOrganization) is not null)
        {
            error = "--influx-bucket and --influx-org need --influx with the InfluxDB address.";
            return null;
        }

        if (options.InfluxUrl is not null && string.IsNullOrWhiteSpace(options.InfluxBucket))
        {
            error = "--influx needs --influx-bucket with the bucket to write to.";
            return null;
        }

        if (options.MetricsIntervalSeconds is not null && options.InfluxUrl is null && options.OtlpEndpoint is null)
        {
            error = "--metrics-interval needs --influx or --otlp.";
            return null;
        }

        return options with { File = file };
    }

    private static readonly HashSet<string> ValueOptions =
        [
            "--test", "--out", "--html", "--junit", "--machine", "--env-file", "--env", "--var", "--data", "--concurrency", "--duration", "--requests", "--max-error-rate",
            "--influx", "--influx-bucket", "--influx-org", "--otlp", "--metrics-interval",
        ];

    private static bool TryPositive(string name, string value, out int result, out string? error)
    {
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result > 0)
        {
            error = null;
            return true;
        }

        error = $"{name} must be a whole number greater than 0.";
        return false;
    }
}
