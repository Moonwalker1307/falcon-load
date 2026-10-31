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
        var url = TemplateRenderer.ResolveVariables(definition.Url, variables);
        var body = string.IsNullOrEmpty(definition.Body) ? null : TemplateRenderer.ResolveVariables(definition.Body, variables);
        var prepared = new PreparedRequest
        {
            _method = HttpMethod.Parse(definition.Method.ToUpperInvariant()),
            _url = url.Trim(),
            _urlHasVariables = TemplateRenderer.HasVariables(url),
            _body = body,
            _bodyHasVariables = body is not null && TemplateRenderer.HasVariables(body),
            _contentType = string.IsNullOrWhiteSpace(definition.ContentType) ? "text/plain" : definition.ContentType.Trim(),
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
            prepared._staticBody = Encoding.UTF8.GetBytes(body);
        }

        return prepared;
    }

    /// <param name="authorization">The <c>Authorization</c> header value to send when <see cref="UsesAuth"/>, such as <c>Bearer …</c>.</param>
    public HttpRequestMessage Build(TemplateContext context, out long bytesSent, string? authorization = null)
    {
        var uri = _staticUri ?? new Uri(TemplateRenderer.Render(_url, context), UriKind.Absolute);
        var request = new HttpRequestMessage(_method, uri);
        bytesSent = 0;

        if (_body is not null)
        {
            var bytes = _staticBody ?? Encoding.UTF8.GetBytes(TemplateRenderer.Render(_body, context));
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
