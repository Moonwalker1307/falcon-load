using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FalconLoad.Fuzz;

/// <summary>
/// Turns a seed into a new input. Most mutations work on the text, so they reach every parser, and inputs that are
/// JSON also get structural mutations, so a plan file that is still valid JSON reaches the model's own checks instead
/// of stopping at the JSON reader.
/// </summary>
internal sealed class Mutator(Random random, IReadOnlyList<string> dictionary)
{
    // Text that tends to find edge cases: boundaries of numeric types, characters with meaning to one of the parsers,
    // lone surrogates and control characters.
    private static readonly string[] Interesting =
    [
        "0", "-0", "-1", "1", "255", "256", "65535", "65536", "2147483647", "2147483648", "-2147483648", "-2147483649",
        "4294967295", "4294967296", "9223372036854775807", "9223372036854775808", "-9223372036854775809",
        "18446744073709551615", "18446744073709551616", "1e308", "1e309", "-1e309", "1e-400", "0.5", "-0.0", "NaN",
        "Infinity", "-Infinity", "0x10", "1_000", "\u0661\u0662\u0663", "", " ", "\t", "\r\n", "\n", "\0", "\\", "\"", "'", "`", "{",
        "}", "[", "]", "(", ")", "<", ">", ",", ":", ";", "=", "&", "?", "#", "%", "%00", "%zz", "$", "@", "*", "|",
        "\ud800", "\udfff", "\ud83d\ude00", "\\ud800", "\\udc00x", "\\ud83d\\ude00", "\\u0000", "\u200b", "\ufeff", "\u2028", "\u00e9", "\u00df", "\u0130", "null", "true", "false",
        "{}", "[]", "\"\"", "{{", "}}", "{{x}}", "{{$guid}}", "{{$randomInt:5:1}}", "{{$randomInt:-9999999999:9999999999}}",
        "{{$randomInt:2147483647:2147483647}}", "{{ $vu }}", "{{$iteration}}", "{{$timestamp}}", "{{$isoTimestamp}}",
        "http://", "https://", "ftp://x", "http://[::1]:99999/", "http://a:-1/", "file:///etc/passwd", "//", "/",
        "../", "~", "a".PadRight(300, 'a'),
    ];

    public string Mutate(string seed, IReadOnlyList<string> corpus)
    {
        var text = seed;
        var count = 1 + random.Next(random.Next(2) == 0 ? 2 : 6);
        for (var i = 0; i < count; i++)
        {
            text = Clean(random.Next(10) < 4 && TryMutateJson(text, out var json) ? json : MutateText(text, corpus));
        }

        return text.Length > 200_000 ? Clean(text[..200_000]) : text;
    }

