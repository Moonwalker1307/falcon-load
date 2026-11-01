using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Grpc;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class GrpcTests
{
    private const string ShopProto = """
        syntax = "proto3";
        package shop.v1;

        import "google/protobuf/timestamp.proto";
        option csharp_namespace = "Shop";

        /* The catalog. */
        service Catalog {
          rpc GetProduct (GetProductRequest) returns (Product);
          rpc ListProducts (ListProductsRequest) returns (stream Product) { option deprecated = false; }
          rpc Upload (stream Product) returns (UploadReply);
        }

        message GetProductRequest { int64 id = 1; string auth = 2 [json_name = "authToken"]; }
        message ListProductsRequest { int32 first = 1; Category category = 2; repeated int32 ids = 3; }
        message UploadReply { int32 count = 1; }

        enum Category { CATEGORY_UNSPECIFIED = 0; BOOKS = 1; GAMES = 2; }

        message Product {
          reserved 9, 10;
          int64 id = 1;
          string name = 2;
          double price = 3;
          Category category = 4;
          repeated string tags = 5;
          map<string, int32> stock = 6;
          google.protobuf.Timestamp updated = 7;
          Dimensions size = 8;
          sint32 delta = 11;
          bool active = 12;
          bytes blob = 13;
          oneof pick { string label = 14; uint64 code = 15; }
          message Dimensions { float width = 1; float height = 2; }
        }
        """;

    private static List<ProtoFile> Protos() => [new ProtoFile { Name = "shop.proto", Content = ShopProto }];

    private static ProtoSchema Schema()
    {
        Assert.True(ProtoSchema.TryParse(Protos(), out var schema, out var errors), string.Join(" ", errors));
        return schema!;
    }

    private static MessageDescriptor Message(string name) => Schema().FindMessage(name)!;

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    private static byte[] Frame(byte[] message)
    {
        var frame = new byte[message.Length + 5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>A gRPC server answering every call with <paramref name="messages"/> and <paramref name="status"/> in the trailers.</summary>
    private static FakeHandler Server(int status = 0, string? message = null, bool trailersOnly = false, params byte[][] messages) =>
        new((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20 };
            response.Content = new ByteArrayContent([.. messages.SelectMany(Frame)]);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            var target = trailersOnly ? (System.Net.Http.Headers.HttpHeaders)response.Headers : response.TrailingHeaders;
            target.TryAddWithoutValidation("grpc-status", status.ToString());
            if (message is not null)
            {
                target.TryAddWithoutValidation("grpc-message", Uri.EscapeDataString(message));
            }

            return Task.FromResult(response);
        });

    private static RequestDefinition Call(string method = "shop.v1.Catalog/GetProduct", string? body = """{ "id": 7 }""") => new()
    {
        Protocol = RequestProtocol.Grpc,
        Url = "http://api.test:5000",
        Body = body,
        Grpc = new GrpcOptions { Method = method },
    };

    private static LoadTestPlan Plan(RequestDefinition request, int requests = 5) => new()
    {
        Requests = [request],
        ProtoFiles = Protos(),
        Profile = new LoadProfile { Concurrency = 2, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    // A Product with id 7, name "Item", price 9.5, category GAMES, tags [a, b], stock {oslo: 3}, delta -2 and active.
    private static readonly byte[] Product = Bytes("08 07 12 04 4974656d 19 0000000000002340 20 02 2a 01 61 2a 01 62 32 08 0a 04 6f736c6f 10 03 58 03 60 01");

    [Fact]
    public void Parser_ReadsServicesMessagesEnumsAndImports()
    {
        var schema = Schema();

        Assert.Equal(["shop.v1.Catalog/GetProduct", "shop.v1.Catalog/ListProducts", "shop.v1.Catalog/Upload"], schema.Methods.Select(m => m.Path));
        Assert.True(schema.FindMethod("shop.v1.Catalog/ListProducts")!.ServerStreaming);
        Assert.True(schema.FindMethod("Catalog/Upload")!.ClientStreaming);
        Assert.Equal("shop.v1.Product", schema.FindMethod("/shop.v1.Catalog/GetProduct")!.Output.FullName);
        var product = schema.FindMessage("shop.v1.Product")!;
        Assert.Equal(FieldType.Message, product.Find("updated")!.Type);
        Assert.Equal("google.protobuf.Timestamp", product.Find("updated")!.Message!.FullName);
        Assert.True(product.Find("stock")!.IsMap);
        Assert.Equal(FieldType.Enum, product.Find("category")!.Type);
        Assert.Equal("shop.v1.Product.Dimensions", product.Find("size")!.Message!.FullName);
        Assert.Equal("authToken", schema.FindMessage("shop.v1.GetProductRequest")!.Find(2)!.JsonName);
        Assert.True(schema.FindMessage("shop.v1.ListProductsRequest")!.Find("ids")!.Packed);
    }

    [Theory]
    [InlineData("syntax = \"proto3\"; message A { B b = 1; }", "the type 'B' of field 'b' isn't defined")]
    [InlineData("syntax = \"proto3\"; import \"other.proto\"; message A {}", "imports 'other.proto'")]
    [InlineData("syntax = \"proto3\"; message A { int32 x = ; }", "expected a number")]
    [InlineData("syntax = \"proto3\"; message A { int32 x = 1; }", "define no service")]
    [InlineData("syntax = \"proto3\"; service S { rpc M (Missing) returns (A); } message A {}", "request type 'Missing'")]
    public void Parser_ReportsProblems(string proto, string expected)
    {
        Assert.False(ProtoSchema.TryParse([new ProtoFile { Name = "a.proto", Content = proto }], out _, out var errors));
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Parser_ResolvesImportsBetweenTheGivenFiles()
    {
        ProtoFile[] files =
        [
            new() { Name = "api/service.proto", Content = "syntax = \"proto3\"; package api; import \"api/types.proto\"; service S { rpc Get (types.Req) returns (types.Res); }" },
            new() { Name = "api/types.proto", Content = "syntax = \"proto3\"; package api.types; message Req { int32 id = 1; } message Res { string name = 1; }" },
        ];

        Assert.True(ProtoSchema.TryParse(files, out var schema, out var errors), string.Join(" ", errors));
        Assert.Equal("api.types.Res", schema!.FindMethod("api.S/Get")!.Output.FullName);
    }

    [Fact]
    public void Codec_EncodesLikeTheProtobufSpecification()
    {
        // The example from the encoding guide: field 1 set to 150 is 08 96 01.
        var request = Message("shop.v1.GetProductRequest");
        Assert.Equal(Bytes("08 96 01"), ProtobufCodec.Encode(request, """{"id":150}"""));
        Assert.Equal(Bytes("08 96 01 12 03 616263"), ProtobufCodec.Encode(request, """{"id":"150","authToken":"abc"}"""));
        Assert.Equal(Bytes("12 03 616263"), ProtobufCodec.Encode(request, """{"auth":"abc"}"""));

        // Packed repeated ints, and an enum by name.
        var list = Message("shop.v1.ListProductsRequest");
        Assert.Equal(Bytes("08 03 10 02 1a 04 01 02 96 01"), ProtobufCodec.Encode(list, """{"first":3,"category":"GAMES","ids":[1,2,150]}"""));
        Assert.Equal(Bytes("10 01"), ProtobufCodec.Encode(list, """{"category":1}"""));

        // A sint32 is zig-zag encoded: -2 is 3.
        Assert.Equal(Bytes("58 03"), ProtobufCodec.Encode(Message("shop.v1.Product"), """{"delta":-2}"""));
    }

    [Fact]
    public void Codec_RoundTripsEveryKindOfField()
    {
        var product = Message("shop.v1.Product");
        const string json = """
            {"id":"9007199254740993","name":"Ünïcode","price":1.25,"category":"BOOKS","tags":["x","y"],"stock":{"oslo":3,"bergen":0},
             "updated":{"seconds":1793448000,"nanos":5},"size":{"width":1.5,"height":2},"delta":-70000,"active":true,"blob":"AQID","code":"18446744073709551615"}
            """;

        var decoded = JsonDocument.Parse(ProtobufCodec.DecodeToString(product, ProtobufCodec.Encode(product, json))).RootElement;

        Assert.Equal(9007199254740993, decoded.GetProperty("id").GetInt64());
        Assert.Equal("Ünïcode", decoded.GetProperty("name").GetString());
        Assert.Equal(1.25, decoded.GetProperty("price").GetDouble());
        Assert.Equal("BOOKS", decoded.GetProperty("category").GetString());
        Assert.Equal(["x", "y"], decoded.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(3, decoded.GetProperty("stock").GetProperty("oslo").GetInt32());
        Assert.Equal(0, decoded.GetProperty("stock").GetProperty("bergen").GetInt32());
        Assert.Equal("2026-10-31T12:00:00.000000005Z", decoded.GetProperty("updated").GetString());
        Assert.Equal(1.5, decoded.GetProperty("size").GetProperty("width").GetDouble());
        Assert.Equal(-70000, decoded.GetProperty("delta").GetInt32());
        Assert.True(decoded.GetProperty("active").GetBoolean());
        Assert.Equal("AQID", decoded.GetProperty("blob").GetString());
        Assert.Equal(ulong.MaxValue, decoded.GetProperty("code").GetUInt64());
    }

    [Fact]
    public void Codec_DecodesBytesFromAnotherImplementation_AndSkipsUnknownFields()
    {
        // Field 99 (a varint) isn't in the schema and is skipped.
        var bytes = Product.Concat(Bytes("98 06 01")).ToArray();

        var json = JsonDocument.Parse(ProtobufCodec.DecodeToString(Message("shop.v1.Product"), bytes)).RootElement;

        Assert.Equal(7, json.GetProperty("id").GetInt64());
        Assert.Equal("Item", json.GetProperty("name").GetString());
        Assert.Equal(9.5, json.GetProperty("price").GetDouble());
        Assert.Equal("GAMES", json.GetProperty("category").GetString());
        Assert.Equal(2, json.GetProperty("tags").GetArrayLength());
        Assert.Equal(3, json.GetProperty("stock").GetProperty("oslo").GetInt32());
        Assert.Equal(-2, json.GetProperty("delta").GetInt32());
        Assert.False(json.TryGetProperty("size", out _));
    }

    [Theory]
    [InlineData("""{"nope":1}""", "'nope' isn't a field of shop.v1.GetProductRequest")]
    [InlineData("""{"id":"abc"}""", "'id' is an integer")]
    [InlineData("""{"id":1.5}""", "'id' is an integer")]
    [InlineData("""{"auth":5}""", "'auth' is a string")]
    [InlineData("""[1]""", "has to be a JSON object")]
    public void Codec_RefusesJsonThatDoesNotFitTheMessage(string json, string expected)
    {
        var error = Assert.Throws<ProtobufException>(() => ProtobufCodec.Encode(Message("shop.v1.GetProductRequest"), json));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Call_IsPostedOverHttp2WithAFramedMessage_AndCountsTheGrpcStatus()
    {
        var handler = Server(messages: Product);
        await using var runner = new LoadTestRunner(Plan(Call("shop.v1.Catalog/GetProduct", """{ "id": {{$vu}}, "authToken": "t-{{$iteration}}" }""")), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(5, result.SuccessfulRequests);
        Assert.Equal(5, result.StatusCodes["OK"]);
        var (request, _) = handler.Received.First();
        Assert.Equal("http://api.test:5000/shop.v1.Catalog/GetProduct", request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
        Assert.Equal("application/grpc", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("trailers", request.Headers.GetValues("TE").Single());
        Assert.Equal((Product.Length + 5) * 5, result.BytesReceived);
    }

    [Fact]
    public async Task SentBytes_AreTheFramedProtobufMessage()
    {
        var bodies = new List<byte[]>();
        var handler = new FakeHandler(async (request, ct) =>
        {
            lock (bodies)
            {
                bodies.Add(request.Content!.ReadAsByteArrayAsync(ct).Result);
            }

            return await Server(messages: Product).SendAsyncPublic(request, ct);
        });
        await using var runner = new LoadTestRunner(Plan(Call(body: """{ "id": 150 }"""), requests: 1), new HttpMessageInvoker(handler));

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(Bytes("00 00000003 08 96 01"), Assert.Single(bodies));
    }

    [Fact]
    public async Task ErrorStatus_CountsAsFailure_WithTheGrpcMessage()
    {
        var handler = Server(status: 5, message: "No product 7 found", trailersOnly: true);
        await using var runner = new LoadTestRunner(Plan(Call()), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(5, result.FailedRequests);
        Assert.Equal(5, result.StatusCodes["NOT_FOUND"]);
        Assert.Equal("gRPC NOT_FOUND: No product 7 found", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task ExpectedStatusCodes_AreGrpcCodes()
    {
        var request = Call();
        request.ExpectedStatusCodes = "OK, NOT_FOUND";
        await using var runner = new LoadTestRunner(Plan(request), new HttpMessageInvoker(Server(status: 5)));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(5, result.SuccessfulRequests);
    }

    [Fact]
    public async Task NonGrpcHttpStatus_IsAnUnexpectedStatus()
    {
        await using var runner = new LoadTestRunner(Plan(Call(), requests: 2), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.BadGateway, "proxy error")));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.StatusCodes["502"]);
        Assert.StartsWith("Unexpected status 502", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task MissingGrpcStatus_IsAFailure()
    {
        await using var runner = new LoadTestRunner(Plan(Call(), requests: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK, "")));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal("gRPC: the response has no grpc-status", Assert.Single(result.Errors).Message);
        Assert.Equal(1, result.StatusCodes["No grpc-status"]);
    }

    [Fact]
    public async Task ChecksAndExtractions_ReadTheResponseAsJson_AndChainIntoTheNextCall()
    {
        var handler = Server(messages: Product);
        var get = Call();
        get.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.category", Operator = AssertionOperator.Equals, Value = "GAMES" });
        get.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.stock.oslo", Operator = AssertionOperator.GreaterThan, Value = "0" });
        get.Assertions.Add(new Assertion { Source = AssertionSource.Header, Target = "grpc-status", Operator = AssertionOperator.Equals, Value = "0" });
        get.Extractions.Add(new ExtractionRule { Variable = "productName", Source = ExtractionSource.JsonPath, Expression = "$.name" });
        var next = Call(body: """{ "id": 1, "authToken": "{{productName}}" }""");
        var plan = Plan(get, requests: 2);
        plan.Profile.Concurrency = 1;
        plan.Requests.Add(next);
        var sent = new List<byte[]>();
        var recording = new FakeHandler(async (request, ct) =>
        {
            sent.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return await handler.SendAsyncPublic(request, ct);
        });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(recording));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SuccessfulRequests);
        Assert.All(result.Checks, c => Assert.Equal(1, c.Passed));
        // The second call carries "Item" from the first one's response in field 2.
        Assert.Equal(Bytes("00 00000008 08 01 12 04 4974656d"), sent[1]);
    }

    [Fact]
    public async Task ServerStreaming_IsReadAsAnArrayOfMessages()
    {
        var handler = Server(messages: [Product, Product, Product]);
        var request = Call("shop.v1.Catalog/ListProducts", """{ "first": 3 }""");
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.length", Operator = AssertionOperator.Equals, Value = "3" });
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$[2].name", Operator = AssertionOperator.Equals, Value = "Item" });
        await using var runner = new LoadTestRunner(Plan(request, requests: 2), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.SuccessfulRequests);
        Assert.All(result.Checks, c => Assert.Equal(2, c.Passed));
    }

    [Fact]
    public async Task ClientStreaming_SendsOneFramePerArrayElement()
    {
        var bodies = new List<byte[]>();
        var handler = new FakeHandler(async (request, ct) =>
        {
            bodies.Add(await request.Content!.ReadAsByteArrayAsync(ct));
            return await Server(messages: Bytes("08 02")).SendAsyncPublic(request, ct);
        });
        await using var runner = new LoadTestRunner(Plan(Call("shop.v1.Catalog/Upload", """[{ "id": 1 }, { "id": 2 }]"""), requests: 1), new HttpMessageInvoker(handler));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.SuccessfulRequests);
        Assert.Equal(Bytes("00 00000002 08 01 00 00000002 08 02"), Assert.Single(bodies));
    }

    [Fact]
    public async Task ResponseThatDoesNotDecode_IsAFailureWhenSomethingReadsIt()
    {
        var request = Call();
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.id", Operator = AssertionOperator.Exists });
        await using var runner = new LoadTestRunner(Plan(request, requests: 1), new HttpMessageInvoker(Server(messages: Bytes("0a"))));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.StartsWith("gRPC: couldn't read the response", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task PlaceholderThatDoesNotFit_IsReportedPerRequest()
    {
        var plan = Plan(Call(body: """{ "id": "{{ids}}" }"""), requests: 2);
        plan.Parameters.Add(new ParameterList { Name = "ids", Values = ["not-a-number"] });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(Server(messages: Product)));

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.FailedRequests);
        Assert.Equal("gRPC: couldn't encode the message: 'id' is an integer, so it needs a whole number.", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task Probe_ShowsTheDecodedResponseAndTrailers()
    {
        var request = Call();
        request.Extractions.Add(new ExtractionRule { Variable = "name", Source = ExtractionSource.JsonPath, Expression = "$.name" });

        var result = await RequestProbe.SendAsync(request, new LoadProfile(), new HttpMessageInvoker(Server(messages: Product)), protoFiles: Protos());

        Assert.True(result.Success, result.Error);
        Assert.Equal("OK", result.StatusLabel);
        Assert.Null(result.StatusCode);
        Assert.Contains("\"name\":\"Item\"", result.BodyPreview);
        Assert.Contains(result.Headers, h => h.Key == "grpc-status" && h.Value == "0");
        Assert.Equal("Item", Assert.Single(result.Extractions!).Value);
    }

    [Fact]
    public async Task Probe_ReportsAnErrorStatus_AndMissingProtoFiles()
    {
        var failed = await RequestProbe.SendAsync(Call(), new LoadProfile(), new HttpMessageInvoker(Server(status: 16, message: "Missing token")), protoFiles: Protos());
        Assert.False(failed.Success);
        Assert.Equal("UNAUTHENTICATED", failed.StatusLabel);
        Assert.Equal("gRPC UNAUTHENTICATED: Missing token", failed.Error);

        var noProtos = await RequestProbe.SendAsync(Call(), new LoadProfile(), new HttpMessageInvoker(Server()));
        Assert.Contains("need the service's .proto files", noProtos.Error);
    }

    [Fact]
    public void Validation_ChecksTheMethodAndTheMessageAgainstTheProtos()
    {
        Assert.Empty(Plan(Call()).Validate());
        Assert.Empty(Plan(Call(body: """{ "id": {{$randomInt}}, "authToken": "{{token}}" }""")).Validate());
        Assert.Empty(Plan(Call(body: null)).Validate());

        Assert.Contains(Plan(Call("shop.v1.Catalog/Nope")).Validate(), p => p.Contains("no method 'shop.v1.Catalog/Nope'"));
        Assert.Contains(Plan(Call(body: """{ "idd": 1 }""")).Validate(), p => p.Contains("'idd' isn't a field"));
        Assert.Contains(Plan(Call(method: "")).Validate(), p => p.Contains("needs the method"));
        Assert.Contains(Plan(Call(body: "[{}]")).Validate(), p => p.Contains("not an array"));

        var noProtos = Plan(Call());
        noProtos.ProtoFiles = [];
        Assert.Contains(noProtos.Validate(), p => p.Contains(".proto files"));

        var badStatus = Call();
        badStatus.ExpectedStatusCodes = "200";
        Assert.Contains(Plan(badStatus).Validate(), p => p.Contains("gRPC status codes"));

        var badProto = Plan(Call());
        badProto.ProtoFiles[0].Content = "syntax = \"proto3\"; message {";
        Assert.Contains(badProto.Validate(), p => p.StartsWith("Proto files: shop.proto:"));
    }

    [Fact]
    public void GrpcPlan_RoundTripsThroughJson_AndOldPlansHaveNoProtos()
    {
        var clone = Plan(Call()).Clone();
        Assert.Equal("shop.v1.Catalog/GetProduct", clone.Requests[0].Grpc!.Method);
        Assert.Equal(ShopProto, clone.ProtoFiles.Single().Content);
        Assert.Equal("gRPC", clone.Requests[0].Label);

        var old = JsonSerializer.Deserialize<LoadTestPlan>("""{"name":"old","requests":[{"method":"GET","url":"http://api.test/x"}]}""")!;
        Assert.Empty(old.ProtoFiles);
    }

    [Fact]
    public void StatusMatcher_ReadsNamesAndNumbers()
    {
        Assert.True(GrpcStatusMatcher.TryParse("ok, not found, 16", out var matcher));
        Assert.True(matcher.IsMatch(0));
        Assert.True(matcher.IsMatch(5));
        Assert.True(matcher.IsMatch(16));
        Assert.False(matcher.IsMatch(2));
        Assert.True(GrpcStatusMatcher.Default.IsMatch(0));
        Assert.False(GrpcStatusMatcher.TryParse("200", out _));
        Assert.Equal("DEADLINE_EXCEEDED", GrpcStatusMatcher.NameOf(4));
    }
}

public class WellKnownJsonTests
{
    private const string Proto = """
        syntax = "proto3"; package t;
        import "google/protobuf/timestamp.proto";
        import "google/protobuf/duration.proto";
        import "google/protobuf/wrappers.proto";
        message M { google.protobuf.Timestamp at = 1; google.protobuf.Duration took = 2; google.protobuf.StringValue note = 3; google.protobuf.Int32Value count = 4; }
        service S { rpc Get (M) returns (M); }
        """;

    private static MessageDescriptor M()
    {
        Assert.True(ProtoSchema.TryParse([new ProtoFile { Name = "t.proto", Content = Proto }], out var schema, out var errors), string.Join("; ", errors));
        return schema!.FindMessage("t.M")!;
    }

    [Fact]
    public void Timestamp_DurationAndWrappers_UseTheirJsonForms()
    {
        var message = M();
        var bytes = ProtobufCodec.Encode(message, """{ "at": "2026-10-31T12:00:00.250Z", "took": "-1.5s", "note": "hi", "count": 7 }""");

        // The same message written as plain objects encodes to the same bytes.
        var plain = ProtobufCodec.Encode(message, """{ "at": { "seconds": 1793448000, "nanos": 250000000 }, "took": { "seconds": -1, "nanos": -500000000 }, "note": { "value": "hi" }, "count": { "value": 7 } }""");
        Assert.Equal(plain, bytes);

        var json = JsonDocument.Parse(ProtobufCodec.DecodeToString(message, bytes)).RootElement;
        Assert.Equal("2026-10-31T12:00:00.250Z", json.GetProperty("at").GetString());
        Assert.Equal("-1.500s", json.GetProperty("took").GetString());
        Assert.Equal("hi", json.GetProperty("note").GetString());
        Assert.Equal(7, json.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("2026-10-31T13:00:00+01:00", 1793448000, 0)]
    [InlineData("2026-10-31T07:00:00-05:00", 1793448000, 0)]
    [InlineData("1970-01-01T00:00:00.000000001Z", 0, 1)]
    public void Timestamp_ReadsOffsetsAndNineDigits(string text, long seconds, long nanos)
    {
        Assert.True(ProtobufCodec.TryParseTimestamp(text, out var s, out var n));
        Assert.Equal((seconds, nanos), (s, n));
    }

    [Theory]
    [InlineData("""{ "at": "yesterday" }""", "RFC 3339")]
    [InlineData("""{ "took": "5" }""", "1.5s")]
    [InlineData("""{ "at": 5 }""", "RFC 3339")]
    public void BadWellKnownValues_SayWhatIsExpected(string json, string hint)
    {
        var error = Assert.Throws<ProtobufException>(() => ProtobufCodec.Encode(M(), json));
        Assert.Contains(hint, error.Message);
    }

    [Fact]
    public void Sample_WritesWellKnownTypesInTheirJsonForms()
    {
        Assert.True(ProtoSchema.TryParse([new ProtoFile { Name = "t.proto", Content = Proto }], out var schema, out _));
        var sample = ProtoSample.For(schema!.FindMethod("t.S/Get")!);
        var json = JsonDocument.Parse(sample).RootElement;

        Assert.Equal(JsonValueKind.String, json.GetProperty("at").ValueKind);
        Assert.Equal("1s", json.GetProperty("took").GetString());
        Assert.Equal(0, json.GetProperty("count").GetInt32());
        ProtobufCodec.Encode(schema.FindMessage("t.M")!, sample);
    }
}

public class ProtoSampleTests
{
    [Fact]
    public void Sample_FillsEveryFieldAndEncodes()
    {
        const string proto = """
            syntax = "proto3"; package t;
            service S { rpc Get (Req) returns (Req); rpc Up (stream Req) returns (Req); }
            enum Kind { KIND_UNSPECIFIED = 0; A = 1; }
            message Req { int64 id = 1; string name = 2; Kind kind = 3; repeated string tags = 4; map<string, int32> counts = 5; Node root = 6; }
            message Node { string label = 1; repeated Node children = 2; }
            """;
        Assert.True(ProtoSchema.TryParse([new ProtoFile { Name = "t.proto", Content = proto }], out var schema, out _));

        var sample = ProtoSample.For(schema!.FindMethod("t.S/Get")!);
        var json = System.Text.Json.JsonDocument.Parse(sample).RootElement;

        Assert.Equal(0, json.GetProperty("id").GetInt32());
        Assert.Equal("KIND_UNSPECIFIED", json.GetProperty("kind").GetString());
        Assert.Equal(1, json.GetProperty("tags").GetArrayLength());
        Assert.Equal(0, json.GetProperty("counts").GetProperty("key").GetInt32());
        Assert.False(json.GetProperty("root").TryGetProperty("children", out _));
        GrpcMessages.EncodeRequest(schema.FindMethod("t.S/Get")!, sample);
        Assert.StartsWith("[", ProtoSample.For(schema.FindMethod("t.S/Up")!));
    }
}
