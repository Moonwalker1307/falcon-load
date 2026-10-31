using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>
/// A named set of variables, such as the base URL and an API token of "Staging" or "Production". Requests refer to a
/// variable as <c>{{name}}</c>, and each run picks which environment supplies the values, so one test can be run
/// against several systems without editing it.
/// </summary>
public sealed partial class TestEnvironment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New environment";

    public List<EnvironmentVariable> Variables { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    /// <summary>Returns human readable problems; empty when the environment can be saved.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Give the environment a name.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                errors.Add("Every variable needs a name.");
            }
            else if (!NamePattern().IsMatch(variable.Name))
            {
                errors.Add($"Variable name '{variable.Name}' can only use letters, digits and underscores, and can't start with a digit.");
            }
            else if (!seen.Add(variable.Name))
            {
                errors.Add($"There is more than one variable named '{variable.Name}'.");
            }
        }

        return errors;
    }

    public TestEnvironment Clone() => new()
    {
        Id = Id,
        Name = Name,
        UpdatedAt = UpdatedAt,
        Variables = [.. Variables.Select(v => new EnvironmentVariable { Name = v.Name, Value = v.Value, Secret = v.Secret })],
    };

    /// <summary>
    /// A copy that is safe to show or export: the value of every secret is left out. A secret that has a value gets
    /// <c>null</c>, so a reader can tell "set but hidden" from "not set yet" (an empty string).
    /// </summary>
    public TestEnvironment Redacted()
    {
        var copy = Clone();
        foreach (var variable in copy.Variables.Where(v => v.Secret))
        {
            variable.Value = string.IsNullOrEmpty(variable.Value) ? string.Empty : null;
        }

        return copy;
    }

    /// <summary>The variable values by name, as the engine substitutes them.</summary>
    public IReadOnlyDictionary<string, string> ToValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
        {
            values.TryAdd(variable.Name, variable.Value ?? string.Empty);
        }

        return values;
    }

    /// <summary>Finds and hides this environment's secret values in free text such as error messages.</summary>
    public SecretMasker CreateMasker() => new(Variables.Where(v => v.Secret).Select(v => v.Value));
}

public sealed class EnvironmentVariable
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The value. <c>null</c> on a secret that has been redacted (see <see cref="TestEnvironment.Redacted"/>); when
    /// such a copy is saved, the stored value is kept.
    /// </summary>
    public string? Value { get; set; } = string.Empty;

    /// <summary>A secret, such as a token. It is masked in the UI and logs and left out of exports and API responses.</summary>
    public bool Secret { get; set; }
}
