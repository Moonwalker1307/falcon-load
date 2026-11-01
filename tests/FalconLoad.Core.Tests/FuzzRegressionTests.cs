using System.Buffers;
using System.Text;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Grpc;
using FalconLoad.Core.Import;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;
using FalconLoad.Core.Reports;
using FalconLoad.Core.Telemetry;

namespace FalconLoad.Core.Tests;

/// <summary>
/// Inputs the fuzzer in <c>tests/FalconLoad.Fuzz</c> found that crashed a parser with an exception its callers didn't
/// expect. Each one is kept here, cut down to what still triggered it, so the crash can't come back.
/// </summary>
public class FuzzRegressionTests
{
    // An escaped lone surrogate is valid JSON text, but System.Text.Json throws InvalidOperationException, not
    // JsonException, when it is read as a string.
    private const string LoneSurrogate = "\"\\ud800\"";

    [Theory]
    [InlineData("""{"plans":null}""")]
    [InlineData("""{"plans":[null]}""")]
    public void PlanFile_NullListsAreReportedAsProblems(string json)
    {
        var import = PlanFile.Read(json);

        Assert.False(import.Succeeded);
    }

    [Fact]
    public void PlanFile_NullValuesInARequestAreTreatedAsLeftOut()
    {
        var import = PlanFile.Read("""
            {"name":"T","requests":[null,{"url":"http://api.test","method":null,"headers":null,"body":null,
             "assertions":[null,{"source":"Body","operator":"Contains","value":null}],"thresholds":[null],"extractions":null}],"profile":null}
            """);

        // Every null is read as left out, so what is wrong is told as a problem with the plan, not a crash.
        Assert.Equal("Request 1 (GET http://api.test): The check \"body contains\" needs a value.", Assert.Single(import.Problems));
    }

    [Theory]
    [InlineData("""[{"name":"T","requests":[{"protocol":"Grpc","grpc":null}]}]""")]
    [InlineData("""[{"name":"T","requests":[{"protocol":"WebSocket","webSocket":null}]}]""")]
    [InlineData("""[{"name":"T","requests":[{"url":"http://api.test","assertions":[{"value":null,"expected":null}]}]}]""")]
    [InlineData("""[{"name":"T","requests":null,"thresholds":null,"parameters":null}]""")]
    [InlineData("[{\"name\":" + LoneSurrogate + ",\"requests\":[{\"url\":\"http://api.test\"}]}]")]
    public void PlanFile_DamagedPlansDontThrow(string json)
    {
        var import = PlanFile.Read(json);

        Assert.True(import.Succeeded || import.Problems.Count > 0);
    }

    [Theory]
    [InlineData("""{"environments":null}""")]
    [InlineData("""{"environments":[null]}""")]
    [InlineData("{\"environments\":[{\"name\":" + LoneSurrogate + "}]}")]
    public void EnvironmentFile_DamagedFilesAreReportedAsProblems(string json)
    {
        Assert.False(EnvironmentFile.Read(json).Succeeded);
    }

