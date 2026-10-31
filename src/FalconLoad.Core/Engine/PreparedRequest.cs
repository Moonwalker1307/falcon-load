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

    /// <summary>The request's extraction rules, or null when it extracts nothing.</summary>
    public ResponseExtractor? Extractor { get; private set; }

    public bool OncePerVirtualUser { get; private set; }

    public static PreparedRequest From(RequestDefinition definition)
    {
        StatusCodeMatcher.TryParse(definition.ExpectedStatusCodes, out var matcher);
        var hasBody = !string.IsNullOrEmpty(definition.Body);
        var prepared = new PreparedRequest
        {
            _method = HttpMethod.Parse(definition.Method.ToUpperInvariant()),
            _url = definition.Url.Trim(),
            _urlHasVariables = TemplateRenderer.HasVariables(definition.Url),
            _body = hasBody ? definition.Body : null,
            _bodyHasVariables = hasBody && TemplateRenderer.HasVariables(definition.Body),
            _contentType = string.IsNullOrWhiteSpace(definition.ContentType) ? "text/plain" : definition.ContentType.Trim(),
            _headers = definition.Headers
                .Where(h => h.Enabled && !string.IsNullOrWhiteSpace(h.Name))
                .Select(h => (h.Name.Trim(), h.Value, TemplateRenderer.HasVariables(h.Value)))
                .ToArray(),
            Matcher = matcher,
            Extractor = ResponseExtractor.From(definition),
            OncePerVirtualUser = definition.OncePerVirtualUser,
        };

        if (!prepared._urlHasVariables)
        {
            prepared._staticUri = new Uri(prepared._url, UriKind.Absolute);
        }

        if (hasBody && !prepared._bodyHasVariables)
        {
            prepared._staticBody = Encoding.UTF8.GetBytes(definition.Body!);
        }

        return prepared;
    }

    public HttpRequestMessage Build(TemplateContext context, out long bytesSent)
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

        return request;
    }
}
