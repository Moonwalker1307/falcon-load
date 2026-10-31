namespace FalconLoad.Core.Model;

/// <summary>A single HTTP request to send as part of a load test.</summary>
public sealed class RequestDefinition
{
    public static readonly string[] SupportedMethods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public string Name { get; set; } = string.Empty;

    public string Method { get; set; } = "GET";

    /// <summary>Absolute URL. May contain template variables such as <c>{{$guid}}</c>.</summary>
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

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Method} {Url}" : Name;

    public IEnumerable<string> Validate()
    {
        if (!SupportedMethods.Contains(Method, StringComparer.OrdinalIgnoreCase))
        {
            yield return $"Unsupported HTTP method '{Method}'.";
        }

        // Validate the URL with template variables replaced by harmless placeholders.
        var probe = TemplateRenderer.RenderForValidation(Url);
        if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return "URL must be an absolute http:// or https:// address.";
        }

        if (!StatusCodeMatcher.TryParse(ExpectedStatusCodes, out _))
        {
            yield return "Expected status codes must look like \"200,201\" or \"200-299\".";
        }

        foreach (var header in Headers.Where(h => h.Enabled))
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                yield return "Header names cannot be empty.";
            }
        }
    }
}

public sealed class HeaderEntry
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
