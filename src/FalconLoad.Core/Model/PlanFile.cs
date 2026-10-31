using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FalconLoad.Core.Model;

/// <summary>
/// The file format used to share load test plans between machines: a small envelope around a list of plans, so a
/// later version can tell what it is reading. Reading also accepts a bare plan or a bare array of plans, such as the
/// output of <c>/api/plans/{id}</c> or a copy of <c>plans.json</c>.
/// </summary>
public sealed class PlanFile
{
    public const string FormatName = "falcon-load/plans";

    /// <summary>Bumped only when an older falcon-load could no longer read the file correctly.</summary>
    public const int CurrentVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,

        // The file is meant to be read and edited by people, so keep characters such as æ, ø and å as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset ExportedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<LoadTestPlan> Plans { get; set; } = [];

    public static string Write(IEnumerable<LoadTestPlan> plans) =>
        JsonSerializer.Serialize(new PlanFile { Plans = [.. plans] }, JsonOptions);

    /// <summary>
    /// Reads plans from exported JSON. Every plan gets a new id, so importing never overwrites a saved test, and
    /// nothing is returned unless every plan in the file is valid.
    /// </summary>
    public static PlanImport Read(string json)
    {
        List<LoadTestPlan?>? plans;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                plans = root.Deserialize<List<LoadTestPlan?>>(JsonOptions);
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "plans", out _))
            {
                var file = root.Deserialize<PlanFile>(JsonOptions)!;
                if (file.Version > CurrentVersion)
                {
                    return PlanImport.Failed($"This file was written by a newer version of falcon-load (format version {file.Version}). Update falcon-load to import it.");
                }

                plans = [.. file.Plans];
            }
            else if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, "requests", out _))
            {
                plans = [root.Deserialize<LoadTestPlan>(JsonOptions)];
            }
            else
            {
                return PlanImport.Failed("This doesn't look like a falcon-load test file.");
            }
        }
        catch (JsonException ex)
        {
            return PlanImport.Failed($"The file isn't valid JSON for a falcon-load test: {ex.Message}");
        }

        var imported = plans?.OfType<LoadTestPlan>().ToList() ?? [];
        if (imported.Count == 0)
        {
            return PlanImport.Failed("The file doesn't contain any tests.");
        }

        var problems = new List<string>();
        foreach (var plan in imported)
        {
            FillMissing(plan);
            plan.Id = Guid.NewGuid();
            foreach (var problem in plan.Validate())
            {
                problems.Add(imported.Count == 1 ? problem : $"{plan.Name}: {problem}");
            }
        }

        return problems.Count > 0 ? new PlanImport([], problems) : new PlanImport(imported, []);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // A hand-edited file can hold explicit nulls where the model expects values; treat them as left out.
    private static void FillMissing(LoadTestPlan plan)
    {
        plan.Name = string.IsNullOrWhiteSpace(plan.Name) ? "Imported test" : plan.Name;
        plan.Profile ??= new LoadProfile();
        plan.Requests = plan.Requests?.OfType<RequestDefinition>().ToList() ?? [];
        foreach (var request in plan.Requests)
        {
            request.Name ??= string.Empty;
            request.Method ??= "GET";
            request.Url ??= string.Empty;
            request.ContentType ??= string.Empty;
            request.Headers = request.Headers?.OfType<HeaderEntry>().ToList() ?? [];
            foreach (var header in request.Headers)
            {
                header.Name ??= string.Empty;
                header.Value ??= string.Empty;
            }
        }
    }
}

/// <summary>The outcome of <see cref="PlanFile.Read"/>: the plans to save, or why the file can't be imported.</summary>
public sealed record PlanImport(IReadOnlyList<LoadTestPlan> Plans, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Problems.Count == 0;

    internal static PlanImport Failed(string problem) => new([], [problem]);
}
