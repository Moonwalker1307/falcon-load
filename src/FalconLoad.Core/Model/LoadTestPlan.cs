namespace FalconLoad.Core.Model;

/// <summary>
/// A complete, runnable load test: one or more requests plus the load profile to drive them with.
/// Each virtual user executes the requests in order (one "iteration"), then starts over.
/// </summary>
public sealed class LoadTestPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New load test";

    public List<RequestDefinition> Requests { get; set; } = [new RequestDefinition()];

    public LoadProfile Profile { get; set; } = new();

    /// <summary>Named lists of values that requests reference as <c>{{name}}</c>. Plans saved before lists existed have none.</summary>
    public List<ParameterList> Parameters { get; set; } = [];

    /// <summary>
    /// Tables of test data, such as a list of users, whose columns requests reference as <c>{{column}}</c>. Plans saved
    /// before data files existed have none.
    /// </summary>
    public List<DataFile> DataFiles { get; set; } = [];

    /// <summary>Pass/fail rules for the whole run. Without any, a run has no verdict.</summary>
    public List<Threshold> Thresholds { get; set; } = [];

    /// <summary>
    /// The name of the <see cref="TestEnvironment"/> whose variables this test runs with unless a run picks another.
    /// It is a name, not an id, so a test exported to another machine finds that machine's environment of the same name.
    /// </summary>
    public string? Environment { get; set; }

    /// <summary>
    /// The <c>.proto</c> files that describe the services gRPC requests call, by file name so they can import each other.
    /// Plans saved before gRPC existed have none.
    /// </summary>
    public List<Grpc.ProtoFile> ProtoFiles { get; set; } = [];

    /// <summary>How requests are authenticated, such as with an OAuth2 token. Plans saved before auth existed have none.</summary>
    public AuthSettings? Auth { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Returns a list of human readable validation problems; empty when the plan can be saved.</summary>
    public IReadOnlyList<string> Validate() => Validate(null, forRun: false);

    /// <summary>
    /// Returns the problems that would stop the plan from running with <paramref name="environment"/>. Unlike
    /// <see cref="Validate()"/>, a variable that neither the environment nor a parameter list supplies is a problem
    /// when it leaves the URL unusable.
    /// </summary>
    public IReadOnlyList<string> ValidateForRun(TestEnvironment? environment) => Validate(environment, forRun: true);

    private List<string> Validate(TestEnvironment? environment, bool forRun)
    {
        var errors = new List<string>();
        if (Requests.Count == 0)
        {
            errors.Add("Add at least one request.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in Parameters)
        {
            errors.AddRange(list.Validate());
            if (!string.IsNullOrWhiteSpace(list.Name) && !names.Add(list.Name))
            {
                errors.Add($"There is more than one parameter list named '{list.Name}'.");
            }
        }

        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in DataFiles)
        {
            errors.AddRange(file.Validate());
            if (!string.IsNullOrWhiteSpace(file.Name) && !fileNames.Add(file.Name))
            {
                errors.Add($"There is more than one data file named '{file.Name}'.");
            }

            if (!file.TryGetTable(out var table, out _))
            {
                continue;
            }

            foreach (var column in table.Columns.Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (names.Contains(column))
                {
                    errors.Add($"Column '{column}' of data file '{file.Name}' has the same name as a parameter list; rename one of them.");
                }
                else if (!columnOwners.TryAdd(column, file.Name))
                {
                    errors.Add($"Column '{column}' is in both data file '{columnOwners[column]}' and '{file.Name}'; rename one of them.");
                }
            }
        }

        var parameters = new ParameterSet(Parameters, DataFiles);
        var variables = ExtractedVariables();
        var values = EnvironmentValues(environment);
        for (var i = 0; i < Requests.Count; i++)
        {
            foreach (var problem in Requests[i].Validate(parameters, variables, values, forRun))
            {
                errors.Add($"Request {i + 1} ({Requests[i].DisplayName}): {problem}");
            }
        }

        foreach (var variable in variables)
        {
            if (names.Contains(variable))
            {
                errors.Add($"'{variable}' is both a parameter list and an extracted variable; rename one of them.");
            }
            else if (columnOwners.TryGetValue(variable, out var owner))
            {
                errors.Add($"'{variable}' is both a column of data file '{owner}' and an extracted variable; rename one of them.");
            }
        }

        if (Requests.Count > 0 && Requests.All(r => r.OncePerVirtualUser))
        {
            errors.Add("At least one request has to run on every iteration; untick \"Once per virtual user\" on one of them.");
        }

        errors.AddRange(ValidateGrpc());

        if (Auth is not null)
        {
            errors.AddRange(Auth.Validate(environment?.ToValues(), forRun));
        }

        errors.AddRange(Profile.Validate());
        errors.AddRange(Thresholds.Where(t => t.Enabled).SelectMany(t => t.Validate(Requests.Count)));
        return errors;
    }

    /// <summary>
    /// Reads the proto files and checks every gRPC request against them: its method has to exist, and its message has
    /// to fit the method's request type, with placeholders standing in as <c>1</c> since their values aren't known yet.
    /// </summary>
    internal IEnumerable<string> ValidateGrpc()
    {
        var grpc = Requests.Select((r, i) => (Request: r, Index: i)).Where(x => x.Request.Protocol == RequestProtocol.Grpc).ToList();
        if (grpc.Count == 0)
        {
            yield break;
        }

        if (ProtoFiles.All(f => string.IsNullOrWhiteSpace(f.Content)))
        {
            yield return "gRPC requests need the service's .proto files; add them under Proto files.";
            yield break;
        }

        var duplicate = ProtoFiles.GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            yield return $"There is more than one proto file named '{duplicate.Key}'.";
        }

        if (!Grpc.ProtoSchema.TryParse(ProtoFiles, out var schema, out var problems))
        {
            foreach (var problem in problems)
            {
                yield return $"Proto files: {problem}";
            }

            yield break;
        }

        foreach (var (request, index) in grpc)
        {
            var label = $"Request {index + 1} ({request.DisplayName})";
            if (string.IsNullOrWhiteSpace(request.Grpc?.Method))
            {
                continue;
            }

            var method = schema!.FindMethod(request.Grpc.Method);
            if (method is null)
            {
                yield return $"{label}: the proto files have no method '{request.Grpc.Method}'. Use package.Service/Method, such as {schema.Methods[0].Path}.";
                continue;
            }

            string? error = null;
            try
            {
                Grpc.GrpcMessages.EncodeRequest(method, TemplateRenderer.ReplaceVariables(request.Body ?? string.Empty, "1"));
            }
            catch (Exception ex) when (ex is Grpc.ProtobufException or System.Text.Json.JsonException)
            {
                error = ex.Message;
            }

            if (error is not null)
            {
                yield return $"{label}: the message doesn't fit {method.Input.FullName}: {error}";
            }
        }
    }

    /// <summary>
    /// The variables of <paramref name="environment"/> that requests of this plan actually read: a parameter list or an
    /// extracted variable or data column with the same name wins, so it is left out here. These values never change during a run.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentValues(TestEnvironment? environment)
    {
        var values = new Dictionary<string, string>(environment?.ToValues() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in new ParameterSet(Parameters, DataFiles).Names.Concat(ExtractedVariables()))
        {
            values.Remove(name);
        }

        return values;
    }

    /// <summary>The distinct names of the variables the plan's requests extract from responses.</summary>
    public IReadOnlyList<string> ExtractedVariables() =>
        [.. Requests.SelectMany(r => r.Extractions).Select(e => e.Variable?.Trim()).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Replaces the text of every data file with its row count. A plan that is kept as the record of a run, in the run
    /// history, an export or an API response, calls this so the rows, which can be large and can hold personal data,
    /// aren't copied into every result.
    /// </summary>
    public void DropDataRows() => DataFiles = [.. DataFiles.Select(f => f.WithoutRows())];

    public LoadTestPlan Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<LoadTestPlan>(json)!;
    }
}
