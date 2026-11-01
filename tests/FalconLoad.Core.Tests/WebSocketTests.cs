using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using FalconLoad.Core.Engine;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Tests;

public class WebSocketTests
{
    private static RequestDefinition Message(string? body = """{"op":"ping","vu":{{$vu}}}""", bool waitForReply = true, bool closeAfter = false) => new()
    {
        Protocol = RequestProtocol.WebSocket,
        Url = "ws://api.test/socket",
        Body = body,
        WebSocket = new WebSocketOptions { WaitForReply = waitForReply, CloseAfter = closeAfter },
    };

    private static LoadTestPlan Plan(RequestDefinition request, int requests = 10, int users = 2) => new()
    {
        Requests = [request],
        Profile = new LoadProfile { Concurrency = users, StopCondition = StopCondition.RequestCount, TotalRequests = requests },
    };

    private static FakeWebSocketConnector Echo() => new(m => m.Replace("ping", "pong"));

    [Fact]
    public async Task Messages_AreSentAndRepliesCounted_OnOneConnectionPerVirtualUser()
    {
        var sockets = Echo();
        await using var runner = new LoadTestRunner(Plan(Message()), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(10, result.SuccessfulRequests);
        Assert.Equal(0, result.FailedRequests);
        Assert.Equal(10, result.StatusCodes[RequestResult.ReplyStatus]);
        // One connection per virtual user that got to send anything; a fast user may use up the count alone, so the
        // connections are compared with the users that actually sent.
        Assert.Equal(10, sockets.Sent.Count);
        var users = sockets.Sent.Distinct().ToList();
        Assert.All(users, m => Assert.Contains(m, new[] { """{"op":"ping","vu":1}""", """{"op":"ping","vu":2}""" }));
        Assert.Equal(users.Count, sockets.Connections.Count);
        Assert.Equal(sockets.Sent.Sum(s => s.Length), result.BytesSent);
        Assert.Equal(sockets.Sent.Sum(s => s.Length), result.BytesReceived);
        Assert.All(sockets.Sockets, s => Assert.Equal(1, s.Closed));
    }

    [Fact]
    public async Task CloseAfter_OpensANewConnectionForEveryMessage()
    {
        var sockets = Echo();
        await using var runner = new LoadTestRunner(Plan(Message(closeAfter: true), requests: 6, users: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(6, result.SuccessfulRequests);
        Assert.Equal(6, sockets.Connections.Count);
    }

    [Fact]
    public async Task WithoutWaitingForAReply_TheRequestIsDoneWhenSent()
    {
        var sockets = new FakeWebSocketConnector(_ => null);
        await using var runner = new LoadTestRunner(Plan(Message(waitForReply: false), requests: 4), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(4, result.SuccessfulRequests);
        Assert.Equal(4, result.StatusCodes[RequestResult.SentStatus]);
        Assert.Equal(0, result.BytesReceived);
    }

    [Fact]
    public async Task ReplyInSeveralFrames_IsReassembledBeforeChecks()
    {
        var sockets = Echo();
        sockets.FrameSize = 3;
        var request = Message();
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.op", Operator = AssertionOperator.Equals, Value = "pong" });
        request.Assertions.Add(new Assertion { Source = AssertionSource.Body, Operator = AssertionOperator.Contains, Value = "\"vu\":" });
        await using var runner = new LoadTestRunner(Plan(request, requests: 5, users: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(5, result.SuccessfulRequests);
        Assert.All(result.Checks, c => Assert.Equal(5, c.Passed));
        Assert.Equal(sockets.Sent.Sum(s => s.Length), result.BytesReceived);
    }

    [Fact]
    public async Task FailedCheck_CountsAsFailure()
    {
        var sockets = Echo();
        var request = Message();
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.op", Operator = AssertionOperator.Equals, Value = "ping" });
        await using var runner = new LoadTestRunner(Plan(request, requests: 3, users: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.FailedRequests);
        Assert.Equal(3, result.StatusCodes[RequestResult.ReplyStatus]);
        Assert.Equal(3, Assert.Single(result.Checks).Failed);
        Assert.StartsWith("Check failed: $.op equals", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task ExtractedValues_FlowIntoLaterMessagesAndHttpRequests()
    {
        var sockets = new FakeWebSocketConnector(m => m.Contains("subscribe") ? """{"channel":"orders-7"}""" : """{"ack":true}""");
        var handler = FakeHandler.Returning(HttpStatusCode.OK, "ok");
        var subscribe = Message("""{"op":"subscribe"}""");
        subscribe.Extractions.Add(new ExtractionRule { Variable = "channel", Source = ExtractionSource.JsonPath, Expression = "$.channel" });
        var publish = Message("""{"op":"publish","channel":"{{channel}}"}""");
        var plan = Plan(subscribe, requests: 3, users: 1);
        plan.Requests.Add(publish);
        plan.Requests.Add(new RequestDefinition { Url = "http://api.test/channels/{{channel}}" });
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(handler), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.SuccessfulRequests);
        Assert.Contains("""{"op":"publish","channel":"orders-7"}""", sockets.Sent);
        Assert.Equal("http://api.test/channels/orders-7", Assert.Single(handler.Received).Request.RequestUri!.ToString());
        Assert.Equal(1, result.StatusCodes["200"]);
        Assert.Equal(2, result.StatusCodes[RequestResult.ReplyStatus]);
    }

    [Fact]
    public async Task ServerClosingTheConnection_IsAFailure_AndTheNextMessageReconnects()
    {
        var sockets = Echo();
        sockets.CloseWith = WebSocketCloseStatus.PolicyViolation;
        await using var runner = new LoadTestRunner(Plan(Message(), requests: 3, users: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(3, result.FailedRequests);
        Assert.Equal(3, result.StatusCodes[RequestResult.NoResponseStatus]);
        Assert.StartsWith("Server closed the connection: PolicyViolation", Assert.Single(result.Errors).Message);
        Assert.Equal(3, sockets.Connections.Count);
    }

    [Fact]
    public async Task FailedHandshake_IsReportedAsTransportError()
    {
        var sockets = new FakeWebSocketConnector(_ => null, _ => throw new WebSocketException(WebSocketError.Faulted, "Unable to connect to the remote server", new HttpRequestException(HttpRequestError.ConnectionError, "boom", new IOException("Connection refused"))));
        await using var runner = new LoadTestRunner(Plan(Message(), requests: 2, users: 1), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.FailedRequests);
        Assert.Equal("WebSocket: Unable to connect to the remote server (ConnectionError: Connection refused)", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task NoReplyInTime_IsATimeout()
    {
        var sockets = new FakeWebSocketConnector(_ => null);
        var plan = Plan(Message(), requests: 1, users: 1);
        plan.Profile.TimeoutSeconds = 1;
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        var result = await runner.RunAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(1, result.FailedRequests);
        Assert.Equal("Timeout after 1s", Assert.Single(result.Errors).Message);
    }

    [Fact]
    public async Task HandshakeHeaders_AreRenderedAndSent()
    {
        var sockets = Echo();
        var request = Message();
        request.Headers.Add(new HeaderEntry { Name = "Authorization", Value = "Bearer {{token}}" });
        request.Headers.Add(new HeaderEntry { Name = "Sec-WebSocket-Protocol", Value = "graphql-ws" });
        var plan = Plan(request, requests: 1, users: 1);
        var environment = new TestEnvironment { Name = "Staging", Variables = [new EnvironmentVariable { Name = "token", Value = "s3cret", Secret = true }] };
        await using var runner = new LoadTestRunner(plan, new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), environment, sockets);

        await runner.RunAsync(cancellationToken: CancellationToken.None);

        var (uri, headers) = Assert.Single(sockets.Connections);
        Assert.Equal("ws://api.test/socket", uri.ToString());
        Assert.Contains(headers, h => h.Key == "Authorization" && h.Value == "Bearer s3cret");
        Assert.Contains(headers, h => h.Key == "Sec-WebSocket-Protocol" && h.Value == "graphql-ws");
    }

    [Fact]
    public async Task Probe_ConnectsSendsAndCloses_ShowingTheReplyAndHandshake()
    {
        var sockets = Echo();
        var request = Message();
        request.Assertions.Add(new Assertion { Source = AssertionSource.JsonPath, Target = "$.op", Operator = AssertionOperator.Equals, Value = "pong" });
        request.Extractions.Add(new ExtractionRule { Variable = "op", Source = ExtractionSource.JsonPath, Expression = "$.op" });

        var result = await RequestProbe.SendAsync(request, new LoadProfile(), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        Assert.True(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Equal(RequestResult.ReplyStatus, result.StatusLabel);
        Assert.True(result.HasResponse);
        Assert.Equal("""{"op":"pong","vu":1}""", result.BodyPreview);
        Assert.Contains(result.Headers, h => h.Key == "Upgrade" && h.Value == "websocket");
        Assert.True(Assert.Single(result.Checks).Passed);
        Assert.Equal("pong", Assert.Single(result.Extractions!).Value);
        Assert.Equal(1, Assert.Single(sockets.Sockets).Closed);
    }

    [Fact]
    public async Task Probe_ReportsAFailedHandshake()
    {
        var sockets = new FakeWebSocketConnector(_ => null, _ => throw new WebSocketException(WebSocketError.NotAWebSocket, "The server returned status code '404' when status code '101' was expected."));

        var result = await RequestProbe.SendAsync(Message(), new LoadProfile(), new HttpMessageInvoker(FakeHandler.Returning(HttpStatusCode.OK)), webSockets: sockets);

        Assert.False(result.Success);
        Assert.False(result.HasResponse);
        Assert.StartsWith("WebSocket: The server returned status code '404'", result.Error);
    }

    [Fact]
    public void Validation_AllowsWsUrls_AndRefusesHeaderChecks()
    {
        Assert.Empty(Message().Validate());
        var secure = Message();
        secure.Url = "wss://api.test/socket";
        Assert.Empty(secure.Validate());
        var http = Message();
        http.Url = "https://api.test/socket";
        Assert.Empty(http.Validate());

        var ftp = Message();
        ftp.Url = "ftp://api.test/socket";
        Assert.Contains(ftp.Validate(), p => p.Contains("ws:// or wss://"));

        var headerCheck = Message();
        headerCheck.Assertions.Add(new Assertion { Source = AssertionSource.Header, Target = "Content-Type", Operator = AssertionOperator.Exists });
        Assert.Contains(headerCheck.Validate(), p => p.Contains("no headers to check"));

        var headerExtract = Message();
        headerExtract.Extractions.Add(new ExtractionRule { Variable = "x", Source = ExtractionSource.Header, Expression = "Location" });
        Assert.Contains(headerExtract.Validate(), p => p.Contains("no headers to extract"));

        Assert.Contains(Message(body: null, waitForReply: false).Validate(), p => p.Contains("needs a message"));
        Assert.Empty(Message(body: null).Validate());

        var httpRequest = new RequestDefinition { Url = "ws://api.test/socket" };
        Assert.Contains(httpRequest.Validate(), p => p.Contains("http:// or https://"));
    }

    [Fact]
    public void WebSocketRequest_RoundTripsThroughJson_AndOldPlansHaveNoOptions()
    {
        var clone = Plan(Message(closeAfter: true)).Clone();
        var request = Assert.Single(clone.Requests);
        Assert.Equal(RequestProtocol.WebSocket, request.Protocol);
        Assert.True(request.WebSocket!.CloseAfter);
        Assert.Equal("WS", request.Label);

        var old = JsonSerializer.Deserialize<LoadTestPlan>("""{"name":"old","requests":[{"method":"GET","url":"http://api.test/x"}]}""")!;
        Assert.Null(Assert.Single(old.Requests).WebSocket);
    }
}
