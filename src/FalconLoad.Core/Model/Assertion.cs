using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FalconLoad.Core.Model;

/// <summary>The part of a response an <see cref="Assertion"/> looks at.</summary>
public enum AssertionSource
{
    /// <summary>The whole response body, as text.</summary>
    Body,

    /// <summary>A value inside a JSON response body, found by <see cref="Assertion.Target"/> (see <see cref="JsonPath"/>).</summary>
    JsonPath,

    /// <summary>A response header named by <see cref="Assertion.Target"/>.</summary>
    Header,
}

public enum AssertionOperator
{
    Contains,
    NotContains,
    Equals,
    NotEquals,

    /// <summary>The value matches the regular expression in <see cref="Assertion.Value"/>.</summary>
    Matches,

    Exists,
    NotExists,

    /// <summary>The value is a number below <see cref="Assertion.Value"/>.</summary>
    LessThan,

    /// <summary>The value is a number above <see cref="Assertion.Value"/>.</summary>
    GreaterThan,
}

/// <summary>
/// A check on each response to a request, on top of its expected status codes. A response that fails any check counts
/// as a failed request, and the run's results count how often each check passed and failed.
/// </summary>
public sealed class Assertion
{
    /// <summary>
    /// A request can have at most this many checks, so the failed ones of a response fit in one 64-bit mask and
    /// recording them costs no allocation.
    /// </summary>
    public const int MaxPerRequest = 64;

    /// <summary>
    /// Bodies longer than this aren't kept in memory to be checked, so a huge download can't eat a virtual user's
    /// memory; their body and JSON checks fail instead.
    /// </summary>
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>Text matching gives up after this long, so a slow pattern can't stall a virtual user.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly AssertionOperator[] BodyOperators =
        [AssertionOperator.Contains, AssertionOperator.NotContains, AssertionOperator.Equals, AssertionOperator.Matches];

    private static readonly AssertionOperator[] ValueOperators = Enum.GetValues<AssertionOperator>();

    public AssertionSource Source { get; set; } = AssertionSource.Body;

    /// <summary>The JSON path or header name. Not used for <see cref="AssertionSource.Body"/>.</summary>
    public string Target { get; set; } = string.Empty;

    public AssertionOperator Operator { get; set; } = AssertionOperator.Contains;

    /// <summary>What to compare with. Not used for <see cref="AssertionOperator.Exists"/> and <see cref="AssertionOperator.NotExists"/>.</summary>
    public string Value { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>The operators that make sense for <paramref name="source"/>.</summary>
    public static IReadOnlyList<AssertionOperator> OperatorsFor(AssertionSource source) =>
        source == AssertionSource.Body ? BodyOperators : ValueOperators;

    public static bool NeedsValue(AssertionOperator op) => op is not (AssertionOperator.Exists or AssertionOperator.NotExists);

    public static string OperatorText(AssertionOperator op) => op switch
    {
        AssertionOperator.Contains => "contains",
        AssertionOperator.NotContains => "does not contain",
        AssertionOperator.Equals => "equals",
        AssertionOperator.NotEquals => "does not equal",
        AssertionOperator.Matches => "matches",
        AssertionOperator.Exists => "exists",
        AssertionOperator.NotExists => "does not exist",
        AssertionOperator.LessThan => "is less than",
        AssertionOperator.GreaterThan => "is greater than",
        _ => op.ToString(),
    };

    /// <summary>A short readable form, e.g. <c>$.status equals "ok"</c>. It names the check in results and errors.</summary>
    [JsonIgnore]
    public string Description
    {
        get
        {
            var subject = Source switch
            {
                AssertionSource.Body => "body",
                AssertionSource.Header => $"header {Target.Trim()}",
                _ => Target.Trim(),
            };
            if (!NeedsValue(Operator))
            {
                return $"{subject} {OperatorText(Operator)}";
            }

            var value = Operator is AssertionOperator.LessThan or AssertionOperator.GreaterThan ? Value.Trim()
                : Operator == AssertionOperator.Matches ? $"/{Value}/"
                : $"\"{Value}\"";
            return $"{subject} {OperatorText(Operator)} {value}";
        }
    }

    public IEnumerable<string> Validate()
    {
        if (!Enum.IsDefined(Source))
        {
            yield return "Unknown check source.";
            yield break;
        }

        if (!OperatorsFor(Source).Contains(Operator))
        {
            yield return Source == AssertionSource.Body
                ? $"The body can't be checked with \"{OperatorText(Operator)}\"; use contains, does not contain, equals or matches."
                : "Unknown check operator.";
            yield break;
        }

        if (Source == AssertionSource.Header && string.IsNullOrWhiteSpace(Target))
        {
            yield return "A header check needs the header name.";
        }

        if (Source == AssertionSource.JsonPath && !Model.JsonPath.TryParse(Target, out _, out var pathError))
        {
            yield return $"JSON path '{Target}': {pathError}";
        }

        if (Operator is AssertionOperator.Contains or AssertionOperator.NotContains or AssertionOperator.Matches && Value.Length == 0)
        {
            yield return $"The check \"{Description}\" needs a value.";
        }

        if (Operator is AssertionOperator.LessThan or AssertionOperator.GreaterThan && !TryParseNumber(Value, out _))
        {
            yield return $"The check \"{Description}\" needs a number to compare with.";
        }

        if (Operator == AssertionOperator.Matches && Value.Length > 0 && !TryCreateRegex(Value, out _))
        {
            yield return $"'{Value}' is not a valid regular expression.";
        }
    }

    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static bool TryCreateRegex(string pattern, out Regex? regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            regex = null;
            return false;
        }
    }
}
