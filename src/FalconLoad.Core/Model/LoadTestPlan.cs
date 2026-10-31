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

    /// <summary>Pass/fail rules for the whole run. Without any, a run has no verdict.</summary>
    public List<Threshold> Thresholds { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Returns a list of human readable validation problems; empty when the plan can run.</summary>
    public IReadOnlyList<string> Validate()
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
        for (var i = 0; i < Requests.Count; i++)
        {
            foreach (var problem in Requests[i].Validate(parameters))
            {
                errors.Add($"Request {i + 1} ({Requests[i].DisplayName}): {problem}");
            }
        }

        errors.AddRange(Profile.Validate());
        errors.AddRange(Thresholds.Where(t => t.Enabled).SelectMany(t => t.Validate(Requests.Count)));
        return errors;
    }

    public LoadTestPlan Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<LoadTestPlan>(json)!;
    }
}
