using System.Text.Json;
using FalconLoad.Core.Grpc;
using FalconLoad.Core.Model;

namespace FalconLoad.Fuzz;

/// <summary>
/// Valid starting points for each target, covering every feature of the format, so mutations start close to inputs
/// that get past the first check.
/// </summary>
internal static class Seeds
{
    public const string ShopProto = """
        syntax = "proto3";
        package shop.v1;

        import "google/protobuf/timestamp.proto";
        import "google/protobuf/duration.proto";
        import "google/protobuf/wrappers.proto";
        import "google/protobuf/struct.proto";
        import "google/protobuf/empty.proto";
        option csharp_namespace = "Shop";

        /* The catalog. */
        service Catalog {
          rpc GetProduct (GetProductRequest) returns (Product);
          rpc ListProducts (ListProductsRequest) returns (stream Product) { option deprecated = false; }
          rpc Upload (stream Product) returns (UploadReply);
          rpc Chat (stream Product) returns (stream Product);
          rpc Ping (google.protobuf.Empty) returns (google.protobuf.Empty);
        }

        message GetProductRequest { int64 id = 1; string auth = 2 [json_name = "authToken"]; }
        message ListProductsRequest { int32 first = 1; Category category = 2; repeated int32 ids = 3; optional string after = 4; }
        message UploadReply { int32 count = 1; google.protobuf.Duration took = 2; google.protobuf.Struct meta = 3; }

        enum Category { option allow_alias = true; CATEGORY_UNSPECIFIED = 0; BOOKS = 1; GAMES = 2; LIVRES = 1; }

        message Product {
          reserved 9, 10, 20 to 30;
          reserved "old";
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
          fixed32 f32 = 16;
          sfixed64 sf64 = 17;
          float ratio = 18;
          repeated Dimensions boxes = 19;
          map<int64, Dimensions> byId = 31;
          google.protobuf.Int64Value big = 32;
          google.protobuf.Value any = 33;
          uint32 u32 = 34;
          sint64 s64 = 35;
          message Dimensions { float width = 1; float height = 2; Product parent = 3; }
        }
        """;

    public const string Proto2 = """
        syntax = "proto2";
        package legacy;
        message Old { required int32 a = 1; optional string b = 2 [default = "x"]; repeated int64 c = 3 [packed = true]; extensions 100 to 199; }
        service Legacy { rpc Get (Old) returns (Old); }
        """;

