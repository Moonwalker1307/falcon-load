using System.Text.Json;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class RunLabelsTests
{
    [Fact]
    public void Tags_AreTrimmed_AndRepeatsIgnoringCaseAreDropped()
    {
        var problems = RunLabels.NormalizeTags([" build-42 ", "", null, "Build-42", "abc123"], out var tags);

        Assert.Empty(problems);
        Assert.Equal(["build-42", "abc123"], tags);
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("line\nbreak")]
    public void Tags_WithCommasOrControlCharacters_AreRejected(string tag)
    {
        Assert.NotEmpty(RunLabels.NormalizeTags([tag], out var tags));
        Assert.Empty(tags);
    }

    [Fact]
    public void Tags_HaveALimitOnCountAndLength()
    {
        Assert.NotEmpty(RunLabels.NormalizeTags(Enumerable.Range(0, RunLabels.MaxTags + 1).Select(i => $"t{i}"), out _));
        Assert.NotEmpty(RunLabels.NormalizeTags([new string('x', RunLabels.MaxTagLength + 1)], out _));
        Assert.Empty(RunLabels.NormalizeTags(Enumerable.Range(0, RunLabels.MaxTags).Select(i => $"t{i}"), out _));
    }

    [Fact]
    public void Note_IsTrimmed_EmptyIsNull_AndTooLongIsRejected()
    {
        Assert.Null(RunLabels.NormalizeNote("  hi  ", out var note, out _));
        Assert.Equal("hi", note);

        Assert.Null(RunLabels.NormalizeNote("   ", out note, out _));
        Assert.Null(note);

        Assert.NotNull(RunLabels.NormalizeNote(new string('x', RunLabels.MaxNoteLength + 1), out note, out _));
        Assert.Null(note);
    }

    [Fact]
    public void HasAll_NeedsEveryTag_IgnoringCase_AndNoTagsMatchesEverything()
    {
        IReadOnlyList<string> tags = ["build-42", "main"];

        Assert.True(RunLabels.HasAll(tags, ["BUILD-42"]));
        Assert.True(RunLabels.HasAll(tags, ["build-42", "main"]));
        Assert.False(RunLabels.HasAll(tags, ["build-42", "release"]));
        Assert.True(RunLabels.HasAll(tags, []));
        Assert.True(RunLabels.HasAll([], []));
    }

    [Fact]
    public void RunExport_KeepsTagsAndNote_AndReadsFilesWithoutThem()
    {
        var plan = new LoadTestPlan { Name = "Smoke" };
        var export = new RunExport { Plan = plan, Results = Snapshot(), Tags = ["build-42"], Note = "after the cache change" };

        var read = JsonSerializer.Deserialize<RunExport>(JsonSerializer.Serialize(export, PlanFile.JsonOptions), PlanFile.JsonOptions)!;
        Assert.Equal(["build-42"], read.Tags);
        Assert.Equal("after the cache change", read.Note);

        // Files from before tags existed, and ones with explicit nulls, still load.
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(export, PlanFile.JsonOptions))!.AsObject();
        node["tags"] = null;
        node["note"] = null;
        var json = node.ToJsonString();
        var old = JsonSerializer.Deserialize<RunExport>(json, PlanFile.JsonOptions)!;
        Assert.Empty(old.Tags);
        Assert.Null(old.Note);

        node.Remove("tags");
        node.Remove("note");
        Assert.Empty(JsonSerializer.Deserialize<RunExport>(node.ToJsonString(), PlanFile.JsonOptions)!.Tags);
    }

    private static LoadTestSnapshot Snapshot() => new()
    {
        State = RunState.Completed,
        Elapsed = TimeSpan.FromSeconds(1),
        TotalRequests = 10,
        SuccessfulRequests = 10,
        FailedRequests = 0,
        ActiveVirtualUsers = 0,
        BytesSent = 0,
        BytesReceived = 0,
        Latency = new LatencyStats(1, 2, 2, 3, 4, 5, 6),
        StatusCodes = new Dictionary<string, long> { ["200"] = 10 },
        Errors = [],
        Steps = [],
        Timeline = [],
        Thresholds = [],
    };
}
