using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>Where an <see cref="ExtractionRule"/> looks for its value in a response.</summary>
public enum ExtractionSource
{
    /// <summary>A value in a JSON body, found with a path such as <c>$.data.token</c> or <c>items[0].id</c>.</summary>
    JsonPath,

    /// <summary>A response header, by name.</summary>
    Header,

    /// <summary>A regular expression over the body text; the first capture group, or the whole match without one.</summary>
    Regex,
}

/// <summary>
/// Takes a value from a response and stores it in a variable of the virtual user that sent the request, so its later
/// requests can use it as <c>{{name}}</c>, for example a token returned by a login request.
/// </summary>
public sealed class ExtractionRule
{
    public string Variable { get; set; } = string.Empty;

    public ExtractionSource Source { get; set; } = ExtractionSource.JsonPath;

    /// <summary>The JSON path, header name or regular expression, depending on <see cref="Source"/>.</summary>
    public string Expression { get; set; } = string.Empty;

    /// <summary>Regular expressions run against every response, so a pathological pattern is stopped instead of stalling a virtual user.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Variable))
        {
            yield return "Every extraction needs a variable name.";
        }
        else if (!ParameterList.NamePattern().IsMatch(Variable))
        {
            yield return $"Variable name '{Variable}' can only use letters, digits and underscores, and can't start with a digit.";
        }

        var label = string.IsNullOrWhiteSpace(Variable) ? "an extraction" : $"'{Variable}'";
        if (string.IsNullOrWhiteSpace(Expression))
        {
            yield return Source switch
            {
                ExtractionSource.Header => $"Extraction {label} needs a header name.",
                ExtractionSource.Regex => $"Extraction {label} needs a regular expression.",
                _ => $"Extraction {label} needs a JSON path.",
            };
            yield break;
        }

        if (Source == ExtractionSource.JsonPath && !JsonPath.TryParse(Expression, out _, out var pathError))
        {
            yield return $"The JSON path for {label} isn't valid: {pathError}";
        }
        else if (Source == ExtractionSource.Regex && !TryCreateRegex(Expression, out _, out var regexError))
        {
            yield return $"The regular expression for {label} isn't valid: {regexError}";
        }
    }

    internal static bool TryCreateRegex(string pattern, out Regex? regex, out string? error)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = ex.Message;
            return false;
        }
    }
}
