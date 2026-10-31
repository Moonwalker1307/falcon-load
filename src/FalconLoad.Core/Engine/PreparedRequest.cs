using System.Net.Http.Headers;
using System.Text;
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

    public RequestProtocol Protocol { get; private set; }

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
    public static PreparedRequest From(RequestDefinition definition, IReadOnlyDictionary<string, string>? variables = null, bool auth = false)
    {
        StatusCodeMatcher.TryParse(definition.ExpectedStatusCodes, out var matcher);
        var graphQL = definition.Protocol == RequestProtocol.GraphQL;
        var url = TemplateRenderer.ResolveVariables(definition.Url, variables);
        var body = string.IsNullOrEmpty(definition.Body) ? null : TemplateRenderer.ResolveVariables(definition.Body, variables);
        var graphQLVariables = graphQL && !string.IsNullOrWhiteSpace(definition.GraphQL?.Variables)
            ? TemplateRenderer.ResolveVariables(definition.GraphQL.Variables, variables)
            : null;
        var prepared = new PreparedRequest
        {
            Protocol = definition.Protocol,
            _method = graphQL ? HttpMethod.Post : HttpMethod.Parse(definition.Method.ToUpperInvariant()),
            _url = url.Trim(),
            _urlHasVariables = TemplateRenderer.HasVariables(url),
            _body = body,
            _bodyHasVariables = (body is not null && TemplateRenderer.HasVariables(body)) || TemplateRenderer.HasVariables(graphQLVariables),
            _graphQLVariables = graphQLVariables,
            _operationName = graphQL ? definition.GraphQL?.OperationName : null,
            _contentType = graphQL ? GraphQLRequest.ContentType
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
        };

        prepared.UsesAuth = auth && !prepared._headers.Any(h => string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));

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
    private byte[] EncodeBody(string body, string? graphQLVariables) =>
        Protocol == RequestProtocol.GraphQL ? GraphQLRequest.BuildBody(body, graphQLVariables, _operationName) : Encoding.UTF8.GetBytes(body);

    /// <summary>
    /// The protocol's own verdict on a response whose status matched: for GraphQL, the first message in its
    /// <c>errors</c> array. Null when the protocol finds nothing wrong, or needs nothing beyond the status.
    /// </summary>
    /// <param name="bodyTooLarge">True when only the start of the body was kept, so it can't be judged.</param>
    public string? ProtocolError(ReadOnlySpan<byte> body, bool bodyTooLarge)
    {
        if (Protocol != RequestProtocol.GraphQL)
        {
            return null;
        }

        return bodyTooLarge ? TooLargeToCheck : GraphQLRequest.FindError(body);
    }

    internal static readonly string TooLargeToCheck =
        $"Response body over {Assertion.MaxBodyBytes / (1024 * 1024)} MB is too large to check";

    /// <param name="authorization">The <c>Authorization</c> header value to send when <see cref="UsesAuth"/>, such as <c>Bearer …</c>.</param>
    public HttpRequestMessage Build(TemplateContext context, out long bytesSent, string? authorization = null)
    {
        var uri = _staticUri ?? new Uri(TemplateRenderer.Render(_url, context), UriKind.Absolute);
        var request = new HttpRequestMessage(_method, uri);
        bytesSent = 0;

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
