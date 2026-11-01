namespace FalconLoad.Core.Model;

/// <summary>A single request to send as part of a load test: an HTTP request, a GraphQL operation over HTTP, or a WebSocket message.</summary>
public sealed class RequestDefinition
{
    public static readonly string[] SupportedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public string Name { get; set; } = string.Empty;

    /// <summary>Plans saved before protocols existed have no protocol and are plain HTTP.</summary>
    public RequestProtocol Protocol { get; set; } = RequestProtocol.Http;

    /// <summary>The HTTP method. A GraphQL request is always a POST, whatever this says.</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Absolute URL. May contain template variables such as <c>{{$guid}}</c> or a parameter list's <c>{{name}}</c>.</summary>
    public string Url { get; set; } = "https://localhost:5001/";

    public List<HeaderEntry> Headers { get; set; } = [];

    /// <summary>
    /// Optional request body, the query of a GraphQL request, or the message of a WebSocket request (empty to only wait
    /// for a message). May contain template variables.
    /// </summary>
    public string? Body { get; set; }

    /// <summary>The variables and operation name of a GraphQL request. Null for other protocols.</summary>
    public GraphQLOptions? GraphQL { get; set; }

    /// <summary>How a WebSocket request waits and closes. Null for other protocols.</summary>
    public WebSocketOptions? WebSocket { get; set; }

    /// <summary>The method a gRPC request calls. Null for other protocols.</summary>
    public GrpcOptions? Grpc { get; set; }

    public string ContentType { get; set; } = "application/json";

    /// <summary>
    /// Status codes that count as success, e.g. "200,201,204" or "200-299".
    /// Empty means any 2xx or 3xx response is a success.
    /// </summary>
    public string? ExpectedStatusCodes { get; set; }

    /// <summary>Checks on the response body and headers. Plans saved before checks existed have none.</summary>
    public List<Assertion> Assertions { get; set; } = [];

    /// <summary>
    /// Values to take from the response into the virtual user's variables, for later requests to use as <c>{{name}}</c>.
    /// Plans saved before extraction existed have none.
    /// </summary>
    public List<ExtractionRule> Extractions { get; set; } = [];

    /// <summary>
    /// Sends this request on a virtual user's first iteration only, such as a login whose token later requests reuse.
    /// If it fails, it is sent again on the next iteration, until it succeeds once.
    /// </summary>
    public bool OncePerVirtualUser { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Label} {Url}" : Name;

    /// <summary>What is shown in place of the method: the method itself for HTTP, otherwise the protocol's name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Protocol switch
    {
        RequestProtocol.GraphQL => "GraphQL",
        RequestProtocol.WebSocket => "WS",
        RequestProtocol.Grpc => "gRPC",
        _ => Method,
    };

    /// <summary>
    /// Where the request goes without the server's address or the query string, such as <c>/users/{{id}}</c>, so failures
    /// can be grouped by endpoint. It comes from the URL as written, placeholders and all, so it never holds an
    /// environment's values. A gRPC request's route is its method, and a GraphQL request adds its operation name.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Route => Protocol switch
    {
        RequestProtocol.Grpc => "/" + (Grpc?.Method ?? string.Empty).Trim().TrimStart('/'),
        RequestProtocol.GraphQL when !string.IsNullOrWhiteSpace(GraphQL?.OperationName) => $"{PathOf(Url)} {GraphQL.OperationName.Trim()}",
        _ => PathOf(Url),
    };

    /// <summary>The path of a URL that may hold placeholders, including one that stands for the whole server address.</summary>
    internal static string PathOf(string? url)
    {
        var text = (url ?? string.Empty).Trim();
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start >= 0)
        {
            start += 3;
        }
        else if (text.StartsWith("{{", StringComparison.Ordinal) && text.IndexOf("}}", StringComparison.Ordinal) is var close and > 0)
        {
            // A URL such as {{baseUrl}}/users: the placeholder is the server's address.
            start = close + 2;
        }
        else
        {
            start = 0;
        }

        var slash = text.IndexOfAny(['/', '?', '#'], start);
        if (slash < 0 || text[slash] != '/')
        {
            return "/";
        }

