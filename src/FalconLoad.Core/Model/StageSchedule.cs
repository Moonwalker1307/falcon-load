namespace FalconLoad.Core.Model;

/// <summary>
/// The load over time that a list of <see cref="LoadStage"/>s describes. The runner uses it to decide how many virtual
/// users are active or when the next request is due, and the editor draws it as a preview, so both agree.
/// </summary>
public sealed class StageSchedule
{
    private readonly double[] _start;       // Second at which each stage begins.
    private readonly double[] _from;        // Load at the start of each stage.
    private readonly double[] _to;          // Load at the end of each stage.
    private readonly double[] _cumulative;  // Requests due by the start of each stage (the area under the load).

    public StageSchedule(IReadOnlyList<LoadStage> stages)
    {
        var n = stages.Count;
        Stages = stages;
        _start = new double[n];
        _from = new double[n];
        _to = new double[n];
        _cumulative = new double[n + 1];

        double time = 0, previousEnd = 0;
        for (var i = 0; i < n; i++)
        {
            var stage = stages[i];
            _start[i] = time;
            _from[i] = stage.Ramp ? previousEnd : stage.Target;
            _to[i] = stage.Target;
            _cumulative[i + 1] = _cumulative[i] + (_from[i] + _to[i]) / 2 * stage.DurationSeconds;
            time += stage.DurationSeconds;
            previousEnd = stage.Target;
            Peak = Math.Max(Peak, Math.Max(_from[i], _to[i]));
        }

        TotalSeconds = time;
    }

    public IReadOnlyList<LoadStage> Stages { get; }

    public double TotalSeconds { get; }

    /// <summary>The highest load the stages reach.</summary>
    public double Peak { get; }

    /// <summary>The number of requests the stages call for in total, when they are read as a request rate.</summary>
    public double TotalRequests => _cumulative[^1];

    /// <summary>The index of the stage running at <paramref name="seconds"/>, or the last stage once the schedule is over.</summary>
    public int StageIndexAt(double seconds)
    {
        if (Stages.Count == 0)
        {
            return -1;
        }

        // Stage lists are short, so a scan beats keeping the search clever.
        for (var i = Stages.Count - 1; i > 0; i--)
        {
            if (seconds >= _start[i])
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The load at <paramref name="seconds"/>. Before the start it is the first stage's opening value, after the end the last stage's.</summary>
    public double TargetAt(double seconds)
    {
        if (Stages.Count == 0)
        {
            return 0;
        }

        var i = StageIndexAt(seconds);
        var duration = Stages[i].DurationSeconds;
        var fraction = Math.Clamp((seconds - _start[i]) / duration, 0, 1);
        return _from[i] + (_to[i] - _from[i]) * fraction;
    }

    /// <summary>How many requests are due by <paramref name="seconds"/> when the load is a request rate.</summary>
    public double RequestsDueBy(double seconds)
    {
        if (Stages.Count == 0 || seconds <= 0)
        {
            return 0;
        }

        if (seconds >= TotalSeconds)
        {
            return TotalRequests;
        }

        var i = StageIndexAt(seconds);
        var s = seconds - _start[i];
        var slope = (_to[i] - _from[i]) / Stages[i].DurationSeconds;
        return _cumulative[i] + _from[i] * s + slope * s * s / 2;
    }

    /// <summary>
    /// The second at which the request with the given zero-based index is due when the load is a request rate, or null
    /// when the stages call for fewer requests than that. The inverse of <see cref="RequestsDueBy"/>, so a ramp from 0 gets
    /// its first requests spread out rather than waiting for a rate that starts at nothing.
    /// </summary>
    public double? DueSecondOfRequest(double index)
    {
        if (index < 0 || index >= TotalRequests)
        {
            return null;
        }

        // The first stage that has not used up the index; stages that call for no requests are passed over.
        var i = 0;
        while (i < Stages.Count - 1 && _cumulative[i + 1] <= index)
        {
            i++;
        }

        var remaining = index - _cumulative[i];
        if (remaining <= 0)
        {
            return _start[i];
        }

        var duration = (double)Stages[i].DurationSeconds;
        var from = _from[i];
        var slope = (_to[i] - from) / duration;
        if (slope == 0)
        {
            return _start[i] + remaining / from;
        }

        // Solves slope/2 * s^2 + from * s = remaining in the form that stays accurate when from is 0.
        var s = 2 * remaining / (from + Math.Sqrt(Math.Max(0, from * from + 2 * slope * remaining)));
        return _start[i] + s;
    }

    /// <summary>The corners of the load over time, with a vertical edge wherever a stage jumps, for drawing the schedule.</summary>
    public IReadOnlyList<(double Seconds, double Target)> Outline()
    {
        var points = new List<(double, double)>(Stages.Count * 3 + 1);
        double previousEnd = 0;
        for (var i = 0; i < Stages.Count; i++)
        {
            var end = _start[i] + Stages[i].DurationSeconds;
            if (!Stages[i].Ramp)
            {
                points.Add((_start[i], previousEnd));
            }

            points.Add((_start[i], _from[i]));
            points.Add((end, _to[i]));
            previousEnd = _to[i];
        }

        return points;
    }
}
