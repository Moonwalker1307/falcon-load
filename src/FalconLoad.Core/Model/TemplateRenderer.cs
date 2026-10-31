using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>
/// Expands dynamic variables in URLs, headers and bodies so every request can be unique:
/// <list type="bullet">
/// <item><c>{{$guid}}</c> a new GUID</item>
/// <item><c>{{$randomInt}}</c> a random integer 0..999999, or <c>{{$randomInt:min:max}}</c></item>
/// <item><c>{{$timestamp}}</c> Unix time in seconds; <c>{{$isoTimestamp}}</c> ISO 8601 UTC</item>
/// <item><c>{{$iteration}}</c> the virtual user's iteration number; <c>{{$vu}}</c> the virtual user number</item>
/// </list>
/// </summary>
public static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{\s*\$(?<name>[A-Za-z]+)(?::(?<a>-?\d+):(?<b>-?\d+))?\s*\}\}")]
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

    /// <summary>Renders with fixed placeholder values, used for validating URLs before a run.</summary>
    public static string RenderForValidation(string text) => Render(text, new TemplateContext(1, 1));

    private static string? Evaluate(Match match, TemplateContext context)
    {
        var name = match.Groups["name"].Value;
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

public readonly record struct TemplateContext(int VirtualUser, long Iteration);
