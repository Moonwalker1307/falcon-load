using System.Text.Json;
using FalconLoad.Core.Telemetry;

namespace FalconLoad.Web.Services;

/// <summary>
/// Persists where live metrics go to <c>telemetry.json</c> next to the saved tests. The InfluxDB token and the OTLP
/// header values are stored as they are typed, like the secrets of an environment; what leaves the store for the UI
/// or the API is <see cref="TelemetrySettings.Redacted"/>.
/// </summary>
public sealed class TelemetryStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ILogger<TelemetryStore> _logger;
    private TelemetrySettings _settings;

    public TelemetryStore(IConfiguration configuration, IHostEnvironment host, ILogger<TelemetryStore> logger)
    {
        _logger = logger;
        var configured = configuration["FalconLoad:DataDirectory"];
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(host.ContentRootPath, "App_Data")
            : Path.GetFullPath(configured, host.ContentRootPath);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "telemetry.json");
        _settings = Load();
    }

    public event Action? Changed;

    /// <summary>The settings with their secrets, for the publisher. Show <see cref="TelemetrySettings.Redacted"/> copies to people.</summary>
    public TelemetrySettings Get()
    {
        lock (_gate)
        {
            return _settings.Clone();
        }
    }

    /// <summary>Saves valid settings; a secret left <c>null</c> keeps its stored value. Returns the saved settings, redacted.</summary>
    public TelemetrySettings Save(TelemetrySettings settings)
    {
        TelemetrySettings saved;
        lock (_gate)
        {
            saved = settings.KeepSecretsFrom(_settings);
            saved.InfluxDb.Url = saved.InfluxDb.Url.Trim();
            saved.InfluxDb.Bucket = saved.InfluxDb.Bucket.Trim();
            saved.InfluxDb.Organization = saved.InfluxDb.Organization?.Trim();
            saved.Otlp.Endpoint = saved.Otlp.Endpoint.Trim();
            saved.Otlp.Headers = saved.Otlp.Headers.Where(h => !string.IsNullOrWhiteSpace(h.Name)).ToList();
            foreach (var header in saved.Otlp.Headers)
            {
                header.Name = header.Name.Trim();
            }

            _settings = saved;
            Persist();
        }

        Changed?.Invoke();
        return saved.Redacted();
    }

    private TelemetrySettings Load()
    {
        if (!File.Exists(_path))
        {
            return new TelemetrySettings();
        }

        try
        {
            return JsonSerializer.Deserialize<TelemetrySettings>(File.ReadAllText(_path), PlanStore.JsonOptions) ?? new TelemetrySettings();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not read the metrics export settings from {Path}; using the defaults", _path);
            return new TelemetrySettings();
        }
    }

    private void Persist()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_settings, PlanStore.JsonOptions));

        // The file holds a token and header values, so on Unix only its owner may read it.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, _path, overwrite: true);
    }
}
