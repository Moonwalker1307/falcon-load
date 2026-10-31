using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>The outcome of one check on one response, with the value it saw, for "Send once".</summary>
public sealed record CheckOutcome(string Name, bool Passed, string? Actual);

/// <summary>
/// A request's enabled <see cref="Assertion"/>s, compiled once: patterns are built, paths parsed and needles encoded
/// up front, so checking a response does as little work as the checks themselves need.
/// </summary>
internal sealed class ResponseChecker
{
    private readonly Check[] _checks;

    private ResponseChecker(Check[] checks)
    {
        _checks = checks;
        NeedsBody = checks.Any(c => c.Source != AssertionSource.Header);
        Names = checks.Select(c => c.Name).ToArray();
        FailureMessages = checks.Select(c => $"Check failed: {c.Name}").ToArray();
    }

    public static ResponseChecker Empty { get; } = new([]);

    public int Count => _checks.Length;

    /// <summary>Whether any check reads the body, so it has to be kept while it is read.</summary>
    public bool NeedsBody { get; }

    public IReadOnlyList<string> Names { get; }

    /// <summary>The error recorded for a response failing each check. They are fixed, so failures don't allocate.</summary>
    public IReadOnlyList<string> FailureMessages { get; }

    public static ResponseChecker From(IEnumerable<Assertion> assertions)
    {
        var checks = assertions.Where(a => a.Enabled).Select(Check.From).ToArray();
        return checks.Length == 0 ? Empty : new ResponseChecker(checks);
    }

    /// <summary>Runs every check and returns a mask with bit <c>i</c> set when check <c>i</c> failed.</summary>
    /// <param name="body">The body, or empty when it was too large to keep (<paramref name="bodyTooLarge"/>).</param>
    public ulong Evaluate(HttpResponseMessage response, ReadOnlyMemory<byte> body, bool bodyTooLarge)
    {
        ulong failed = 0;
        JsonDocument? json = null;
        var jsonParsed = false;
        try
        {
            for (var i = 0; i < _checks.Length; i++)
            {
                var check = _checks[i];
                if (check.Source != AssertionSource.Header && bodyTooLarge)
                {
                    failed |= 1UL << i;
                    continue;
                }

                var passed = check.Source switch
                {
                    AssertionSource.Body => check.TestBody(body.Span),
                    AssertionSource.Header => check.TestText(HeaderValue(response, check.Target), out _),
                    _ => check.TestJson(ParseJson(body, ref json, ref jsonParsed), out _),
                };
                if (!passed)
                {
                    failed |= 1UL << i;
                }
            }
        }
        finally
        {
            json?.Dispose();
        }

        return failed;
    }

    /// <summary>Runs every check and reports what each one saw. Slower than <see cref="Evaluate"/>; for single requests.</summary>
    public IReadOnlyList<CheckOutcome> Explain(HttpResponseMessage response, ReadOnlyMemory<byte> body)
    {
        var outcomes = new List<CheckOutcome>(_checks.Length);
        JsonDocument? json = null;
        var jsonParsed = false;
        try
        {
            foreach (var check in _checks)
            {
                switch (check.Source)
                {
                    case AssertionSource.Body:
                        outcomes.Add(new CheckOutcome(check.Name, check.TestBody(body.Span), null));
                        break;
                    case AssertionSource.Header:
                        var passed = check.TestText(HeaderValue(response, check.Target), out var header);
                        outcomes.Add(new CheckOutcome(check.Name, passed, header ?? "(no such header)"));
                        break;
                    default:
                        var document = ParseJson(body, ref json, ref jsonParsed);
                        passed = check.TestJson(document, out var found);
                        outcomes.Add(new CheckOutcome(check.Name, passed,
                            document is null ? "(the body is not JSON)" : found ?? "(not found)"));
                        break;
                }
            }
        }
        finally
        {
            json?.Dispose();
        }

        return outcomes;
    }

    private static JsonDocument? ParseJson(ReadOnlyMemory<byte> body, ref JsonDocument? json, ref bool parsed)
    {
        if (!parsed)
        {
            parsed = true;
            try
            {
                // The document reads the body in place; it is disposed before the buffer behind the body is reused.
                json = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                json = null;
            }
        }

        return json;
    }

