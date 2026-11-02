using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using FalconLoad.Cli;
using FalconLoad.Core.Distributed;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Grpc;
using FalconLoad.Core.Import;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Reports;
using FalconLoad.Core.Telemetry;
using FalconLoad.Core.Tests;

namespace FalconLoad.Fuzz;

/// <summary>
/// The surfaces that read text a user pastes, uploads or passes on the command line, and the engine that runs what
/// they produce. Each target states which exceptions its callers expect; anything else is a finding. Several also
/// check a promise the code makes, such as an export importing again, by throwing <see cref="InvariantException"/>.
/// </summary>
internal static class Targets
{
    private static readonly string[] ModelWords =
    [
        "name", "requests", "profile", "parameters", "dataFiles", "thresholds", "environment", "protoFiles", "auth",
        "protocol", "method", "url", "headers", "queryParameters", "body", "graphQL", "webSocket", "grpc", "contentType",
        "expectedStatusCodes", "assertions", "extractions", "oncePerVirtualUser", "enabled", "value", "values",
        "selection", "format", "content", "omittedRows", "concurrency", "stopCondition", "durationSeconds",
        "totalRequests", "rampUpSeconds", "maxRequestsPerSecond", "timeoutSeconds", "ignoreTlsErrors",
        "followRedirects", "stages", "stageMode", "virtualUserOffset", "target", "ramp", "metric", "operator",
        "requestIndex", "source", "variable", "expression", "type", "tokenUrl", "clientId", "clientSecret", "scope",
        "audience", "clientAuthentication", "variables", "operationName", "waitForReply", "closeAfter", "secret",
        "Http", "GraphQL", "WebSocket", "Grpc", "Duration", "RequestCount", "VirtualUsers", "RequestRate", "Csv", "Json",
        "Sequential", "Random", "Body", "JsonPath", "Header", "Regex", "Contains", "Equals", "Matches", "Exists",
        "NotExists", "LessThan", "GreaterThan", "ErrorRate", "P95Latency", "MaxLatency", "RequestsPerSecond",
        "OAuth2ClientCredentials", "BasicHeader", "RequestBody", "version", "plans", "environments", "GET", "POST",
        "HEAD", "OPTIONS", "TRACE", "CONNECT", "PATCH", "G E T", "wss://", "ws://", "grpc://",
    ];

    private static readonly SecretMasker NoSecrets = SecretMasker.None;

    private static List<FuzzTarget>? _all;

    public static IReadOnlyList<FuzzTarget> All => _all ??= Build();

