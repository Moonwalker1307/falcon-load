using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Import;

/// <summary>
/// Reads a Postman collection (v2.0 or v2.1) into requests. Folders become name prefixes, collection variables
/// become parameter lists, and bearer, basic and API key authorization becomes a header.
/// </summary>
public static partial class PostmanImporter
{
    [GeneratedRegex(@"\{\{\s*(?<name>[^{}]*?)\s*\}\}")]
    private static partial Regex VariablePattern();

    public static bool Looks(JsonNode? root) =>
        root is JsonObject obj && obj["item"] is JsonArray && obj["info"] is JsonObject;

    public static ImportResult Read(JsonNode root)
    {
        if (root is not JsonObject collection || collection["item"] is not JsonArray items)
        {
            throw new ImportException("This doesn't look like a Postman collection (v2.0 or v2.1).");
        }

        var info = collection["info"] as JsonObject;
        var result = new ImportResult { Name = Text(info?["name"]) is { Length: > 0 } name ? name : "Postman collection" };
        var context = new Context(result);

        foreach (var variable in (collection["variable"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Text(variable["key"]) is { Length: > 0 } key && !ImportHelpers.IsTrue(variable["disabled"]))
            {
                var parameter = ImportHelpers.ToParameterName(key);
                context.Defined[key.Trim()] = parameter;
                result.AddParameter(parameter, Text(variable["value"]));
            }
        }

        Walk(items, [], collection["auth"], context);

        if (result.Requests.Count == 0)
        {
            throw new ImportException("The collection doesn't contain any requests.");
        }

        if (context.SawScripts)
        {
            result.Warnings.Add("Pre-request and test scripts were ignored. Values they set are not available.");
        }

        foreach (var undefined in context.Undefined.Order())
        {
            result.Warnings.Add($"The variable '{undefined}' isn't defined in the collection (it may come from a Postman environment). Set its value in the parameter list of the same name.");
            result.AddParameter(ImportHelpers.ToParameterName(undefined), string.Empty);
        }

        foreach (var unsupported in context.UnsupportedDynamic.Order())
        {
            result.Warnings.Add($"The dynamic value {{{{{unsupported}}}}} isn't supported by falcon-load and is sent as written.");
        }

        return result;
    }

    private sealed class Context(ImportResult result)
    {
        public ImportResult Result { get; } = result;

        /// <summary>Postman variable name to the parameter list name it became.</summary>
        public Dictionary<string, string> Defined { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> Undefined { get; } = new(StringComparer.Ordinal);

        public SortedSet<string> UnsupportedDynamic { get; } = new(StringComparer.Ordinal);

        public bool SawScripts { get; set; }
    }

    private static void Walk(JsonArray items, List<string> path, JsonNode? inheritedAuth, Context context)
    {
        foreach (var item in items.OfType<JsonObject>())
        {
            var name = Text(item["name"]);
            if (item["event"] is JsonArray { Count: > 0 })
            {
                context.SawScripts = true;
            }

            var auth = item["auth"] ?? inheritedAuth;
            if (item["item"] is JsonArray children)
            {
                Walk(children, name.Length > 0 ? [.. path, name] : path, auth, context);
            }
            else if (item["request"] is { } request)
            {
                var definition = ReadRequest(request, auth, context);
                var label = name.Length > 0 ? name : definition.Name;
                definition.Name = string.Join(" / ", [.. path, label]);
                context.Result.Requests.Add(definition);
            }
        }
    }

    private static RequestDefinition ReadRequest(JsonNode node, JsonNode? auth, Context context)
    {
        var request = new RequestDefinition { Method = "GET" };
        if (node is JsonValue)
        {
            request.Url = Rewrite(Text(node), context);
            ApplyAuth(auth, request, context);
            request.Name = ImportHelpers.NameFor("GET", request.Url);
            return request;
        }

        var source = node as JsonObject ?? [];
        request.Method = Text(source["method"]) is { Length: > 0 } method ? method.ToUpperInvariant() : "GET";
        request.Url = Rewrite(ReadUrl(source["url"]), context);

        var hasContentType = false;
        foreach (var header in (source["header"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var enabled = !ImportHelpers.IsTrue(header["disabled"]);
            hasContentType |= enabled && Text(header["key"]).Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase);
            ImportHelpers.AddHeader(request, Rewrite(Text(header["key"]), context), Rewrite(Text(header["value"]), context), enabled);
        }

        ReadBody(source["body"] as JsonObject, request, hasContentType, context);
        ApplyAuth(source["auth"] ?? auth, request, context);
        request.Name = ImportHelpers.NameFor(request.Method, request.Url);
        return request;
    }

    // The raw URL is what the user typed. Query parameters switched off in Postman are still part of it, so when
    // the structured form is there, rebuild the query from the enabled ones.
    private static string ReadUrl(JsonNode? url)
    {
        if (url is JsonValue)
        {
            return Text(url);
        }

        if (url is not JsonObject obj)
        {
            return string.Empty;
        }

        var raw = Text(obj["raw"]);
        if (raw.Length == 0)
        {
            var host = obj["host"] is JsonArray hosts ? string.Join('.', hosts.Select(Text)) : Text(obj["host"]);
            var path = obj["path"] is JsonArray segments ? string.Join('/', segments.Select(s => s is JsonObject o ? Text(o["value"]) : Text(s))) : Text(obj["path"]);
            var protocol = Text(obj["protocol"]);
            var port = Text(obj["port"]);
            raw = (protocol.Length > 0 ? protocol + "://" : string.Empty) + host + (port.Length > 0 ? ":" + port : string.Empty) + (path.Length > 0 ? "/" + path : string.Empty);
        }

        if (obj["query"] is JsonArray query && query.OfType<JsonObject>().Any(q => ImportHelpers.IsTrue(q["disabled"])))
        {
            var cut = raw.IndexOf('?');
            var fragment = raw.IndexOf('#');
            raw = cut >= 0 ? raw[..cut] : fragment >= 0 ? raw[..fragment] : raw;
            var pairs = query.OfType<JsonObject>().Where(q => !ImportHelpers.IsTrue(q["disabled"]))
                .Select(q => Text(q["value"]) is { Length: > 0 } value ? $"{Text(q["key"])}={value}" : Text(q["key"]));
            raw = ImportHelpers.AppendQuery(raw, string.Join('&', pairs));
        }

        // Path variables are written as /:id and filled from the "variable" list.
        foreach (var variable in (obj["variable"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var key = Text(variable["key"]);
            if (key.Length > 0 && Text(variable["value"]) is { Length: > 0 } value)
            {
                raw = Regex.Replace(raw, $@"(?<=/):{Regex.Escape(key)}(?=[/?#]|$)", value.Replace("$", "$$"));
            }
        }

        return raw;
    }

    private static void ReadBody(JsonObject? body, RequestDefinition request, bool hasContentType, Context context)
    {
        if (body is null || ImportHelpers.IsTrue(body["disabled"]))
        {
            return;
        }

        switch (Text(body["mode"]))
        {
            case "raw":
                request.Body = Rewrite(Text(body["raw"]), context);
                if (!hasContentType)
                {
                    request.ContentType = Text(body["options"]?["raw"]?["language"]) switch
                    {
                        "json" => "application/json",
                        "xml" => "application/xml",
                        "html" => "text/html",
                        "javascript" => "application/javascript",
                        _ => "text/plain",
                    };
                }

                break;
            case "urlencoded":
                request.Body = ImportHelpers.FormBody(EnabledFields(body["urlencoded"]).Select(f => (Rewrite(f.Name, context), Rewrite(f.Value, context))));
                request.ContentType = ImportHelpers.FormContentType;
                break;
            case "formdata":
            {
                var fields = (body["formdata"] as JsonArray ?? []).OfType<JsonObject>().Where(f => !ImportHelpers.IsTrue(f["disabled"])).ToList();
                foreach (var file in fields.Where(f => Text(f["type"]) == "file"))
                {
                    context.Result.Warnings.Add($"The form field '{Text(file["key"])}' uploads a file, which falcon-load can't do, so it was left out.");
                }

                var text = fields.Where(f => Text(f["type"]) != "file").Select(f => (Rewrite(Text(f["key"]), context), Rewrite(Text(f["value"]), context))).ToList();
                if (text.Count > 0)
                {
                    (request.Body, request.ContentType) = ImportHelpers.MultipartBody(text);
                }

                break;
            }

            case "graphql":
            {
                // A GraphQL body becomes a GraphQL request, so the query and variables are edited as such.
                var graphql = body["graphql"] as JsonObject;
                var variables = Rewrite(Text(graphql?["variables"]), context);
                request.Protocol = RequestProtocol.GraphQL;
                request.Body = Rewrite(Text(graphql?["query"]), context);
                request.GraphQL = new GraphQLOptions { Variables = string.IsNullOrWhiteSpace(variables) ? null : variables };
                request.ContentType = "application/json";
                break;
            }

            case "file" or "binary":
                context.Result.Warnings.Add($"{request.Method} {request.Url}: the body is a file, which falcon-load can't send, so it was left out.");
                break;
        }

    }

    private static IEnumerable<(string Name, string Value)> EnabledFields(JsonNode? array) =>
        (array as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => !ImportHelpers.IsTrue(f["disabled"]))
            .Select(f => (Text(f["key"]), Text(f["value"])));

    private static void ApplyAuth(JsonNode? auth, RequestDefinition request, Context context)
    {
        if (auth is not JsonObject obj || Text(obj["type"]) is not { Length: > 0 } type || type == "noauth")
        {
            return;
        }

        string Field(string key)
        {
            // v2.1 lists {key, value}; v2.0 uses a plain object.
            return obj[type] switch
            {
                JsonArray list => Text(list.OfType<JsonObject>().FirstOrDefault(e => Text(e["key"]) == key)?["value"]),
                JsonObject map => Text(map[key]),
                _ => string.Empty,
            };
        }

        void Add(string name, string value)
        {
            if (!ImportHelpers.HasHeader(request, name))
            {
                request.Headers.Add(new HeaderEntry { Name = name, Value = Rewrite(value, context) });
            }
        }

        switch (type)
        {
            case "bearer":
                Add("Authorization", "Bearer " + Field("token"));
                break;
            case "basic":
            {
                var credentials = Field("username") + ":" + Field("password");
                if (credentials.Contains("{{", StringComparison.Ordinal))
                {
                    context.Result.Warnings.Add($"{request.Method} {request.Url}: basic authorization uses variables, which can't be encoded for you. Add an Authorization header with the encoded value.");
                }
                else
                {
                    Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
                }

                break;
            }

            case "apikey":
                if (Field("in") == "query")
                {
                    request.Url = ImportHelpers.AppendQuery(request.Url, $"{ImportHelpers.Encode(Field("key"))}={ImportHelpers.Encode(Rewrite(Field("value"), context))}");
                }
                else if (Field("key").Length > 0)
                {
                    Add(Field("key"), Field("value"));
                }

                break;
            default:
                context.Result.Warnings.Add($"{request.Method} {request.Url}: '{type}' authorization isn't supported, so no Authorization header was added.");
                break;
        }
    }

    /// <summary>
    /// Renames variables whose names a parameter list can't have, maps Postman's dynamic values to falcon-load's, and
    /// notes variables that were never defined.
    /// </summary>
    private static string Rewrite(string text, Context context)
    {
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        return VariablePattern().Replace(text, match =>
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith('$'))
            {
                switch (name)
                {
                    case "$guid" or "$randomUUID":
                        return "{{$guid}}";
                    case "$timestamp" or "$isoTimestamp" or "$randomInt":
                        return $"{{{{{name}}}}}";
                    default:
                        context.UnsupportedDynamic.Add(name);
                        return match.Value;
                }
            }

            if (context.Defined.TryGetValue(name, out var parameter))
            {
                return $"{{{{{parameter}}}}}";
            }

            context.Undefined.Add(name);
            return $"{{{{{ImportHelpers.ToParameterName(name)}}}}}";
        });
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value ? value.ToString() : string.Empty;
}
