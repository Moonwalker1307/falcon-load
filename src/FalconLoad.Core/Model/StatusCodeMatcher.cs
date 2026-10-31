namespace FalconLoad.Core.Model;

/// <summary>Parses expressions like "200,201,204" or "200-299,404" and checks status codes against them.</summary>
public sealed class StatusCodeMatcher
{
    private readonly List<(int From, int To)> _ranges;

    private StatusCodeMatcher(List<(int From, int To)> ranges) => _ranges = ranges;

    /// <summary>Matches any 2xx or 3xx status code.</summary>
    public static StatusCodeMatcher Default { get; } = new([(200, 399)]);

    public bool IsMatch(int statusCode) => _ranges.Any(r => statusCode >= r.From && statusCode <= r.To);

    public static bool TryParse(string? expression, out StatusCodeMatcher matcher)
    {
        matcher = Default;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var ranges = new List<(int, int)>();
        foreach (var part in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length == 1 && int.TryParse(bounds[0], out var single) && IsValid(single))
            {
                ranges.Add((single, single));
            }
            else if (bounds.Length == 2 && int.TryParse(bounds[0], out var from) && int.TryParse(bounds[1], out var to)
                     && IsValid(from) && IsValid(to) && from <= to)
            {
                ranges.Add((from, to));
            }
            else
            {
                return false;
            }
        }

        if (ranges.Count == 0)
        {
            return false;
        }

        matcher = new StatusCodeMatcher(ranges);
        return true;
    }

    private static bool IsValid(int code) => code is >= 100 and <= 599;
}
