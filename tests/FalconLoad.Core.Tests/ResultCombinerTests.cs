using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class ResultCombinerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Histogram_MergedEqualsOneHistogramWithEverySample()
    {
        var all = new LatencyHistogram();
        var a = new LatencyHistogram();
        var b = new LatencyHistogram();
        for (var i = 1; i <= 2000; i++)
        {
            var latency = TimeSpan.FromMilliseconds(i * 0.37);
            all.Record(latency);
            (i % 3 == 0 ? a : b).Record(latency);
        }

        a.Add(b);

        // Percentiles come from the same buckets, so they match exactly; the mean only differs by rounding.
        Assert.Equal(all.Count, a.Count);
        Assert.Equal(all.ToStats() with { MeanMs = 0 }, a.ToStats() with { MeanMs = 0 });
        Assert.Equal(all.MeanMs, a.MeanMs, 9);
        Assert.Equal(all.Percentile(99.9), a.Percentile(99.9));
    }

    [Fact]
    public void HistogramData_RoundTripsThroughJson()
    {
        var histogram = new LatencyHistogram();
        foreach (var ms in new[] { 3.0, 3.1, 40, 41, 900 })
        {
            histogram.Record(TimeSpan.FromMilliseconds(ms));
        }

        var json = JsonSerializer.Serialize(histogram.ToData(), Json);
        var data = JsonSerializer.Deserialize<HistogramData>(json, Json)!;
        var copy = LatencyHistogram.FromData(data);

        Assert.Equal(histogram.ToStats(), copy.ToStats());
        // The bucket counts are written on one line, so a long run's export stays small.
        Assert.Single(json.Split('\n'), line => line.Contains("\"counts\": [") && line.TrimEnd().EndsWith("],"));
    }

    [Fact]
    public void HistogramData_RejectsCountsThatDontAddUp()
    {
        var data = new HistogramData(10, [1, 2, 3], 7, 1, 2, 9);
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(data));
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(2399, [1, 1], 2, 1, 2, 3)));
    }

    [Fact]
    public async Task Combine_AddsUpMachinesAndMergesLatency()
    {
        var fast = await RunAsync("load-1", HttpStatusCode.OK, requests: 40, Start);
        var slow = await RunAsync("load-2", HttpStatusCode.ServiceUnavailable, requests: 10, Start.AddSeconds(5));

        var result = ResultCombiner.Combine([fast, slow]);
        var combined = result.Combined;

        Assert.Equal(50, combined.TotalRequests);
        Assert.Equal(40, combined.SuccessfulRequests);
        Assert.Equal(10, combined.FailedRequests);
        Assert.Equal(0.2, combined.ErrorRate, 6);
        Assert.Equal(40, combined.StatusCodes["200"]);
        Assert.Equal(10, combined.StatusCodes["503"]);
        Assert.Equal(10, Assert.Single(combined.Errors).Count);

        var step = Assert.Single(combined.Steps);
        Assert.Equal(50, step.Requests);
        Assert.Equal(10, step.Failures);

        var expected = LatencyHistogram.FromData(fast.Histograms!.Overall);
        expected.Add(LatencyHistogram.FromData(slow.Histograms!.Overall));
        Assert.Equal(expected.ToStats() with { MeanMs = 0 }, combined.Latency with { MeanMs = 0 });
        Assert.Equal(expected.MeanMs, combined.Latency.MeanMs, 9);

        // The second machine started five seconds later, so its requests land five seconds into the timeline.
        Assert.Equal(["load-1", "load-2"], result.Machines.Select(m => m.Name));
        Assert.Equal(5, result.Machines[1].OffsetSeconds);
        Assert.Equal(40, combined.Timeline[0].Requests);
        Assert.Equal(10, combined.Timeline[5].Requests);
        Assert.Equal(10, combined.Timeline[5].Failures);
        Assert.Equal(0, combined.Timeline[3].Requests);
        Assert.Equal(Start, result.StartedAt);
        Assert.Equal(slow.StartedAt + slow.Results.Elapsed - Start, combined.Elapsed);

        // The runs didn't overlap, which makes the averaged throughput misleading.
        Assert.Contains(result.Warnings, w => w.Contains("overlapped"));
    }

    [Fact]
    public async Task Combine_ReadsExportedJson()
    {
        var a = await RunAsync("load-1", HttpStatusCode.OK, requests: 20, Start);
        var b = await RunAsync("load-1", HttpStatusCode.OK, requests: 30, Start);

        var files = new[] { a, b }.Select(r => JsonSerializer.Serialize(r, Json)).ToList();
        var imported = files.Select(f => JsonSerializer.Deserialize<RunExport>(f, Json)!).ToList();
        var result = ResultCombiner.Combine(imported);

        Assert.Equal(50, result.Combined.TotalRequests);
        Assert.Equal(50, result.Combined.Timeline.Sum(t => t.Requests));
        Assert.Equal(["load-1 #1", "load-1 #2"], result.Machines.Select(m => m.Name));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Validate_RejectsFilesWithoutHistogramsAndDuplicates()
    {
        var run = await RunAsync("load-1", HttpStatusCode.OK, requests: 5, Start);
        var old = run with { Id = Guid.NewGuid(), Histograms = null };

        var problems = ResultCombiner.Validate([run, run, old]);

        Assert.Contains(problems, p => p.Contains("added twice"));
        Assert.Contains(problems, p => p.Contains("no latency histograms"));
        Assert.Throws<ArgumentException>(() => ResultCombiner.Combine([old]));
        Assert.NotEmpty(ResultCombiner.Validate([]));
    }

    private static async Task<RunExport> RunAsync(string machine, HttpStatusCode status, int requests, DateTimeOffset startedAt)
    {
        var plan = new LoadTestPlan
        {
            Name = "Products",
            Requests = [new RequestDefinition { Url = "http://api.test/products" }],
            Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
        };
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(status)));
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        var (snapshot, histograms) = runner.GetResults();
        return new RunExport
        {
            Id = Guid.NewGuid(),
            StartedAt = startedAt,
            Machine = machine,
            Plan = runner.Plan,
            Results = snapshot,
            Histograms = histograms,
        };
    }
}
