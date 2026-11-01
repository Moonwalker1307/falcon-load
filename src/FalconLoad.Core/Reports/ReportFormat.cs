using System.Globalization;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Reports;

/// <summary>
/// Formats numbers as text for the command line summary and the run reports. Everything uses the invariant culture, so a
/// report reads the same on every machine that produced it.
/// </summary>
public static class ReportFormat
{
    /// <summary>A threshold's measured value in the unit it is written in: ms, % or req/s.</summary>
    public static string Value(double value, string unit) => unit switch
    {
        "ms" => Ms(value),
        "%" => Invariant($"{value:0.##}%"),
        "" => Invariant($"{value:0.##}"),
        _ => Invariant($"{value:#,0.##} {unit}"),
    };

    public static string Ms(double ms) => ms switch
    {
        0 => "0 ms",
        < 1 => Invariant($"{ms:0.00} ms"),
        < 100 => Invariant($"{ms:0.0} ms"),
        < 1000 => Invariant($"{ms:0} ms"),
        _ => Invariant($"{ms / 1000:0.00} s"),
    };

    public static string Rate(double perSecond) => perSecond < 10 ? Invariant($"{perSecond:0.##}") : Invariant($"{perSecond:#,0.#}");

    public static string Percent(double fraction) =>
        fraction == 0 ? "0%" : fraction < 0.0001 ? "<0.01%" : Invariant($"{fraction * 100:0.##}%");

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => Invariant($"{bytes / (double)(1L << 30):0.##} GB"),
        >= 1L << 20 => Invariant($"{bytes / (double)(1L << 20):0.##} MB"),
        >= 1L << 10 => Invariant($"{bytes / 1024.0:0.#} KB"),
        _ => Invariant($"{bytes} B"),
    };

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? Invariant($"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s")
        : t.TotalMinutes >= 1 ? Invariant($"{t.Minutes}m {t.Seconds:00}s") : Invariant($"{t.TotalSeconds:0.0}s");

    public static string Count(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>
    /// A request of the plan as its method and route, such as <c>GET /users/{{id}}</c>. The route comes from the URL as
    /// written, so it holds no environment values and may appear in reports.
    /// </summary>
    public static string RequestRoute(LoadTestPlan plan, int step) =>
        step >= 0 && step < plan.Requests.Count ? $"{plan.Requests[step].Label} {plan.Requests[step].Route}" : $"Request {step + 1}";

    /// <summary>The most frequent errors with the request that made them, for text reports; falls back to the errors alone for results saved before errors were counted per request.</summary>
    public static IEnumerable<(long Count, string Text)> ErrorLines(LoadTestPlan plan, Metrics.LoadTestSnapshot s, int max) =>
        s.RequestErrors.Count > 0
            ? s.RequestErrors.OrderByDescending(e => e.Count).Take(max).Select(e => (e.Count, $"{RequestRoute(plan, e.StepIndex)}: {e.Message}"))
            : s.Errors.Take(max).Select(e => (e.Count, e.Message));

    /// <summary>The load a profile applies, in one line, such as "20 virtual users, for 30s".</summary>
    public static string DescribeProfile(LoadProfile profile)
    {
        if (profile.IsStaged)
        {
            var peak = new StageSchedule(profile.Stages).Peak;
            var unit = profile.StageMode == StageMode.RequestRate ? "req/s" : "virtual users";
            var pool = profile.StageMode == StageMode.RequestRate ? $", sent by up to {profile.Concurrency:#,0} virtual users" : string.Empty;
            return $"{profile.Stages.Count} {(profile.Stages.Count == 1 ? "stage" : "stages")} over {profile.TotalStageSeconds}s, up to {peak:#,0.##} {unit}{pool}";
        }

        var stop = profile.StopCondition == StopCondition.Duration
            ? $"for {profile.DurationSeconds}s"
            : $"{profile.TotalRequests:#,0} requests";
        var users = profile.Concurrency == 1 ? "1 virtual user" : $"{profile.Concurrency:#,0} virtual users";
        return $"{users}, {stop}";
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
