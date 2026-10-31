using System.Text.Json;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>
/// Persists environments, with their secret values, to a JSON file next to the saved tests. Secrets are stored as
/// they are typed, like the headers in a saved test; what leaves the store for display, the API or an export is
/// <see cref="TestEnvironment.Redacted"/>.
/// </summary>
public sealed class EnvironmentStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly PlanStore _plans;
    private readonly ILogger<EnvironmentStore> _logger;
    private List<TestEnvironment> _environments;

    public EnvironmentStore(IConfiguration configuration, IHostEnvironment host, PlanStore plans, ILogger<EnvironmentStore> logger)
    {
        _plans = plans;
        _logger = logger;
        var configured = configuration["FalconLoad:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(host.ContentRootPath, "App_Data")
            : Path.GetFullPath(configured, host.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "environments.json");
        _environments = Load();
    }

    public event Action? Changed;

    /// <summary>Every environment with its secrets. For starting runs; show <see cref="TestEnvironment.Redacted"/> copies to people.</summary>
    public IReadOnlyList<TestEnvironment> GetAll()
    {
        lock (_gate)
        {
            return _environments.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.Clone()).ToList();
        }
    }

    public TestEnvironment? Get(Guid id)
    {
        lock (_gate)
        {
            return _environments.FirstOrDefault(e => e.Id == id)?.Clone();
        }
    }

    public TestEnvironment? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            return _environments.FirstOrDefault(e => string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))?.Clone();
        }
    }

    /// <summary>
    /// Finds the environment a run should use. A blank name means "no environment" and is fine; a name that matches
    /// nothing is not, so a typo can't silently send requests with unsubstituted placeholders.
    /// </summary>
    public bool TryResolve(string? name, out TestEnvironment? environment, out string? problem)
    {
        problem = null;
        environment = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        environment = FindByName(name);
        if (environment is null)
        {
            problem = $"There is no environment named '{name.Trim()}'.";
            return false;
        }

        return true;
    }

    /// <summary>The problems with saving <paramref name="environment"/>, including a name another environment already has.</summary>
    public IReadOnlyList<string> Validate(TestEnvironment environment)
    {
        var problems = environment.Validate().ToList();
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(environment.Name)
                && _environments.Any(e => e.Id != environment.Id && string.Equals(e.Name, environment.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"There is already an environment named '{environment.Name.Trim()}'.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Saves <paramref name="environment"/>. A secret whose value is <c>null</c> (hidden when it was shown) keeps the
    /// value already stored under its name. Renaming updates the tests that use the old name.
    /// </summary>
    public TestEnvironment Save(TestEnvironment environment)
    {
        var copy = environment.Clone();
        copy.Name = copy.Name.Trim();
        copy.UpdatedAt = DateTimeOffset.UtcNow;
        string? renamedFrom = null;
        lock (_gate)
        {
            var existing = _environments.FirstOrDefault(e => e.Id == copy.Id);
            var kept = existing?.Variables.ToDictionary(v => v.Name, v => v.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase) ?? [];
            foreach (var variable in copy.Variables)
            {
                variable.Name = variable.Name.Trim();
                variable.Value = variable.Value is null && variable.Secret
                    ? kept.GetValueOrDefault(variable.Name, string.Empty)
                    : variable.Value ?? string.Empty;
            }

            if (existing is not null && !string.Equals(existing.Name, copy.Name, StringComparison.OrdinalIgnoreCase))
            {
                renamedFrom = existing.Name;
            }

            _environments.RemoveAll(e => e.Id == copy.Id);
            _environments.Add(copy);
            Persist();
        }

        if (renamedFrom is not null)
        {
            _plans.RenameEnvironment(renamedFrom, copy.Name);
        }

        Changed?.Invoke();
        return copy.Clone();
    }

    /// <summary>Saves imported environments with one write; a name that is taken gets a number, so an import never replaces one.</summary>
    public IReadOnlyList<TestEnvironment> SaveAll(IEnumerable<TestEnvironment> environments)
    {
        var saved = new List<TestEnvironment>();
        lock (_gate)
        {
            foreach (var environment in environments)
            {
                var copy = environment.Clone();
                copy.Name = UniqueName(copy.Name.Trim());
                copy.UpdatedAt = DateTimeOffset.UtcNow;
                _environments.RemoveAll(e => e.Id == copy.Id);
                _environments.Add(copy);
                saved.Add(copy);
            }

            Persist();
        }

        Changed?.Invoke();
        return saved.Select(e => e.Clone()).ToList();
    }

    public bool Delete(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _environments.RemoveAll(e => e.Id == id) > 0;
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

    // Called with the lock held.
    private string UniqueName(string name)
    {
        var candidate = name;
        for (var n = 2; _environments.Any(e => string.Equals(e.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }

    private List<TestEnvironment> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TestEnvironment>>(File.ReadAllText(_path), PlanStore.JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not read environments from {Path}; starting with an empty list", _path);
            return [];
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_environments, PlanStore.JsonOptions));

        // The file holds secrets, so on Unix only its owner may read it.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
