using System.Text;
using System.Text.RegularExpressions;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Import;

internal static partial class ImportHelpers
{
    public const string FormContentType = "application/x-www-form-urlencoded";

    [GeneratedRegex(@"\{\{.*?\}\}")]
    private static partial Regex Placeholder();

    /// <summary>Percent-encodes text for a URL or form, leaving <c>{{...}}</c> placeholders alone so they still work.</summary>
    public static string Encode(string text)
    {
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return Uri.EscapeDataString(text);
        }

        var result = new StringBuilder();
        var last = 0;
        foreach (Match match in Placeholder().Matches(text))
        {
            result.Append(Uri.EscapeDataString(text[last..match.Index])).Append(match.Value);
            last = match.Index + match.Length;
        }

        return result.Append(Uri.EscapeDataString(text[last..])).ToString();
    }

    /// <summary>
    /// Whether a flag such as Postman's <c>disabled</c> or OpenAPI's <c>required</c> is set. Hand-written files also
    /// use <c>"true"</c>, and anything that isn't a boolean or that text counts as not set.
    /// </summary>
    public static bool IsTrue(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value
        && (value.TryGetValue<bool>(out var flag) ? flag : value.TryGetValue<string>(out var text) && bool.TryParse(text, out flag) && flag);

    public static string FormBody(IEnumerable<(string Name, string Value)> fields) =>
        string.Join('&', fields.Select(f => $"{Encode(f.Name)}={Encode(f.Value)}"));

    /// <summary>Builds a multipart/form-data body with text fields; the boundary goes into the returned content type.</summary>
    public static (string Body, string ContentType) MultipartBody(IEnumerable<(string Name, string Value)> fields)
    {
        const string boundary = "----FalconLoadBoundary";
        var body = new StringBuilder();
        foreach (var (name, value) in fields)
        {
            body.Append("--").Append(boundary).Append("\r\n")
                .Append("Content-Disposition: form-data; name=\"").Append(name.Replace("\"", "%22")).Append("\"\r\n\r\n")
                .Append(value).Append("\r\n");
        }

        body.Append("--").Append(boundary).Append("--\r\n");
        return (body.ToString(), $"multipart/form-data; boundary={boundary}");
    }

    /// <summary>Adds a header, moving Content-Type into the request's own field and dropping Content-Length, which is always recomputed.</summary>
    public static void AddHeader(RequestDefinition request, string name, string value, bool enabled = true)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && enabled)
        {
            request.ContentType = value.Trim();
            return;
        }

        request.Headers.Add(new HeaderEntry { Name = name, Value = value.Trim(), Enabled = enabled });
    }

    public static bool HasHeader(RequestDefinition request, string name) =>
        request.Headers.Any(h => h.Enabled && h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A short request name such as "GET /orders/17", from a URL that may contain placeholders.</summary>
    public static string NameFor(string method, string url)
    {
        var rest = url;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            rest = rest[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            rest = slash < 0 ? "/" : rest[slash..];
        }
        else if (!rest.StartsWith('/') && !rest.StartsWith("{{", StringComparison.Ordinal))
        {
            rest = "/" + rest;
        }

        var cut = rest.IndexOfAny(['?', '#']);
        return $"{method} {(cut < 0 ? rest : rest[..cut])}";
    }

    /// <summary>Turns any text into a name a parameter list can have (letters, digits, underscores).</summary>
    public static string ToParameterName(string text)
    {
        var chars = text.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var name = new string(chars);
        if (name.Length == 0)
        {
            return "value";
        }

        return char.IsAsciiDigit(name[0]) ? "_" + name : name;
    }

    public static string AppendQuery(string url, string query)
    {
        if (query.Length == 0)
        {
            return url;
        }

        var hash = url.IndexOf('#');
        var fragment = hash < 0 ? string.Empty : url[hash..];
        var main = hash < 0 ? url : url[..hash];
        return main + (main.Contains('?') ? (main.EndsWith('?') || main.EndsWith('&') ? string.Empty : "&") : "?") + query + fragment;
    }
}
