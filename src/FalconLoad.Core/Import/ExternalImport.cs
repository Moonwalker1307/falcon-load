using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FalconLoad.Core.Import;

public enum ImportKind
{
    Curl,
    Postman,
    OpenApi,
}

/// <summary>
/// Text that came from another tool: a curl command, a Postman collection or an OpenAPI 3 spec. <see cref="Parse"/>
/// works out which one it is; <see cref="Build"/> turns it into requests. Nothing here touches the network or disk.
/// </summary>
public sealed partial class ImportSource
{
    [GeneratedRegex(@"^\s*(\$\s+)?(sudo\s+)?(\S*/)?curl(\s|$)")]
    private static partial Regex CurlStart();

    private readonly string _text;
    private readonly JsonNode? _document;

    private ImportSource(ImportKind kind, string text, JsonNode? document, OpenApiSpec? spec)
    {
        Kind = kind;
        _text = text;
        _document = document;
        OpenApi = spec;
    }

    public ImportKind Kind { get; }

    /// <summary>The parsed spec, so a caller can list <see cref="OpenApiSpec.Operations"/> before choosing; null for other kinds.</summary>
    public OpenApiSpec? OpenApi { get; }

    public static ImportSource Parse(string text, ImportKind? kind = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ImportException("Paste a curl command, a Postman collection or an OpenAPI spec.");
        }

        if (kind == ImportKind.Curl || kind is null && CurlStart().IsMatch(text))
        {
            CurlImporter.Read(text);
            return new ImportSource(ImportKind.Curl, text, null, null);
        }

        var document = ParseDocument(text);
        var detected = kind ?? (PostmanImporter.Looks(document) ? ImportKind.Postman : OpenApiSpec.Looks(document) ? ImportKind.OpenApi : null);
        switch (detected)
        {
            case ImportKind.Postman:
                PostmanImporter.Read(document!);
                return new ImportSource(ImportKind.Postman, text, document, null);
            case ImportKind.OpenApi:
                return new ImportSource(ImportKind.OpenApi, text, document, OpenApiSpec.Read(document!));
            default:
                throw new ImportException("This isn't a curl command, a Postman collection (v2.0 or v2.1) or an OpenAPI 3 spec.");
        }
    }

    /// <param name="operations">OpenAPI only: the keys of the operations to import; null imports all of them.</param>
    /// <param name="baseUrl">OpenAPI only: the address to send requests to, instead of the spec's server.</param>
    public ImportResult Build(IEnumerable<string>? operations = null, string? baseUrl = null) => Kind switch
    {
        ImportKind.Curl => CurlImporter.Read(_text),
        ImportKind.Postman => PostmanImporter.Read(_document!),
        _ => OpenApi!.Build(operations, baseUrl),
    };

    private static JsonNode? ParseDocument(string text)
    {
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        try
        {
            return trimmed.StartsWith('{') || trimmed.StartsWith('[')
                ? JsonNode.Parse(trimmed, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                : SimpleYaml.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ImportException($"The text isn't valid JSON: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new ImportException($"The text isn't YAML falcon-load can read. {ex.Message}");
        }
    }
}