    private static string? HeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
        {
            return values is string[] { Length: 1 } single ? single[0] : string.Join(", ", values);
        }

        return null;
    }

    private sealed class Check
    {
        public required string Name { get; init; }

        public required AssertionSource Source { get; init; }

        public required AssertionOperator Operator { get; init; }

        public required string Target { get; init; }

        public required string Value { get; init; }

        public byte[] ValueUtf8 { get; init; } = [];

        public double Number { get; init; }

        public bool ValueIsNumber { get; init; }

        public Regex? Pattern { get; init; }

        public JsonPath? Path { get; init; }

        public static Check From(Assertion a)
        {
            var isNumber = Assertion.TryParseNumber(a.Value, out var number);
            Regex? pattern = null;
            if (a.Operator == AssertionOperator.Matches)
            {
                Assertion.TryCreateRegex(a.Value, out pattern);
            }

            JsonPath? path = null;
            if (a.Source == AssertionSource.JsonPath)
            {
                JsonPath.TryParse(a.Target, out path, out _);
            }

            return new Check
            {
                Name = a.Description,
                Source = a.Source,
                Operator = a.Operator,
                Target = a.Target.Trim(),
                Value = a.Value,
                ValueUtf8 = Encoding.UTF8.GetBytes(a.Value),
                Number = number,
                ValueIsNumber = isNumber,
                Pattern = pattern,
                Path = path,
            };
        }

        /// <summary>Body checks work on the raw bytes where they can, so only a pattern needs the body decoded.</summary>
        public bool TestBody(ReadOnlySpan<byte> body)
        {
            switch (Operator)
            {
                case AssertionOperator.Contains:
                    return body.IndexOf(ValueUtf8) >= 0;
                case AssertionOperator.NotContains:
                    return body.IndexOf(ValueUtf8) < 0;
                case AssertionOperator.Equals:
                    return body.SequenceEqual(ValueUtf8);
                case AssertionOperator.Matches:
                    var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(body.Length));
                    try
                    {
                        var length = Encoding.UTF8.GetChars(body, chars);
                        return IsMatch(chars.AsSpan(0, length));
                    }
                    finally
                    {
                        ArrayPool<char>.Shared.Return(chars);
                    }

                default:
                    return false;
            }
        }

        public bool TestJson(JsonDocument? document, out string? actual)
        {
            actual = null;
            // A body that isn't JSON has no values, so it is treated like a path that leads nowhere.
            if (document is null || Path is null || !Path.TryFind(document.RootElement, out var value))
            {
                return TestText(null, out _);
            }

            actual = value.Kind == JsonValueKind.String ? $"\"{value.Text}\"" : value.Text;
            if (Operator is AssertionOperator.Equals or AssertionOperator.NotEquals
                && value.Kind == JsonValueKind.Number && ValueIsNumber
                && double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            {
                // Numbers compare by value, so 1.0 equals 1.
                return (n == Number) == (Operator == AssertionOperator.Equals);
            }

            return TestText(value.Text, out _);
        }

        /// <summary>Tests a header value or JSON value; null means it isn't there.</summary>
        public bool TestText(string? text, out string? actual)
        {
            actual = text;
            if (text is null)
            {
                return Operator is AssertionOperator.NotExists or AssertionOperator.NotContains or AssertionOperator.NotEquals;
            }

            return Operator switch
            {
                AssertionOperator.Exists => true,
                AssertionOperator.NotExists => false,
                AssertionOperator.Contains => text.Contains(Value, StringComparison.Ordinal),
                AssertionOperator.NotContains => !text.Contains(Value, StringComparison.Ordinal),
                AssertionOperator.Equals => text == Value,
                AssertionOperator.NotEquals => text != Value,
                AssertionOperator.Matches => IsMatch(text),
                AssertionOperator.LessThan => TryNumber(text, out var n) && n < Number,
                AssertionOperator.GreaterThan => TryNumber(text, out var m) && m > Number,
                _ => false,
            };
        }

        private bool IsMatch(ReadOnlySpan<char> text)
        {
            try
            {
                return Pattern?.IsMatch(text) ?? false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