    private static List<FuzzTarget> Build() =>
    [
        new()
        {
            Name = "plan-file",
            Description = "Importing a plan file: PlanFile.Read, then every check and copy of the plans it returns, and exporting them again.",
            Run = PlanFileRoundTrip,
            Seeds = Seeds.PlanFiles(),
            Dictionary = ModelWords,
        },
        new()
        {
            Name = "plan-run",
            Description = "Running an imported plan against a fake server that answers with random responses, then reporting on it.",
            Run = PlanRun,
            Seeds = Seeds.PlanFiles(),
            Dictionary = ModelWords,
            Slow = true,
        },
        new()
        {
            Name = "probe",
            Description = "\"Send once\" (RequestProbe.SendAsync) with each request of an imported plan, against a fake server.",
            Run = Probe,
            Seeds = Seeds.PlanFiles(),
            Dictionary = ModelWords,
            Slow = true,
        },
        new()
        {
            Name = "environment-file",
            Description = "Importing an environments file, its checks, redaction and export.",
            Run = EnvironmentRoundTrip,
            Seeds = Seeds.EnvironmentFiles(),
            Dictionary = ModelWords,
        },
        new()
        {
            Name = "import",
            Description = "The import page and /api/import: curl commands, Postman collections and OpenAPI specs in JSON or YAML.",
            Run = Import,
            Seeds = [.. Seeds.Curl, Seeds.Postman, Seeds.OpenApiYaml, Seeds.OpenApiJson],
            Dictionary =
            [
                "curl", "-H", "-d", "-X", "-u", "-b", "-F", "-G", "-I", "-k", "-L", "--data", "--data-raw", "--data-binary",
                "--data-urlencode", "--json", "--header", "--request", "--url", "--user", "--cookie", "--form", "$'", "\\\n",
                "@", "openapi", "swagger", "paths", "servers", "components", "$ref", "#/components/schemas/", "schema",
                "parameters", "requestBody", "content", "application/json", "properties", "items", "allOf", "oneOf",
                "anyOf", "enum", "example", "examples", "default", "format", "required", "in", "query", "path", "header",
                "cookie", "security", "securitySchemes", "item", "request", "url", "raw", "host", "variable", "auth",
                "bearer", "basic", "apikey", "mode", "urlencoded", "formdata", "graphql", "info", "schema", "- ", ": ",
                "|", ">", "{{", "}}", ":id",
            ],
            Expected = [typeof(ImportException)],
        },
        new()
        {
            Name = "yaml",
            Description = "The YAML reader behind OpenAPI import.",
            Run = text => SimpleYaml.Parse(text)?.ToJsonString() is { } json ? $"ok {json.Length / 100}" : "null",
            Seeds = Seeds.Yaml,
            Dictionary = ["- ", ": ", "? ", "|", "|-", "|+", ">", ">-", "&a ", "*a", "!!str ", "---", "...", "# ", "'", "\"", "[", "]", "{", "}", ",", "\n  ", "\t"],
            Expected = [typeof(FormatException)],
        },
        new()
        {
            Name = "csv",
            Description = "A CSV data file: parsing, the plan's checks of it, and writing it back out.",
            Run = text => DataFileRoundTrip(text, DataFormat.Csv),
            Seeds = Seeds.Csv,
            Dictionary = [",", ";", "\t", "\"", "\"\"", "\r\n", "\n", "\r", "\ufeff"],
        },
        new()
        {
            Name = "json-data",
            Description = "A JSON data file: parsing, the plan's checks of it, and writing it back out.",
            Run = text => DataFileRoundTrip(text, DataFormat.Json),
            Seeds = Seeds.JsonData,
            Dictionary = ["[", "]", "{", "}", ":", ",", "null", "true", "1.5e300", "\"\"", "\"a\""],
        },
        new()
        {
            Name = "jsonpath",
            Description = "A JSONPath expression (first line) used as an assertion and an extraction on a body (the rest).",
            Run = JsonPathTarget,
            Seeds = Seeds.JsonPathInputs,
            Dictionary = ["$", ".", "..", "[", "]", "[*]", "['", "']", "[\"", "\"]", "[-1]", "[0]", "*", "?", "(@", "length", "\\"],
        },
        new()
        {
            Name = "template",
            Description = "Dynamic values and {{variables}} in URLs, headers and bodies.",
            Run = Template,
            Seeds = Seeds.Templates,
            Dictionary = ["{{", "}}", "$", "$guid", "$randomInt", "$randomInt:", ":", "$timestamp", "$isoTimestamp", "$iteration", "$vu", "users", "token", "next", "email", " "],
        },
        new()
        {
            Name = "assertions",
            Description = "Assertions (a JSON list, first line) checked against a response body (the rest).",
            Run = AssertionsTarget,
            Seeds = Seeds.Assertions(),
            Dictionary = ModelWords,
        },
        new()
        {
            Name = "extractions",
            Description = "Extraction rules (a JSON list, first line) applied to a response body (the rest).",
            Run = ExtractionsTarget,
            Seeds = Seeds.Extractions(),
            Dictionary = ModelWords,
        },
        new()
        {
            Name = "status-codes",
            Description = "Expected status codes, for HTTP and gRPC.",
            Run = StatusCodes,
            Seeds = Seeds.Status,
            Dictionary = ["-", ",", " ", "OK", "CANCELLED", "UNKNOWN", "NOT_FOUND", "UNAVAILABLE", "100", "599", "16"],
        },
        new()
        {
            Name = "proto",
            Description = ".proto files (separated by a line of =====), and sample messages for every method they define.",
            Run = Proto,
            Seeds = [Seeds.ShopProto, Seeds.Proto2, Seeds.ShopProto + "\n=====\n" + Seeds.Proto2, "syntax = \"proto3\";\nimport \"b.proto\";\nmessage A { B b = 1; }\nservice S { rpc M (A) returns (B); }\n=====\nsyntax = \"proto3\";\nmessage B { map<string, A> as = 1; }"],
            Dictionary = ["syntax", "package", "import", "option", "message", "enum", "service", "rpc", "returns", "stream", "repeated", "optional", "required", "oneof", "map<", ">", "reserved", "to", "max", "extensions", "extend", "group", ";", "{", "}", "(", ")", "=", "[", "]", "\"", "'", "/*", "*/", "//", ".", "google.protobuf.", "int32", "int64", "uint64", "sint32", "fixed64", "double", "float", "bool", "string", "bytes"],
        },
        new()
        {
            Name = "protobuf-json",
            Description = "A gRPC message written as JSON (the message type on the first line), encoded and decoded again.",
            Run = ProtobufJson,
            Seeds = Seeds.ProtobufJson,
            Dictionary = ["\"NaN\"", "\"Infinity\"", "\"-Infinity\"", "1e400", "\"1.5s\"", "\"-0.000000001s\"", "\"9999-12-31T23:59:59.999999999Z\"", "\"0001-01-01T00:00:00Z\"", "\"AAE=\"", "\"AA-_\"", "\"GAMES\"", "{\"seconds\":", "\"nanos\":"],
            Expected = [typeof(ProtobufException)],
        },
        new()
        {
            Name = "protobuf-binary",
            Description = "A gRPC response body (Latin-1 bytes) decoded as each message and method of the shop schema.",
            Run = ProtobufBinary,
            Seeds = [.. Seeds.ProtobufJson.Select(EncodeSeed)],
            Dictionary = ["\u0000", "\u0001", "\u0008", "\u000a", "\u0012", "\u007f", "\u0080", "\u00ff", "\u00ff\u00ff\u00ff\u00ff\u000f", "\u0000\u0000\u0000\u0000\u0005"],
            Expected = [typeof(ProtobufException)],
        },
        new()
        {
            Name = "cli",
            Description = "falcon-load command lines (one argument per line), run against a fake server.",
            Run = CliTarget,
            Seeds = Seeds.Cli,
            Dictionary = ["run", "PLAN", "ENV", "DATA", "-", "--test", "--out", "--html", "--junit", "--concurrency", "--duration", "--requests", "--max-error-rate", "--env-file", "--env", "--var", "--data", "--machine", "--influx", "--influx-bucket", "--influx-org", "--otlp", "--metrics-interval", "--quiet", "--help", "--version", "=", "\n"],
            Slow = true,
        },
        new()
        {
            Name = "results",
            Description = "Exported results: combining, comparing, verdicts, HTML and JUnit reports and live metrics.",
            Run = Results,
            Seeds = ResultSeeds(),
            Dictionary = ["results", "histograms", "overall", "steps", "seconds", "offset", "counts", "count", "minMs", "maxMs", "sumMs", "latency", "timeline", "statusCodes", "errors", "thresholds", "checks", "state", "elapsed", "machine", "plan", "Completed", "Failed", "Running", "Cancelled"],
        },
        new()
        {
            Name = "telemetry",
            Description = "Live metrics settings: their checks, addresses and redaction.",
            Run = TelemetryTarget,
            Seeds = Seeds.Telemetry,
            Dictionary = ["prometheusEnabled", "influxDb", "otlp", "enabled", "url", "bucket", "organization", "token", "endpoint", "headers", "name", "value", "pushIntervalSeconds", "http://", "https://", "/api/v2/write", "/v1/metrics", "?", "#"],
        },
        new()
        {
            Name = "secret-masker",
            Description = "Masking secrets (one per line, the last line is the text): no secret may be left in the output.",
            Run = Masking,
            Seeds = Seeds.Masking,
            Dictionary = ["%", "%2F", "%25", "*", "******", "\n"],
        },
        new()
        {
            Name = "graphql",
            Description = "GraphQL bodies (query, variables, operation name) and finding errors in a response (the fourth line).",
            Run = GraphQLTarget,
            Seeds = Seeds.GraphQL,
            Dictionary = ["{", "}", "\"errors\"", "\"message\"", "\"data\"", "null", "[", "]"],
        },
    ];

