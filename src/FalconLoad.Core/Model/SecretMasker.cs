using System.Diagnostics.CodeAnalysis;

namespace FalconLoad.Core.Model;

/// <summary>
/// Replaces secret values in text that may be shown to people, such as an error message or a response that echoes a
/// token back. Both the raw and the URL-escaped form of a secret are hidden.
/// </summary>
public sealed class SecretMasker
{
    public const string Mask = "******";

    // A one or two character secret would garble ordinary text such as status codes, so shorter values are not searched for.
    private const int MinimumLength = 3;

    private readonly string[] _secrets;

    public SecretMasker(IEnumerable<string?> secrets)
    {
        _secrets = [.. secrets
            .Where(s => s is { Length: >= MinimumLength })
            .SelectMany(s => new[] { s!, Uri.EscapeDataString(s!) })
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)];
    }

    public static SecretMasker None { get; } = new([]);

    public bool IsEmpty => _secrets.Length == 0;

    [return: NotNullIfNotNull(nameof(text))]
    public string? Apply(string? text)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }

        return text;
    }
}