    /// <summary>
    /// Every real input reaches falcon-load decoded from UTF-8 (a request body, an uploaded file, a form field), so a
    /// lone surrogate can't occur: it would have become U+FFFD. Inputs are cleaned the same way.
    /// </summary>
    public static string Clean(string text) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text));

    private string Token() => random.Next(3) == 0 || dictionary.Count == 0
        ? Interesting[random.Next(Interesting.Length)]
        : dictionary[random.Next(dictionary.Count)];

    private string MutateText(string text, IReadOnlyList<string> corpus)
    {
        var sb = new StringBuilder(text);
        var at = sb.Length == 0 ? 0 : random.Next(sb.Length + 1);
        switch (random.Next(13))
        {
            case 0 when sb.Length > 0:
                // Replace one character with any other.
                sb[Math.Min(at, sb.Length - 1)] = (char)(random.Next(4) == 0 ? random.Next(0x10000) : random.Next(0x20, 0x7f));
                break;
            case 1:
            case 2:
                sb.Insert(at, Token());
                break;
            case 3 when sb.Length > 0:
            {
                // Delete a range.
                var length = random.Next(1, Math.Min(sb.Length - Math.Min(at, sb.Length - 1), 1 + random.Next(64)) + 1);
                var start = Math.Min(at, sb.Length - 1);
                sb.Remove(start, Math.Min(length, sb.Length - start));
                break;
            }

            case 4 when sb.Length > 0:
            {
                // Repeat a range, sometimes many times, to find work that grows faster than the input.
                var start = random.Next(sb.Length);
                var length = Math.Min(sb.Length - start, 1 + random.Next(32));
                var piece = sb.ToString(start, length);
                var times = random.Next(5) == 0 ? random.Next(100, 3000) : random.Next(1, 8);
                sb.Insert(start, string.Concat(Enumerable.Repeat(piece, times)));
                break;
            }

            case 5 when sb.Length > 0:
                // Truncate.
                sb.Length = random.Next(sb.Length);
                break;
            case 6 when corpus.Count > 0:
            {
                // Splice in part of another input.
                var other = corpus[random.Next(corpus.Count)];
                if (other.Length > 0)
                {
                    var start = random.Next(other.Length);
                    sb.Insert(at, other.AsSpan(start, Math.Min(other.Length - start, 1 + random.Next(200))));
                }

                break;
            }

            case 7:
            {
                // Nest deeply: parsers that recurse can run out of stack.
                var open = random.Next(6) switch { 0 => "[", 1 => "{\"a\":", 2 => "(", 3 => "message M { ", 4 => "- ", _ => "  " };
                var depth = random.Next(5) == 0 ? random.Next(1000, 20000) : random.Next(10, 200);
                sb.Insert(at, string.Concat(Enumerable.Repeat(open, depth)));
                break;
            }

            case 8 when sb.Length > 1:
            {
                // Swap two characters.
                var a = random.Next(sb.Length);
                var b = random.Next(sb.Length);
                (sb[a], sb[b]) = (sb[b], sb[a]);
                break;
            }

            case 9 when sb.Length > 0:
            {
                // Replace a run of digits with an interesting number.
                var digits = FindDigits(sb.ToString(), at);
                if (digits is { } d)
                {
                    sb.Remove(d.Start, d.Length).Insert(d.Start, Interesting[random.Next(22)]);
                }
                else
                {
                    sb.Insert(at, Interesting[random.Next(22)]);
                }

                break;
            }

            case 10:
                sb.Insert(at, random.Next(2) == 0 ? "\n" : "\r\n");
                break;
            case 11 when sb.Length > 0:
                // Change the case of a character, for names that should or shouldn't be compared without case.
                var c = sb[Math.Min(at, sb.Length - 1)];
                sb[Math.Min(at, sb.Length - 1)] = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
                break;
            default:
                sb.Insert(at, Token());
                break;
        }

        return sb.ToString();
    }

    private static (int Start, int Length)? FindDigits(string text, int from)
    {
        for (var i = Math.Min(from, text.Length - 1); i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i]))
            {
                var end = i;
                while (end < text.Length && char.IsAsciiDigit(text[end]))
                {
                    end++;
                }

                return (i, end - i);
            }
        }

        return null;
    }

    private bool TryMutateJson(string text, out string result)
    {
        try
        {
            return TryMutateJsonCore(text, out result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // A duplicate key or an escaped lone surrogate, which JsonNode only notices when it gets to them.
            result = text;
            return false;
        }
    }

    private bool TryMutateJsonCore(string text, out string result)
    {
        result = text;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 256 });
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        var nodes = new List<JsonNode>();
        Collect(root, nodes);
        var target = nodes[random.Next(nodes.Count)];
        switch (random.Next(7))
        {
            case 0:
                Replace(target, RandomValue());
                break;
            case 1 when target.Parent is JsonObject parent:
                parent.Remove(target.GetPropertyName());
                break;
            case 2 when target.Parent is JsonArray array:
                // Duplicate an element, or many: lists that should be unique, or large ones.
                var index = target.GetElementIndex();
                var times = random.Next(4) == 0 ? random.Next(20, 200) : 1;
                for (var i = 0; i < times; i++)
                {
                    array.Insert(index, target.DeepClone());
                }

                break;
            case 3 when target.Parent is JsonObject parent:
            {
                // Rename a key: a case change (the readers ignore case) or another key from the dictionary.
                var name = target.GetPropertyName();
                var renamed = random.Next(2) == 0 ? name.ToUpperInvariant() : Token();
                if (!parent.ContainsKey(renamed))
                {
                    parent.Remove(name);
                    parent[renamed] = target;
                }

                break;
            }

            case 4 when target is JsonObject obj:
                // Add a property, maybe one the model knows, with a random value.
                var key = dictionary.Count > 0 && random.Next(2) == 0 ? dictionary[random.Next(dictionary.Count)] : Token();
                obj[key] = RandomValue();
                break;
            case 5 when target is JsonValue value && value.TryGetValue<string>(out var s):
                Replace(target, JsonValue.Create(MutateText(s, [])));
                break;
            case 6 when target.Parent is JsonArray array:
                array.Remove(target);
                break;
            default:
                Replace(target, RandomValue());
                break;
        }

        result = root.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 512 });
        return true;
    }

    private static void Collect(JsonNode node, List<JsonNode> nodes)
    {
        nodes.Add(node);
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    if (child is not null)
                    {
                        Collect(child, nodes);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Collect(child, nodes);
                    }
                }

                break;
        }
    }

    private static void Replace(JsonNode target, JsonNode? value)
    {
        switch (target.Parent)
        {
            case JsonObject parent:
                parent[target.GetPropertyName()] = value;
                break;
            case JsonArray array:
                array[target.GetElementIndex()] = value;
                break;
        }
    }

    private JsonNode? RandomValue() => random.Next(12) switch
    {
        0 => null,
        1 => JsonValue.Create(random.Next(2) == 0),
        2 => JsonValue.Create(random.Next(3) switch { 0 => int.MaxValue, 1 => int.MinValue, _ => random.Next(-3, 70000) }),
        3 => JsonValue.Create(random.Next(3) switch { 0 => long.MaxValue, 1 => long.MinValue, _ => -1L }),
        4 => JsonValue.Create(random.Next(3) switch { 0 => double.MaxValue, 1 => 1e-300, _ => random.NextDouble() * 1000 - 500 }),
        5 => new JsonArray(),
        6 => new JsonObject(),
        7 => new JsonArray(JsonValue.Create(Token()), null, JsonValue.Create(1)),
        8 => new JsonObject { [Token()] = JsonValue.Create(Token()) },
        9 => JsonNode.Parse("1" + new string('0', random.Next(20, 400))),
        _ => JsonValue.Create(Token()),
    };
}