    [Theory]
    [InlineData("""{"offset":0,"count":0}""")]
    [InlineData("""{"offset":0,"counts":null,"count":0}""")]
    public void PositionalRecords_MissingOrNullValuesAreInvalidJson(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HistogramData>(json, PlanFile.JsonOptions));
    }

    [Theory]
    [InlineData("{{$randomInt:9:9999999999}}", 9, 9999999999)]
    [InlineData("{{$randomInt:-9223372036854775808:9223372036854775807}}", long.MinValue, long.MaxValue)]
    public void RandomInt_TakesBoundsBeyondInt32(string template, long min, long max)
    {
        var value = long.Parse(TemplateRenderer.Render(template, default), System.Globalization.CultureInfo.InvariantCulture);

        Assert.InRange(value, min, max);
    }

    [Fact]
    public void RandomInt_WithBoundsNoNumberHoldsIsSentAsWritten()
    {
        const string template = "{{$randomInt:1:99999999999999999999}}";

        Assert.Equal(template, TemplateRenderer.Render(template, default));
    }

    [Theory]
    [InlineData("[{")]
    [InlineData(": {")]
    [InlineData("a: {b: 1")]
    public void Yaml_UnclosedFlowCollectionsAreFormatErrors(string yaml)
    {
        Assert.Throws<FormatException>(() => SimpleYaml.Parse(yaml));
    }

    [Fact]
    public void Yaml_DeepNestingIsAFormatError()
    {
        Assert.Throws<FormatException>(() => SimpleYaml.Parse(string.Concat(Enumerable.Repeat("- ", 500)) + "x"));
        Assert.Throws<FormatException>(() => SimpleYaml.Parse(new string('[', 500)));
    }

    [Fact]
    public void Yaml_NumbersTooLargeForADoubleStayText()
    {
        var node = SimpleYaml.Parse("a: 1e999");

        Assert.Equal("1e999", node!["a"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"openapi":"3.0.0","paths":{"/a":{"get":{"parameters":[{"name":"q","in":"query","required":3}]}}}}""")]
    [InlineData("""{"openapi":"3.0.0","info":["",null],"paths":{"/a":{"get":{}}}}""")]
    [InlineData("""{"openapi":"3.0.0","paths":{"/a":{"get":{"parameters":[{"name":"q","in":"query","schema":{"type":["striull"]}}]}}}}""")]
    [InlineData("""{"info":{"name":"c","schema":"https://schema.getpostman.com/json/collection/v2.1.0/collection.json"},"item":[{"name":"a","request":{"url":"http://api.test","method":"GET"},"disabled":"true"}]}""")]
    [InlineData("""{"info":{"name":"c","schema":"https://schema.getpostman.com/json/collection/v2.1.0/collection.json"},"item":[{"name":"a","name":"b"}]}""")]
    [InlineData("{\"openapi\":" + LoneSurrogate + ",\"paths\":{}}")]
    public void Import_DamagedFilesAreImportErrors(string text)
    {
        var error = Record.Exception(() => ImportSource.Parse(text).Build());

        Assert.True(error is null or ImportException, error?.ToString());
    }

    [Fact]
    public void Curl_AnInvalidCodePointBecomesAReplacementCharacter()
    {
        var result = CurlImporter.Read("curl http://api.test -d $'\\U7fffffff'");

        Assert.Equal("\uFFFD", Assert.Single(result.Requests).Body);
    }

    [Theory]
    [InlineData("""{"metric":"P95Latency","value":-1e309}""")]
    [InlineData("""{"metric":"P95Latency","value":"Infinity"}""")]
    [InlineData("""{"metric":"P95Latency","value":"NaN"}""")]
    public void Json_NumbersADoubleCantHoldAreInvalidJson(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Threshold>(json, PlanFile.JsonOptions));
    }

    [Fact]
    public void JsonData_ObjectsWithoutKeysHaveNoColumns()
    {
        var file = new DataFile { Name = "d", Format = DataFormat.Json, Content = "[{}]" };

        Assert.Contains(file.Validate(), problem => problem.Contains("has no columns", StringComparison.Ordinal));
    }

    [Fact]
    public void JsonData_ALoneSurrogateIsAFormatError()
    {
        Assert.Throws<DataFormatException>(() => DataTable.ParseJson("[{\"a\":" + LoneSurrogate + "}]"));
    }

    [Fact]
    public void JsonPath_ALoneSurrogateInANameIsNotFound()
    {
        Assert.True(JsonPath.TryParse("$.a.b", out var path, out _));
        using var document = JsonDocument.Parse("{\"a\":{\"\\ud800\":1}}");

        Assert.False(path.TryFind(document.RootElement, out _));
    }

    [Fact]
    public void JsonPath_ALoneSurrogateInAValueComesBackAsWritten()
    {
        Assert.True(JsonPath.TryParse("$.a", out var path, out _));
        using var document = JsonDocument.Parse("{\"a\":" + LoneSurrogate + "}");

        Assert.True(path.TryFind(document.RootElement, out var value));
        Assert.Equal("\\ud800", value.Text);
    }

    [Fact]
    public void GraphQL_VariablesThatArentJsonOnceFilledInAreAFormatError()
    {
        Assert.Throws<FormatException>(() => GraphQLRequest.BuildBody("{ a }", """{"":[{{o:9:9}}]}""", null));
    }

    [Fact]
    public void OAuth2_AnEnormousLifetimeIsTreatedAsNone()
    {
        var (value, lifetime) = OAuth2TokenSource.Parse("""{"access_token":"t","expires_in":1e308}""")!.Value;

        Assert.Equal("t", value);
        Assert.Null(lifetime);
    }

    [Fact]
    public void OAuth2_ALoneSurrogateInTheResponseIsNoToken()
    {
        Assert.Null(OAuth2TokenSource.Parse("{\"access_token\":" + LoneSurrogate + "}"));
    }

    [Theory]
    [InlineData("syntax = \"proto3\"; message M { int32 a = 0; }")]
    [InlineData("syntax = \"proto3\"; message M { int32 a = 536870912; }")]
    [InlineData("syntax = \"proto3\"; message M { int32 a = 1; string b = 1; }")]
    [InlineData("syntax = \"proto3\"; message M { int32 a = 1; string a = 2; }")]
    [InlineData("syntax = \"proto3\"; message M { map<string, int32> a = 0; }")]
    public void Proto_BadFieldNumbersAndNamesAreErrors(string proto)
    {
        Assert.False(ProtoSchema.TryParse([new ProtoFile { Name = "a.proto", Content = proto }], out _, out var errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Proto_TwoFilesWithTheSameNameAreAnError()
    {
        var file = new ProtoFile { Name = "a.proto", Content = "syntax = \"proto3\"; message M {}" };

        Assert.False(ProtoSchema.TryParse([file, file], out _, out var errors));
        Assert.Contains(errors, error => error.Contains("Two proto files", StringComparison.Ordinal));
    }

    [Fact]
    public void ProtoSample_StaysSmallForRecursiveMessagesAndEncodes()
    {
        const string proto = """
            syntax = "proto3";
            package t;
            import "google/protobuf/struct.proto";
            message Node { repeated Node children = 1; map<string, Node> named = 2; Node a = 3; Node b = 4; google.protobuf.ListValue list = 5; }
            service S { rpc Get(Node) returns (Node); }
            """;
        Assert.True(ProtoSchema.TryParse([new ProtoFile { Name = "t.proto", Content = proto }], out var schema, out var errors), string.Join("; ", errors));
        var method = schema!.FindMethod("t.S/Get")!;

        var sample = ProtoSample.For(method);

        Assert.InRange(sample.Length, 1, 100_000);
        _ = ProtobufCodec.Encode(method.Input, sample);
    }

    [Theory]
    [InlineData(new byte[] { 0x08, 0x01, 0x12, 0xff, 0xff, 0xff, 0xff, 0x0f, 0x1a })]
    [InlineData(new byte[] { 0x12, 0x05, 0x41 })]
    public void Protobuf_ALengthBeyondTheMessageIsAProtobufError(byte[] bytes)
    {
        var message = Message("syntax = \"proto3\"; message M { int32 a = 1; string b = 2; }");

        Assert.Throws<ProtobufException>(() => ProtobufCodec.Decode(message, bytes, new ArrayBufferWriter<byte>()));
    }

    [Fact]
    public void Protobuf_AFieldWithTheWrongWireTypeIsSkipped()
    {
        var message = Message("syntax = \"proto3\"; message M { string a = 1; int32 b = 2; }");

        // Field 1 sent as a fixed32 and field 2 as a fixed64, neither of which their types use.
        var json = ProtobufCodec.DecodeToString(message, [0x0d, 0x7e, 0x02, 0x96, 0x58, 0x11, 1, 2, 3, 4, 5, 6, 7, 8]);

        Assert.Equal("{}", json);
    }

    [Fact]
    public void Protobuf_ADurationBeyondTheProtobufRangeStaysAnObject()
    {
        var message = Message("""
            syntax = "proto3";
            import "google/protobuf/duration.proto";
            message M { google.protobuf.Duration d = 1; }
            """);

        // d { seconds: 10^15 }, past the 10,000 years a Duration may hold.
        var seconds = Varint(1_000_000_000_000_000);
        var json = ProtobufCodec.DecodeToString(message, [0x0a, (byte)(seconds.Length + 1), 0x08, .. seconds]);

        Assert.Contains("\"seconds\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Histogram_DamagedDataIsAFormatError()
    {
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(int.MaxValue, [1], 1, 1, 1, 1)));
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(0, [long.MaxValue, long.MaxValue], -2, 1, 1, 1)));
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(0, [1], 1, double.NaN, 1, 1)));
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(0, [1], 1, 5, 1, 5)));
        Assert.Throws<FormatException>(() => LatencyHistogram.FromData(new HistogramData(0, [1], 1, 1, double.PositiveInfinity, 1)));
    }

    [Fact]
    public async Task Results_NullsInsideHistogramsAreInvalidJsonOrProblems()
    {
        var export = await ExportAsync();
        var json = JsonSerializer.Serialize(new[] { export }, PlanFile.JsonOptions);
        var timeline = JsonDocument.Parse(json).RootElement[0].GetProperty("histograms").GetProperty("timeline").GetRawText();

        foreach (var damaged in new[]
        {
            json.Replace(timeline, "[null]", StringComparison.Ordinal),
            json.Replace(timeline, """[{"second":0,"failures":0}]""", StringComparison.Ordinal),
            json.Replace(timeline, "null", StringComparison.Ordinal),
        })
        {
            RunExport[] runs;
            try
            {
                runs = JsonSerializer.Deserialize<RunExport[]>(damaged, PlanFile.JsonOptions)!;
            }
            catch (JsonException)
            {
                continue;
            }

            if (ResultCombiner.Validate(runs).Count == 0)
            {
                _ = ResultCombiner.Combine(runs);
            }
        }
    }

    [Fact]
    public async Task Results_ANullRunIsAProblem()
    {
        var runs = JsonSerializer.Deserialize<RunExport[]>("[null]", PlanFile.JsonOptions)!;

        Assert.Equal("Result 1 is null instead of an exported result.", Assert.Single(ResultCombiner.Validate(runs)));
        Assert.Single(ResultCombiner.Validate([await ExportAsync(), null!]));
    }

    [Fact]
    public async Task HtmlReport_StatusCountsBeyondALongDontOverflow()
    {
        var export = await ExportAsync();
        export = export with
        {
            Results = export.Results with
            {
                StatusCodes = new Dictionary<string, long> { ["200"] = long.MaxValue, ["500"] = long.MaxValue },
            },
        };

        var html = HtmlReport.Render(export, RunVerdict.Evaluate(export.Results, null));

        Assert.Contains("200", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"otlp":null,"influxDb":null}""")]
    [InlineData("""{"otlp":{"enabled":true,"endpoint":"http://localhost:4318","headers":[null,{"name":null,"value":"B"}]}}""")]
    public void Telemetry_NullsAreTreatedAsLeftOut(string json)
    {
        var settings = JsonSerializer.Deserialize<TelemetrySettings>(json, PlanFile.JsonOptions)!;

        _ = settings.Validate();
        _ = settings.KeepSecretsFrom(new TelemetrySettings());
    }

    private static MessageDescriptor Message(string proto)
    {
        proto += " service S { rpc Call(M) returns (M); }";
        Assert.True(ProtoSchema.TryParse([new ProtoFile { Name = "m.proto", Content = proto }], out var schema, out var errors), string.Join("; ", errors));
        return schema!.FindMessage("M")!;
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return [.. bytes];
    }

    private static async Task<RunExport> ExportAsync()
    {
        var plan = new LoadTestPlan
        {
            Name = "Products",
            Requests = [new RequestDefinition { Url = "http://api.test/products" }],
            Profile = new LoadProfile { Concurrency = 1, StopCondition = StopCondition.RequestCount, TotalRequests = 3 },
        };
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(System.Net.HttpStatusCode.OK)));
        await runner.RunAsync(cancellationToken: CancellationToken.None);
        var (snapshot, histograms) = runner.GetResults();
        return new RunExport { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UnixEpoch, Machine = "a", Plan = runner.Plan, Results = snapshot, Histograms = histograms };
    }
}
