using System.Text.Json;
using System.Text.Json.Serialization;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>Persists saved load test plans to a JSON file.</summary>
public sealed class PlanStore
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<PlanStore> _logger;
    private List<LoadTestPlan> _plans;

    public PlanStore(IConfiguration configuration, IHostEnvironment environment, ILogger<PlanStore> logger)
    {
        _logger = logger;
        var directory = DataDirectory(configuration, environment);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "plans.json");
        _plans = Load();
    }

    public event Action? Changed;

    /// <summary>The folder that holds saved plans and run history: <c>App_Data</c>, or the <c>FalconLoad:DataDirectory</c> setting.</summary>
    public static string DataDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["FalconLoad:DataDirectory"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.GetFullPath(configured, environment.ContentRootPath);
    }

    public IReadOnlyList<LoadTestPlan> GetAll()
    {
        lock (_gate)
        {
            return _plans.OrderByDescending(p => p.UpdatedAt).Select(p => p.Clone()).ToList();
        }
    }

    public LoadTestPlan? Get(Guid id)
    {
        lock (_gate)
        {
            return _plans.FirstOrDefault(p => p.Id == id)?.Clone();
        }
    }

    public LoadTestPlan Save(LoadTestPlan plan)
    {
        var copy = plan.Clone();
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _plans.RemoveAll(p => p.Id == copy.Id);
            _plans.Add(copy);
            Persist();
        }

        Changed?.Invoke();
        return copy.Clone();
    }

    /// <summary>Saves several plans with one write to disk and one change notification, e.g. for an import.</summary>
    public IReadOnlyList<LoadTestPlan> SaveAll(IEnumerable<LoadTestPlan> plans)
    {
        var copies = plans.Select(p => p.Clone()).ToList();
        lock (_gate)
        {
            foreach (var copy in copies)
            {
                copy.UpdatedAt = DateTimeOffset.UtcNow;
                _plans.RemoveAll(p => p.Id == copy.Id);
                _plans.Add(copy);
            }

            Persist();
        }

        Changed?.Invoke();
        return copies.Select(p => p.Clone()).ToList();
    }

    public bool Delete(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _plans.RemoveAll(p => p.Id == id) > 0;
            if (removed)
            {
                Persist();
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Points every test that used environment <paramref name="oldName"/> at its new name, after the environment is renamed.</summary>
    public void RenameEnvironment(string oldName, string newName)
    {
        bool changed;
        lock (_gate)
        {
            var affected = _plans.Where(p => string.Equals(p.Environment, oldName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var plan in affected)
            {
                plan.Environment = newName;
            }

            changed = affected.Count > 0;
            if (changed)
            {
                Persist();
            }
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private List<LoadTestPlan> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LoadTestPlan>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not read saved plans from {Path}; starting with an empty list", _path);
            return [];
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_plans, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
