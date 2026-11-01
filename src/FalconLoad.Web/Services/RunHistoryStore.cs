using System.Text.Json;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Web.Services;

/// <summary>A finished run in a saved test's history: its summary, without the per-second timeline.</summary>
public sealed record RunHistoryEntry
{
    public required Guid RunId { get; init; }

    public required Guid PlanId { get; init; }

    /// <summary>The test's name when it ran; the test may have been renamed since.</summary>
    public required string PlanName { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public string? Machine { get; init; }

    /// <summary>What the run tested, such as a build or commit. Missing from runs stored before tags existed.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Note { get; init; }

    /// <summary>The final results with an empty timeline, which is enough to compare runs in a table.</summary>
    public required LoadTestSnapshot Results { get; init; }
}

/// <summary>
/// Keeps the finished runs of saved tests on disk, so a test's history and its baseline survive a restart. Each run
/// is stored whole, as its export file, under <c>runs/</c> in the data directory, and <c>runs/index.json</c> holds the
/// summaries and each test's baseline so that listing a history doesn't read every run. A test keeps its last
/// <see cref="MaxRunsPerPlan"/> runs plus its baseline, and its history is deleted with the test.
/// </summary>
public sealed class RunHistoryStore : IDisposable
{
    public const int MaxRunsPerPlan = 30;

    private const string IndexFile = "index.json";

    private readonly string _directory;
    private readonly PlanStore _plans;
    private readonly ILogger<RunHistoryStore> _logger;
    private readonly Lock _gate = new();
    private readonly HistoryIndex _index;

    public RunHistoryStore(PlanStore plans, IConfiguration configuration, IHostEnvironment environment, ILogger<RunHistoryStore> logger)
    {
        _plans = plans;
        _logger = logger;
        _directory = Path.Combine(PlanStore.DataDirectory(configuration, environment), "runs");
        Directory.CreateDirectory(_directory);
        _index = LoadIndex();

        // Only a change to the saved tests prunes the history, not startup: if plans.json couldn't be read, every
        // test would look deleted and its history would go with it.
        _plans.Changed += OnPlansChanged;
    }

    /// <summary>Raised when a run is added or deleted, or a baseline changes. May be raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>The test's stored runs, newest first, only those with every tag in <paramref name="tags"/> when given.</summary>
    public IReadOnlyList<RunHistoryEntry> GetForPlan(Guid planId, IReadOnlyCollection<string>? tags = null)
    {
        lock (_gate)
        {
            return _index.Runs
                .Where(r => r.PlanId == planId && (tags is null || RunLabels.HasAll(r.Tags, tags)))
                .OrderByDescending(r => r.StartedAt)
                .ToList();
        }
    }

