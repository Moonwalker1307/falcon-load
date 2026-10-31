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
        if (data.Offset < 0 || data.Offset + counts.Length > BucketCount)
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
        }

        if (total != data.Count)
        {
            throw new FormatException($"The histogram buckets add up to {total}, not {data.Count}.");
        }

        if (total == 0)
        {
            return histogram;
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
