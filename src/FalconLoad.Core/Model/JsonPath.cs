using System.Globalization;
using System.Text.Json;

namespace FalconLoad.Core.Model;

/// <summary>
/// A small subset of JSONPath for picking one value out of a response: <c>$.data.items[0].id</c>,
/// <c>$['odd key']</c>, <c>items[-1]</c> (counted from the end) and <c>items.length</c> (the number of items in an array
/// or characters in a string, unless the object really has a "length" property). The leading <c>$</c> is optional.
/// Wildcards, filters and slices are not supported, because a check compares one value.
/// </summary>
public sealed class JsonPath
{
    private readonly Segment[] _segments;

    private JsonPath(Segment[] segments, string text)
    {
        _segments = segments;
        Text = text;
    }

    public string Text { get; }

    public static bool TryParse(string? text, out JsonPath path, out string error)
    {
        path = new JsonPath([], "$");
        error = string.Empty;
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            error = "enter a path such as $.status or $.items[0].id.";
            return false;
        }

        var segments = new List<Segment>();
        var i = 0;
        if (s[0] == '$')
        {
            i = 1;
        }
        else if (s[0] is not ('.' or '['))
        {
            // "status.code" means "$.status.code".
            s = "." + s;
        }

        while (i < s.Length)
        {
            if (s[i] == '.')
            {
                var start = ++i;
                while (i < s.Length && s[i] is not ('.' or '['))
                {
                    i++;
                }

                if (i == start)
                {
                    error = $"a name is missing after the dot at position {start}.";
                    return false;
                }

                segments.Add(Segment.Property(s[start..i]));
            }
            else if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                if (close < 0)
                {
                    error = "a '[' has no matching ']'.";
                    return false;
                }

                var inner = s[(i + 1)..close].Trim();
                if (inner.Length >= 2 && inner[0] is '\'' or '"' && inner[^1] == inner[0])
                {
                    segments.Add(Segment.Property(inner[1..^1]));
                }
                else if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index))
                {
                    segments.Add(Segment.Index(index));
                }
                else
                {
                    error = $"'[{inner}]' must be an array index such as [0] or a quoted name such as ['id']. Wildcards and filters aren't supported.";
                    return false;
                }

                i = close + 1;
            }
            else
            {
                error = $"unexpected '{s[i]}' at position {i}; use .name or [0].";
                return false;
            }
        }

        path = new JsonPath([.. segments], s);
        return true;
    }

    /// <summary>
    /// Finds the value in <paramref name="root"/>. Strings come back unquoted, numbers, booleans and null as written,
    /// and objects and arrays as their JSON text.
    /// </summary>
    /// <returns>False when the path leads nowhere.</returns>
    public bool TryEvaluate(JsonElement root, out JsonPathValue value)
    {
        value = default;
        var current = root;
        for (var i = 0; i < _segments.Length; i++)
        {
            var segment = _segments[i];
            if (segment.Name is { } name)
            {
                if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(name, out var child))
                {
                    current = child;
                    continue;
                }

                // ".length" counts, but only as the last step and only where no real property is called that.
                if (name == "length" && i == _segments.Length - 1)
                {
                    if (current.ValueKind == JsonValueKind.Array)
                    {
                        value = new JsonPathValue(JsonValueKind.Number, current.GetArrayLength().ToString(CultureInfo.InvariantCulture));
                        return true;
                    }

                    if (current.ValueKind == JsonValueKind.String)
                    {
                        value = new JsonPathValue(JsonValueKind.Number, current.GetString()!.Length.ToString(CultureInfo.InvariantCulture));
                        return true;
                    }
                }

                return false;
            }

            if (current.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var length = current.GetArrayLength();
            var index = segment.Position < 0 ? length + segment.Position : segment.Position;
            if (index < 0 || index >= length)
            {
                return false;
            }

            current = current[index];
        }

        value = current.ValueKind switch
        {
            JsonValueKind.String => new JsonPathValue(JsonValueKind.String, current.GetString()!),
            _ => new JsonPathValue(current.ValueKind, current.GetRawText()),
        };
        return true;
    }

    public override string ToString() => Text;

    private readonly record struct Segment(string? Name, int Position)
    {
        public static Segment Property(string name) => new(name, 0);

        public static Segment Index(int position) => new(null, position);
    }
}

/// <summary>A value found by a <see cref="JsonPath"/>.</summary>
/// <param name="Kind">The JSON kind of the value; a <c>.length</c> is a number.</param>
/// <param name="Text">Strings unquoted, everything else as its JSON text.</param>
public readonly record struct JsonPathValue(JsonValueKind Kind, string Text);