    public static LoadTestPlan RichPlan()
    {
        var plan = new LoadTestPlan
        {
            Name = "Everything",
            Requests =
            [
                new RequestDefinition
                {
                    Name = "Log in",
                    Method = "POST",
                    Url = "https://api.test/login?user={{users}}&r={{$randomInt:1:9}}",
                    Headers = [new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" }, new HeaderEntry { Name = "X-Off", Value = "1", Enabled = false }],
                    Body = """{ "email": "{{email}}", "vu": {{$vu}}, "it": {{$iteration}}, "id": "{{$guid}}", "at": "{{$isoTimestamp}}", "t": {{$timestamp}} }""",
                    ExpectedStatusCodes = "200-299,404",
                    OncePerVirtualUser = true,
                    Assertions =
                    [
                        new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "ok" },
                        new Assertion { Source = AssertionSource.JsonPath, Target = "$.items[0].id", Operator = AssertionOperator.GreaterThan, Value = "3" },
                        new Assertion { Source = AssertionSource.Header, Target = "Content-Type", Operator = AssertionOperator.Matches, Value = "^application/(json|xml)" },
                        new Assertion { Source = AssertionSource.JsonPath, Target = "$['a b'][0]", Operator = AssertionOperator.Exists },
                        new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.LessThan, Value = "1e3", Enabled = false },
                    ],
                    Extractions =
                    [
                        new ExtractionRule { Variable = "session", Source = ExtractionSource.JsonPath, Expression = "$.data.token" },
                        new ExtractionRule { Variable = "next", Source = ExtractionSource.Header, Expression = "Location" },
                        new ExtractionRule { Variable = "num", Source = ExtractionSource.Regex, Expression = "\"id\":\\s*(\\d+)" },
                    ],
                },
                new RequestDefinition
                {
                    Name = "Follow",
                    Url = "{{next}}",
                    Headers = [new HeaderEntry { Name = "Cookie", Value = "s={{session}}" }],
                },
                new RequestDefinition
                {
                    Name = "Search",
                    Protocol = RequestProtocol.GraphQL,
                    Method = "POST",
                    Url = "https://api.test/graphql",
                    Body = "query Q($n: Int) { items(first: $n) { id name } }",
                    GraphQL = new GraphQLOptions { Variables = """{ "n": {{num}} }""", OperationName = "Q" },
                },
                new RequestDefinition
                {
                    Name = "Socket",
                    Protocol = RequestProtocol.WebSocket,
                    Url = "wss://api.test/ws?u={{users}}",
                    Body = """{ "type": "ping", "n": {{$iteration}} }""",
                    WebSocket = new WebSocketOptions { WaitForReply = true, CloseAfter = false },
                },
                new RequestDefinition
                {
                    Name = "Product",
                    Protocol = RequestProtocol.Grpc,
                    Url = "https://grpc.test",
                    Body = """{ "id": {{$vu}}, "authToken": "{{token}}" }""",
                    Grpc = new GrpcOptions { Method = "shop.v1.Catalog/GetProduct" },
                    ExpectedStatusCodes = "OK,NOT_FOUND",
                },
                new RequestDefinition
                {
                    Name = "Upload",
                    Protocol = RequestProtocol.Grpc,
                    Url = "https://grpc.test",
                    Body = """[{ "id": 1, "tags": ["a"], "stock": { "x": 1 }, "updated": "2026-10-31T12:00:00Z" }, { "id": 2, "blob": "AAE=" }]""",
                    Grpc = new GrpcOptions { Method = "Catalog/Upload" },
                },
            ],
            Profile = new LoadProfile
            {
                Concurrency = 3,
                StopCondition = StopCondition.RequestCount,
                TotalRequests = 12,
                RampUpSeconds = 0,
                MaxRequestsPerSecond = 0,
                TimeoutSeconds = 5,
                IgnoreTlsErrors = true,
                FollowRedirects = false,
            },
            Parameters =
            [
                new ParameterList { Name = "users", Values = ["ana", "bo", "{{$guid}}"], Selection = ParameterSelection.Sequential },
                new ParameterList { Name = "colours", Values = ["red", "", "blue"], Selection = ParameterSelection.Random },
            ],
            DataFiles =
            [
                new DataFile { Name = "people.csv", Format = DataFormat.Csv, Content = "email,name\nana@x.test,Ana\n\"bo@x.test\",\"Bo, \"\"B\"\"\"\n", Selection = ParameterSelection.Sequential },
                new DataFile { Name = "ids.json", Format = DataFormat.Json, Content = """[{"id": 1, "flag": true}, {"id": 2, "flag": null}]""", Selection = ParameterSelection.Random },
            ],
            Thresholds =
            [
                new Threshold { Metric = ThresholdMetric.P95Latency, Operator = ThresholdOperator.LessThan, Value = 500 },
                new Threshold { Metric = ThresholdMetric.ErrorRate, Operator = ThresholdOperator.LessThanOrEqual, Value = 1, RequestIndex = 0 },
                new Threshold { Metric = ThresholdMetric.RequestsPerSecond, Operator = ThresholdOperator.GreaterThan, Value = 1, Enabled = false },
            ],
            ProtoFiles = [new ProtoFile { Name = "shop.proto", Content = ShopProto }],
        };
        return plan;
    }

    public static LoadTestPlan StagedPlan() => new()
    {
        Name = "Staged",
        Requests = [new RequestDefinition { Url = "http://api.test/items/{{$randomInt}}" }],
        Profile = new LoadProfile
        {
            StageMode = StageMode.RequestRate,
            Stages = [new LoadStage { DurationSeconds = 1, Target = 20, Ramp = true }, new LoadStage { DurationSeconds = 1, Target = 5 }],
            Concurrency = 2,
            MaxRequestsPerSecond = 50,
        },
        Auth = new AuthSettings
        {
            Type = AuthType.OAuth2ClientCredentials,
            TokenUrl = "https://auth.test/token",
            ClientId = "client",
            ClientSecret = "{{secret}}",
            Scope = "read write",
            Audience = "api",
            ClientAuthentication = ClientAuthentication.RequestBody,
        },
        Environment = "Staging",
    };

    public static LoadTestPlan VirtualUserStages() => new()
    {
        Name = "Users",
        Requests = [new RequestDefinition { Method = "DELETE", Url = "http://api.test/x", Body = "a=b", ContentType = "application/x-www-form-urlencoded" }],
        Profile = new LoadProfile
        {
            StageMode = StageMode.VirtualUsers,
            Stages = [new LoadStage { DurationSeconds = 1, Target = 3, Ramp = true }],
        },
    };

    public static IReadOnlyList<string> PlanFiles() =>
    [
        PlanFile.Write([RichPlan(), StagedPlan(), VirtualUserStages()]),
        PlanFile.Write([RichPlan()]),
        JsonSerializer.Serialize(StagedPlan(), PlanFile.JsonOptions),
        JsonSerializer.Serialize(new[] { VirtualUserStages() }, PlanFile.JsonOptions),
        """{"name":"old","requests":[{"method":"GET","url":"http://api.test/x"}]}""",
        """{"format":"falcon-load/plans","version":1,"plans":[{"name":"p","requests":[{"url":"http://api.test/"}],"parameters":[{"name":"n","values":[1,true,"x"]}]}]}""",
    ];

    public static IReadOnlyList<string> EnvironmentFiles() =>
    [
        EnvironmentFile.Write(
        [
            new TestEnvironment { Name = "Staging", Variables = [new EnvironmentVariable { Name = "baseUrl", Value = "https://staging.test" }, new EnvironmentVariable { Name = "token", Value = "s3cr3t-value", Secret = true }] },
            new TestEnvironment { Name = "Prod", Variables = [new EnvironmentVariable { Name = "baseUrl", Value = "https://prod.test" }] },
        ]),
        """[{"name":"Local","variables":[{"name":"a","value":"1"},{"name":"b","value":null,"secret":true}]}]""",
        """{"name":"One","variables":[{"name":"x","value":"y"}]}""",
    ];

    public static readonly string[] Curl =
    [
        "curl https://api.test/items?page=2",
        "curl https://api.test/login -d user=a -d 'pass=b c'",
        "curl https://api.test/x -H 'content-type: application/json' -d '{}'",
        "curl -sSLk -XPUT -H'X-Id: 5' https://api.test/a",
        """curl 'https://api.test/a' --data-raw $'{"name":"caf\u00e9\\n","q":"it\'s"}' -H 'accept: */*'""",
        "curl -u alice:s3cret -b 'a=1; b=2' https://api.test/a",
        "curl -I https://api.test/a",
        "curl -G https://api.test/search -d q=falcon --data-urlencode 'tag=a b'",
        "curl --json '{\"a\":1}' https://api.test/a",
        "curl -F name=falcon -F file=@photo.png https://api.test/upload",
        "curl -d @payload.json https://api.test/a",
        "curl -x http://proxy:8080 --compressed -o out.txt -s https://api.test/a",
        "curl 'https://api.test/users/{{userId}}?t={{$timestamp}}' -H 'X-Trace: {{$guid}}'",
        "curl https://api.test/a \\\n  -H \"Authorization: Bearer abc\" \\\n  --request PATCH \\\n  --data-binary \"x=1\"\ncurl https://api.test/b",
        "$ sudo /usr/bin/curl --url https://api.test/c --user-agent ua --referer r -A x -e y --header=\"A: b\"",
        "curl \"https://api.test/q\" -H \"X: a\\\"b\" -d \"{\\\"k\\\": \\\"v\\\"}\" --insecure --location",
    ];

    public const string Postman = """
        {
          "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "variable": [ { "key": "base-url", "value": "https://api.test" }, { "key": "token", "value": "abc" }, { "key": "n", "value": 5, "type": "number" } ],
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
          "item": [
            {
              "name": "Orders",
              "auth": { "type": "basic", "basic": [ { "key": "username", "value": "u" }, { "key": "password", "value": "p" } ] },
              "item": [
                {
                  "name": "Create order",
                  "event": [ { "listen": "test", "script": { "exec": [ "pm.test()" ] } } ],
                  "request": {
                    "method": "POST",
                    "header": [
                      { "key": "Content-Type", "value": "application/json" },
                      { "key": "X-Off", "value": "1", "disabled": true }
                    ],
                    "body": { "mode": "raw", "raw": "{ \"id\": \"{{$randomUUID}}\", \"shop\": \"{{base-url}}\" }", "options": { "raw": { "language": "json" } } },
                    "url": { "raw": "{{base-url}}/orders?a=1&b=2", "host": ["{{base-url}}"], "path": ["orders"], "query": [ { "key": "a", "value": "1" }, { "key": "b", "value": "2", "disabled": true } ] }
                  }
                },
                {
                  "name": "Get order",
                  "request": {
                    "method": "GET",
                    "auth": { "type": "apikey", "apikey": [ { "key": "key", "value": "X-Key" }, { "key": "value", "value": "k" }, { "key": "in", "value": "query" } ] },
                    "url": { "raw": "{{base-url}}/orders/:id", "variable": [ { "key": "id", "value": "42" } ] }
                  }
                },
                {
                  "name": "Form",
                  "request": {
                    "method": "PUT",
                    "auth": { "type": "noauth" },
                    "body": { "mode": "urlencoded", "urlencoded": [ { "key": "a", "value": "1" }, { "key": "b", "value": "x y", "disabled": true } ] },
                    "url": "{{base-url}}/form"
                  }
                },
                {
                  "name": "Query",
                  "request": {
                    "method": "POST",
                    "body": { "mode": "graphql", "graphql": { "query": "{ a }", "variables": "{\"a\":1}" } },
                    "url": { "protocol": "https", "host": ["api", "test"], "port": "8443", "path": ["graphql", ":v"] }
                  }
                },
                {
                  "name": "Upload",
                  "request": {
                    "method": "POST",
                    "body": { "mode": "formdata", "formdata": [ { "key": "f", "type": "file", "src": "a.png" }, { "key": "t", "value": "x", "type": "text" } ] },
                    "url": "https://api.test/upload"
                  }
                }
              ]
            },
            { "name": "Ping", "request": "https://api.test/ping" }
          ]
        }
        """;

    public const string OpenApiYaml = """
        openapi: 3.0.3
        info:
          title: Pet store
          version: '1.0'
        servers:
          - url: https://{env}.pets.test/v1
            variables:
              env:
                default: api
                enum: [api, staging]
        security:
          - bearerAuth: []
          - apiKey: []
        paths:
          /pets:
            get:
              tags: [pets]
              summary: List pets
              parameters:
                - name: limit
                  in: query
                  required: true
                  schema: { type: integer, minimum: 5, maximum: 9 }
                - name: cursor
                  in: query
                  schema: { type: string }
                - $ref: '#/components/parameters/Trace'
              responses:
                '200': { description: ok }
                '404': { description: none }
            post:
              tags: [pets]
              operationId: createPet
              requestBody:
                required: true
                content:
                  application/json:
                    schema:
                      $ref: '#/components/schemas/NewPet'
                    example: { name: Rex }
              responses:
                '201': { description: created }
                default: { description: error }
          /pets/{petId}:
            parameters:
              - name: petId
                in: path
                required: true
                schema: { type: string, format: uuid }
            get:
              summary: One pet
              security: []
              parameters:
                - name: X-Trace
                  in: header
                  required: true
                  schema: { type: string }
                  example: abc
                - name: when
                  in: query
                  required: true
                  schema: { type: string, format: date-time }
              responses:
                2XX: { description: ok }
            put:
              requestBody:
                content:
                  application/x-www-form-urlencoded:
                    schema:
                      type: object
                      properties:
                        a: { type: number, default: 1.5 }
                        b: { type: boolean }
                  text/plain:
                    schema: { type: string }
              responses:
                '200': { description: ok }
            delete:
              deprecated: true
              responses:
                '204': { description: gone }
        components:
          parameters:
            Trace:
              name: X-Trace-Id
              in: header
              schema: { type: string, format: uuid }
          securitySchemes:
            bearerAuth:
              type: http
              scheme: bearer
            apiKey:
              type: apiKey
              in: header
              name: X-Api-Key
            basic:
              type: http
              scheme: basic
          schemas:
            NewPet:
              type: object
              required: [name]
              properties:
                id: { type: string, format: uuid, readOnly: true }
                name: { type: string, example: Rex }
                age: { type: integer }
                weight: { type: number, format: double }
                kind:
                  oneOf:
                    - { type: string, enum: [cat, dog] }
                    - { type: integer }
                extra:
                  allOf:
                    - { type: object, properties: { x: { type: string } } }
                    - { type: object, properties: { y: { type: string } } }
                tags:
                  type: array
                  items: { type: string, enum: [good, bad] }
                owner:
                  $ref: '#/components/schemas/Owner'
                map:
                  type: object
                  additionalProperties: { type: integer }
            Owner:
              type: object
              properties:
                email: { type: string, format: email }
                born: { type: string, format: date }
                pet:
                  $ref: '#/components/schemas/NewPet'
        """;

    public const string OpenApiJson = """
        { "openapi": "3.1.0", "info": { "title": "t" }, "servers": [ { "url": "/api" } ],
          "paths": {
            "/a": { "get": { "parameters": [ { "name": "q", "in": "query", "required": true, "schema": { "type": "array", "items": { "type": "integer" } } } ] } },
            "/b/{id}": { "post": { "operationId": "makeB", "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": ["string", "null"] } } ],
              "requestBody": { "content": { "application/json": { "schema": { "type": "object", "properties": { "n": { "type": "integer", "enum": [3, 4] }, "s": { "$ref": "#/components/schemas/S" } } } } } } } }
          },
          "components": { "schemas": { "S": { "type": "object", "properties": { "self": { "$ref": "#/components/schemas/S" }, "v": { "const": "x" } } } } }
        }
        """;

    public static readonly string[] Yaml =
    [
        OpenApiYaml,
        "a: 1\nb: x y\nc: true\nd:\ne: 1.5\nf: 1.0.3.4\n",
        "servers:\n  - url: https://a.test\n    description: first\n  - url: https://b.test\ntags:\n- x\n- y\nnested:\n  deep:\n    - - 1\n      - 2\n",
        "a: [1, two, {k: v}]\nb: {x: [], y: {}}\n",
        "a: [x,\n   y]\n",
        "literal: |\n  line one\n  line two\nfolded: >\n  one\n  two\nstrip: |-\n  only\nkeep: |+\n  k\n\nafter: x\n",
        "paths:\n  /pets/{id}:\n    get:\n      summary: One\n",
        "d: one\n  two\n  three\ne: 1\n",
        "# comment\n---\nkey: 'single ''quoted'''\nother: \"double \\\"q\\\" \\u00e9 \\n\"\nanchor: &a 1\nref: *a\n? complex\n: value\nempty: ~\nlist: []\n...\n",
        "a:\n  - b: 1\n    c:\n      - d\n      - e: f\n        g: h\n  -\n    i: j\n",
    ];

    public static readonly string[] Csv =
    [
        "email,name\nana@x.test,Ana\nbo@x.test,Bo\n",
        "a,b,c\r\n1,\"two, three\",\"say \"\"hi\"\"\"\r\n\"multi\nline\",,x\r\n",
        "\ufeffid;name\n1;x\n",
        "id\tname\n1\ttab\n",
        "only\n\n\nx\n",
        "a,b\n\"1\",\"2\"",
        "a,a,b\n1,2,3\n",
    ];

    public static readonly string[] JsonData =
    [
        """[{"id": 17, "name": "Ana", "admin": true, "note": null}, {"id": 42, "name": "Bo", "tags": ["a", "b"]}]""",
        """[{"a": {"nested": 1}}, {"a": 1.5e10}, {"a": "x", "b": false}]""",
        """[]""",
        """[{}]""",
        """{"rows": [{"a": 1}]}""",
    ];

    public static readonly string[] JsonPathInputs =
    [
        "$.data.token\n{\"data\":{\"token\":\"abc\"}}",
        "$.items[0].id\n{\"items\":[{\"id\":7},{\"id\":8}]}",
        "$['a b'][0]\n{\"a b\":[1,2,3]}",
        "$.items[-1].name\n{\"items\":[{\"name\":\"x\"},{\"name\":\"y\"}]}",
        "$[2]\n[1,2,{\"k\":null}]",
        "$.a.b.c\n{\"a\":{\"b\":{\"c\":true}}}",
        "$..id\n{\"a\":{\"id\":1},\"b\":[{\"id\":2}]}",
        "$.length\n\"text\"",
        "$[\"q\\\"uote\"]\n{\"q\\\"uote\":1.5e300}",
        "data.token\n{\"data\":{\"token\":\"abc\"}}",
        "$.a\n{\"a\":\"\\ud800\",\"\\udc00\":[\"\\ud83d\\ude00\"]}",
    ];

    public static readonly string[] Templates =
    [
        "https://api.test/users/{{users}}?r={{$randomInt}}&r2={{$randomInt:5:10}}&vu={{$vu}}&i={{$iteration}}",
        "{\"id\": \"{{$guid}}\", \"t\": {{$timestamp}}, \"iso\": \"{{$isoTimestamp}}\", \"tok\": \"{{token}}\", \"x\": \"{{ session }}\"}",
        "{{baseUrl}}/a/{{next}}/{{email}}",
        "{{$randomInt:-10:-1}} {{$unknown}} {{ notdefined }} {{{{x}}}}",
        "plain text with no variables",
    ];

    public static IReadOnlyList<string> Assertions() =>
    [
        JsonSerializer.Serialize(RichPlan().Requests[0].Assertions, PlanFile.JsonOptions) + "\n{\"items\":[{\"id\":7}],\"a b\":[1],\"ok\":true}",
        """[{"source":"JsonPath","target":"$.n","operator":"LessThan","value":"10"},{"source":"Body","operator":"Equals","value":"x"},{"source":"Header","target":"X-A","operator":"NotExists"}]""" + "\n{\"n\": 5}",
        """[{"source":"Body","operator":"Matches","value":"(a+)+$"},{"source":"JsonPath","target":"$[0]","operator":"NotEquals","value":"1"}]""" + "\n[1,2]",
    ];

    public static IReadOnlyList<string> Extractions() =>
    [
        JsonSerializer.Serialize(RichPlan().Requests[0].Extractions, PlanFile.JsonOptions) + "\n{\"data\":{\"token\":\"abc\"},\"id\": 7}",
        """[{"variable":"x","source":"Regex","expression":"(?<v>[a-z]+)"},{"variable":"h","source":"Header","expression":"Set-Cookie"}]""" + "\nhello world",
    ];

    public static readonly string[] ProtobufJson =
    [
        "shop.v1.GetProductRequest\n{\"id\":150,\"authToken\":\"abc\"}",
        "shop.v1.GetProductRequest\n{\"id\":\"150\",\"auth\":\"abc\"}",
        "shop.v1.ListProductsRequest\n{\"first\":3,\"category\":\"GAMES\",\"ids\":[1,2,150],\"after\":\"x\"}",
        "shop.v1.Product\n{\"id\":1,\"name\":\"n\",\"price\":1.5,\"category\":2,\"tags\":[\"a\",\"b\"],\"stock\":{\"x\":1,\"y\":-2},\"updated\":\"2026-10-31T12:00:00.250Z\",\"size\":{\"width\":1.5,\"height\":\"NaN\",\"parent\":{\"id\":2}},\"delta\":-2,\"active\":true,\"blob\":\"AAEC\",\"label\":\"l\",\"f32\":4294967295,\"sf64\":\"-9223372036854775808\",\"ratio\":\"Infinity\",\"boxes\":[{\"width\":1}],\"byId\":{\"5\":{\"width\":2}},\"big\":\"12\",\"any\":{\"stringValue\":\"a\"},\"u32\":7,\"s64\":-5}",
        "shop.v1.UploadReply\n{\"count\":1,\"took\":\"-1.5s\",\"meta\":{\"fields\":{\"k\":{\"numberValue\":1}}}}",
        "shop.v1.Product\n{\"code\":\"18446744073709551615\",\"updated\":{\"seconds\":1793448000,\"nanos\":250000000}}",
    ];

    public static readonly string[] Cli =
    [
        "run\nPLAN\n--quiet\n--requests\n3",
        "run\nPLAN\n--test\nEverything\n--concurrency=2\n--requests=4\n--out\nout.json\n--html\nreport.html\n--junit\njunit.xml\n--quiet",
        "run\nPLAN\n--duration\n1\n--max-error-rate\n50\n--var\ntoken=abc\n--var\nnext=http://api.test/n\n--machine\nm1\n--quiet",
        "run\nPLAN\n--env-file\nENV\n--env\nStaging\n--data\npeople.csv=DATA\n--requests\n2\n--quiet",
        "run\nPLAN\n--influx\nhttp://influx.test:8086\n--influx-bucket\nb\n--influx-org\no\n--otlp\nhttp://otel.test:4318\n--metrics-interval\n1\n--requests\n2\n--quiet",
        "--help",
        "--version",
        "run\n-\n--quiet",
    ];

    public static readonly string[] Telemetry =
    [
        """{"prometheusEnabled":true,"influxDb":{"enabled":true,"url":"http://influx.test:8086","bucket":"b","organization":"o","token":"t0ken-value"},"otlp":{"enabled":true,"endpoint":"http://otel.test:4318","headers":[{"name":"Authorization","value":"Bearer x-secret"}]},"pushIntervalSeconds":5}""",
        """{"influxDb":{"enabled":true,"url":"https://influx.test/api/v2/write?x=1","bucket":"db name"},"otlp":{"enabled":false,"endpoint":"https://otel.test/v1/metrics"},"pushIntervalSeconds":1}""",
    ];

    public static readonly string[] Status =
    [
        "200",
        "200,201",
        "200-299,404",
        " 100 - 599 ",
        "OK,NOT_FOUND",
        "0,5,16",
        "ok, unavailable,DEADLINE_EXCEEDED",
    ];

    public static readonly string[] Masking =
    [
        "s3cr3t-value\nabc\n%2Fslash/\nText with s3cr3t-value and s3cr3t%2Dvalue and abc and %2Fslash%2F and %252F",
        "aaa\naaaa\nTextaaaaaaaaaaaa",
    ];

    public static readonly string[] GraphQL =
    [
        "query Q($n: Int) { items(first: $n) { id } }\n{\"n\": 3}\nQ\n{\"data\":{\"a\":1}}",
        "{ a }\n\n\n{\"errors\":[{\"message\":\"bad\",\"path\":[\"a\"]}],\"data\":null}",
        "mutation { x }\n  {\"a\":[1,2]}  \n op \n{\"errors\":[]}",
    ];
}
