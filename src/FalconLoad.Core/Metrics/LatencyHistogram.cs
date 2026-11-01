using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Adds every sample of <paramref name="other"/> to this histogram. Both use the same buckets, so the result is
    /// exactly what one histogram would hold had it recorded all the samples itself.
    /// </summary>
    public void Add(LatencyHistogram other)
    {
        if (other.Count == 0)
        {
            return;
        }

        for (var i = 0; i < BucketCount; i++)
        {
            _buckets[i] += other._buckets[i];
        }

        Count += other.Count;
        SumMs += other.SumMs;
        MinMs = Math.Min(MinMs, other.MinMs);
        MaxMs = Math.Max(MaxMs, other.MaxMs);
    }

    /// <summary>A compact, serialisable copy of the buckets, from the first non-empty bucket to the last.</summary>
    public HistogramData ToData()
    {
        if (Count == 0)
        {
            return HistogramData.Empty;
        }

        var first = Array.FindIndex(_buckets, c => c != 0);
        var last = Array.FindLastIndex(_buckets, c => c != 0);
        return new HistogramData(first, _buckets[first..(last + 1)], Count, MinMs, MaxMs, SumMs);
    }

    /// <summary>Rebuilds a histogram from <see cref="ToData"/>. Throws <see cref="FormatException"/> if the data is inconsistent.</summary>
    public static LatencyHistogram FromData(HistogramData data)
    {
        var histogram = new LatencyHistogram();
        var counts = data.Counts ?? [];
        if (data.Offset < 0 || (long)data.Offset + counts.Length > BucketCount)
        {
            throw new FormatException($"The histogram buckets {data.Offset}..{data.Offset + counts.Length} are outside 0..{BucketCount}.");
        }

        long total = 0;
        foreach (var count in counts)
        {
            if (count < 0)
            {
                throw new FormatException("A histogram bucket has a negative count.");
            }

            total += count;
            if (total < 0)
            {
                throw new FormatException("The histogram buckets add up to more than a histogram can count.");
            }
        }

        if (total != data.Count)
        {
            throw new FormatException($"The histogram buckets add up to {total}, not {data.Count}.");
        }

        if (total == 0)
        {
            return histogram;
        }

        // Percentiles are clamped between the minimum and the maximum, so those have to be real latencies in order.
        if (!(data.MinMs >= 0 && data.MinMs <= data.MaxMs && double.IsFinite(data.MaxMs) && double.IsFinite(data.SumMs)))
        {
            throw new FormatException($"The histogram's latencies don't fit together: minimum {data.MinMs} ms, maximum {data.MaxMs} ms, sum {data.SumMs} ms.");
        }

        counts.CopyTo(histogram._buckets, data.Offset);
        histogram.Count = total;
        histogram.SumMs = data.SumMs;
        histogram.MinMs = data.MinMs;
        histogram.MaxMs = data.MaxMs;
        return histogram;
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

    /// <summary>
    /// The samples of <paramref name="now"/> that <paramref name="before"/>, an earlier copy of the same histogram,
    /// didn't hold yet: what was recorded in between. The mean is exact; the minimum and maximum are exact when they
    /// changed in between and within a bucket's width otherwise. A <paramref name="before"/> that holds more than
    /// <paramref name="now"/> belongs to something else and is ignored.
    /// </summary>
    internal static LatencyHistogram Since(HistogramData now, HistogramData? before)
    {
        if (before is not null && (before.Count > now.Count || before.Count == 0))
        {
            before = null;
        }

        var histogram = new LatencyHistogram();
        var counts = now.Counts ?? [];
        int first = -1, last = -1;
        for (var i = 0; i < counts.Length; i++)
        {
            var bucket = now.Offset + i;
            if (bucket is < 0 or >= BucketCount)
            {
                continue;
            }

            var added = counts[i] - (before?.CountAt(bucket) ?? 0);
            if (added <= 0)
            {
                continue;
            }

            histogram._buckets[bucket] = added;
            histogram.Count += added;
            first = first < 0 ? bucket : first;
            last = bucket;
        }

        if (histogram.Count == 0)
        {
            return new LatencyHistogram();
        }

        histogram.SumMs = Math.Max(0, now.SumMs - (before?.SumMs ?? 0));
        histogram.MinMs = before is null || now.MinMs < before.MinMs ? now.MinMs : Math.Max(BucketMidpointMs(first), now.MinMs);
        histogram.MaxMs = before is null || now.MaxMs > before.MaxMs ? now.MaxMs : Math.Min(BucketMidpointMs(last), now.MaxMs);
        histogram.MinMs = Math.Min(histogram.MinMs, histogram.MaxMs);
        return histogram;
    }

    /// <summary>
    /// Sorts the samples of <paramref name="data"/> into coarser buckets with the given upper bounds, as a Prometheus
    /// or OpenTelemetry histogram has them. Returns one count per bound plus one for samples above the last bound, not
    /// cumulative. Each fine bucket goes whole to the coarse bucket its midpoint falls in, so a sample within about
    /// 1% of a bound may be counted on the other side of it.
    /// </summary>
    internal static long[] Rebucket(HistogramData data, IReadOnlyList<double> upperBoundsMs)
    {
        var result = new long[upperBoundsMs.Count + 1];
        var counts = data.Counts ?? [];
        var target = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] <= 0)
            {
                continue;
            }

            var ms = Math.Clamp(BucketMidpointMs(data.Offset + i), data.MinMs, Math.Max(data.MinMs, data.MaxMs));
            while (target < upperBoundsMs.Count && ms > upperBoundsMs[target])
            {
                target++;
            }

            result[target] += counts[i];
        }

        return result;
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

/// <summary>
/// The buckets of a <see cref="LatencyHistogram"/>, as written to an exported result so that results from several
/// machines can be merged without losing percentile precision. <see cref="Counts"/> starts at bucket <see cref="Offset"/>.
/// </summary>
public sealed record HistogramData(
    int Offset,
    [property: JsonConverter(typeof(CompactArrayConverter))] long[] Counts,
    long Count,
    double MinMs,
    double MaxMs,
    double SumMs)
{
    public static HistogramData Empty { get; } = new(0, [], 0, 0, 0, 0);

    internal long CountAt(int bucket)
    {
        var i = bucket - Offset;
        return Counts is not null && i >= 0 && i < Counts.Length ? Counts[i] : 0;
    }
}

/// <summary>Writes a number array on one line even in indented JSON, so an exported histogram stays small and readable.</summary>
internal sealed class CompactArrayConverter : JsonConverter<long[]>
{
    public override long[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of numbers.");
        }

        var values = new List<long>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.GetInt64());
        }

        return [.. values];
    }

    public override void Write(Utf8JsonWriter writer, long[] value, JsonSerializerOptions options) =>
        writer.WriteRawValue("[" + string.Join(',', value) + "]", skipInputValidation: true);
}

public sealed record LatencyStats(double MinMs, double MeanMs, double P50Ms, double P90Ms, double P95Ms, double P99Ms, double MaxMs)
{
    public static LatencyStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