    private static string Outcome(IReadOnlyCollection<string> problems) =>
        problems.Count == 0 ? "valid" : "invalid: " + string.Join(" | ", problems.Take(2).Select(p => p.Length > 40 ? p[..40] : p));

    private static string PlanFileRoundTrip(string input)
    {
        var import = PlanFile.Read(input);
        if (!import.Succeeded)
        {
            if (import.Problems.Count == 0)
            {
                throw new InvariantException("PlanFile.Read failed without saying why.");
            }

            return Outcome(import.Problems);
        }

        foreach (var plan in import.Plans)
        {
            _ = plan.ValidateForRun(null);
            _ = plan.ExtractedVariables();
            _ = plan.EnvironmentValues(null);
            var clone = plan.Clone();
            if (!clone.Validate().SequenceEqual(plan.Validate()))
            {
                throw new InvariantException("A clone of a plan validates differently from the plan.");
            }

            _ = LoadSplitter.Split(plan, [1, 3]);
            _ = HttpHandlerFactory.Create(plan.Profile);
            if (plan.Profile.IsStaged)
            {
                var schedule = new StageSchedule(plan.Profile.Stages);
                for (var t = -1.0; t <= schedule.TotalSeconds + 1; t += Math.Max(0.5, schedule.TotalSeconds / 7))
                {
                    _ = schedule.TargetAt(t);
                    _ = schedule.StageIndexAt(t);
                    _ = schedule.RequestsDueBy(t);
                    _ = schedule.DueSecondOfRequest(t * 3);
                }

                _ = schedule.Outline();
            }

            var copy = plan.Clone();
            copy.DropDataRows();
            _ = copy.Validate();
        }

        var exported = PlanFile.Write(import.Plans);
        var again = PlanFile.Read(exported);
        if (!again.Succeeded)
        {
            throw new InvariantException("Exported plans can't be imported again: " + again.Problems.FirstOrDefault());
        }

        return "imported " + Math.Min(import.Plans.Count, 3) + " " + Outcome(import.Plans.SelectMany(p => p.Validate()).ToList());
    }

    /// <summary>Clamps a plan to a few requests over a moment, so a run takes milliseconds whatever the file asked for.</summary>
    private static void Shrink(LoadTestPlan plan)
    {
        var profile = plan.Profile;
        profile.Concurrency = Math.Clamp(profile.Concurrency, 1, 3);
        profile.StopCondition = StopCondition.RequestCount;
        profile.TotalRequests = Math.Clamp(profile.TotalRequests, 1, 8);
        profile.DurationSeconds = 1;
        profile.RampUpSeconds = 0;
        profile.TimeoutSeconds = Math.Clamp(profile.TimeoutSeconds, 1, 2);
        if (profile.MaxRequestsPerSecond is > 0 and < 20)
        {
            profile.MaxRequestsPerSecond = 20;
        }

        if (profile.IsStaged)
        {
            profile.Stages = [.. profile.Stages.Take(2).Select(s => new LoadStage { DurationSeconds = 1, Target = Math.Clamp(s.Target, 0, 5), Ramp = s.Ramp })];
        }
    }

    private static TestEnvironment SecretEnvironment(string name) => new()
    {
        Name = name ?? "Staging",
        Variables =
        [
            new EnvironmentVariable { Name = "token", Value = "Tok3n-Secret-Value", Secret = true },
            new EnvironmentVariable { Name = "secret", Value = "Client-Secret-Value", Secret = true },
            new EnvironmentVariable { Name = "baseUrl", Value = "http://api.test" },
            new EnvironmentVariable { Name = "next", Value = "http://api.test/next" },
            new EnvironmentVariable { Name = "email", Value = "a@b.test" },
            new EnvironmentVariable { Name = "session", Value = "s" },
            new EnvironmentVariable { Name = "num", Value = "1" },
        ],
    };

    private static readonly string[] SecretValues = ["Tok3n-Secret-Value", "Client-Secret-Value"];

