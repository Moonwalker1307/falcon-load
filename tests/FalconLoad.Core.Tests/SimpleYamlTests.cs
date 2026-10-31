using System.Text.Json.Nodes;
using FalconLoad.Core.Import;

namespace FalconLoad.Core.Tests;

public class SimpleYamlTests
{
    private static string Json(string yaml) => SimpleYaml.Parse(yaml)?.ToJsonString() ?? "null";

    [Fact]
    public void MapsListsAndScalars_AreTyped()
    {
        Assert.Equal("""{"a":1,"b":"x y","c":true,"d":null,"e":1.5,"f":"1.0.3.4"}""", Json("a: 1\nb: x y\nc: true\nd:\ne: 1.5\nf: 1.0.3.4\n"));
    }

    [Fact]
    public void NestedMapsAndListsOfMaps()
    {
        var yaml = """
            servers:
              - url: https://a.test
                description: first
              - url: https://b.test
            tags:
            - x
            - y
            nested:
              deep:
                - - 1
                  - 2
            """;

        Assert.Equal("""{"servers":[{"url":"https://a.test","description":"first"},{"url":"https://b.test"}],"tags":["x","y"],"nested":{"deep":[[1,2]]}}""", Json(yaml));
    }

    [Fact]
    public void QuotedStrings_CommentsAndColonsInValues()
    {
        var yaml = "'200': \"say \\\"hi\\\"\" # trailing comment\nurl: http://x.test:8080/a#frag\nit: 'don''t'\n# whole line\nplain: don't # comment\n";

        var node = SimpleYaml.Parse(yaml)!;
        Assert.Equal("say \"hi\"", (string?)node["200"]);
        Assert.Equal("http://x.test:8080/a#frag", (string?)node["url"]);
        Assert.Equal("don't", (string?)node["it"]);
        Assert.Equal("don't", (string?)node["plain"]);
    }

    [Fact]
    public void FlowCollections()
    {
        Assert.Equal("""{"a":[1,"two",{"k":"v"}],"b":{"x":[],"y":{}}}""", Json("a: [1, two, {k: v}]\nb: {x: [], y: {}}\n"));
        Assert.Equal("""{"a":["x","y"]}""", Json("a: [x,\n   y]\n"));
    }

    [Fact]
    public void BlockScalars_KeepOrFoldLines()
    {
        var yaml = "literal: |\n  line one\n  line two\nfolded: >\n  one\n  two\nstrip: |-\n  only\nafter: x\n";

        Assert.Equal("""{"literal":"line one\nline two\n","folded":"one two\n","strip":"only","after":"x"}""", Json(yaml));
    }

    [Fact]
    public void PathKeysWithBraces_AreKeys()
    {
        Assert.Equal("""{"paths":{"/pets/{id}":{"get":{"summary":"One"}}}}""", Json("paths:\n  /pets/{id}:\n    get:\n      summary: One\n"));
    }

    [Fact]
    public void MultiLinePlainScalar_IsJoined() =>
        Assert.Equal("""{"d":"one two three","e":1}""", Json("d: one\n  two\n  three\ne: 1\n"));

    [Theory]
    [InlineData("a: &anchor 1\n")]
    [InlineData("a: *alias\n")]
    [InlineData("a: !!str 1\n")]
    [InlineData("a: 1\n---\nb: 2\n")]
    [InlineData("a: 'unclosed\n")]
    public void Unsupported_IsAnErrorNotAMisreading(string yaml) =>
        Assert.Throws<FormatException>(() => SimpleYaml.Parse(yaml));
}
