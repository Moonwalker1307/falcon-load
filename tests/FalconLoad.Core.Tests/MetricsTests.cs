using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class MetricsTests
{
    [Fact]
    public void Histogram_PercentilesAreWithinOnePercent()
    {
        var histogram = new LatencyHistogram();
        for (var i = 1; i <= 1000; i++)
        {
            histogram.Record(TimeSpan.FromMilliseconds(i));
        }

        Assert.Equal(1000, histogram.Count);
        Assert.Equal(1, histogram.MinMs, 3);
        Assert.Equal(1000, histogram.MaxMs, 3);
        Assert.Equal(500.5, histogram.MeanMs, 3);
        Assert.InRange(histogram.Percentile(50), 495, 505);
        Assert.InRange(histogram.Percentile(99), 980, 1000);
    }

    [Fact]
    public void Histogram_EmptyReturnsZeros()
    {
        Assert.Equal(LatencyStats.Empty, new LatencyHistogram().ToStats());
    }

    [Theory]
    [InlineData(null, 200, true)]
    [InlineData("", 302, true)]
    [InlineData(null, 404, false)]
    [InlineData("200,201", 201, true)]
    [InlineData("200,201", 204, false)]
    [InlineData("200-299, 404", 404, true)]
    public void StatusCodeMatcher_Matches(string? expression, int code, bool expected)
    {
        Assert.True(StatusCodeMatcher.TryParse(expression, out var matcher));
        Assert.Equal(expected, matcher.IsMatch(code));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("299-200")]
    [InlineData("99")]
    public void StatusCodeMatcher_RejectsInvalid(string expression)
    {
        Assert.False(StatusCodeMatcher.TryParse(expression, out _));
    }

    [Fact]
    public void Templates_ExpandVariables()
    {
        var context = new TemplateContext(3, 7);
        Assert.Equal("vu=3 it=7", TemplateRenderer.Render("vu={{$vu}} it={{$iteration}}", context));
        Assert.True(Guid.TryParse(TemplateRenderer.Render("{{$guid}}", context), out _));
        var n = int.Parse(TemplateRenderer.Render("{{$randomInt:5:9}}", context));
        Assert.InRange(n, 5, 9);
        Assert.Equal("{{$unknown}}", TemplateRenderer.Render("{{$unknown}}", context));
    }

    [Fact]
    public void Collector_BuildsTimelineAndErrorBreakdown()
    {
        var collector = new MetricsCollector(["a"]);
        collector.Record(new RequestResult(0, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), 200, true, null, 0, 10));
        collector.Record(new RequestResult(0, TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(50), 500, false, "Unexpected status 500", 0, 10));

        var snapshot = collector.Snapshot(RunState.Completed, TimeSpan.FromSeconds(2.3), 0);

        Assert.Equal(2, snapshot.Timeline.Count);
        Assert.Equal(1, snapshot.Timeline[0].Requests);
        Assert.Equal(1, snapshot.Timeline[1].Failures);
        Assert.Equal(1, snapshot.Errors.Single().Count);
        Assert.Equal(2 / 2.3, snapshot.RequestsPerSecond, 6);
    }
}
