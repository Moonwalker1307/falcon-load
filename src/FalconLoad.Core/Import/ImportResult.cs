using FalconLoad.Core.Model;

namespace FalconLoad.Core.Import;

/// <summary>Raised when pasted or uploaded text can't be read as a curl command, a Postman collection or an OpenAPI spec.</summary>
public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// The requests, parameter lists and notes produced by reading a curl command, a Postman collection or an OpenAPI
/// spec. Nothing is saved: the caller turns it into a new test or adds it to an existing one.
/// </summary>
public sealed class ImportResult
{
    public string Name { get; set; } = "Imported test";

    public List<RequestDefinition> Requests { get; } = [];

    /// <summary>Values the requests refer to as <c>{{name}}</c>: Postman variables, or placeholders for tokens an OpenAPI spec asks for.</summary>
    public List<ParameterList> Parameters { get; } = [];

    /// <summary>Things that were left out, changed or still need the user's attention.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Set when the source asked to skip certificate checks (curl's <c>-k</c>).</summary>
    public bool IgnoreTlsErrors { get; set; }

    public LoadTestPlan ToPlan()
    {
        var plan = new LoadTestPlan
        {
            Name = Name,
            Requests = [.. Requests],
            Parameters = [.. Parameters],
        };
        plan.Profile.IgnoreTlsErrors = IgnoreTlsErrors;
        return plan;
    }

    /// <summary>
    /// Appends the requests and lists to an existing test. A list whose name the test already uses is not replaced,
    /// so the notes returned say which ones were kept as they were.
    /// </summary>
    public IReadOnlyList<string> AddTo(LoadTestPlan plan)
    {
        var notes = new List<string>();
        plan.Requests.AddRange(Requests);
        foreach (var list in Parameters)
        {
            var existing = plan.Parameters.FirstOrDefault(p => string.Equals(p.Name, list.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                plan.Parameters.Add(list);
            }
            else if (!existing.Values.SequenceEqual(list.Values))
            {
                notes.Add($"The test already has a parameter list named '{list.Name}', so its values were kept.");
            }
        }

        if (IgnoreTlsErrors && !plan.Profile.IgnoreTlsErrors)
        {
            notes.Add("The source skipped certificate checks (curl -k); the test's \"Ignore TLS errors\" setting was left off.");
        }

        return notes;
    }

    internal void AddParameter(string name, string value)
    {
        if (!Parameters.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Parameters.Add(new ParameterList { Name = name, Values = [value] });
        }
    }
}