    /// <summary>Every tag used in the test's history, most used first, for the filter.</summary>
    public IReadOnlyList<string> GetTags(Guid planId)
    {
        lock (_gate)
        {
            return _index.Runs
                .Where(r => r.PlanId == planId)
                .SelectMany(r => r.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .ToList();
        }
    }

    public RunHistoryEntry? GetEntry(Guid runId)
    {
        lock (_gate)
        {
            return _index.Runs.FirstOrDefault(r => r.RunId == runId);
        }
    }

    public Guid? GetBaselineId(Guid planId)
    {
        lock (_gate)
        {
            return _index.Baselines.TryGetValue(planId, out var runId) ? runId : null;
        }
    }

    public RunHistoryEntry? GetBaseline(Guid planId)
    {
        lock (_gate)
        {
            return _index.Baselines.TryGetValue(planId, out var runId) ? _index.Runs.FirstOrDefault(r => r.RunId == runId) : null;
        }
    }

    /// <summary>Reads a stored run in full, with its timeline and histograms. Null if it isn't stored or can't be read.</summary>
    public RunExport? Load(Guid runId)
    {
        if (GetEntry(runId) is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RunExport>(File.ReadAllText(RunPath(runId)), PlanStore.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read stored run {RunId}", runId);
            return null;
        }
    }

    /// <summary>Stores a finished run of a saved test. Runs of tests that aren't saved, such as ad-hoc API runs, are skipped.</summary>
    public bool Add(RunExport run)
    {
        if (!run.Results.IsFinished || _plans.Get(run.Plan.Id) is null)
        {
            return false;
        }

        // The run file is written before the index points at it, so a crash in between leaves at most an orphan file.
        WriteAtomically(RunPath(run.Id), JsonSerializer.Serialize(run, PlanStore.JsonOptions));
        var entry = new RunHistoryEntry
        {
            RunId = run.Id,
            PlanId = run.Plan.Id,
            PlanName = run.Plan.Name,
            StartedAt = run.StartedAt,
            Machine = run.Machine,
            Tags = run.Tags,
            Note = run.Note,
            Results = run.Results with { Timeline = [] },
        };

        lock (_gate)
        {
            _index.Runs.RemoveAll(r => r.RunId == run.Id);
            _index.Runs.Add(entry);
            Trim(run.Plan.Id);
            PersistIndex();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Replaces the tags and note of a stored run, in its file and in the index. False if the run isn't stored or can't
    /// be read. The values must already be valid, see <see cref="RunLabels"/>.
    /// </summary>
    public bool Annotate(Guid runId, IReadOnlyList<string> tags, string? note)
    {
        // The whole check-and-write is under the lock so two edits of one run can't interleave their file and index.
        lock (_gate)
        {
            var entry = _index.Runs.FirstOrDefault(r => r.RunId == runId);
            if (entry is null || Load(runId) is not { } run)
            {
                return false;
            }

            WriteAtomically(RunPath(runId), JsonSerializer.Serialize(run with { Tags = tags, Note = note }, PlanStore.JsonOptions));
            _index.Runs[_index.Runs.IndexOf(entry)] = entry with { Tags = tags, Note = note };
            PersistIndex();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Makes <paramref name="runId"/> the baseline of its test. False if the run isn't in that test's history.</summary>
    public bool SetBaseline(Guid planId, Guid runId)
    {
        lock (_gate)
        {
            if (!_index.Runs.Any(r => r.RunId == runId && r.PlanId == planId))
            {
                return false;
            }

            _index.Baselines[planId] = runId;
            PersistIndex();
        }

        Changed?.Invoke();
        return true;
    }

    public bool ClearBaseline(Guid planId)
    {
        lock (_gate)
        {
            if (!_index.Baselines.Remove(planId))
            {
                return false;
            }

            PersistIndex();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Deletes a stored run. If it was its test's baseline, the test no longer has one.</summary>
    public bool Delete(Guid runId)
    {
        lock (_gate)
        {
            var entry = _index.Runs.FirstOrDefault(r => r.RunId == runId);
            if (entry is null)
            {
                return false;
            }

            RemoveRuns([entry]);
            PersistIndex();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>Deletes the history of tests that were deleted, since nothing in the UI can reach it any more.</summary>
    private void OnPlansChanged()
    {
        var saved = _plans.GetAll().Select(p => p.Id).ToHashSet();
        lock (_gate)
        {
            var orphans = _index.Runs.Where(r => !saved.Contains(r.PlanId)).ToList();
            var orphanBaselines = _index.Baselines.Keys.Where(id => !saved.Contains(id)).ToList();
            if (orphans.Count == 0 && orphanBaselines.Count == 0)
            {
                return;
            }

            RemoveRuns(orphans);
            foreach (var planId in orphanBaselines)
            {
                _index.Baselines.Remove(planId);
            }

            PersistIndex();
        }

        Changed?.Invoke();
    }

    /// <summary>Keeps the newest runs of a test and its baseline, however old that is.</summary>
    private void Trim(Guid planId)
    {
        var baseline = _index.Baselines.GetValueOrDefault(planId);
        var old = _index.Runs
            .Where(r => r.PlanId == planId && r.RunId != baseline)
            .OrderByDescending(r => r.StartedAt)
            .Skip(MaxRunsPerPlan)
            .ToList();
        RemoveRuns(old);
    }

    private void RemoveRuns(IReadOnlyCollection<RunHistoryEntry> entries)
    {
        foreach (var entry in entries)
        {
            _index.Runs.Remove(entry);
            if (_index.Baselines.TryGetValue(entry.PlanId, out var baseline) && baseline == entry.RunId)
            {
                _index.Baselines.Remove(entry.PlanId);
            }

            try
            {
                File.Delete(RunPath(entry.RunId));
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not delete stored run {RunId}", entry.RunId);
            }
        }
    }

    private HistoryIndex LoadIndex()
    {
        var path = Path.Combine(_directory, IndexFile);
        if (File.Exists(path))
        {
            try
            {
                var index = JsonSerializer.Deserialize<HistoryIndex>(File.ReadAllText(path), PlanStore.JsonOptions);
                if (index is not null)
                {
                    return index;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Could not read the run history index {Path}; rebuilding it from the stored runs", path);
            }
        }

        return RebuildIndex();
    }

    /// <summary>Recovers the summaries from the run files when the index is missing or damaged. Baselines are lost.</summary>
    private HistoryIndex RebuildIndex()
    {
        var index = new HistoryIndex();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (Path.GetFileName(file) == IndexFile)
            {
                continue;
            }

            try
            {
                if (JsonSerializer.Deserialize<RunExport>(File.ReadAllText(file), PlanStore.JsonOptions) is { } run)
                {
                    index.Runs.Add(new RunHistoryEntry
                    {
                        RunId = run.Id,
                        PlanId = run.Plan.Id,
                        PlanName = run.Plan.Name,
                        StartedAt = run.StartedAt,
                        Machine = run.Machine,
                        Tags = run.Tags,
                        Note = run.Note,
                        Results = run.Results with { Timeline = [] },
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogWarning(ex, "Skipping unreadable stored run {Path}", file);
            }
        }

        if (index.Runs.Count > 0)
        {
            WriteIndex(index);
        }

        return index;
    }

    private void PersistIndex() => WriteIndex(_index);

    private void WriteIndex(HistoryIndex index) =>
        WriteAtomically(Path.Combine(_directory, IndexFile), JsonSerializer.Serialize(index, PlanStore.JsonOptions));

    private string RunPath(Guid runId) => Path.Combine(_directory, $"{runId}.json");

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose() => _plans.Changed -= OnPlansChanged;

    private sealed class HistoryIndex
    {
        public int Version { get; set; } = 1;

        public List<RunHistoryEntry> Runs { get; set; } = [];

        public Dictionary<Guid, Guid> Baselines { get; set; } = [];
    }
}
