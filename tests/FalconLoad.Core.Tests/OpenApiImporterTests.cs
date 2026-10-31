using FalconLoad.Core.Import;

namespace FalconLoad.Core.Tests;

public class OpenApiImporterTests
{
    private const string Spec = """
        openapi: 3.0.3
        info:
          title: Pet store
          version: '1.0'
        servers:
          - url: https://{env}.pets.test/v1
            variables:
              env:
                default: api
        security:
          - bearerAuth: []
        paths:
          /pets:
            get:
              tags: [pets]
              summary: List pets
              parameters:
                - name: limit
                  in: query
                  required: true
                  schema: { type: integer, minimum: 5 }
                - name: cursor
                  in: query
                  schema: { type: string }
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
              responses:
                2XX: { description: ok }
            delete:
              deprecated: true
              responses:
                '204': { description: gone }
        components:
          securitySchemes:
            bearerAuth:
              type: http
              scheme: bearer
          schemas:
            NewPet:
              type: object
              required: [name]
              properties:
                id: { type: string, format: uuid, readOnly: true }
                name: { type: string, example: Rex }
                age: { type: integer }
                tags:
                  type: array
                  items: { type: string, enum: [good, bad] }
                owner:
                  $ref: '#/components/schemas/Owner'
            Owner:
              type: object
              properties:
                email: { type: string, format: email }
                pet:
                  $ref: '#/components/schemas/NewPet'
        """;

    private static OpenApiSpec Load(string text = Spec) => ImportSource.Parse(text).OpenApi!;

    [Fact]
    public void Operations_AreListedWithTheirDetails()
    {
        var spec = Load();

        Assert.Equal("Pet store", spec.Title);
        Assert.Equal("https://api.pets.test/v1", spec.ServerUrl);
        Assert.Equal(["GET /pets", "POST /pets", "GET /pets/{petId}", "DELETE /pets/{petId}"], spec.Operations.Select(o => o.Key));
        Assert.Equal("List pets", spec.Operations[0].Summary);
        Assert.Equal("createPet", spec.Operations[1].Summary);
        Assert.Equal(["pets"], spec.Operations[0].Tags);
        Assert.True(spec.Operations[3].Deprecated);
    }

    [Fact]
    public void Build_OnlyIncludesThePickedOperations()
    {
        var result = Load().Build(["POST /pets", "GET /pets/{petId}"]);

        Assert.Equal(["POST /pets", "GET /pets/{petId}"], result.Requests.Select(r => r.Name));
        Assert.Equal("Pet store", result.Name);
    }

    [Fact]
    public void Parameters_UseExamplesAndRequiredOnly()
    {
        var requests = Load().Build().Requests;

        Assert.Equal("https://api.pets.test/v1/pets?limit=5", requests[0].Url);
        Assert.Equal("https://api.pets.test/v1/pets/{{$guid}}", requests[2].Url);
        var trace = Assert.Single(requests[2].Headers, h => h.Name == "X-Trace");
        Assert.Equal("abc", trace.Value);
    }

    [Fact]
    public void Body_IsGeneratedFromTheSchema_WithoutLoopingOnRecursiveRefs()
    {
        var request = Load().Build().Requests[1];

        Assert.Equal("application/json", request.ContentType);
        var body = System.Text.Json.Nodes.JsonNode.Parse(request.Body!)!;
        Assert.Equal("Rex", (string?)body["name"]);
        Assert.Equal(1, (int?)body["age"]);
        Assert.Equal("good", (string?)body["tags"]![0]);
        Assert.Equal("user@example.com", (string?)body["owner"]!["email"]);
        Assert.Null(body["id"]);
    }

    [Fact]
    public void Security_UsesBearerPlaceholder_AndOperationsCanOptOut()
    {
        var result = Load().Build();

        Assert.Equal("Bearer {{bearerAuth}}", result.Requests[0].Headers.Single(h => h.Name == "Authorization").Value);
        Assert.DoesNotContain(result.Requests[2].Headers, h => h.Name == "Authorization");
        var list = Assert.Single(result.Parameters, p => p.Name == "bearerAuth");
        Assert.Equal([string.Empty], list.Values);
        Assert.Contains(result.Warnings, w => w.Contains("bearerAuth"));
    }

    [Fact]
    public void ExpectedStatuses_ComeFromThe2xxResponses()
    {
        var requests = Load().Build().Requests;

        Assert.Equal("200", requests[0].ExpectedStatusCodes);
        Assert.Equal("201", requests[1].ExpectedStatusCodes);
        Assert.Equal("200-299", requests[2].ExpectedStatusCodes);
        Assert.Equal("204", requests[3].ExpectedStatusCodes);
    }

    [Fact]
    public void BaseUrlOverride_ReplacesTheServer()
    {
        var request = Load().Build(["GET /pets"], "http://localhost:5000/").Requests[0];

        Assert.StartsWith("http://localhost:5000/pets?", request.Url);
    }

    [Fact]
    public void SpecWithoutAbsoluteServer_UsesABaseUrlParameter()
    {
        var result = Load("""{ "openapi": "3.0.0", "info": { "title": "t" }, "servers": [ { "url": "/api" } ], "paths": { "/a": { "get": {} } } }""").Build();

        Assert.Equal("{{baseUrl}}/a", result.Requests[0].Url);
        Assert.Contains(result.Parameters, p => p.Name == "baseUrl");
        Assert.Contains(result.Warnings, w => w.Contains("baseUrl"));
    }

    [Fact]
    public void FormBodies_AreBuiltFromTheSchema()
    {
        var result = Load("""
            { "openapi": "3.0.0", "info": { "title": "t" }, "servers": [ { "url": "https://x.test" } ],
              "paths": { "/login": { "post": { "requestBody": { "content": { "application/x-www-form-urlencoded": { "schema": { "type": "object", "properties": { "user": { "type": "string", "example": "a b" }, "n": { "type": "integer" } } } } } } } } } }
            """).Build();

        Assert.Equal("user=a%20b&n=1", result.Requests[0].Body);
        Assert.Equal("application/x-www-form-urlencoded", result.Requests[0].ContentType);
    }

    [Fact]
    public void Swagger2_IsRejectedWithAHint()
    {
        var ex = Assert.Throws<ImportException>(() => ImportSource.Parse("""{ "swagger": "2.0", "paths": { "/a": { "get": {} } } }"""));

        Assert.Contains("Swagger 2.0", ex.Message);
    }
}