    private static void AssertNoSecrets(string text, string where)
    {
        foreach (var secret in SecretValues)
        {
            if (text.Contains(secret, StringComparison.Ordinal) || text.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal))
            {
                throw new InvariantException($"A secret value came out in {where}.");
            }
        }
    }

    private static string PlanRun(string input)
    {
        var import = PlanFile.Read(input);
        if (!import.Succeeded)
        {
            return "not imported";
        }

        var plan = import.Plans[0];
        Shrink(plan);
        var environment = SecretEnvironment(plan.Environment ?? "Staging");
        var problems = plan.ValidateForRun(environment);
        if (problems.Count > 0)
        {
            return Outcome(problems);
        }

        var handler = new RandomServer(input.GetHashCode());
        var webSockets = new FakeWebSocketConnector(message => message.Length % 3 == 0 ? null : "{\"echo\":" + JsonSerializer.Serialize(message) + "}");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        LoadTestSnapshot snapshot;
        ResultHistograms histograms;
        var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler, disposeHandler: false), environment, webSockets);
        try
        {
            snapshot = runner.RunAsync(cancellationToken: stop.Token).GetAwaiter().GetResult();
            (_, histograms) = runner.GetResults();
        }
        finally
        {
            runner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // A staged test runs for as long as its stages, whatever its request count says.
        if (!plan.Profile.IsStaged && snapshot.TotalRequests > Math.Max(plan.Profile.TotalRequests, 200))
        {
            throw new InvariantException($"A test with a request-count stop condition of {plan.Profile.TotalRequests} sent {snapshot.TotalRequests} requests.");
        }

        // Only a failed token request may stop a run; anything else that goes wrong fails a request, not the test.
        if (snapshot.State == RunState.Failed && plan.Auth?.IsEnabled != true)
        {
            throw new InvariantException($"A run failed: {snapshot.FailureReason}");
        }

        if (snapshot.SuccessfulRequests + snapshot.FailedRequests != snapshot.TotalRequests)
        {
            throw new InvariantException("Successful plus failed requests don't add up to the total.");
        }

        var export = new RunExport { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Machine = "fuzz", Plan = plan, Results = snapshot, Histograms = histograms };
        var verdict = RunVerdict.Evaluate(snapshot, null);
        var json = JsonSerializer.Serialize(export, PlanFile.JsonOptions);
        AssertNoSecrets(JsonSerializer.Serialize(snapshot, PlanFile.JsonOptions), "the results");
        AssertNoSecrets(HtmlReport.Render(export, verdict), "the HTML report");
        AssertNoSecrets(JUnitReport.Render(export, verdict), "the JUnit report");
        var back = JsonSerializer.Deserialize<RunExport>(json, PlanFile.JsonOptions)!;
        if (ResultCombiner.Validate([back, back]) is { Count: 0 })
        {
            _ = ResultCombiner.Combine([back, back]);
        }

        return $"{snapshot.State} {snapshot.FailedRequests > 0} {string.Join(",", snapshot.Errors.Select(e => e.Message.Length > 30 ? e.Message[..30] : e.Message).Take(2))}";
    }

    private static string Probe(string input)
    {
        var import = PlanFile.Read(input);
        if (!import.Succeeded)
        {
            return "not imported";
        }

        var plan = import.Plans[0];
        plan.Profile.TimeoutSeconds = Math.Clamp(plan.Profile.TimeoutSeconds, 1, 2);
        var environment = SecretEnvironment("Staging");
        var handler = new RandomServer(input.GetHashCode());
        var webSockets = new FakeWebSocketConnector(message => message);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var outcomes = new List<string>();
        foreach (var request in plan.Requests.Take(4))
        {
            var result = RequestProbe.SendAsync(
                request,
                plan.Profile,
                new HttpMessageInvoker(handler, disposeHandler: false),
                CancellationToken.None,
                plan.Parameters,
                variables,
                environment,
                plan.DataFiles,
                plan.Auth,
                webSockets,
                plan.ProtoFiles).GetAwaiter().GetResult();
            AssertNoSecrets(JsonSerializer.Serialize(result, PlanFile.JsonOptions), "a Send once error");
            outcomes.Add(result.Error is { } e ? e[..Math.Min(e.Length, 30)] : "ok");
        }

        return string.Join(",", outcomes);
    }

    private static string EnvironmentRoundTrip(string input)
    {
        var import = EnvironmentFile.Read(input);
        if (!import.Succeeded)
        {
            return Outcome(import.Problems);
        }

        foreach (var environment in import.Environments)
        {
            _ = environment.Validate();
            _ = environment.ToValues();
            var redacted = environment.Redacted();
            foreach (var variable in environment.Variables.Where(v => v.Secret && v.Value is { Length: > 0 }))
            {
                if (redacted.Variables.Any(r => r.Value == variable.Value && r.Value!.Length > 0 && r.Secret))
                {
                    throw new InvariantException("Redacted() kept a secret's value.");
                }
            }

            var masker = environment.CreateMasker();
            _ = masker.Apply(input);
            _ = environment.Clone().Validate();
        }

        var again = EnvironmentFile.Read(EnvironmentFile.Write(import.Environments));
        if (!again.Succeeded)
        {
            throw new InvariantException("Exported environments can't be imported again: " + again.Problems.FirstOrDefault());
        }

        return "imported " + Outcome(import.Environments.SelectMany(e => e.Validate()).ToList());
    }

    private static string Import(string input)
    {
        var source = ImportSource.Parse(input);
        var result = source.Build();
        if (source.OpenApi is { } spec)
        {
            var keys = spec.Operations.Select(o => o.Key).ToList();
            _ = source.Build(keys.Take(1), "http://localhost:9/base/");
            _ = source.Build([], null);
            _ = source.Build(["no such operation"], "not a url");
        }

        var plan = result.ToPlan();
        var problems = plan.Validate();
        var existing = Seeds.RichPlan();
        result.AddTo(existing);
        _ = existing.Validate();
        _ = PlanFile.Read(PlanFile.Write([plan]));
        return $"{source.Kind} {result.Requests.Count} {result.Warnings.Count > 0} {Outcome(problems)}";
    }

    private static string DataFileRoundTrip(string input, DataFormat format)
    {
        var file = new DataFile { Name = "data", Format = format, Content = input };
        var problems = file.Validate().ToList();
        _ = DataFile.DetectFormat("x.csv", input);
        _ = DataFile.DetectFormat(null, input);
        if (!file.TryGetTable(out var table, out var error))
        {
            if (string.IsNullOrEmpty(error))
            {
                throw new InvariantException("TryGetTable failed without saying why.");
            }

            return "unreadable: " + error[..Math.Min(error.Length, 30)];
        }

        // Only a file that passes its checks is ever copied with ToCsv (TakeEvery, for agents). Column names that
        // aren't variable names, such as ones with a delimiter or a byte order mark in them, needn't survive the copy.
        if (problems.Count > 0)
        {
            return $"{table.Columns.Count} {Outcome(problems)}";
        }

        var csv = table.ToCsv();
        DataTable back;
        try
        {
            back = DataTable.ParseCsv(csv);
        }
        catch (DataFormatException ex)
        {
            throw new InvariantException($"ToCsv() writes text ParseCsv can't read: {ex.Message}");
        }

        if (!back.Columns.SequenceEqual(table.Columns) || back.Rows.Count != table.Rows.Count
            || back.Rows.Zip(table.Rows).Any(p => !p.First.SequenceEqual(p.Second)))
        {
            throw new InvariantException("ToCsv() doesn't read back as the same rows.");
        }

        _ = file.TakeEvery(3, 1).Validate().ToList();
        _ = file.WithoutRows().Validate().ToList();
        var set = new ParameterSet([new ParameterList { Name = "list", Values = ["a"] }], [file]);
        var row = new string[set.Count];
        set.Next(row);
        set.Next(row);
        var first = set.First();
        foreach (var column in table.Columns)
        {
            _ = first.TryGet(column, out _);
        }

        var plan = new LoadTestPlan { Requests = [new RequestDefinition { Url = "http://api.test/" + string.Concat(table.Columns.Take(3).Select(c => "{{" + c + "}}")) }], DataFiles = [file] };
        _ = plan.Validate();
        return $"{table.Columns.Count} {Math.Min(table.Rows.Count, 5)} {Outcome(problems)}";
    }

    private static (string Head, string Tail) SplitFirstLine(string input)
    {
        var newline = input.IndexOf('\n');
        return newline < 0 ? (input, string.Empty) : (input[..newline], input[(newline + 1)..]);
    }

    private static HttpResponseMessage Response(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.TryAddWithoutValidation("X-A", body.Length > 20 ? body[..20] : body);
        response.Headers.TryAddWithoutValidation("Set-Cookie", ["a=1", "b=2"]);
        response.Headers.TryAddWithoutValidation("Location", "http://api.test/next");
        return response;
    }

    private static string JsonPathTarget(string input)
    {
        var (expression, body) = SplitFirstLine(input);
        if (!JsonPath.TryParse(expression, out var path, out var error))
        {
            return "invalid: " + error[..Math.Min(error.Length, 30)];
        }

        string outcome = "no json";
        try
        {
            using var document = JsonDocument.Parse(body);
            var found = path.TryEvaluate(document.RootElement, out var value);
            _ = path.TryFind(document.RootElement, out var kind);
            outcome = found ? "found " + kind.Kind : "missing";
            _ = value;
        }
        catch (JsonException)
        {
        }

        var assertion = new Assertion { Source = AssertionSource.JsonPath, Target = expression, Operator = AssertionOperator.Equals, Value = "1" };
        if (!assertion.Validate().Any())
        {
            var checker = ResponseChecker.From([assertion]);
            using var response = Response(body);
            var bytes = Encoding.UTF8.GetBytes(body);
            _ = checker.Evaluate(response, bytes, bodyTooLarge: false);
            _ = checker.Explain(response, bytes);
        }

        var rule = new ExtractionRule { Variable = "v", Source = ExtractionSource.JsonPath, Expression = expression };
        if (!rule.Validate().Any())
        {
            var extractor = ResponseExtractor.From(new RequestDefinition { Extractions = [rule] });
            using var response = Response(body);
            _ = extractor?.Apply(response, Encoding.UTF8.GetBytes(body), new Dictionary<string, string>());
        }

        return outcome;
    }

    private static string Template(string input)
    {
        var set = new ParameterSet(
            [new ParameterList { Name = "users", Values = ["ana", "{{$guid}}"] }, new ParameterList { Name = "email", Values = ["e"] }],
            [new DataFile { Name = "d", Content = "token,col\nx,y\n" }]);
        var row = new string[set.Count];
        set.Next(row);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["next"] = "http://api.test", ["session"] = "{{session}}" };
        var environment = new Dictionary<string, string> { ["baseUrl"] = "http://api.test", ["token"] = "t" };
        var context = new TemplateContext(3, 7, new ParameterValues(set, row), variables);
        var rendered = TemplateRenderer.Render(input, context);
        _ = TemplateRenderer.Render(input, default);
        _ = TemplateRenderer.ResolveVariables(input, environment);
        _ = TemplateRenderer.UnresolvedNames(input, environment, set, ["next"]);
        _ = TemplateRenderer.RenderForValidation(input, set, ["next", "session"], environment, assumeUnresolvedDefined: true);
        _ = TemplateRenderer.RenderForValidation(input);
        _ = TemplateRenderer.ReplaceVariables(input, "x");
        var request = new RequestDefinition { Url = input, Body = input, Headers = [new HeaderEntry { Name = "X", Value = input }] };
        var plan = new LoadTestPlan { Requests = [request] };
        var problems = plan.Validate();
        if (problems.Count == 0)
        {
            _ = PreparedRequest.From(request, environment);
        }

        return TemplateRenderer.HasVariables(rendered) ? "still has variables" : $"rendered {Outcome(problems)}";
    }

    private static string AssertionsTarget(string input)
    {
        var (json, body) = SplitFirstLine(input);
        List<Assertion>? assertions;
        try
        {
            assertions = JsonSerializer.Deserialize<List<Assertion>>(json, PlanFile.JsonOptions);
        }
        catch (JsonException)
        {
            return "not a list";
        }

        if (assertions is null || assertions.Any(a => a is null))
        {
            return "null";
        }

        var problems = assertions.SelectMany(a => a.Validate()).ToList();
        var request = new RequestDefinition { Url = "http://api.test/", Assertions = assertions };
        _ = new LoadTestPlan { Requests = [request] }.Validate();
        if (problems.Count > 0)
        {
            return Outcome(problems);
        }

        var checker = ResponseChecker.From(assertions);
        using var response = Response(body);
        var bytes = Encoding.UTF8.GetBytes(body);
        var mask = checker.Evaluate(response, bytes, bodyTooLarge: false);
        _ = checker.Evaluate(null, bytes, bodyTooLarge: true);
        var explained = checker.Explain(response, bytes);
        _ = checker.Explain(null, ReadOnlyMemory<byte>.Empty);
        _ = PreparedRequest.From(request);
        return $"{mask} {explained.Count(e => e.Passed)}";
    }

    private static string ExtractionsTarget(string input)
    {
        var (json, body) = SplitFirstLine(input);
        List<ExtractionRule>? rules;
        try
        {
            rules = JsonSerializer.Deserialize<List<ExtractionRule>>(json, PlanFile.JsonOptions);
        }
        catch (JsonException)
        {
            return "not a list";
        }

        if (rules is null || rules.Any(r => r is null))
        {
            return "null";
        }

        var request = new RequestDefinition { Url = "http://api.test/", Extractions = rules };
        var problems = new LoadTestPlan { Requests = [request, new RequestDefinition { Url = "http://api.test/{{x}}" }] }.Validate();
        if (problems.Count > 0)
        {
            return Outcome(problems);
        }

        var extractor = ResponseExtractor.From(request);
        using var response = Response(body);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ExtractedValue>();
        var error = extractor?.Apply(response, Encoding.UTF8.GetBytes(body), variables, results);
        _ = extractor?.Apply(null, [], variables);
        return $"{error is null} {variables.Count}";
    }

    private static string StatusCodes(string input)
    {
        var http = StatusCodeMatcher.TryParse(input, out var matcher);
        var grpc = GrpcStatusMatcher.TryParse(input, out var grpcMatcher);
        foreach (var code in new[] { -1, 0, 1, 16, 17, 99, 100, 200, 404, 599, 600, int.MaxValue, int.MinValue })
        {
            _ = matcher?.IsMatch(code);
            _ = grpcMatcher?.IsMatch(code);
            _ = GrpcStatusMatcher.NameOf(code);
        }

        var request = new RequestDefinition { Url = "http://api.test/", ExpectedStatusCodes = input };
        var problems = new LoadTestPlan { Requests = [request] }.Validate();
        if (problems.Count == 0)
        {
            _ = PreparedRequest.From(request);
        }

        return $"{http} {grpc}";
    }

    private static readonly ProtoSchema ShopSchema = ProtoSchema.TryParse([new ProtoFile { Name = "shop.proto", Content = Seeds.ShopProto }], out var schema, out var errors)
        ? schema!
        : throw new InvalidOperationException("The seed proto doesn't parse: " + string.Join("; ", errors));

    private static string Proto(string input)
    {
        var files = input.Split("\n=====\n").Select((content, i) => new ProtoFile { Name = i == 0 ? "a.proto" : i == 1 ? "b.proto" : $"f{i}.proto", Content = content }).ToList();
        if (!ProtoSchema.TryParse(files, out var schema, out var errors))
        {
            if (errors.Count == 0)
            {
                throw new InvariantException("ProtoSchema.TryParse failed without saying why.");
            }

            return "invalid: " + errors[0][..Math.Min(errors[0].Length, 30)];
        }

        foreach (var method in schema!.Methods.Take(20))
        {
            if (schema.FindMethod(method.Path) != method)
            {
                throw new InvariantException("FindMethod doesn't find a method by its own path.");
            }

            var sample = ProtoSample.For(method);
            byte[] encoded;
            try
            {
                encoded = GrpcMessages.EncodeRequest(method, sample);
            }
            catch (ProtobufException ex)
            {
                throw new InvariantException($"The sample message for {method.Path} doesn't encode: {ex.Message}");
            }

            var output = new ArrayBufferWriter<byte>();
            try
            {
                ProtobufCodec.Decode(method.Input, encoded.AsSpan(5), output);
            }
            catch (ProtobufException) when (method.ClientStreaming)
            {
            }

            var request = new RequestDefinition { Protocol = RequestProtocol.Grpc, Url = "https://grpc.test", Body = sample, Grpc = new GrpcOptions { Method = method.Path } };
            var plan = new LoadTestPlan { Requests = [request], ProtoFiles = files };
            var problems = plan.Validate();
            if (problems.Count == 0)
            {
                _ = PreparedRequest.From(request, schema: schema);
            }
        }

        foreach (var message in schema.Messages.Take(30))
        {
            try
            {
                _ = ProtobufCodec.DecodeToString(message, [0x08, 0x96, 0x01, 0x12, 0x01, 0x41]);
            }
            catch (ProtobufException)
            {
                // These bytes don't fit every message, which is what the exception is for.
            }
        }

        return $"ok {Math.Min(schema.Methods.Count, 5)} {Math.Min(schema.Messages.Count, 9)}";
    }

    private static string ProtobufJson(string input)
    {
        var (type, json) = SplitFirstLine(input);
        var message = ShopSchema.FindMessage(type.Trim()) ?? ShopSchema.FindMessage("shop.v1.Product")!;
        var bytes = ProtobufCodec.Encode(message, json);
        var decoded = ProtobufCodec.DecodeToString(message, bytes);
        byte[] again;
        try
        {
            again = ProtobufCodec.Encode(message, decoded);
        }
        catch (ProtobufException ex)
        {
            throw new InvariantException($"A decoded message doesn't encode again: {ex.Message}");
        }

        // Fields may come out in another order, but decoding both must give the same JSON.
        if (ProtobufCodec.DecodeToString(message, again) != decoded)
        {
            throw new InvariantException("Encoding a decoded message gives a different message.");
        }

        return "ok " + message.FullName;
    }

    private static string EncodeSeed(string seed)
    {
        var (type, json) = SplitFirstLine(seed);
        return Encoding.Latin1.GetString(ProtobufCodec.Encode(ShopSchema.FindMessage(type)!, json));
    }

    private static string ProtobufBinary(string input)
    {
        var bytes = Encoding.Latin1.GetBytes(input);
        var outcomes = new StringBuilder();
        foreach (var message in ShopSchema.Messages)
        {
            try
            {
                var json = ProtobufCodec.DecodeToString(message, bytes);
                using (JsonDocument.Parse(json))
                {
                }

                outcomes.Append('o');
            }
            catch (ProtobufException)
            {
                outcomes.Append('x');
            }
            catch (JsonException ex)
            {
                throw new InvariantException($"Decoding gave JSON that doesn't parse: {ex.Message}");
            }
        }

        foreach (var method in ShopSchema.Methods)
        {
            try
            {
                var framed = new byte[bytes.Length + 5];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(1), (uint)bytes.Length);
                bytes.CopyTo(framed, 5);
                _ = GrpcMessages.DecodeResponseToString(method, framed);
                _ = GrpcMessages.DecodeResponseToString(method, bytes);
            }
            catch (ProtobufException)
            {
            }
        }

        return outcomes.ToString();
    }

    private static readonly Lazy<string> Sandbox = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), "falcon-load-fuzz-" + Environment.ProcessId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "plan.json"), PlanFile.Write([Seeds.RichPlan(), Seeds.StagedPlan()]));
        File.WriteAllText(Path.Combine(path, "env.json"), Seeds.EnvironmentFiles()[0]);
        File.WriteAllText(Path.Combine(path, "data.csv"), "email,name\nx@y.test,X\n");
        return path;
    });

    private static string CliTarget(string input)
    {
        var sandbox = Sandbox.Value;

        // The fuzzer runs in the sandbox, so relative paths stay inside it; absolute paths and ".." are moved there too,
        // so a mutated --out can't write anywhere else.
        var args = input.Split('\n').Select(a => a
            .Replace("PLAN", Path.Combine(sandbox, "plan.json"))
            .Replace("ENV", Path.Combine(sandbox, "env.json"))
            .Replace("DATA", Path.Combine(sandbox, "data.csv"))
            .Replace("..", "_")).Select(a => Confine(a, sandbox)).ToList();

        // Never let a mutation turn the duration up: a run is cut off after a few seconds anyway.
        var options = CliOptions.Parse(args, out var error);
        if (options is null)
        {
            if (string.IsNullOrEmpty(error))
            {
                throw new InvariantException("CliOptions.Parse failed without saying why.");
            }

            return "invalid: " + error[..Math.Min(error.Length, 25)];
        }

        if (options.DurationSeconds > 1 || options.TotalRequests > 20 || options.Concurrency > 4)
        {
            return "too big to run";
        }

        var output = new StringWriter();
        var errors = new StringWriter();
        var handler = new RandomServer(input.GetHashCode());
        var stdin = new StringReader(Seeds.PlanFiles()[2]);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var code = CliApp.RunAsync(
            args,
            new CliConsole(stdin, output, errors, ErrorIsTerminal: false),
            _ => new HttpMessageInvoker(handler, disposeHandler: false),
            TimeSpan.FromMilliseconds(200),
            stop.Token,
            new HttpMessageInvoker(handler, disposeHandler: false)).GetAwaiter().GetResult();
        if (code is not (CliApp.Passed or CliApp.TestFailed or CliApp.InvalidInput or CliApp.Interrupted))
        {
            throw new InvariantException($"The CLI exited with {code}, which isn't one of its documented codes.");
        }

        AssertNoSecrets(output + "\n" + errors, "the CLI's output");
        return $"exit {code} {errors.ToString().Split('\n')[0][..Math.Min(errors.ToString().Split('\n')[0].Length, 30)]}";
    }

    private static string Confine(string arg, string sandbox)
    {
        var equals = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=') : -1;
        var value = equals >= 0 ? arg[(equals + 1)..] : arg;
        if (value.Contains("://", StringComparison.Ordinal) || !(Path.IsPathRooted(value) || value.StartsWith('~')) || value.StartsWith(sandbox, StringComparison.Ordinal))
        {
            return arg;
        }

        var confined = Path.Combine(sandbox, "o-" + Path.GetFileName(value.Replace('\\', '/')));
        return equals >= 0 ? arg[..(equals + 1)] + confined : confined;
    }

    public static string SandboxDirectory => Sandbox.Value;

    private static IReadOnlyList<string> ResultSeeds()
    {
        var plan = Seeds.RichPlan();
        plan.Requests = [plan.Requests[0], plan.Requests[1]];
        plan.Requests[0].OncePerVirtualUser = false;
        plan.Profile.TotalRequests = 30;
        var handler = new RandomServer(7);
        var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler, disposeHandler: false), SecretEnvironment("Staging"));
        var snapshot = runner.RunAsync().GetAwaiter().GetResult();
        var (_, histograms) = runner.GetResults();
        var export = new RunExport { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Machine = "a", Plan = plan, Results = snapshot, Histograms = histograms };
        var other = export with { Id = Guid.NewGuid(), Machine = "b" };
        var old = export with { Histograms = null, Machine = null };
        return
        [
            JsonSerializer.Serialize(new[] { export, other }, PlanFile.JsonOptions),
            JsonSerializer.Serialize(new[] { export, old }, PlanFile.JsonOptions),
        ];
    }

    private static string Results(string input)
    {
        RunExport[]? runs;
        try
        {
            runs = JsonSerializer.Deserialize<RunExport[]>(input, PlanFile.JsonOptions);
        }
        catch (JsonException)
        {
            return "not results";
        }

        if (runs is null)
        {
            return "null";
        }

        // The web API binds a posted array straight to runs, so a null run and a run without its parts must be caught
        // by validation and reading, not by the caller.
        if (runs.Any(r => r is null))
        {
            return ResultCombiner.Validate(runs).Count > 0 ? "invalid: null run" : throw new InvariantException("A null run passed validation.");
        }

        if (runs.Any(r => r.Plan is null || r.Results is null))
        {
            throw new InvariantException("A result was read without its plan or its results.");
        }

        var problems = ResultCombiner.Validate(runs);
        var outcome = Outcome(problems);
        if (problems.Count == 0)
        {
            var combined = ResultCombiner.Combine(runs);
            _ = ResultCombiner.Combine(runs, live: true);
            _ = JsonSerializer.Serialize(combined, PlanFile.JsonOptions);
            _ = RunComparer.Compare(runs[0], runs[^1]);
            var telemetry = runs.Select(MachineTelemetry.From).OfType<MachineTelemetry>().ToList();
            if (telemetry.Count > 0)
            {
                var run = new RunTelemetry(Guid.NewGuid(), runs[0].Plan.Name, runs[0].StartedAt, runs[0].Results.State, runs[0].Results.Passed, telemetry);
                var families = TelemetryMetrics.Build([run]);
                _ = PrometheusText.Write(families);
                _ = OtlpJson.Write(families, DateTimeOffset.UtcNow, "fuzz", "1.0");
                _ = new InfluxLineProtocol().Write([run], DateTimeOffset.UtcNow);
            }
        }

        foreach (var run in runs.Take(2))
        {
            var verdict = RunVerdict.Evaluate(run.Results, 0.5);
            _ = RunVerdict.Evaluate(run.Results, null);
            _ = HtmlReport.Render(run, verdict);
            _ = JUnitReport.Render(run, verdict);
            _ = ThresholdEvaluator.Evaluate(run.Plan.Thresholds ?? [], run.Results);
            _ = RunComparer.Compare(run.Results, run.Results, new RegressionThresholds { LatencyPercent = 0, ThroughputPercent = 0 });
            if (run.Histograms is { } histograms)
            {
                try
                {
                    _ = LatencyHistogram.FromData(histograms.Overall).ToStats();
                }
                catch (FormatException)
                {
                }
            }
        }

        return outcome;
    }

    private static string TelemetryTarget(string input)
    {
        TelemetrySettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<TelemetrySettings>(input, PlanFile.JsonOptions);
        }
        catch (JsonException)
        {
            return "not settings";
        }

        if (settings is null)
        {
            return "null";
        }

        var problems = settings.Validate();
        var redacted = settings.Redacted();
        _ = redacted.KeepSecretsFrom(settings);
        _ = settings.Clone();
        _ = settings.CreateMasker().Apply(input);
        if (problems.Count == 0)
        {
            if (settings.InfluxDb.Enabled)
            {
                _ = settings.InfluxDb.WriteUri();
            }

            if (settings.Otlp.Enabled)
            {
                _ = settings.Otlp.MetricsUri();
            }
        }

        var kept = redacted.InfluxDb.Token is { Length: > 0 } token && token == settings.InfluxDb.Token
            || redacted.Otlp.Headers.Zip(settings.Otlp.Headers).Any(p => p.First?.Value is { Length: > 0 } value && value == p.Second?.Value);
        if (kept)
        {
            throw new InvariantException("Redacted telemetry settings still contain a token or header value.");
        }

        return Outcome(problems);
    }

    private static string Masking(string input)
    {
        var lines = input.Split('\n');
        var secrets = lines[..^1];
        var text = lines[^1];
        var masked = new SecretMasker(secrets).Apply(text);
        foreach (var secret in secrets.Where(s => s.Length >= 3 && !s.Contains('*')))
        {
            if (masked.Contains(secret, StringComparison.Ordinal))
            {
                throw new InvariantException("A secret is still in the masked text.");
            }
        }

        return masked == text ? "unchanged" : "masked";
    }

    private static string GraphQLTarget(string input)
    {
        var lines = input.Split('\n');
        var query = lines[0];
        var variables = lines.Length > 1 ? lines[1] : null;
        var operation = lines.Length > 2 ? lines[2] : null;
        var response = lines.Length > 3 ? string.Join('\n', lines[3..]) : "{}";
        _ = GraphQLRequest.FindError(Encoding.UTF8.GetBytes(response));
        var request = new RequestDefinition
        {
            Protocol = RequestProtocol.GraphQL,
            Method = "POST",
            Url = "http://api.test/graphql",
            Body = query,
            GraphQL = new GraphQLOptions { Variables = variables, OperationName = operation },
        };
        var problems = new LoadTestPlan { Requests = [request] }.Validate();
        if (problems.Count == 0)
        {
            byte[] body;
            try
            {
                body = GraphQLRequest.BuildBody(query, variables, operation);
            }
            catch (FormatException) when (TemplateRenderer.HasVariables(variables))
            {
                // Validation only knows the variables are JSON once their placeholders are filled in.
                return "variables need values";
            }

            using (JsonDocument.Parse(body))
            {
            }

            _ = PreparedRequest.From(request);
        }

        return $"{GraphQLRequest.IsJsonObject(variables ?? "")} {Outcome(problems)}";
    }
}

