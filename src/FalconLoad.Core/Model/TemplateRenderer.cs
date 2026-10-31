using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>
/// Expands dynamic variables in URLs, headers and bodies so every request can be unique:
/// <list type="bullet">
/// <item><c>{{$guid}}</c> a new GUID</item>
/// <item><c>{{$randomInt}}</c> a random integer 0..999999, or <c>{{$randomInt:min:max}}</c></item>
/// <item><c>{{$timestamp}}</c> Unix time in seconds; <c>{{$isoTimestamp}}</c> ISO 8601 UTC</item>
/// <item><c>{{$iteration}}</c> the virtual user's iteration number; <c>{{$vu}}</c> the virtual user number</item>
/// <item><c>{{name}}</c> the current value of the plan's <see cref="ParameterList"/> with that name, or else the
/// virtual user's variable of that name, taken from an earlier response by an <see cref="ExtractionRule"/></item>
/// </list>
/// Placeholders that match nothing, including a variable no response has supplied yet, are sent as written.
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*(?<builtin>\$)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<a>-?\d+):(?<b>-?\d+))?\s*\}\}")]
    private static partial Regex VariablePattern();

    public static bool HasVariables(string? text) => text is not null && text.Contains("{{", StringComparison.Ordinal) && VariablePattern().IsMatch(text);

    public static string Render(string text, TemplateContext context)
    {
        if (!HasVariables(text))
        {
            return text;
        }

        return VariablePattern().Replace(text, match => Evaluate(match, context) ?? match.Value);
    }

    /// <summary>
    /// Renders with fixed placeholder values and the first value of each list, used for validating URLs before a run.
    /// Extracted variables have no value until a response supplies one, so they stand in as <c>1</c>, or as a URL when
    /// the text starts with one, such as a URL taken from a <c>Location</c> header.
    /// </summary>
    public static string RenderForValidation(string text, ParameterSet? parameters = null, IReadOnlyCollection<string>? variables = null)
    {
        Dictionary<string, string>? stand = null;
        if (variables is { Count: > 0 })
        {
            stand = variables.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(v => v, _ => "1", StringComparer.OrdinalIgnoreCase);
            var first = VariablePattern().Match(text.TrimStart());
            if (first is { Success: true, Index: 0 } && !first.Groups["builtin"].Success && stand.ContainsKey(first.Groups["name"].Value))
            {
                text = "http://localhost" + text.TrimStart()[first.Length..];
            }
        }

        return Render(text, new TemplateContext(1, 1, parameters?.First() ?? default, stand));
    }

    private static string? Evaluate(Match match, TemplateContext context)
    {
        var name = match.Groups["name"].Value;
        if (!match.Groups["builtin"].Success)
        {
            if (match.Groups["a"].Success)
            {
                return null;
            }

            if (context.Parameters.TryGet(name, out var value))
            {
                return value;
            }

            return context.Variables is not null && context.Variables.TryGetValue(name, out var extracted) ? extracted : null;
        }

        return name.ToLowerInvariant() switch
        {
            "guid" => Guid.NewGuid().ToString(),
            "randomint" when match.Groups["a"].Success => RandomBetween(match.Groups["a"].Value, match.Groups["b"].Value),
            "randomint" => Random.Shared.Next(0, 1_000_000).ToString(),
            "timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "isotimestamp" => DateTimeOffset.UtcNow.ToString("O"),
            "iteration" => context.Iteration.ToString(),
            "vu" => context.VirtualUser.ToString(),
            _ => null,
        };
    }

    private static string RandomBetween(string a, string b)
    {
        var min = int.Parse(a);
        var max = int.Parse(b);
        if (min > max)
        {
            (min, max) = (max, min);
        }

        return Random.Shared.NextInt64(min, (long)max + 1).ToString();
    }
}

/// <param name="Variables">The virtual user's extracted variables, compared without regard to case; null when the plan extracts nothing.</param>
public readonly record struct TemplateContext(
    int VirtualUser,
    long Iteration,
    ParameterValues Parameters = default,
    IReadOnlyDictionary<string, string>? Variables = null);
