using System.Text.Json;

namespace FalconLoad.Core.Model;

/// <summary>
/// The file format used to share environments between machines. Secret values are never written: the file holds the
/// names of the secrets, so whoever imports it knows what to fill in, but not the tokens themselves. Reading also
/// accepts a bare environment or a bare array of environments.
/// </summary>
public sealed class EnvironmentFile
{
    public const string FormatName = "falcon-load/environments";

    /// <summary>Bumped only when an older falcon-load could no longer read the file correctly.</summary>
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TestEnvironment> Environments { get; set; } = [];

    /// <summary>Writes the environments with every secret's value left out.</summary>
    public static string Write(IEnumerable<TestEnvironment> environments) =>
        JsonSerializer.Serialize(new EnvironmentFile { Environments = [.. environments.Select(e => e.Redacted())] }, PlanFile.JsonOptions);

    /// <summary>
    /// Reads environments from exported JSON. Every environment gets a new id, so importing never overwrites a saved
    /// one, and nothing is returned unless every environment in the file is valid.
    /// </summary>
    public static EnvironmentImport Read(string json)
    {
        List<TestEnvironment?>? environments;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                environments = root.Deserialize<List<TestEnvironment?>>(PlanFile.JsonOptions);
            }
            else if (root.ValueKind == JsonValueKind.Object && HasProperty(root, "environments"))
            {
                var file = root.Deserialize<EnvironmentFile>(PlanFile.JsonOptions)!;
                if (file.Version > CurrentVersion)
                {
                    return EnvironmentImport.Failed($"This file was written by a newer version of falcon-load (format version {file.Version}). Update falcon-load to import it.");
                }

                environments = [.. file.Environments];
            }
            else if (root.ValueKind == JsonValueKind.Object && HasProperty(root, "variables"))
            {
                environments = [root.Deserialize<TestEnvironment>(PlanFile.JsonOptions)];
            }
            else
            {
                return EnvironmentImport.Failed("This doesn't look like a falcon-load environments file.");
            }
        }
        catch (Exception ex) when (JsonErrors.IsInvalidJson(ex))
        {
            return EnvironmentImport.Failed($"The file isn't valid JSON for a falcon-load environment: {ex.Message}");
        }

        var imported = environments?.OfType<TestEnvironment>().ToList() ?? [];
        if (imported.Count == 0)
        {
            return EnvironmentImport.Failed("The file doesn't contain any environments.");
        }

        var problems = new List<string>();
        foreach (var environment in imported)
        {
            environment.Name ??= string.Empty;
            environment.Variables = environment.Variables?.OfType<EnvironmentVariable>().ToList() ?? [];
            foreach (var variable in environment.Variables)
            {
                variable.Name ??= string.Empty;

                // A secret's value is left out of exports, so it starts empty here and has to be filled in.
                variable.Value ??= string.Empty;
            }

            environment.Id = Guid.NewGuid();
            foreach (var problem in environment.Validate())
            {
                problems.Add(imported.Count == 1 ? problem : $"{environment.Name}: {problem}");
            }
        }

        return problems.Count > 0 ? new EnvironmentImport([], problems) : new EnvironmentImport(imported, []);
    }

    private static bool HasProperty(JsonElement element, string name) =>
        element.EnumerateObject().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>The outcome of <see cref="EnvironmentFile.Read"/>: the environments to save, or why the file can't be imported.</summary>
public sealed record EnvironmentImport(IReadOnlyList<TestEnvironment> Environments, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;

    internal static EnvironmentImport Failed(string problem) => new([], [problem]);
}
