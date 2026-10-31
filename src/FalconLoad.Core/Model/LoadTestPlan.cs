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
    /// The name of the <see cref="TestEnvironment"/> whose variables this test runs with unless a run picks another.
    /// It is a name, not an id, so a test exported to another machine finds that machine's environment of the same name.
    /// </summary>
    public string? Environment { get; set; }

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

        var parameters = new ParameterSet(Parameters);
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
        }

        if (Requests.Count > 0 && Requests.All(r => r.OncePerVirtualUser))
        {
            errors.Add("At least one request has to run on every iteration; untick \"Once per virtual user\" on one of them.");
        }

        errors.AddRange(Profile.Validate());
        return errors;
    }

    /// <summary>
    /// The variables of <paramref name="environment"/> that requests of this plan actually read: a parameter list or an
    /// extracted variable with the same name wins, so it is left out here. These values never change during a run.
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentValues(TestEnvironment? environment)
    {
        var values = new Dictionary<string, string>(environment?.ToValues() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in Parameters.Select(p => p.Name).Concat(ExtractedVariables()))
        {
            values.Remove(name);
        }

        return values;
    }

    /// <summary>The distinct names of the variables the plan's requests extract from responses.</summary>
    public IReadOnlyList<string> ExtractedVariables() =>
        [.. Requests.SelectMany(r => r.Extractions).Select(e => e.Variable?.Trim()).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    public LoadTestPlan Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<LoadTestPlan>(json)!;
    }
}
