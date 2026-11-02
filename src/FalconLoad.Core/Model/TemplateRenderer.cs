using System.Globalization;
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
/// virtual user's variable of that name, taken from an earlier response by an <see cref="ExtractionRule"/>, or else
/// the variable of that name in the selected <see cref="TestEnvironment"/></item>
/// </list>
/// Placeholders that match nothing, including a variable no response has supplied yet, are sent as written.
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*(?<builtin>\$)?(?<name>[A-Za-z_][A-Za-z0-9_]*)(?::(?<a>-?\d+):(?<b>-?\d+))?\s*\}\}")]
    private static partial Regex VariablePattern();

    public static bool HasVariables(string? text) => text is not null && text.Contains("{{", StringComparison.Ordinal) && VariablePattern().IsMatch(text);

    /// <summary>Replaces every placeholder with <paramref name="replacement"/>, for checking the shape of text around them.</summary>
    public static string ReplaceVariables(string text, string replacement) =>
        HasVariables(text) ? VariablePattern().Replace(text, replacement) : text;

    public static string Render(string text, TemplateContext context)
    {
        if (!HasVariables(text))
        {
            return text;
        }

        return VariablePattern().Replace(text, match => Evaluate(match, context, null) ?? match.Value);
    }

    /// <summary>
    /// Renders text whose own characters were already encoded by <see cref="EscapeText"/>, encoding each value that is
    /// filled in with <paramref name="escape"/>, as a query string needs. A placeholder that matches nothing is sent as written.
    /// </summary>
    public static string Render(string text, TemplateContext context, Func<string, string> escape)
    {
        if (!HasVariables(text))
        {
            return text;
        }

        return VariablePattern().Replace(text, match => Evaluate(match, context, null) is { } value ? escape(value) : match.Value);
    }

    /// <summary>Encodes the text around placeholders with <paramref name="escape"/>, leaving the placeholders to be filled in later.</summary>
    public static string EscapeText(string text, Func<string, string> escape)
    {
        if (!HasVariables(text))
        {
            return escape(text);
        }

        var result = new System.Text.StringBuilder(text.Length + 16);
        var last = 0;
        foreach (Match match in VariablePattern().Matches(text))
        {
            result.Append(escape(text[last..match.Index])).Append(match.Value);
            last = match.Index + match.Length;
        }

        return result.Append(escape(text[last..])).ToString();
    }

    /// <summary>
    /// Substitutes the environment's variables, which never change during a run, so a request is prepared once
    /// instead of rendered for every send. The caller leaves out the names a parameter list or an extracted variable
    /// supplies, because those win and change from request to request.
    /// </summary>
    /// <param name="escape">Encodes each substituted value, for text prepared with <see cref="EscapeText"/>.</param>
    public static string ResolveVariables(string text, IReadOnlyDictionary<string, string>? variables, Func<string, string>? escape = null)
    {
        if (variables is not { Count: > 0 } || !HasVariables(text))
        {
            return text;
        }

        return VariablePattern().Replace(text, match =>
        {
            return IsUserVariable(match) && variables.TryGetValue(match.Groups["name"].Value, out var value) ? escape is null ? value : escape(value) : match.Value;
        });
    }

    /// <summary>
    /// The names of <c>{{name}}</c> placeholders in <paramref name="text"/> that no parameter list, extracted variable
    /// or environment variable supplies.
    /// </summary>
    public static IReadOnlyList<string> UnresolvedNames(
        string text,
        IReadOnlyDictionary<string, string>? environment,
        ParameterSet? parameters,
        IReadOnlyCollection<string>? extracted)
    {
        if (!HasVariables(text))
        {
            return [];
        }

        return [.. VariablePattern().Matches(text)
            .Where(IsUserVariable)
            .Select(m => m.Groups["name"].Value)
            .Where(name => !IsParameter(name, parameters)
                && environment?.ContainsKey(name) != true
                && extracted?.Contains(name, StringComparer.OrdinalIgnoreCase) != true)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Renders with fixed placeholder values and the first value of each list, used for validating URLs before a run.
    /// Extracted variables have no value until a response supplies one, so they stand in as <c>1</c>, or as a URL when
    /// the text starts with one, such as a URL taken from a <c>Location</c> header.
    /// </summary>
    /// <param name="variables">The names of the plan's extracted variables.</param>
    /// <param name="environment">The selected environment's values.</param>
    /// <param name="assumeUnresolvedDefined">
    /// For saving a test, when no environment has been chosen yet: a placeholder nothing supplies is replaced by a
    /// stand-in (a whole address when it starts the URL), because the environment picked for a run may provide it.
    /// </param>
    public static string RenderForValidation(
        string text,
        ParameterSet? parameters = null,
        IReadOnlyCollection<string>? variables = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool assumeUnresolvedDefined = false)
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

        if (!HasVariables(text))
        {
            return text;
        }

        var context = new TemplateContext(1, 1, parameters?.First() ?? default, stand);
        var leading = text.Length - text.TrimStart().Length;
        return VariablePattern().Replace(text, match =>
        {
            var value = Evaluate(match, context, environment);
            if (value is null && assumeUnresolvedDefined && IsUserVariable(match))
            {
                return match.Index == leading ? "http://placeholder.invalid" : "placeholder";
            }

            return value ?? match.Value;
        });
    }

    private static bool IsUserVariable(Match match) => !match.Groups["builtin"].Success && !match.Groups["a"].Success;

    private static bool IsParameter(string name, ParameterSet? parameters) => parameters is not null && parameters.TryGetIndex(name, out _);

    private static string? Evaluate(Match match, TemplateContext context, IReadOnlyDictionary<string, string>? environment)
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

            if (context.Variables is not null && context.Variables.TryGetValue(name, out var extracted))
            {
                return extracted;
            }

            return environment is not null && environment.TryGetValue(name, out var variable) ? variable : null;
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

    private static string? RandomBetween(string a, string b)
    {
        // The pattern takes any digits, including ones from other scripts and numbers too big for 64 bits. Those match
        // nothing, so the placeholder is sent as written, like any other that doesn't resolve.
        if (!long.TryParse(a, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var min)
            || !long.TryParse(b, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var max))
        {
            return null;
        }

        if (min > max)
        {
            (min, max) = (max, min);
        }

        // The upper bound of NextInt64 is exclusive, so shift the range down by one when max + 1 would overflow.
        var value = max < long.MaxValue ? Random.Shared.NextInt64(min, max + 1)
            : min > long.MinValue ? Random.Shared.NextInt64(min - 1, max) + 1
            : Random.Shared.NextInt64(min, max);
        return value.ToString(CultureInfo.InvariantCulture);
    }
}

/// <param name="Variables">The virtual user's extracted variables, compared without regard to case; null when the plan extracts nothing.</param>
public readonly record struct TemplateContext(
    int VirtualUser,
    long Iteration,
    ParameterValues Parameters = default,
    IReadOnlyDictionary<string, string>? Variables = null);
