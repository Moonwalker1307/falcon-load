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
        var configured = configuration["FalconLoad:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.GetFullPath(configured, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "plans.json");
        _plans = Load();
    }

    public event Action? Changed;

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
