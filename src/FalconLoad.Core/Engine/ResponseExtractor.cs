using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>The outcome of one extraction rule on one response, shown by "Send once".</summary>
/// <param name="Value">The extracted value, or null when the rule found nothing.</param>
/// <param name="Error">Why nothing was found.</param>
public sealed record ExtractedValue(string Variable, string? Value, string? Error);

/// <summary>
/// A request's extraction rules, compiled once, applied to each of its responses to update the virtual user's
/// variables.
/// </summary>
internal sealed class ResponseExtractor
{
    /// <summary>
    /// Only the start of a body is kept for extraction, so a huge response can't make every virtual user hold a copy
    /// of it. The rest is still read and counted.
    /// </summary>
    public const int MaxBodyBytes = 1024 * 1024;

    private readonly Rule[] _rules;
    private readonly bool _hasJson;
    private readonly bool _hasRegex;

    private ResponseExtractor(Rule[] rules)
    {
        _rules = rules;
        _hasJson = rules.Any(r => r.Path is not null);
        _hasRegex = rules.Any(r => r.Regex is not null);
    }

    /// <summary>Whether the rules read the body, so the runner keeps it instead of only counting its bytes.</summary>
    public bool NeedsBody => _hasJson || _hasRegex;

    /// <summary>Returns null when the definition extracts nothing. The rules must be valid.</summary>
    public static ResponseExtractor? From(RequestDefinition definition)
    {
        if (definition.Extractions.Count == 0)
        {
            return null;
        }

        var rules = definition.Extractions.Select(e =>
        {
            var expression = e.Expression.Trim();
            JsonPath? path = null;
            Regex? regex = null;
            if (e.Source == ExtractionSource.JsonPath)
            {
                JsonPath.TryParse(expression, out path, out _);
            }
            else if (e.Source == ExtractionSource.Regex)
            {
                // A regex is kept as written, since leading or trailing spaces can be part of the pattern.
                ExtractionRule.TryCreateRegex(e.Expression, out regex, out _);
            }

            var described = e.Source switch
            {
                ExtractionSource.Header => $"header {expression}",
                ExtractionSource.Regex => "the regular expression",
                _ => $"JSON path {expression}",
            };
            return new Rule(e.Variable.Trim(), e.Source, expression, path, regex, $"Couldn't extract '{e.Variable.Trim()}': {described} found nothing");
        }).ToArray();

        return new ResponseExtractor(rules);
    }

    /// <summary>
    /// Applies every rule, storing what each one finds in <paramref name="variables"/>; a rule that finds nothing
    /// leaves the variable as it was. Returns the first failure, used as the request's error, or null.
    /// </summary>
    /// <param name="results">When given, receives the outcome of every rule, for "Send once".</param>
    public string? Apply(HttpResponseMessage response, ReadOnlySpan<byte> body, IDictionary<string, string> variables, List<ExtractedValue>? results = null)
    {
        string? firstError = null;
        JsonDocument? document = null;
        var jsonParsed = false;
        string? text = null;
        try
        {
            foreach (var rule in _rules)
            {
                string? value = null;
                var error = rule.NotFound;
                switch (rule.Source)
                {
                    case ExtractionSource.Header:
                        if (response.Headers.TryGetValues(rule.Expression, out var values) || response.Content.Headers.TryGetValues(rule.Expression, out values))
                        {
                            value = string.Join(", ", values);
                        }

                        break;

                    case ExtractionSource.JsonPath when rule.Path is not null:
                        if (!jsonParsed)
                        {
                            jsonParsed = true;
                            document = TryParseJson(body);
                        }

                        if (document is null)
                        {
                            error = $"Couldn't extract '{rule.Variable}': the response isn't JSON";
                        }
                        else if (rule.Path.TryEvaluate(document.RootElement, out var found))
                        {
                            value = found;
                        }

                        break;

                    case ExtractionSource.Regex when rule.Regex is not null:
                        text ??= Encoding.UTF8.GetString(body);
                        try
                        {
                            var match = rule.Regex.Match(text);
                            if (match.Success)
                            {
                                value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                            }
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            error = $"Couldn't extract '{rule.Variable}': the regular expression took too long";
                        }

                        break;
                }

                if (value is not null)
                {
                    variables[rule.Variable] = value;
                    error = null;
                }
                else
                {
                    firstError ??= error;
                }

                results?.Add(new ExtractedValue(rule.Variable, value, value is null ? error : null));
            }
        }
        finally
        {
            document?.Dispose();
        }

        return firstError;
    }

    private static JsonDocument? TryParseJson(ReadOnlySpan<byte> body)
    {
        if (body.StartsWith("\uFEFF"u8))
        {
            body = body[3..];
        }

        try
        {
            var reader = new Utf8JsonReader(body);
            return JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Rule(string Variable, ExtractionSource Source, string Expression, JsonPath? Path, Regex? Regex, string NotFound);
}
