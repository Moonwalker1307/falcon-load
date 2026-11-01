using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Import;

/// <summary>One endpoint of an OpenAPI spec that can be imported as a request.</summary>
/// <param name="Key">Identifies the operation, e.g. <c>GET /pets/{id}</c>.</param>
public sealed record OpenApiOperation(string Key, string Method, string Path, string Summary, IReadOnlyList<string> Tags, bool Deprecated);

/// <summary>
/// An OpenAPI 3 document (JSON or YAML). Lists its operations so the user can pick some, then turns the chosen ones
/// into requests with sample values for parameters and bodies taken from the spec's examples or schemas.
/// </summary>
public sealed partial class OpenApiSpec
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch"];

    private readonly JsonObject _root;
    private readonly List<(OpenApiOperation Info, JsonObject Operation, JsonObject PathItem)> _operations = [];

    [GeneratedRegex(@"\{(?<name>[^{}/]+)\}")]
    private static partial Regex PathParameterPattern();

    private OpenApiSpec(JsonObject root)
    {
        _root = root;
        Title = Text(root["info"]?["title"]);
        ServerUrl = ReadServer();
        foreach (var (path, item) in root["paths"] as JsonObject ?? [])
        {
            if (item is not JsonObject pathItem)
            {
                continue;
            }

            foreach (var method in Methods)
            {
                if (pathItem[method] is JsonObject operation)
                {
                    var key = $"{method.ToUpperInvariant()} {path}";
                    var tags = (operation["tags"] as JsonArray ?? []).Select(Text).Where(t => t.Length > 0).ToList();
                    var summary = Text(operation["summary"]) is { Length: > 0 } s ? s : Text(operation["operationId"]);
                    _operations.Add((new OpenApiOperation(key, method.ToUpperInvariant(), path, summary, tags, ImportHelpers.IsTrue(operation["deprecated"])), operation, pathItem));
                }
            }
        }
    }

    public string Title { get; }

    /// <summary>The first server's address with its variables filled in, or empty when the spec has none (or only a relative one).</summary>
    public string ServerUrl { get; }

    public IReadOnlyList<OpenApiOperation> Operations => _operations.Select(o => o.Info).ToList();

    public static bool Looks(JsonNode? root) => root is JsonObject obj && (obj["openapi"] is not null || obj["swagger"] is not null) && obj["paths"] is JsonObject;

    public static OpenApiSpec Read(JsonNode root)
    {
        if (root is not JsonObject obj || obj["paths"] is not JsonObject)
        {
            throw new ImportException("This doesn't look like an OpenAPI spec: there is no \"paths\" section.");
        }

        if (obj["swagger"] is not null)
        {
            throw new ImportException("This is a Swagger 2.0 file. falcon-load reads OpenAPI 3; convert it first (for example with the Swagger editor's \"Convert to OpenAPI 3\").");
        }

        var spec = new OpenApiSpec(obj);
        if (spec._operations.Count == 0)
        {
            throw new ImportException("The spec doesn't describe any operations.");
        }

        return spec;
    }

    /// <summary>Builds requests for the chosen operations (all of them when <paramref name="keys"/> is null).</summary>
    /// <param name="baseUrl">Replaces the spec's server; needed when the spec has none or only a relative one.</param>
    public ImportResult Build(IEnumerable<string>? keys = null, string? baseUrl = null)
    {
        var chosen = keys is null ? null : new HashSet<string>(keys, StringComparer.Ordinal);
        var result = new ImportResult { Name = Title.Length > 0 ? Title : "OpenAPI spec" };

        var server = (string.IsNullOrWhiteSpace(baseUrl) ? ServerUrl : baseUrl.Trim()).TrimEnd('/');
        if (server.Length == 0)
        {
            server = "{{baseUrl}}";
            result.AddParameter("baseUrl", string.Empty);
            result.Warnings.Add("The spec doesn't name an absolute server address. Set the parameter list 'baseUrl' to the address to test, for example https://localhost:5001.");
        }

        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (info, operation, pathItem) in _operations)
        {
            if (chosen is not null && !chosen.Contains(info.Key))
            {
                continue;
            }

            result.Requests.Add(BuildRequest(info, operation, pathItem, server, result, secrets));
        }

        foreach (var secret in secrets.Order())
        {
            result.AddParameter(secret, string.Empty);
            result.Warnings.Add($"The requests need a credential. Fill in the parameter list '{secret}' before running.");
        }

        return result;
    }

    private RequestDefinition BuildRequest(OpenApiOperation info, JsonObject operation, JsonObject pathItem, string server, ImportResult result, HashSet<string> secrets)
    {
        var request = new RequestDefinition { Method = info.Method, Name = info.Key };
        var path = info.Path;
        var query = new List<(string Name, string Value)>();

        foreach (var parameter in Parameters(pathItem, operation))
        {
            var location = Text(parameter["in"]);
            var name = Text(parameter["name"]);
            var required = ImportHelpers.IsTrue(parameter["required"]);
            var value = Scalar(ParameterValue(parameter));
            switch (location)
            {
                case "path":
                    path = path.Replace("{" + name + "}", ImportHelpers.Encode(value));
                    break;
                case "query" when required:
                    query.Add((name, value));
                    break;
                case "header" when required:
                    request.Headers.Add(new HeaderEntry { Name = name, Value = value });
                    break;
                case "cookie" when required:
                    request.Headers.Add(new HeaderEntry { Name = "Cookie", Value = $"{name}={value}" });
                    break;
            }
        }

        // A path parameter the spec forgot to declare would stay as {name}; flag it rather than send it.
        foreach (Match left in PathParameterPattern().Matches(path))
        {
            result.Warnings.Add($"{info.Key}: the path parameter '{left.Groups["name"].Value}' isn't described in the spec, so it was left as written.");
        }

        ApplySecurity(operation, request, query, secrets, result, info);
        ApplyBody(operation, request, result, info);

        request.Url = ImportHelpers.AppendQuery(server + path, string.Join('&', query.Select(q => $"{ImportHelpers.Encode(q.Name)}={ImportHelpers.Encode(q.Value)}")));
        request.ExpectedStatusCodes = ExpectedStatuses(operation);
        return request;
    }

    // Path-level parameters apply to every operation; an operation can override one with the same name and location.
    private IEnumerable<JsonObject> Parameters(JsonObject pathItem, JsonObject operation)
    {
        var merged = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var source in new[] { pathItem["parameters"], operation["parameters"] })
        {
            foreach (var node in source as JsonArray ?? [])
            {
                if (Resolve(node) is JsonObject parameter)
                {
                    merged[Text(parameter["in"]) + ":" + Text(parameter["name"])] = parameter;
                }
            }
        }

        return merged.Values;
    }

    private JsonNode? ParameterValue(JsonObject parameter)
    {
        if (parameter["example"] is { } example)
        {
            return example;
        }

        if (parameter["examples"] is JsonObject examples && examples.FirstOrDefault().Value is JsonObject first && Resolve(first) is JsonObject resolved && resolved["value"] is { } value)
        {
            return value;
        }

        return Sample(parameter["schema"], 0);
    }

    private void ApplySecurity(JsonObject operation, RequestDefinition request, List<(string Name, string Value)> query, HashSet<string> secrets, ImportResult result, OpenApiOperation info)
    {
        // An operation's own "security" replaces the spec-wide one, and an empty list means none is needed.
        var requirements = (operation["security"] ?? _root["security"]) as JsonArray;
        if (requirements is null || requirements.FirstOrDefault() is not JsonObject alternative)
        {
            return;
        }

        var schemes = _root["components"]?["securitySchemes"] as JsonObject;
        foreach (var (name, _) in alternative)
        {
            if (Resolve(schemes?[name]) is not JsonObject scheme)
            {
                continue;
            }

            var parameter = ImportHelpers.ToParameterName(name);
            switch (Text(scheme["type"]))
            {
                case "http" when Text(scheme["scheme"]).Equals("bearer", StringComparison.OrdinalIgnoreCase):
                case "oauth2":
                case "openIdConnect":
                    request.Headers.Add(new HeaderEntry { Name = "Authorization", Value = $"Bearer {{{{{parameter}}}}}" });
                    secrets.Add(parameter);
                    break;
                case "http" when Text(scheme["scheme"]).Equals("basic", StringComparison.OrdinalIgnoreCase):
                    request.Headers.Add(new HeaderEntry { Name = "Authorization", Value = $"Basic {{{{{parameter}}}}}" });
                    secrets.Add(parameter);
                    result.Warnings.Add($"{info.Key}: basic authorization needs the base64 form of user:password in the parameter list '{parameter}'.");
                    break;
                case "apiKey" when Text(scheme["in"]) == "header":
                    request.Headers.Add(new HeaderEntry { Name = Text(scheme["name"]), Value = $"{{{{{parameter}}}}}" });
                    secrets.Add(parameter);
                    break;
                case "apiKey" when Text(scheme["in"]) == "query":
                    query.Add((Text(scheme["name"]), $"{{{{{parameter}}}}}"));
                    secrets.Add(parameter);
                    break;
                default:
                    result.Warnings.Add($"{info.Key}: the security scheme '{name}' isn't supported, so no credential was added.");
                    break;
            }
        }
    }

    private void ApplyBody(JsonObject operation, RequestDefinition request, ImportResult result, OpenApiOperation info)
    {
        if (Resolve(operation["requestBody"]) is not JsonObject body || body["content"] is not JsonObject content || content.Count == 0)
        {
            return;
        }

        var pick = content.FirstOrDefault(c => c.Key.Equals("application/json", StringComparison.OrdinalIgnoreCase));
        if (pick.Key is null)
        {
            pick = content.FirstOrDefault(c => c.Key.Contains("json", StringComparison.OrdinalIgnoreCase));
        }

        if (pick.Key is null)
        {
            pick = content.FirstOrDefault(c => c.Key is "application/x-www-form-urlencoded" or "multipart/form-data" or "text/plain" or "application/xml" or "text/xml");
        }

        if (pick.Key is null || pick.Value is not JsonObject media)
        {
            result.Warnings.Add($"{info.Key}: the request body type '{content.First().Key}' isn't supported, so no body was added.");
            return;
        }

        var contentType = pick.Key;
        var example = Example(media);
        var schema = media["schema"];

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            request.Body = (example ?? Sample(schema, 0))?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        else if (contentType is "application/x-www-form-urlencoded" or "multipart/form-data")
        {
            var properties = (example ?? Sample(schema, 0)) as JsonObject ?? [];
            var fields = properties.Select(p => (p.Key, Scalar(p.Value))).ToList();
            if (contentType == "multipart/form-data")
            {
                (request.Body, contentType) = ImportHelpers.MultipartBody(fields);
            }
            else
            {
                request.Body = ImportHelpers.FormBody(fields);
            }
        }
        else if (example is not null)
        {
            request.Body = Scalar(example);
        }
        else
        {
            result.Warnings.Add($"{info.Key}: the spec has no example for its {contentType} body, so none was added.");
            return;
        }

        request.ContentType = contentType;
    }

    private JsonNode? Example(JsonObject media)
    {
        if (media["example"] is { } example)
        {
            return example.DeepClone();
        }

        if (media["examples"] is JsonObject examples && examples.FirstOrDefault().Value is JsonObject first && Resolve(first) is JsonObject resolved)
        {
            return resolved["value"]?.DeepClone();
        }

        return null;
    }

    // Only 2xx responses count as success, so a documented 404 shows up as an error in the results.
    private static string? ExpectedStatuses(JsonObject operation)
    {
        var codes = new List<string>();
        foreach (var (code, _) in operation["responses"] as JsonObject ?? [])
        {
            if (code.Length == 3 && code[0] == '2' && code.Skip(1).All(char.IsAsciiDigit))
            {
                codes.Add(code);
            }
            else if (code.Equals("2XX", StringComparison.OrdinalIgnoreCase))
            {
                return "200-299";
            }
        }

        return codes.Count == 0 ? null : string.Join(',', codes);
    }

    // --- schemas ---

    /// <summary>A sample value for a schema: its example or default, the first enum value, or a value of the right type.</summary>
    private JsonNode? Sample(JsonNode? node, int depth)
    {
        if (Resolve(node) is not JsonObject schema || depth > 5)
        {
            return null;
        }

        if (schema["example"] is { } example)
        {
            return example.DeepClone();
        }

        if (schema["default"] is { } fallback)
        {
            return fallback.DeepClone();
        }

        if (schema["enum"] is JsonArray { Count: > 0 } choices)
        {
            return choices[0]?.DeepClone();
        }

        foreach (var combined in new[] { "oneOf", "anyOf" })
        {
            if (schema[combined] is JsonArray { Count: > 0 } options)
            {
                return Sample(options[0], depth + 1);
            }
        }

        if (schema["allOf"] is JsonArray parts)
        {
            var merged = new JsonObject();
            foreach (var part in parts)
            {
                if (Sample(part, depth + 1) is JsonObject piece)
                {
                    foreach (var (name, value) in piece)
                    {
                        merged[name] = value?.DeepClone();
                    }
                }
            }

            return merged;
        }

        var type = Text(schema["type"]);
        if (type.Length == 0)
        {
            type = schema["properties"] is not null ? "object" : schema["items"] is not null ? "array" : "string";
        }

        switch (type)
        {
            case "object":
            {
                var obj = new JsonObject();
                foreach (var (name, property) in schema["properties"] as JsonObject ?? [])
                {
                    if (Resolve(property) is JsonObject p && ImportHelpers.IsTrue(p["readOnly"]))
                    {
                        continue;
                    }

                    obj[name] = Sample(property, depth + 1);
                }

                return obj;
            }

            case "array":
                return new JsonArray(Sample(schema["items"], depth + 1));
            case "integer":
                return JsonValue.Create(Number(schema["minimum"]) is { } min && min > 1 ? (long)min : 1L);
            case "number":
                return JsonValue.Create(Number(schema["minimum"]) is { } low && low > 1 ? low : 1.5);
            case "boolean":
                return JsonValue.Create(true);
            default:
                return JsonValue.Create(Text(schema["format"]) switch
                {
                    "uuid" => "{{$guid}}",
                    "date-time" => "{{$isoTimestamp}}",
                    "date" => "2024-01-01",
                    "email" => "user@example.com",
                    "uri" or "url" => "https://example.com",
                    _ => "string",
                });
        }
    }

    /// <summary>Follows a local <c>$ref</c> such as <c>#/components/schemas/Pet</c>.</summary>
    private JsonNode? Resolve(JsonNode? node)
    {
        for (var hops = 0; hops < 10 && node is JsonObject obj && obj["$ref"] is JsonNode reference; hops++)
        {
            var target = Text(reference);
            if (!target.StartsWith("#/", StringComparison.Ordinal))
            {
                return null;
            }

            JsonNode? current = _root;
            foreach (var part in target[2..].Split('/'))
            {
                current = (current as JsonObject)?[part.Replace("~1", "/").Replace("~0", "~")];
            }

            node = current;
        }

        return node;
    }

    private string ReadServer()
    {
        if (_root["servers"] is not JsonArray { Count: > 0 } servers || servers[0] is not JsonObject server)
        {
            return string.Empty;
        }

        var url = Text(server["url"]);
        foreach (var (name, variable) in server["variables"] as JsonObject ?? [])
        {
            url = url.Replace("{" + name + "}", Text(variable?["default"]));
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? url : string.Empty;
    }

    private static string Scalar(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value => value.ToString(),
        JsonArray { Count: > 0 } array => Scalar(array[0]),
        _ => node.ToJsonString(),
    };

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string Text(JsonNode? node) => node is JsonValue value ? value.ToString() : string.Empty;
}
