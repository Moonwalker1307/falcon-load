using System.Text.Json;

namespace FalconLoad.Core.Model;

/// <summary>
/// The small subset of JSONPath that picking one value out of a response needs: an optional <c>$</c>, then property
/// names (<c>.token</c> or <c>['token type']</c>) and array indexes (<c>[0]</c>, or <c>[-1]</c> for the last item).
/// A leading name can leave out the <c>$.</c>, so <c>data.items[0].id</c> works too. Property names are case sensitive,
/// as they are in JSON.
/// </summary>
public sealed class JsonPath
{
    private readonly Segment[] _segments;

    private JsonPath(Segment[] segments) => _segments = segments;

    public static bool TryParse(string expression, out JsonPath path, out string? error)
    {
        path = new JsonPath([]);
        var text = expression.Trim();
        var segments = new List<Segment>();
        var i = 0;
        if (text.StartsWith('$'))
        {
            i = 1;
        }
        else if (text.Length > 0 && text[0] is not '.' and not '[')
        {
            segments.Add(new Segment(ReadName(text, ref i), null));
        }

        while (i < text.Length)
        {
            if (text[i] == '.')
            {
                i++;
                var name = ReadName(text, ref i);
                if (name.Length == 0)
                {
                    error = $"a property name is missing at position {i + 1}.";
                    return false;
                }

                segments.Add(new Segment(name, null));
            }
            else if (text[i] == '[')
            {
                var close = text.IndexOf(']', i);
                if (close < 0)
                {
                    error = "a '[' has no closing ']'.";
                    return false;
                }

                var inner = text[(i + 1)..close].Trim();
                if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0])
                {
                    segments.Add(new Segment(inner[1..^1], null));
                }
                else if (int.TryParse(inner, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var index))
                {
                    segments.Add(new Segment(null, index));
                }
                else
                {
                    error = $"'[{inner}]' must be an index such as [0] or a quoted name such as ['id'].";
                    return false;
                }

                i = close + 1;
            }
            else
            {
                error = $"unexpected '{text[i]}' at position {i + 1}; use '.' before a property name.";
                return false;
            }
        }

        path = new JsonPath([.. segments]);
        error = null;
        return true;
    }

    /// <summary>
    /// Finds the value at this path. Strings are returned without quotes, numbers and booleans as written, and objects
    /// and arrays as JSON. A missing value or a JSON null finds nothing.
    /// </summary>
    public bool TryEvaluate(JsonElement root, out string value)
    {
        var current = root;
        foreach (var segment in _segments)
        {
            if (segment.Name is { } name)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    value = string.Empty;
                    return false;
                }
            }
            else
            {
                var index = segment.Index!.Value;
                var length = current.ValueKind == JsonValueKind.Array ? current.GetArrayLength() : 0;
                if (index < 0)
                {
                    index += length;
                }

                if (index < 0 || index >= length)
                {
                    value = string.Empty;
                    return false;
                }

                current = current[index];
            }
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => current.GetString()!,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => current.GetRawText(),
        };
        return current.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    private static string ReadName(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && text[i] is not '.' and not '[')
        {
            i++;
        }

        return text[start..i].Trim();
    }

    private readonly record struct Segment(string? Name, int? Index);
}
