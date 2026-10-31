using FalconLoad.Core.Import;

namespace FalconLoad.Core.Tests;

public class CurlImporterTests
{
    private static ImportResult Read(string text) => CurlImporter.Read(text);

    [Fact]
    public void SimpleGet_IsReadAsGet()
    {
        var result = Read("curl https://api.test/items?page=2");

        var request = Assert.Single(result.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.test/items?page=2", request.Url);
        Assert.Equal("GET /items", request.Name);
        Assert.Null(request.Body);
    }

    [Fact]
    public void PostWithHeadersAndJsonBody_KeepsEverything()
    {
        var result = Read("""
            curl -X POST 'https://api.test/orders' \
              -H 'Content-Type: application/json' \
              -H "Authorization: Bearer abc" \
              --data-raw '{"id": 7, "note": "it'\''s fine"}'
            """);

        var request = Assert.Single(result.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"id": 7, "note": "it's fine"}""", request.Body);
        var header = Assert.Single(request.Headers);
        Assert.Equal("Authorization", header.Name);
        Assert.Equal("Bearer abc", header.Value);
    }

    [Fact]
    public void Data_ImpliesPost_AndFormContentTypeLikeCurl()
    {
        var request = Assert.Single(Read("curl https://api.test/login -d user=a -d 'pass=b c'").Requests);

        Assert.Equal("POST", request.Method);
        Assert.Equal("user=a&pass=b c", request.Body);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
    }

    [Fact]
    public void ExplicitContentTypeHeader_BeatsTheFormDefault()
    {
        var request = Assert.Single(Read("curl https://api.test/x -H 'content-type: application/json' -d '{}'").Requests);

        Assert.Equal("application/json", request.ContentType);
        Assert.Empty(request.Headers);
    }

    [Fact]
    public void BundledAndAttachedShortOptions_AreUnderstood()
    {
        var request = Assert.Single(Read("curl -sSLk -XPUT -H'X-Id: 5' https://api.test/a").Requests);

        Assert.Equal("PUT", request.Method);
        Assert.Equal("5", Assert.Single(request.Headers).Value);
        Assert.True(Read("curl -k https://api.test/a").IgnoreTlsErrors);
        Assert.False(Read("curl https://api.test/a").IgnoreTlsErrors);
    }

    [Fact]
    public void BrowserStyleAnsiCQuoting_IsDecoded()
    {
        var request = Assert.Single(Read("""curl 'https://api.test/a' --data-raw $'{"name":"caf\u00e9\\n","q":"it\'s"}' -H 'accept: */*'""").Requests);

        Assert.Equal("{\"name\":\"café\\n\",\"q\":\"it's\"}", request.Body);
    }

    [Fact]
    public void BasicAuthAndCookie_BecomeHeaders()
    {
        var request = Assert.Single(Read("curl -u alice:s3cret -b 'a=1; b=2' -A MyApp/1.0 https://api.test/me").Requests);

        Assert.Equal("Basic YWxpY2U6czNjcmV0", request.Headers.Single(h => h.Name == "Authorization").Value);
        Assert.Equal("a=1; b=2", request.Headers.Single(h => h.Name == "Cookie").Value);
        Assert.Equal("MyApp/1.0", request.Headers.Single(h => h.Name == "User-Agent").Value);
    }

    [Fact]
    public void HeadAndGetWithData_UseTheRightMethodAndUrl()
    {
        Assert.Equal("HEAD", Read("curl -I https://api.test/a").Requests[0].Method);

        var request = Assert.Single(Read("curl -G https://api.test/search -d q=falcon --data-urlencode 'tag=a b'").Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.test/search?q=falcon&tag=a%20b", request.Url);
        Assert.Null(request.Body);
    }

    [Fact]
    public void JsonOption_SetsBodyAndHeaders()
    {
        var request = Assert.Single(Read("curl --json '{\"a\":1}' https://api.test/a").Requests);

        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("{\"a\":1}", request.Body);
        Assert.Contains(request.Headers, h => h.Name == "Accept");
    }

    [Fact]
    public void FormFields_BecomeAMultipartBody_AndFilesAreReported()
    {
        var result = Read("curl -F name=falcon -F file=@photo.png https://api.test/upload");

        var request = Assert.Single(result.Requests);
        Assert.StartsWith("multipart/form-data; boundary=", request.ContentType);
        Assert.Contains("name=\"name\"", request.Body);
        Assert.DoesNotContain("photo.png", request.Body);
        Assert.Contains(result.Warnings, w => w.Contains("uploads a file"));
    }

    [Fact]
    public void BodyFromAFile_IsLeftOutWithAWarning()
    {
        var result = Read("curl -d @payload.json https://api.test/a");

        Assert.Null(result.Requests[0].Body);
        Assert.Contains(result.Warnings, w => w.Contains("payload.json"));
    }

    [Fact]
    public void SeveralCommands_BecomeSeveralRequests_AndSchemeIsAdded()
    {
        var result = Read("""
            # warm up
            curl localhost:5000/health && curl -X DELETE https://api.test/items/1
            echo done
            """);

        Assert.Equal(2, result.Requests.Count);
        Assert.Equal("http://localhost:5000/health", result.Requests[0].Url);
        Assert.Equal("DELETE", result.Requests[1].Method);
    }

    [Fact]
    public void IgnoredOptions_AreReportedOnceAndTheirValuesAreNotTakenAsUrls()
    {
        var result = Read("curl -x http://proxy:8080 --compressed -o out.txt -s https://api.test/a");

        Assert.Equal("https://api.test/a", Assert.Single(result.Requests).Url);
        Assert.Single(result.Warnings, w => w.Contains("-x"));
    }

    [Fact]
    public void ImportedVariablesStillWork()
    {
        var request = Assert.Single(Read("curl 'https://api.test/users/{{userId}}?t={{$timestamp}}' -H 'X-Trace: {{$guid}}'").Requests);

        Assert.Equal("https://api.test/users/{{userId}}?t={{$timestamp}}", request.Url);
        Assert.Equal("{{$guid}}", request.Headers[0].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("curl -s")]
    public void NoCommandWithAUrl_IsRejected(string text) =>
        Assert.Throws<ImportException>(() => Read(text));
}
