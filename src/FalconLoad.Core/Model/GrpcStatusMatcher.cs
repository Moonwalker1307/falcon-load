namespace FalconLoad.Core.Model;

/// <summary>
/// The gRPC status codes that count as success for a gRPC request, written as names or numbers, e.g. <c>OK</c> or
/// <c>OK,NOT_FOUND</c> or <c>0,5</c>. Empty means OK only.
/// </summary>
public sealed class GrpcStatusMatcher
{
    /// <summary>The names of the status codes 0 to 16, as the gRPC specification spells them.</summary>
    public static readonly string[] Names =
    [
        "OK", "CANCELLED", "UNKNOWN", "INVALID_ARGUMENT", "DEADLINE_EXCEEDED", "NOT_FOUND", "ALREADY_EXISTS", "PERMISSION_DENIED",
        "RESOURCE_EXHAUSTED", "FAILED_PRECONDITION", "ABORTED", "OUT_OF_RANGE", "UNIMPLEMENTED", "INTERNAL", "UNAVAILABLE", "DATA_LOSS",
        "UNAUTHENTICATED",
    ];

    private readonly int _mask;

    private GrpcStatusMatcher(int mask) => _mask = mask;

    public static GrpcStatusMatcher Default { get; } = new(1);

    public bool IsMatch(int status) => status is >= 0 and <= 16 && (_mask & (1 << status)) != 0;

    /// <summary>The name of a status code, or its number when it isn't one of the standard ones.</summary>
    public static string NameOf(int status) => status is >= 0 and <= 16 ? Names[status] : status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? expression, out GrpcStatusMatcher matcher)
    {
        matcher = Default;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var mask = 0;
        foreach (var part in expression.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = Array.FindIndex(Names, n => n.Equals(part.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase));
            if (index < 0 && (!int.TryParse(part, out index) || index is < 0 or > 16))
            {
                return false;
            }

            mask |= 1 << index;
        }

        matcher = new GrpcStatusMatcher(mask);
        return true;
    }
}
