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
    /// <summary>Number of virtual users sending requests in parallel.</summary>
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

    public IEnumerable<string> Validate()
    {
        if (Concurrency is < 1 or > 10_000)
        {
            yield return "Concurrency must be between 1 and 10,000.";
        }

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

        if (TimeoutSeconds is < 1 or > 600)
        {
            yield return "Timeout must be between 1 and 600 seconds.";
        }

        if (MaxRequestsPerSecond < 0)
        {
            yield return "Max requests per second cannot be negative.";
        }
    }
}
