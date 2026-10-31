using System.Collections.Concurrent;
using System.Text.Json;
using FalconLoad.Core.Distributed;

namespace FalconLoad.Web.Services;

/// <summary>A machine registered on this controller, as saved in <c>agents.json</c>.</summary>
public sealed class AgentDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>The address of the agent's falcon-load, e.g. <c>http://load-2:5299</c>.</summary>
    public string Url { get; set; } = "http://";

    /// <summary>The agent's <c>FalconLoad:AgentToken</c>. Stored as it is, like the headers of a saved test.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>A disabled agent stays registered but gets no share of a test.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Its share of the load relative to the other agents: weight 2 gets twice the virtual users of weight 1.</summary>
    public int Weight { get; set; } = 1;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return "Give the agent a name.";
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return "The URL must be an absolute http:// or https:// address, such as http://load-2:5299.";
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            yield return "The token is needed; it is the agent's FalconLoad:AgentToken setting.";
        }

        if (Weight is < 1 or > 100)
        {
            yield return "The weight must be between 1 and 100.";
        }
    }

    public AgentEndpoint ToEndpoint() => new(Name.Trim(), new Uri(Url.Trim()), Token.Trim());

    public AgentDefinition Clone() => (AgentDefinition)MemberwiseClone();
}

/// <summary>What the last check of an agent found.</summary>
public sealed record AgentStatus(AgentProbe? Probe, string? Error, DateTimeOffset CheckedAt)
{
    public bool IsReachable => Probe is not null;
}

/// <summary>The agents this controller knows, persisted next to the saved tests, and the result of their last check.</summary>
public sealed class AgentRegistry
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<AgentRegistry> _logger;
    private readonly AgentClient _client;
    private readonly ConcurrentDictionary<Guid, AgentStatus> _status = new();
    private List<AgentDefinition> _agents;

    public AgentRegistry(IConfiguration configuration, IHostEnvironment environment, ILogger<AgentRegistry> logger, AgentClient client)
    {
        _logger = logger;
        _client = client;
        var configured = configuration["FalconLoad:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "App_Data")
            : Path.GetFullPath(configured, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "agents.json");
        _agents = Load();
    }

    /// <summary>Raised when an agent is added, changed or removed, or a check finishes.</summary>
    public event Action? Changed;

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        lock (_gate)
        {
            return _agents.Select(a => a.Clone()).ToList();
        }
    }

    public AgentDefinition? Get(Guid id)
    {
        lock (_gate)
        {
            return _agents.FirstOrDefault(a => a.Id == id)?.Clone();
        }
    }

    /// <summary>The agents a test is run on when none are picked: every enabled one, in the order they were added.</summary>
    public IReadOnlyList<AgentDefinition> GetEnabled() => GetAll().Where(a => a.Enabled).ToList();

    public AgentStatus? GetStatus(Guid id) => _status.GetValueOrDefault(id);

    public AgentDefinition Save(AgentDefinition agent)
    {
        var copy = agent.Clone();
        copy.Name = copy.Name.Trim();
        copy.Url = copy.Url.Trim();
        copy.Token = copy.Token.Trim();
        lock (_gate)
        {
            var index = _agents.FindIndex(a => a.Id == copy.Id);
            if (index < 0)
            {
                _agents.Add(copy);
            }
            else
            {
                _agents[index] = copy;
            }

            Persist();
        }

        // The address or token may have changed, so the last check no longer says anything.
        _status.TryRemove(copy.Id, out _);
        Changed?.Invoke();
        return copy.Clone();
    }

    public bool Delete(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _agents.RemoveAll(a => a.Id == id) > 0;
            if (removed)
            {
                Persist();
            }
        }

        if (removed)
        {
            _status.TryRemove(id, out _);
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>Contacts the agent and remembers what it said. Never throws; the status holds the error.</summary>
    public async Task<AgentStatus> CheckAsync(AgentDefinition agent, CancellationToken cancellationToken = default)
    {
        AgentStatus status;
        try
        {
            var probe = await _client.ProbeAsync(agent.ToEndpoint(), cancellationToken);
            status = new AgentStatus(probe, null, DateTimeOffset.UtcNow);
        }
        catch (AgentException ex)
        {
            status = new AgentStatus(null, ex.Message, DateTimeOffset.UtcNow);
        }
        catch (UriFormatException ex)
        {
            status = new AgentStatus(null, ex.Message, DateTimeOffset.UtcNow);
        }

        _status[agent.Id] = status;
        Changed?.Invoke();
        return status;
    }

    public Task<AgentStatus[]> CheckAllAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(GetAll().Select(a => CheckAsync(a, cancellationToken)));

    private List<AgentDefinition> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AgentDefinition>>(File.ReadAllText(_path), PlanStore.JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not read agents from {Path}; starting with none", _path);
            return [];
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_agents, PlanStore.JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}
