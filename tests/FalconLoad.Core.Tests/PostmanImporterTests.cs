using System.Text.Json.Nodes;
using FalconLoad.Core.Import;

namespace FalconLoad.Core.Tests;

public class PostmanImporterTests
{
    private static ImportResult Read(string json) => PostmanImporter.Read(JsonNode.Parse(json)!);

    private const string Collection = """
        {
          "info": { "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
          "variable": [ { "key": "base-url", "value": "https://api.test" }, { "key": "token", "value": "abc" } ],
          "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
          "item": [
            {
              "name": "Orders",
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
                    "auth": { "type": "noauth" },
                    "url": { "raw": "{{base-url}}/orders/:id", "variable": [ { "key": "id", "value": "42" } ] }
                  }
                }
              ]
            },
            { "name": "Ping", "request": "https://api.test/ping" }
          ]
        }
        """;

    [Fact]
    public void Collection_BecomesRequestsInOrder_WithFolderNames()
    {
        var result = Read(Collection);

        Assert.Equal("Shop", result.Name);
        Assert.Equal(["Orders / Create order", "Orders / Get order", "Ping"], result.Requests.Select(r => r.Name));
        Assert.Equal("GET", result.Requests[2].Method);
        Assert.Equal("https://api.test/ping", result.Requests[2].Url);
    }

    [Fact]
    public void Variables_BecomeParameterLists_WithUsableNames()
    {
        var result = Read(Collection);

        var baseUrl = Assert.Single(result.Parameters, p => p.Name == "base_url");
        Assert.Equal(["https://api.test"], baseUrl.Values);
        Assert.Equal("{{base_url}}/orders?a=1", result.Requests[0].Url);
        Assert.Contains("\"shop\": \"{{base_url}}\"", result.Requests[0].Body);
    }

    [Fact]
    public void Request_KeepsMethodHeadersBodyAndContentType()
    {
        var request = Read(Collection).Requests[0];

        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Contains("{{$guid}}", request.Body);
        var off = Assert.Single(request.Headers, h => h.Name == "X-Off");
        Assert.False(off.Enabled);
    }

    [Fact]
    public void DisabledQueryParameters_AreDropped_AndPathVariablesFilled()
    {
        var result = Read(Collection);

        Assert.Equal("{{base_url}}/orders?a=1", result.Requests[0].Url);
        Assert.Equal("{{base_url}}/orders/42", result.Requests[1].Url);
    }

    [Fact]
    public void CollectionAuth_IsInherited_AndNoAuthOptsOut()
    {
        var result = Read(Collection);

        Assert.Equal("Bearer {{token}}", result.Requests[0].Headers.Single(h => h.Name == "Authorization").Value);
        Assert.DoesNotContain(result.Requests[1].Headers, h => h.Name == "Authorization");
        Assert.Contains(result.Requests[2].Headers, h => h.Name == "Authorization");
    }

    [Fact]
    public void ScriptsAndUndefinedVariables_AreReported()
    {
        var result = Read("""
            { "info": { "name": "x" }, "item": [ { "name": "a", "event": [ { "listen": "prerequest" } ], "request": { "method": "GET", "url": "{{host}}/a/{{$randomEmail}}" } } ] }
            """);

        Assert.Contains(result.Warnings, w => w.Contains("scripts"));
        Assert.Contains(result.Warnings, w => w.Contains("'host'"));
        Assert.Contains(result.Warnings, w => w.Contains("$randomEmail"));
        Assert.Contains(result.Parameters, p => p.Name == "host");
    }

    [Fact]
    public void UrlencodedFormdataAndGraphQlBodies_AreConverted()
    {
        var result = Read("""
            { "info": { "name": "x" }, "item": [
              { "name": "form", "request": { "method": "POST", "url": "https://api.test/f", "body": { "mode": "urlencoded", "urlencoded": [ { "key": "a b", "value": "x&y" }, { "key": "t", "value": "{{$timestamp}}" }, { "key": "off", "value": "1", "disabled": true } ] } } },
              { "name": "multi", "request": { "method": "POST", "url": "https://api.test/m", "body": { "mode": "formdata", "formdata": [ { "key": "n", "value": "v", "type": "text" }, { "key": "f", "type": "file", "src": "a.png" } ] } } },
              { "name": "gql", "request": { "method": "POST", "url": "https://api.test/g", "body": { "mode": "graphql", "graphql": { "query": "{ me { id } }", "variables": "{\"a\":1}" } } } }
            ] }
            """);

        Assert.Equal("a%20b=x%26y&t={{$timestamp}}", result.Requests[0].Body);
        Assert.Equal("application/x-www-form-urlencoded", result.Requests[0].ContentType);
        Assert.StartsWith("multipart/form-data", result.Requests[1].ContentType);
        Assert.DoesNotContain("a.png", result.Requests[1].Body);
        Assert.Contains(result.Warnings, w => w.Contains("uploads a file"));
        Assert.Equal("""{"query":"{ me { id } }","variables":{"a":1}}""", result.Requests[2].Body);
    }

    [Fact]
    public void BasicAuth_IsEncoded()
    {
        var request = Assert.Single(Read("""
            { "info": { "name": "x" }, "item": [ { "name": "a", "request": { "method": "GET", "url": "https://api.test/a", "auth": { "type": "basic", "basic": [ { "key": "username", "value": "alice" }, { "key": "password", "value": "s3cret" } ] } } } ] }
            """).Requests);

        Assert.Equal("Basic YWxpY2U6czNjcmV0", request.Headers.Single().Value);
    }

    [Fact]
    public void EmptyCollection_IsRejected() =>
        Assert.Throws<ImportException>(() => Read("""{ "info": { "name": "x" }, "item": [ { "name": "folder", "item": [] } ] }"""));
}
