using FalconLoad.Core.Metrics;

namespace FalconLoad.Web.Components.Shared;

public static class Format
{
    public static string Ms(double ms) => ms switch
    {
        0 => "0 ms",
        < 1 => $"{ms:0.00} ms",
        < 100 => $"{ms:0.0} ms",
        < 1000 => $"{ms:0} ms",
        _ => $"{ms / 1000:0.00} s",
    };

    public static string Number(double n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000:0.##}M",
        >= 10_000 => $"{n / 1000:0.#}k",
        _ => n.ToString("#,0"),
    };

    public static string Rate(double perSecond) => perSecond < 10 ? $"{perSecond:0.##}" : $"{perSecond:#,0.#}";

    public static string Percent(double fraction) => fraction == 0 ? "0%" : fraction < 0.001 ? "<0.1%" : $"{fraction:0.#%}";

    /// <summary>A threshold's limit or measured value in its unit: milliseconds, percent or requests per second.</summary>
    public static string Measured(double value, string unit) => unit switch
    {
        "ms" => Ms(value),
        "%" => value == 0 ? "0%" : value < 0.1 ? $"{value:0.###}%" : $"{value:0.##}%",
        _ => $"{Rate(value)} {unit}",
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.##} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:00}s" : $"{t.TotalSeconds:0.0}s";

    public static string When(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>A clock offset such as "+12 ms" or "-1.3 s"; the sign says whether the other clock is ahead.</summary>
    public static string SignedMs(TimeSpan t)
    {
        var sign = t < TimeSpan.Zero ? "-" : "+";
        var ms = Math.Abs(t.TotalMilliseconds);
        return ms < 1000 ? $"{sign}{ms:0} ms" : $"{sign}{ms / 1000:0.0} s";
    }

    public static string StateBadge(RunState state) => "badge badge-" + state.ToString().ToLowerInvariant();

    public static string MethodClass(string method) => "method method-" + method.ToLowerInvariant();

    public static string StatusClass(string code) =>
        code.Length == 3 && char.IsDigit(code[0]) ? $"status-{code[0]}xx"
        : code is RequestResult.ReplyStatus or RequestResult.SentStatus ? "status-2xx"
        : "status-err";

    public static string StopSummary(FalconLoad.Core.Model.LoadProfile p) =>
        p.IsStaged ? Seconds(p.TotalStageSeconds)
        : p.StopCondition == FalconLoad.Core.Model.StopCondition.Duration ? Seconds(p.DurationSeconds)
        : $"{p.TotalRequests:#,0} requests";

    /// <summary>The unit a staged profile's targets are in.</summary>
    public static string StageUnit(FalconLoad.Core.Model.LoadProfile p) =>
        p.StageMode == FalconLoad.Core.Model.StageMode.RequestRate ? "req/s" : "virtual users";

    /// <summary>How much load a profile applies, such as "10 virtual users" or "up to 200 req/s in 5 stages".</summary>
    public static string LoadSummary(FalconLoad.Core.Model.LoadProfile p)
    {
        if (!p.IsStaged)
        {
            return p.Concurrency == 1 ? "1 virtual user" : $"{p.Concurrency:#,0} virtual users";
        }

        var peak = new FalconLoad.Core.Model.StageSchedule(p.Stages).Peak;
        var stages = p.Stages.Count == 1 ? "1 stage" : $"{p.Stages.Count} stages";
        return $"up to {Rate(peak)} {StageUnit(p)} in {stages}";
    }

    /// <summary>A configured length such as "45s" or "2m 30s", without the decimals <see cref="Duration"/> shows for live timings.</summary>
    public static string Seconds(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var parts = new List<string>();
        if (t.TotalHours >= 1)
        {
            parts.Add($"{(int)t.TotalHours}h");
        }

        if (t.Minutes > 0)
        {
            parts.Add($"{t.Minutes}m");
        }

        if (t.Seconds > 0 || parts.Count == 0)
        {
            parts.Add($"{t.Seconds}s");
        }

        return string.Join(' ', parts);
    }

    /// <summary>A whole-second countdown such as "1m 05s left", for the run's progress line.</summary>
    public static string Remaining(TimeSpan t) => t.TotalMinutes >= 1
        ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s left"
        : $"{Math.Max(0, (int)Math.Ceiling(t.TotalSeconds))}s left";

    /// <summary>A compared metric's value in its own unit.</summary>
    public static string Value(MetricUnit unit, double value) => unit switch
    {
        MetricUnit.Milliseconds => Ms(value),
        MetricUnit.RequestsPerSecond => $"{Rate(value)} req/s",
        MetricUnit.Fraction => Percent(value),
        MetricUnit.Bytes => Bytes((long)value),
        _ => value.ToString("#,0"),
    };

    /// <summary>
    /// How a metric moved from the baseline: in percent of the baseline, or for a rate in percentage points, since
    /// "errors up 100%" for 1% to 2% reads far worse than it is.
    /// </summary>
    public static string Change(MetricChange m)
    {
        if (m.Unit == MetricUnit.Fraction)
        {
            var points = m.Change * 100;
            return Math.Abs(points) < 0.05 ? "±0 pts" : $"{(points > 0 ? "+" : "−")}{Math.Abs(points):0.#} pts";
        }

        if (m.RelativeChange is not { } relative)
        {
            return m.Current == 0 ? "±0" : "new";
        }

        return Math.Abs(relative) < 0.0005 ? "±0%" : $"{(relative > 0 ? "+" : "−")}{Math.Abs(relative):0.#%}";
    }

    /// <summary>The CSS class that colours a change: red for a regression, green for an improvement.</summary>
    public static string ChangeClass(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Regressed => "delta delta-bad",
        ChangeVerdict.Improved => "delta delta-good",
        _ => "delta",
    };

    /// <summary>Spoken and tooltip text for a change, since the colour alone doesn't say whether it is good.</summary>
    public static string VerdictText(ChangeVerdict verdict) => verdict switch
    {
        ChangeVerdict.Regressed => "regression",
        ChangeVerdict.Improved => "improvement",
        ChangeVerdict.Unchanged => "within the thresholds",
        _ => "not judged",
    };

    /// <summary>The thresholds in words, to finish the sentence "A run counts as a regression when …".</summary>
    public static string Thresholds(RegressionThresholds t) =>
        $"a latency rises by more than {t.LatencyPercent:0.#}% (and at least {Ms(t.MinLatencyChangeMs)}), " +
        $"throughput falls by more than {t.ThroughputPercent:0.#}%, or the error rate rises by more than " +
        $"{t.ErrorRatePoints:0.#} percentage {(t.ErrorRatePoints == 1 ? "point" : "points")}";
}
