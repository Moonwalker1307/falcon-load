namespace FalconLoad.Core.Model;

/// <summary>How a <see cref="RequestDefinition"/> talks to its server.</summary>
public enum RequestProtocol
{
    /// <summary>A plain HTTP request: method, URL, headers and body.</summary>
    Http,

    /// <summary>
    /// A GraphQL operation sent as an HTTP POST with a JSON body of <c>query</c>, <c>variables</c> and
    /// <c>operationName</c>. A response whose <c>errors</c> array has entries counts as a failed request.
    /// </summary>
    GraphQL,

    /// <summary>
    /// A message over a WebSocket connection that the virtual user keeps open across its requests and iterations. The
    /// request is timed from sending the message, after connecting if need be, until the reply has been received.
    /// </summary>
    WebSocket,

    /// <summary>
    /// A gRPC call over HTTP/2: the request message is written as JSON and encoded with the plan's <c>.proto</c> files,
    /// and the response is decoded back to JSON for checks and extractions. A call succeeds when its <c>grpc-status</c>
    /// is one of the expected codes, OK by default.
    /// </summary>
    Grpc,
}

/// <summary>The parts of a GraphQL request besides the query, which is the request's body.</summary>
public sealed class GraphQLOptions
{
    /// <summary>The operation's variables as a JSON object, or empty for none. May contain template variables.</summary>
    public string? Variables { get; set; }

    /// <summary>Which operation to run when the query defines several. Optional.</summary>
    public string? OperationName { get; set; }
}

/// <summary>How a WebSocket request behaves beyond its message, which is the request's body.</summary>
public sealed class WebSocketOptions
{
    /// <summary>
    /// Wait for the next message from the server after sending, and time the request until it has arrived. Off, the
    /// request is done once the message is sent; a reply the server sends anyway is read by the next request that waits.
    /// </summary>
    public bool WaitForReply { get; set; } = true;

    /// <summary>Close the connection after this request, so the next one opens a new one. Off, it stays open for the virtual user.</summary>
    public bool CloseAfter { get; set; }
}

/// <summary>The parts of a gRPC call besides its message, which is the request's body as JSON.</summary>
public sealed class GrpcOptions
{
    /// <summary>The method to call, as <c>package.Service/Method</c> from the plan's proto files.</summary>
    public string Method { get; set; } = string.Empty;
}
