using System.Text.Json.Serialization;

namespace FalconLoad.Core.Model;

public enum StopCondition
{
    /// <summary>Run for <see cref="LoadProfile.DurationSeconds"/>.</summary>
    Duration,

    /// <summary>Stop after <see cref="LoadProfile.TotalRequests"/> requests have been sent.</summary>
    RequestCount,
}

/// <summary>How hard, and for how long, to hit the target.</summary>
public sealed class LoadProfile
{
    /// <summary>
    /// Number of virtual users sending requests in parallel. With stages that set a request rate it is the pool of users
    /// that send them; with stages that set virtual users it is not used.
    /// </summary>
    public int Concurrency { get; set; } = 10;

    public StopCondition StopCondition { get; set; } = StopCondition.Duration;

    public int DurationSeconds { get; set; } = 30;

    public int TotalRequests { get; set; } = 1000;

    /// <summary>Virtual users are started gradually over this many seconds. 0 starts them all at once.</summary>
    public int RampUpSeconds { get; set; }

    /// <summary>Optional global cap on requests per second. 0 or less means unlimited.</summary>
    public double MaxRequestsPerSecond { get; set; }

    /// <summary>Per request timeout.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Accept invalid or self signed TLS certificates (handy for local dev APIs).</summary>
    public bool IgnoreTlsErrors { get; set; }

    public bool FollowRedirects { get; set; } = true;

    /// <summary>
    /// Load that changes over time. When there are stages they replace the stop condition, the ramp-up and, for virtual
    /// user stages, the concurrency: the test lasts as long as the stages add up to. Plans saved before stages existed
    /// have none and run at a constant load.
    /// </summary>
    public List<LoadStage> Stages { get; set; } = [];

    /// <summary>Whether the stages set virtual users or a request rate.</summary>
    public StageMode StageMode { get; set; } = StageMode.VirtualUsers;

    [JsonIgnore]
    public bool IsStaged => Stages.Count > 0;

    /// <summary>How long the test lasts when it is staged, otherwise the configured duration (which a request count may cut short).</summary>
    [JsonIgnore]
    public int TotalStageSeconds => Stages.Sum(s => s.DurationSeconds);

    /// <summary>The most virtual users the test will have at once.</summary>
    [JsonIgnore]
    public int PeakVirtualUsers => IsStaged && StageMode == StageMode.VirtualUsers
        ? (int)Math.Ceiling(new StageSchedule(Stages).Peak)
        : Concurrency;

    /// <summary>
    /// Added to the virtual user numbers that <c>{{$vu}}</c> reports. A controller sets it on each agent's share of a
    /// distributed test, so every virtual user across all agents has its own number. 0 for a test on one machine.
    /// </summary>
    public int VirtualUserOffset { get; set; }

    public IEnumerable<string> Validate()
    {
        var staged = IsStaged;

        // Virtual user stages choose the number of users themselves, so the fixed number only matters otherwise.
        if ((!staged || StageMode == StageMode.RequestRate) && Concurrency is < 1 or > 10_000)
        {
            yield return staged
                ? "Virtual users must be between 1 and 10,000."
                : "Concurrency must be between 1 and 10,000.";
        }

        if (staged)
        {
            foreach (var problem in ValidateStages())
            {
                yield return problem;
            }
        }
        else
        {
            if (StopCondition == StopCondition.Duration && DurationSeconds is < 1 or > 86_400)
            {
                yield return "Duration must be between 1 second and 24 hours.";
            }

            if (StopCondition == StopCondition.RequestCount && TotalRequests < 1)
            {
                yield return "Total requests must be at least 1.";
            }

            if (RampUpSeconds < 0)
            {
                yield return "Ramp-up cannot be negative.";
            }
        }

        if (TimeoutSeconds is < 1 or > 600)
        {
            yield return "Timeout must be between 1 and 600 seconds.";
        }

        if (MaxRequestsPerSecond < 0)
        {
            yield return "Max requests per second cannot be negative.";
        }

        if (VirtualUserOffset < 0)
        {
            yield return "The virtual user offset cannot be negative.";
        }
    }

    private const int MaxStages = 100;

    /// <summary>The problems with <see cref="Stages"/> alone, for showing them next to the stages. Empty when there are no stages.</summary>
    public IEnumerable<string> ValidateStages()
    {
        if (!IsStaged)
        {
            yield break;
        }

        if (Stages.Count > MaxStages)
        {
            yield return $"Use at most {MaxStages} stages.";
            yield break;
        }

        var unit = StageMode == StageMode.VirtualUsers ? "virtual users" : "requests per second";
        var maxTarget = StageMode == StageMode.VirtualUsers ? 10_000 : 100_000;
        long total = 0;
        for (var i = 0; i < Stages.Count; i++)
        {
            var stage = Stages[i];
            total += Math.Max(0, stage.DurationSeconds);
            if (stage.DurationSeconds is < 1 or > 86_400)
            {
                yield return $"Stage {i + 1}: the duration must be between 1 second and 24 hours.";
            }

            if (!(stage.Target >= 0 && stage.Target <= maxTarget))
            {
                yield return $"Stage {i + 1}: the target must be between 0 and {maxTarget:#,0} {unit}.";
            }
        }

        if (total > 86_400)
        {
            yield return "The stages add up to more than 24 hours.";
        }

        if (!Stages.Any(s => s.Target > 0))
        {
            yield return "At least one stage needs a target above 0.";
        }
    }
}
