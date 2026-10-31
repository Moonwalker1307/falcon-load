namespace FalconLoad.Core.Metrics;

/// <summary>
/// Fixed-memory latency histogram with roughly 1% relative precision.
/// Buckets grow logarithmically, so percentiles stay cheap no matter how many samples are recorded.
/// Not thread safe; callers synchronise access.
/// </summary>
public sealed class LatencyHistogram
{
    private const double Growth = 1.01;
    private static readonly double LogGrowth = Math.Log(Growth);

    // 1.01^2400 microseconds is far beyond any sane request timeout (about 7.6 days).
    private const int BucketCount = 2400;

    private readonly long[] _buckets = new long[BucketCount];

    public long Count { get; private set; }

    public double MinMs { get; private set; } = double.MaxValue;

    public double MaxMs { get; private set; }

    public double SumMs { get; private set; }

    public double MeanMs => Count == 0 ? 0 : SumMs / Count;

    public void Record(TimeSpan latency)
    {
        var ms = latency.TotalMilliseconds;
        if (ms < 0)
        {
            ms = 0;
        }

        _buckets[BucketFor(ms * 1000)]++;
        Count++;
        SumMs += ms;
        MinMs = Math.Min(MinMs, ms);
        MaxMs = Math.Max(MaxMs, ms);
    }

    /// <summary>Returns the latency in milliseconds below which <paramref name="percentile"/> percent of samples fall.</summary>
    public double Percentile(double percentile)
    {
        if (Count == 0)
        {
            return 0;
        }

        var rank = (long)Math.Ceiling(Math.Clamp(percentile, 0, 100) / 100.0 * Count);
        rank = Math.Max(rank, 1);
        long seen = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            seen += _buckets[i];
            if (seen >= rank)
            {
                return Math.Clamp(BucketMidpointMs(i), MinMs, MaxMs);
            }
        }

        return MaxMs;
    }

    public LatencyStats ToStats() => Count == 0
        ? LatencyStats.Empty
        : new LatencyStats(MinMs, MeanMs, Percentile(50), Percentile(90), Percentile(95), Percentile(99), MaxMs);

    private static int BucketFor(double microseconds)
    {
        if (microseconds < 1)
        {
            return 0;
        }

        var index = (int)(Math.Log(microseconds) / LogGrowth) + 1;
        return Math.Min(index, BucketCount - 1);
    }

    private static double BucketMidpointMs(int index)
    {
        if (index == 0)
        {
            return 0;
        }

        var lower = Math.Pow(Growth, index - 1);
        var upper = Math.Pow(Growth, index);
        return (lower + upper) / 2 / 1000;
    }
}

public sealed record LatencyStats(double MinMs, double MeanMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs)
{
    public static LatencyStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
