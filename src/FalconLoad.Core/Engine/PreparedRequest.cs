using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FalconLoad.Core.Grpc;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>A request definition with everything that doesn't change between requests pre-computed.</summary>
internal sealed class PreparedRequest
{
    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language", "Content-Disposition", "Content-MD5", "Content-Range", "Expires", "Last-Modified", "Allow",
    };

    private HttpMethod _method = HttpMethod.Get;
    private string _url = string.Empty;
    private bool _urlHasVariables;
    private Uri? _staticUri;
    private string? _body;
    private byte[]? _staticBody;
    private bool _bodyHasVariables;
    private string _contentType = "application/json";
    private (string Name, string Value, bool HasVariables)[] _headers = [];
    private string? _graphQLVariables;
    private string? _operationName;
    private MethodDescriptor? _grpcMethod;
    private GrpcStatusMatcher _grpcMatcher = GrpcStatusMatcher.Default;

    public RequestProtocol Protocol { get; private set; }

    /// <summary>The request's method or protocol, as error samples show it.</summary>
    public string Label { get; private set; } = "GET";

    /// <summary>The request's <see cref="RequestDefinition.Route"/>.</summary>
    public string Route { get; private set; } = "/";

    /// <summary>For a WebSocket request, whether to wait for the server's next message after sending.</summary>
    public bool WaitForReply { get; private set; } = true;

    /// <summary>For a WebSocket request, whether to close the connection once the request is done.</summary>
    public bool CloseAfter { get; private set; }

    /// <summary>Whether the protocol itself has to read the body to judge the response, as GraphQL does for its errors.</summary>
    public bool ProtocolNeedsBody => Protocol == RequestProtocol.GraphQL;

    public StatusCodeMatcher Matcher { get; private set; } = StatusCodeMatcher.Default;

    public ResponseChecker Checker { get; private set; } = ResponseChecker.Empty;

    /// <summary>The request's extraction rules, or null when it extracts nothing.</summary>
    public ResponseExtractor? Extractor { get; private set; }

    public bool OncePerVirtualUser { get; private set; }

    /// <summary>Whether this request gets the plan's OAuth2 token: it does unless it sets its own Authorization header.</summary>
    public bool UsesAuth { get; private set; }

    /// <param name="variables">
    /// The selected environment's values, without the names a parameter list or an extracted variable supplies (see
    /// <see cref="LoadTestPlan.EnvironmentValues"/>). They don't change during a run, so they are substituted here,
    /// once, and text that only used them is then as cheap to send as text with no variables at all.
    /// </param>
    /// <param name="auth">Whether the plan sends an OAuth2 token with its requests.</param>
    /// <param name="schema">The plan's proto files, read once for the run; needed by gRPC requests.</param>
    public static PreparedRequest From(RequestDefinition definition, IReadOnlyDictionary<string, string>? variables = null, bool auth = false, ProtoSchema? schema = null)
    {
        StatusCodeMatcher.TryParse(definition.ExpectedStatusCodes, out var matcher);
        var graphQL = definition.Protocol == RequestProtocol.GraphQL;
        var grpc = definition.Protocol == RequestProtocol.Grpc;
        var grpcMethod = grpc
            ? schema?.FindMethod(definition.Grpc?.Method) ?? throw new ArgumentException($"The proto files have no method '{definition.Grpc?.Method}'.")
            : null;
        GrpcStatusMatcher.TryParse(grpc ? definition.ExpectedStatusCodes : null, out var grpcMatcher);

        // A gRPC call goes to the method's path on the server's address, and always has a message, even an empty one.
        var url = TemplateRenderer.ResolveVariables(grpc ? definition.Url.Trim().TrimEnd('/') + "/" + grpcMethod!.Path : definition.Url, variables);
        var body = string.IsNullOrEmpty(definition.Body) ? grpc ? "{}" : null : TemplateRenderer.ResolveVariables(definition.Body, variables);
        var graphQLVariables = graphQL && !string.IsNullOrWhiteSpace(definition.GraphQL?.Variables)
            ? TemplateRenderer.ResolveVariables(definition.GraphQL.Variables, variables)
            : null;
        var prepared = new PreparedRequest
        {
            Protocol = definition.Protocol,
            Label = definition.Label,
            Route = definition.Route,
            _method = graphQL || grpc ? HttpMethod.Post : HttpMethod.Parse(definition.Method.ToUpperInvariant()),
            _grpcMethod = grpcMethod,
            _grpcMatcher = grpcMatcher,
            _url = url.Trim(),
            _urlHasVariables = TemplateRenderer.HasVariables(url),
            _body = body,
            _bodyHasVariables = (body is not null && TemplateRenderer.HasVariables(body)) || TemplateRenderer.HasVariables(graphQLVariables),
            _graphQLVariables = graphQLVariables,
            _operationName = graphQL ? definition.GraphQL?.OperationName : null,
            _contentType = graphQL ? GraphQLRequest.ContentType
                : grpc ? GrpcMessages.ContentType
                : string.IsNullOrWhiteSpace(definition.ContentType) ? "text/plain" : definition.ContentType.Trim(),
            _headers = definition.Headers
                .Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                .Select(h =>
                {
                    var value = TemplateRenderer.ResolveVariables(h.Value, variables);
                    return (h.Name.Trim(), value, TemplateRenderer.HasVariables(value));
                })
                .ToArray(),
            Matcher = matcher,
            Checker = ResponseChecker.From(definition.Assertions),
            Extractor = ResponseExtractor.From(definition),
            OncePerVirtualUser = definition.OncePerVirtualUser,
            WaitForReply = definition.WebSocket?.WaitForReply ?? true,
            CloseAfter = definition.WebSocket?.CloseAfter ?? false,
        };

        prepared.UsesAuth = auth && !prepared._headers.Any(h => string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));

        // Name the tool to the server, so whoever runs the target can tell where the traffic comes from and block it if
        // it isn't welcome. A test that sets its own User-Agent keeps it.
        if (!prepared._headers.Any(h => string.Equals(h.Name, "User-Agent", StringComparison.OrdinalIgnoreCase)))
        {
            prepared._headers = [.. prepared._headers, ("User-Agent", FalconLoadUserAgent.Value, false)];
        }

        if (!prepared._urlHasVariables)
        {
            prepared._staticUri = new Uri(prepared._url, UriKind.Absolute);
        }

        if (body is not null && !prepared._bodyHasVariables)
        {
            prepared._staticBody = prepared.EncodeBody(body, graphQLVariables);
        }

        return prepared;
    }

    /// <summary>The bytes to send for a body whose placeholders have been filled in.</summary>
    private byte[] EncodeBody(string body, string? graphQLVariables) => Protocol switch
    {
        RequestProtocol.GraphQL => GraphQLRequest.BuildBody(body, graphQLVariables, _operationName),
        RequestProtocol.Grpc => GrpcMessages.EncodeRequest(_grpcMethod!, body),
        _ => Encoding.UTF8.GetBytes(body),
    };

    /// <summary>
    /// Whether the HTTP status is a success. A gRPC call always answers 200 and reports its outcome in
    /// <c>grpc-status</c>, so its expected codes are gRPC codes, judged by <see cref="ProtocolError"/>.
    /// </summary>
    public bool HttpStatusMatches(int code) => Protocol == RequestProtocol.Grpc ? code == 200 : Matcher.IsMatch(code);

    /// <summary>What a gRPC call's status is recorded as, such as <c>OK</c> or <c>NOT_FOUND</c>. Null for other protocols.</summary>
    public string? StatusLabel(HttpResponseMessage response)
    {
        if (Protocol != RequestProtocol.Grpc || response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        return GrpcMessages.ReadStatus(response) is { } status ? GrpcStatusMatcher.NameOf(status.Status) : "No grpc-status";
    }

    /// <summary>
    /// The protocol's own verdict on a response whose HTTP status matched: for GraphQL, the first message in its
    /// <c>errors</c> array; for gRPC, a <c>grpc-status</c> that isn't expected. Null when the protocol finds nothing
    /// wrong, or needs nothing beyond the status.
    /// </summary>
    /// <param name="bodyTooLarge">True when only the start of the body was kept, so it can't be judged.</param>
    public string? ProtocolError(HttpResponseMessage response, ReadOnlySpan<byte> body, bool bodyTooLarge)
    {
        switch (Protocol)
        {
            case RequestProtocol.GraphQL:
                return bodyTooLarge ? TooLargeToCheck : GraphQLRequest.FindError(body);
            case RequestProtocol.Grpc:
                if (GrpcMessages.ReadStatus(response) is not { } status)
                {
                    return "gRPC: the response has no grpc-status";
                }

                return _grpcMatcher.IsMatch(status.Status)
                    ? null
                    : status.Message is null ? $"gRPC {GrpcStatusMatcher.NameOf(status.Status)}" : $"gRPC {GrpcStatusMatcher.NameOf(status.Status)}: {status.Message}";
            default:
                return null;
        }
    }

    /// <summary>
    /// The body as checks and extractions see it: a gRPC response decoded to JSON into <paramref name="scratch"/>,
    /// anything else as it came. Only called when something reads the body.
    /// </summary>
    /// <exception cref="ProtobufException">The gRPC response doesn't fit the method's response type.</exception>
    public ReadOnlyMemory<byte> ReadableBody(ReadOnlyMemory<byte> raw, ArrayBufferWriter<byte> scratch)
    {
        if (Protocol != RequestProtocol.Grpc)
        {
            return raw;
        }

        scratch.ResetWrittenCount();
        GrpcMessages.DecodeResponse(_grpcMethod!, raw.Span, scratch);
        return scratch.WrittenMemory;
    }

    internal static readonly string TooLargeToCheck =
        $"Response body over {Assertion.MaxBodyBytes / (1024 * 1024)} MB is too large to check";

    /// <summary>The address a WebSocket request connects to, with its placeholders filled in.</summary>
    public Uri ResolveUri(TemplateContext context) => _staticUri ?? new Uri(TemplateRenderer.Render(_url, context), UriKind.Absolute);

    /// <summary>The message a WebSocket request sends, or empty when it only waits for one.</summary>
    public ReadOnlyMemory<byte> Message(TemplateContext context) =>
        _body is null ? ReadOnlyMemory<byte>.Empty : _staticBody ?? Encoding.UTF8.GetBytes(TemplateRenderer.Render(_body, context));

    /// <summary>The headers a WebSocket handshake sends. Static headers are shared, so a request without placeholders allocates nothing here.</summary>
    /// <param name="authorization">The run's OAuth2 <c>Authorization</c> value, sent when <see cref="UsesAuth"/>.</param>
    public IReadOnlyList<KeyValuePair<string, string>> HandshakeHeaders(TemplateContext context, string? authorization = null)
    {
        if (UsesAuth && authorization is not null)
        {
            return [.. HandshakeHeaders(context), new KeyValuePair<string, string>("Authorization", authorization)];
        }

        if (_headers.Length == 0)
        {
            return [];
        }

        if (_staticHeaders is not null)
        {
            return _staticHeaders;
        }

        var headers = _headers.Select(h => new KeyValuePair<string, string>(h.Name, h.HasVariables ? TemplateRenderer.Render(h.Value, context) : h.Value)).ToArray();
        if (_headers.All(h => !h.HasVariables))
        {
            _staticHeaders = headers;
        }

        return headers;
    }

    private KeyValuePair<string, string>[]? _staticHeaders;

    /// <param name="authorization">The <c>Authorization</c> header value to send when <see cref="UsesAuth"/>, such as <c>Bearer …</c>.</param>
    public HttpRequestMessage Build(TemplateContext context, out long bytesSent, string? authorization = null)
    {
        var uri = _staticUri ?? new Uri(TemplateRenderer.Render(_url, context), UriKind.Absolute);
        var request = new HttpRequestMessage(_method, uri);
        bytesSent = 0;
        if (Protocol == RequestProtocol.Grpc)
        {
            // gRPC needs HTTP/2. Asking for exactly 2.0 also lets a plain http:// address use HTTP/2 without TLS (h2c).
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.TryAddWithoutValidation("TE", "trailers");
        }

        if (_body is not null)
        {
            var bytes = _staticBody ?? EncodeBody(
                TemplateRenderer.Render(_body, context),
                _graphQLVariables is null ? null : TemplateRenderer.Render(_graphQLVariables, context));
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
            request.Content = content;
            bytesSent = bytes.Length;
        }

        foreach (var (name, rawValue, hasVariables) in _headers)
        {
            var value = hasVariables ? TemplateRenderer.Render(rawValue, context) : rawValue;
            if (ContentHeaders.Contains(name))
            {
                if (request.Content is not null)
                {
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (UsesAuth && authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }
}
