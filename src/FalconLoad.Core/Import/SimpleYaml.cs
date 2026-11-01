using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FalconLoad.Core.Import;

/// <summary>
/// A reader for the YAML that OpenAPI files use: nested maps and lists, flow lists and maps, quoted and plain
/// scalars, and <c>|</c> and <c>&gt;</c> text blocks. Anchors, aliases, tags and several documents in one file are
/// not supported and are reported as errors rather than misread. The core stays free of packages, so this stands
/// in for a YAML library.
/// </summary>
internal sealed partial class SimpleYaml
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][-+]?[0-9]+)?$")]
    private static partial Regex NumberPattern();

    // As deep as System.Text.Json reads JSON by default; deeper nesting would only come from a broken or hostile file,
    // and the parser recurses, so it stops there instead of running out of stack.
    private const int MaxDepth = 64;

    private readonly List<string> _lines;
    private int _pos;
    private int _depth;

    private SimpleYaml(string text)
    {
        _lines = [.. text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')];
    }

    public static JsonNode? Parse(string text)
    {
        var yaml = new SimpleYaml(text.TrimStart('﻿'));
        yaml.SkipBlank();
        if (yaml._pos < yaml._lines.Count && yaml._lines[yaml._pos].TrimEnd() == "---")
        {
            yaml._pos++;
        }

        yaml.SkipBlank();
        if (yaml._pos >= yaml._lines.Count)
        {
            return null;
        }

        var node = yaml.ParseNode(Indent(yaml._lines[yaml._pos]));
        yaml.SkipBlank();
        if (yaml._pos < yaml._lines.Count)
        {
            throw yaml.Error("Unexpected content (several documents in one file are not supported).");
        }

        return node;
    }

    private FormatException Error(string message) => new($"YAML line {_pos + 1}: {message}");

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool IsBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed[0] == '#';
    }

    private void SkipBlank()
    {
        while (_pos < _lines.Count && IsBlank(_lines[_pos]))
        {
            _pos++;
        }
    }

    private JsonNode? ParseNode(int indent)
    {
        Enter();
        try
        {
            return ParseNodeCore(indent);
        }
        finally
        {
            _depth--;
        }
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
        {
            throw Error($"The document is nested more than {MaxDepth} levels deep.");
        }
    }

    private JsonNode? ParseNodeCore(int indent)
    {
        var content = _lines[_pos][indent..];
        if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
        {
            return ParseSequence(indent);
        }

        if (FindKeyColon(StripComment(content)) >= 0)
        {
            return ParseMap(indent);
        }

        return ParseInlineValue(content, indent);
    }

    private JsonArray ParseSequence(int indent)
    {
        var array = new JsonArray();
        while (true)
        {
            SkipBlank();
            if (_pos >= _lines.Count || Indent(_lines[_pos]) != indent)
            {
                break;
            }

            var content = _lines[_pos][indent..];
            if (content != "-" && !content.StartsWith("- ", StringComparison.Ordinal))
            {
                break;
            }

            var rest = content.Length > 1 ? content[1..] : string.Empty;
            var spaces = Indent(rest);
            if (StripComment(rest).Trim().Length == 0)
            {
                _pos++;
                array.Add(ParseChild(indent));
                continue;
            }

            // Treat the text after the dash as a line of its own, indented past the dash: "- name: x" starts a map.
            _lines[_pos] = new string(' ', indent + 1 + spaces) + rest[spaces..];
            array.Add(ParseNode(indent + 1 + spaces));
        }

        return array;
    }

    private JsonObject ParseMap(int indent)
    {
        var map = new JsonObject();
        while (true)
        {
            SkipBlank();
            if (_pos >= _lines.Count || Indent(_lines[_pos]) != indent)
            {
                break;
            }

            var line = StripComment(_lines[_pos][indent..]);
            var colon = FindKeyColon(line);
            if (colon < 0)
            {
                throw Error("Expected \"key: value\".");
            }

            var key = Unquote(line[..colon].Trim());
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                _pos++;
                map[key] = ParseChild(indent, allowSameIndentList: true);
            }
            else if (value[0] is '|' or '>')
            {
                map[key] = ParseBlockScalar(value, indent);
            }
            else
            {
                map[key] = ParseInlineValue(value, indent);
            }
        }

        return map;
    }

    // The value of "key:" or "-" with nothing after it: whatever is nested below, or null.
    private JsonNode? ParseChild(int parentIndent, bool allowSameIndentList = false)
    {
        SkipBlank();
        if (_pos >= _lines.Count)
        {
            return null;
        }

        var indent = Indent(_lines[_pos]);
        if (indent > parentIndent)
        {
            return ParseNode(indent);
        }

        var content = _lines[_pos][indent..];
        if (allowSameIndentList && indent == parentIndent && (content == "-" || content.StartsWith("- ", StringComparison.Ordinal)))
        {
            return ParseSequence(indent);
        }

        return null;
    }

    private JsonNode? ParseInlineValue(string text, int parentIndent)
    {
        text = StripComment(text).Trim();
        _pos++;

        // Flow collections can span lines; keep reading until the brackets balance.
        if (text.Length > 0 && text[0] is '[' or '{')
        {
            while (Depth(text) > 0 && _pos < _lines.Count)
            {
                text += " " + StripComment(_lines[_pos]).Trim();
                _pos++;
            }

            var index = 0;
            var node = ParseFlow(text, ref index);
            SkipSpaces(text, ref index);
            if (index < text.Length)
            {
                throw Error("Unexpected text after a flow collection.");
            }

            return node;
        }

        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            // A quoted string may continue on following lines until the closing quote.
            while (!IsClosed(text) && _pos < _lines.Count)
            {
                text += "\n" + _lines[_pos].Trim();
                _pos++;
            }

            var index = 0;
            return JsonValue.Create(ReadQuoted(text, ref index));
        }

        // A plain scalar continues on following lines that are indented deeper than its key.
        while (_pos < _lines.Count && !IsBlank(_lines[_pos]) && Indent(_lines[_pos]) > parentIndent && text.Length > 0)
        {
            text += " " + StripComment(_lines[_pos]).Trim();
            _pos++;
        }

        return ParsePlain(text);
    }

    private JsonNode? ParsePlain(string text)
    {
        if (text.Length > 0 && text[0] is '&' or '*' or '!')
        {
            throw Error("Anchors, aliases and tags are not supported.");
        }

        if (text == "<<")
        {
            throw Error("Merge keys are not supported.");
        }

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (NumberPattern().IsMatch(text))
        {
            if (!text.Contains('.') && !text.Contains('e') && !text.Contains('E')
                && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole))
            {
                return JsonValue.Create(whole);
            }

            // A number too big for a double, such as 1e999, can't be written as JSON, so it stays text.
            var number = double.Parse(text, CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(text);
        }

        return JsonValue.Create(text);
    }

    private JsonValue ParseBlockScalar(string header, int parentIndent)
    {
        _pos++;
        var folded = header[0] == '>';
        var chomp = header.Contains('-') ? '-' : header.Contains('+') ? '+' : ' ';
        var body = new List<string>();
        var blockIndent = -1;
        while (_pos < _lines.Count)
        {
            var line = _lines[_pos];
            if (line.Trim().Length == 0)
            {
                body.Add(string.Empty);
                _pos++;
                continue;
            }

            var indent = Indent(line);
            if (indent <= parentIndent)
            {
                break;
            }

            blockIndent = blockIndent < 0 ? indent : blockIndent;
            body.Add(line.Length >= blockIndent ? line[Math.Min(blockIndent, indent)..] : line.TrimStart());
            _pos++;
        }

        var trailingBlank = 0;
        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
            trailingBlank++;
        }

        string text;
        if (!folded)
        {
            text = string.Join('\n', body);
        }
        else
        {
            var builder = new StringBuilder();
            for (var i = 0; i < body.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(body[i - 1].Length == 0 || body[i].Length == 0 || body[i].StartsWith(' ') ? "\n" : " ");
                }

                builder.Append(body[i]);
            }

            text = builder.ToString();
        }

        text += chomp switch
        {
            '-' => string.Empty,
            '+' => new string('\n', trailingBlank + 1),
            _ => body.Count > 0 ? "\n" : string.Empty,
        };
        return JsonValue.Create(text)!;
    }

    // --- flow collections and quoted strings ---

    private JsonNode? ParseFlow(string text, ref int i)
    {
        Enter();
        try
        {
            return ParseFlowCore(text, ref i);
        }
        finally
        {
            _depth--;
        }
    }

    private JsonNode? ParseFlowCore(string text, ref int i)
    {
        SkipSpaces(text, ref i);
        if (i >= text.Length)
        {
            throw Error("Unexpected end of a flow collection.");
        }

        if (text[i] == '[')
        {
            i++;
            var array = new JsonArray();
            while (true)
            {
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ']')
                {
                    i++;
                    return array;
                }

                array.Add(ParseFlow(text, ref i));
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                }
                else if (i >= text.Length || text[i] != ']')
                {
                    throw Error("Expected ',' or ']'.");
                }
            }
        }

        if (text[i] == '{')
        {
            i++;
            var map = new JsonObject();
            while (true)
            {
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == '}')
                {
                    i++;
                    return map;
                }

                if (i >= text.Length)
                {
                    throw Error("Expected ',' or '}'.");
                }

                var key = text[i] is '"' or '\'' ? ReadQuoted(text, ref i) : ReadPlainFlow(text, ref i, stopAtColon: true);
                SkipSpaces(text, ref i);
                JsonNode? value = null;
                if (i < text.Length && text[i] == ':')
                {
                    i++;
                    value = ParseFlow(text, ref i);
                }

                map[key] = value;
                SkipSpaces(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                }
                else if (i >= text.Length || text[i] != '}')
                {
                    throw Error("Expected ',' or '}'.");
                }
            }
        }

        if (text[i] is '"' or '\'')
        {
            return JsonValue.Create(ReadQuoted(text, ref i));
        }

        return ParsePlain(ReadPlainFlow(text, ref i, stopAtColon: false));
    }

    private static string ReadPlainFlow(string text, ref int i, bool stopAtColon)
    {
        var start = i;
        while (i < text.Length && text[i] is not (',' or ']' or '}') && !(stopAtColon && text[i] == ':' && (i + 1 >= text.Length || text[i + 1] is ' ' or ',' or '}')))
        {
            i++;
        }

        return text[start..i].Trim();
    }

    private string ReadQuoted(string text, ref int i)
    {
        var quote = text[i++];
        var builder = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i];
            if (quote == '\'' && c == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    builder.Append('\'');
                    i += 2;
                    continue;
                }

                i++;
                return builder.ToString();
            }

            if (quote == '"' && c == '"')
            {
                i++;
                return builder.ToString();
            }

            if (quote == '"' && c == '\\' && i + 1 < text.Length)
            {
                var escape = text[i + 1];
                i += 2;
                switch (escape)
                {
                    case 'n': builder.Append('\n'); break;
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case '0': builder.Append('\0'); break;
                    case 'u' when i + 4 <= text.Length:
                        builder.Append((char)int.Parse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    case 'x' when i + 2 <= text.Length:
                        builder.Append((char)int.Parse(text.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 2;
                        break;
                    default: builder.Append(escape); break;
                }

                continue;
            }

            builder.Append(c == '\n' ? ' ' : c);
            i++;
        }

        throw Error("A quoted string is not closed.");
    }

    private static bool IsClosed(string text)
    {
        var quote = text[0];
        for (var i = 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\')
            {
                i++;
            }
            else if (text[i] == quote)
            {
                if (quote == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    private static void SkipSpaces(string text, ref int i)
    {
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }
    }

    private static int Depth(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"' or '\'':
                {
                    var end = i + 1;
                    while (end < text.Length && text[end] != text[i])
                    {
                        end += text[i] == '"' && text[end] == '\\' ? 2 : 1;
                    }

                    i = end;
                    break;
                }

                case '[' or '{': depth++; break;
                case ']' or '}': depth--; break;
            }
        }

        return depth;
    }

    // --- lines ---

    /// <summary>Finds the colon that ends a map key, or -1 when the text is not a "key: value" line.</summary>
    private static int FindKeyColon(string text)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var i = 1;
            while (i < text.Length && text[i] != quote)
            {
                i += quote == '"' && text[i] == '\\' ? 2 : 1;
            }

            return i + 1 < text.Length && text[i + 1] == ':' && (i + 2 >= text.Length || text[i + 2] == ' ') ? i + 1 : -1;
        }

        if (text[0] is '[' or '{')
        {
            return -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unquote(string key)
    {
        if (key.Length >= 2 && key[0] is '"' or '\'' && key[^1] == key[0])
        {
            return key[1..^1];
        }

        return key;
    }

    /// <summary>Removes a trailing "# comment" that is outside quotes.</summary>
    private static string StripComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is { } q)
            {
                if (c == '\\' && q == '"')
                {
                    i++;
                }
                else if (c == q)
                {
                    quote = null;
                }
            }
            else if ((c == '"' || c == '\'') && (i == 0 || line[i - 1] is ' ' or '[' or '{' or ',' or ':'))
            {
                quote = c;
            }
            else if (c == '#' && (i == 0 || line[i - 1] is ' ' or '\t'))
            {
                return line[..i].TrimEnd();
            }
        }

        return line.TrimEnd();
    }
}