        var end = text.IndexOfAny(['?', '#'], slash);
        return end < 0 ? text[slash..] : text[slash..end];
    }

    /// <param name="parameters">The plan's parameter lists; URLs that use them are checked with their first values.</param>
    /// <param name="variables">The names of the plan's extracted variables, which URLs may use too.</param>
    /// <param name="environment">The selected environment's values, substituted into the URL before it is checked.</param>
    /// <param name="forRun">
    /// True when the request is about to be sent, so a variable nothing supplies is a problem. When saving, the
    /// environment isn't chosen yet, so the URL is only checked as far as it can be.
    /// </param>
    public IEnumerable<string> Validate(
        ParameterSet? parameters = null,
        IReadOnlyCollection<string>? variables = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool forRun = false)
    {
        if (!Enum.IsDefined(Protocol))
        {
            yield return "Unknown protocol.";
        }

        if (Protocol == RequestProtocol.Http && !SupportedMethods.Contains(Method, StringComparer.OrdinalIgnoreCase))
        {
            yield return $"Unsupported HTTP method '{Method}'.";
        }

        if (Protocol == RequestProtocol.GraphQL)
        {
            if (string.IsNullOrWhiteSpace(Body))
            {
                yield return "A GraphQL request needs a query.";
            }

            // Placeholders may stand anywhere in the JSON, as a string's text or as a bare number, so they are replaced by a
            // number before the shape is checked.
            var graphQLVariables = GraphQL?.Variables;
            if (!string.IsNullOrWhiteSpace(graphQLVariables) && !Engine.GraphQLRequest.IsJsonObject(TemplateRenderer.ReplaceVariables(graphQLVariables, "1")))
            {
                yield return "GraphQL variables must be a JSON object, such as { \"id\": 1 }.";
            }
        }

        if (Protocol == RequestProtocol.WebSocket)
        {
            // A WebSocket message has no status and no headers; only its text can be checked or extracted from.
            if (Assertions.Any(a => a.Enabled && a.Source == AssertionSource.Header))
            {
                yield return "A WebSocket reply has no headers to check; check its body or a JSON path instead.";
            }

            if (Extractions.Any(e => e.Source == ExtractionSource.Header))
            {
                yield return "A WebSocket reply has no headers to extract from; use a JSON path or a regular expression.";
            }

            if (string.IsNullOrEmpty(Body) && WebSocket is { WaitForReply: false })
            {
                yield return "A WebSocket request needs a message to send, or has to wait for one.";
            }
        }

        // Validate the URL with template variables replaced by harmless placeholders.
        var webSocket = Protocol == RequestProtocol.WebSocket;
        var probe = TemplateRenderer.RenderForValidation(Url, parameters, variables, environment, assumeUnresolvedDefined: !forRun);
        var schemes = webSocket ? "ws:// or wss://" : "http:// or https://";
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || !IsAllowedScheme(uri.Scheme, webSocket))
        {
            var missing = TemplateRenderer.UnresolvedNames(Url, environment, parameters, variables);
            yield return missing.Count == 0
                ? $"URL must be an absolute {schemes} address."
                : $"URL must be an absolute {schemes} address. No value for {string.Join(", ", missing.Select(m => "{{" + m + "}}"))}: choose an environment that defines it.";
        }

        if (Protocol == RequestProtocol.Grpc)
        {
            // The method and the message are checked against the plan's proto files, in LoadTestPlan.Validate.
            if (string.IsNullOrWhiteSpace(Grpc?.Method))
            {
                yield return "A gRPC request needs the method to call, such as shop.v1.Catalog/GetProduct.";
            }

            if (!GrpcStatusMatcher.TryParse(ExpectedStatusCodes, out _))
            {
                yield return "Expected gRPC status codes must be names or numbers from 0 to 16, such as \"OK\" or \"OK,NOT_FOUND\".";
            }
        }
        else if (!webSocket && !StatusCodeMatcher.TryParse(ExpectedStatusCodes, out _))
        {
            yield return "Expected status codes must look like \"200,201\" or \"200-299\".";
        }

        if (Assertions.Count > Assertion.MaxPerRequest)
        {
            yield return $"A request can have at most {Assertion.MaxPerRequest} checks.";
        }

        foreach (var problem in Assertions.Where(a => a.Enabled).SelectMany(a => a.Validate()))
        {
            yield return problem;
        }

        foreach (var header in Headers.Where(h => h.Enabled))
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                yield return "Header names cannot be empty.";
            }
        }

        foreach (var problem in Extractions.SelectMany(e => e.Validate()))
        {
            yield return problem;
        }
    }
    // A WebSocket address is usually written ws:// or wss://, but the http:// form of the same address works too.
    private static bool IsAllowedScheme(string scheme, bool webSocket) =>
        scheme == Uri.UriSchemeHttp || scheme == Uri.UriSchemeHttps || (webSocket && (scheme == Uri.UriSchemeWs || scheme == Uri.UriSchemeWss));
}

public sealed class HeaderEntry
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
