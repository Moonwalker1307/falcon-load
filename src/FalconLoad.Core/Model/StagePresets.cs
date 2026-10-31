namespace FalconLoad.Core.Model;

/// <summary>
/// Starting points for the usual staged tests. They only fill in stages, which stay ordinary stages the user can edit,
/// so a saved plan never depends on a preset.
/// </summary>
public static class StagePresets
{
    /// <summary>Climbs to <paramref name="peak"/> in equal steps, holding each one, to find where latency or errors start to rise.</summary>
    public static List<LoadStage> Step(double peak, int steps = 5, int secondsPerStep = 60)
    {
        steps = Math.Max(1, steps);
        return [.. Enumerable.Range(1, steps).Select(k => Hold(Math.Max(1, Math.Round(peak * k / steps)), secondsPerStep))];
    }

    /// <summary>A quiet baseline, a sudden burst up to <paramref name="peak"/>, then the baseline again to see whether the system recovers.</summary>
    public static List<LoadStage> Spike(double peak, int baselineSeconds = 60, int spikeSeconds = 30, int recoverySeconds = 120)
    {
        var baseline = Math.Max(1, Math.Round(peak / 10));
        return [Hold(baseline, baselineSeconds), Hold(peak, spikeSeconds), Hold(baseline, recoverySeconds)];
    }

    /// <summary>Climbs to <paramref name="peak"/> and stays there for a long time, to expose leaks and slow degradation.</summary>
    public static List<LoadStage> Soak(double peak, int rampSeconds = 120, int holdSeconds = 1800) =>
        [new LoadStage { DurationSeconds = rampSeconds, Target = peak, Ramp = true }, Hold(peak, holdSeconds)];

    private static LoadStage Hold(double target, int seconds) => new() { DurationSeconds = seconds, Target = target };
}