/// <summary>
/// A fake server whose answers are drawn at random from responses that stress the engine: odd status codes, bodies
/// that are and aren't JSON, gRPC frames, token responses, redirects, slow and failing answers.
/// </summary>
internal sealed class RandomServer(int seed) : HttpMessageHandler
{
    private readonly Random _random = new(seed);
    private readonly Lock _lock = new();

    private static readonly string[] Bodies =
    [
        "", "ok", "{}", "[]", "null", "{\"data\":{\"token\":\"abc\"},\"items\":[{\"id\":7}],\"id\":7,\"ok\":true}",
        "{\"access_token\":\"tok\",\"token_type\":\"Bearer\",\"expires_in\":3600}",
        "{\"access_token\":\"\",\"expires_in\":-5}", "{\"access_token\":\"t\",\"expires_in\":1e308}",
        "{\"error\":\"invalid_client\",\"error_description\":\"bad secret\"}", "{\"errors\":[{\"message\":\"bad\"}]}",
        "{\"a\":" + new string('[', 300) + new string(']', 300) + "}", "<html>not json</html>", "\"id\": 12", "{\"id\": 1e400}",
        "\u0000\u0000\u0000\u0000\u0003\u0008\u0096\u0001", "\u0000\u0000\u0000\u0000\u0000", "\u0001\u0000\u0000\u0000\u0001x",
        "\u0000\u00ff\u00ff\u00ff\u00ff", "\u0000\u0000\u0000\u0000\u0002\u0012\u0005",
        "{\"data\":{\"token\":\"\\ud800\"},\"items\":[{\"id\":\"\\udc00\"}],\"\\ud800\":1,\"id\":7,\"errors\":[{\"message\":\"\\ud83d\"}]}",
        "{\"access_token\":\"\\ud800\",\"expires_in\":60}",
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            await request.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        int pick, status, kind;
        lock (_lock)
        {
            pick = _random.Next(Bodies.Length);
            status = _random.Next(6) switch { 0 => 500, 1 => 404, 2 => 302, 3 => _random.Next(100, 1000), _ => 200 };
            kind = _random.Next(20);
        }

        switch (kind)
        {
            case 0:
                throw new HttpRequestException("Connection refused (fuzz)");
            case 1:
                // Longer than the timeout the targets give a request, so some requests time out.
                await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
                break;
            case 2:
                throw new IOException("The response ended prematurely (fuzz).");
        }

        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new ByteArrayContent(Encoding.Latin1.GetBytes(Bodies[pick])),
            RequestMessage = request,
            Version = request.Version,
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", Bodies[pick] is [< (char)2, ..] ? "application/grpc" : "application/json; charset=utf-8");
        response.Headers.TryAddWithoutValidation("Location", kind == 3 ? "::not a url::" : "http://api.test/next");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "s=1; Path=/");
        response.TrailingHeaders.TryAddWithoutValidation("grpc-status", kind switch { 4 => "abc", 5 => "99", 6 => "-1", _ => (pick % 17).ToString() });
        response.TrailingHeaders.TryAddWithoutValidation("grpc-message", kind == 7 ? "%zz%" : "bad%20thing");
        return response;
    }
}
