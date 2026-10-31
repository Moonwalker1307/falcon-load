namespace FalconLoad.Core.Model;

/// <summary>A single HTTP request to send as part of a load test.</summary>
public sealed class RequestDefinition
{
    public static readonly string[] SupportedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    /// <summary>Absolute URL. May contain template variables such as <c>{{$guid}}</c> or a parameter list's <c>{{name}}</c>.</summary>
    public string Url { get; set; } = "https://localhost:5001/";

    public List<HeaderEntry> Headers { get; set; } = [];

    /// <summary>Optional request body. May contain template variables.</summary>
    public string? Body { get; set; }

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
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Method} {Url}" : Name;

    /// <param name="parameters">The plan's parameter lists; URLs that use them are checked with their first values.</param>
    /// <param name="variables">The names of the plan's extracted variables, which URLs may use too.</param>
    public IEnumerable<string> Validate(ParameterSet? parameters = null, IReadOnlyCollection<string>? variables = null)
    {
        if (!SupportedMethods.Contains(Method, StringComparer.OrdinalIgnoreCase))
        {
            yield return $"Unsupported HTTP method '{Method}'.";
        }

        // Validate the URL with template variables replaced by harmless placeholders.
        var probe = TemplateRenderer.RenderForValidation(Url, parameters, variables);
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return "URL must be an absolute http:// or https:// address.";
        }

        if (!StatusCodeMatcher.TryParse(ExpectedStatusCodes, out _))
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
}

public sealed class HeaderEntry
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
