using System.Globalization;
using System.Text;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Import;

/// <summary>
/// Reads pasted curl commands (bash style, as copied from a browser's "Copy as cURL" or from API docs) into requests.
/// Several commands, one after another, become several requests in order.
/// </summary>
public static class CurlImporter
{
    // Options that take a value and have no effect on a load test, so they are consumed without a note.
    private static readonly HashSet<string> SilentWithValue = new(StringComparer.Ordinal)
    {
        "-o", "-O", "-w", "-D", "-c", "-C", "-K", "-m", "-r", "-t", "-z", "-P", "-Q", "-U", "-y", "-Y", "-T",
        "--output", "--write-out", "--dump-header", "--cookie-jar", "--config", "--max-time", "--connect-timeout",
        "--range", "--proxy-user", "--user-agent-ignored", "--trace", "--trace-ascii", "--interface", "--local-port",
        "--retry-delay", "--retry-max-time", "--max-redirs", "--keepalive-time", "--expect100-timeout", "--time-cond",
        "--cookie-jar", "--proxy-header", "--speed-limit", "--speed-time", "--stderr", "--ciphers", "--tls-max",
        "--tlsv1.0", "--pinnedpubkey", "--hostpubsha256", "--happy-eyeballs-timeout-ms", "--dns-servers",
    };

    // Options that take a value and would change what is sent, which falcon-load can't do.
    private static readonly HashSet<string> WarnWithValue = new(StringComparer.Ordinal)
    {
        "-x", "-E", "--proxy", "--cert", "--key", "--cacert", "--capath", "--resolve", "--connect-to", "--limit-rate",
        "--retry", "--upload-file", "--unix-socket", "--cert-type", "--key-type", "--pass",
    };

    public static ImportResult Read(string text)
    {
        var result = new ImportResult();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tokens in Tokenize(text))
        {
            var start = tokens.FindIndex(t => t == "curl" || t.EndsWith("/curl", StringComparison.Ordinal));
            if (start < 0 || tokens.Take(start).Any(t => t != "$" && t != "sudo"))
            {
                continue;
            }

            if (ParseCommand(tokens.Skip(start + 1).ToList(), result, seen) is { } request)
            {
                result.Requests.Add(request);
            }
        }

        if (result.Requests.Count == 0)
        {
            throw new ImportException("No curl command with a URL was found.");
        }

        result.Name = result.Requests.Count == 1 ? result.Requests[0].Name : "Imported curl commands";
        return result;
    }

    private static RequestDefinition? ParseCommand(List<string> args, ImportResult result, HashSet<string> seen)
    {
        string? url = null, method = null, user = null;
        var dataParts = new List<(string Kind, string Value)>();
        var formFields = new List<(string Name, string Value)>();
        var headers = new List<(string Name, string Value)>();
        string? cookie = null, userAgent = null, referer = null, bearer = null;
        var head = false;
        var get = false;
        var json = false;

        void Note(string option)
        {
            if (seen.Add(option))
            {
                result.Warnings.Add($"curl option {option} was ignored; falcon-load can't do that.");
            }
        }

        string? Value(ref int i, string inline)
        {
            if (inline.Length > 0)
            {
                return inline;
            }

            return i + 1 < args.Count ? args[++i] : null;
        }

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--" || !arg.StartsWith('-') || arg == "-")
            {
                url ??= arg;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg;
                var inline = string.Empty;
                var equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    name = arg[..equals];
                    inline = arg[(equals + 1)..];
                }

                switch (name)
                {
                    case "--request": method = Value(ref i, inline); break;
                    case "--header": AddHeader(headers, Value(ref i, inline), result); break;
                    case "--data" or "--data-ascii": dataParts.Add(("data", Value(ref i, inline) ?? string.Empty)); break;
                    case "--data-binary": dataParts.Add(("binary", Value(ref i, inline) ?? string.Empty)); break;
                    case "--data-raw": dataParts.Add(("raw", Value(ref i, inline) ?? string.Empty)); break;
                    case "--data-urlencode": dataParts.Add(("urlencode", Value(ref i, inline) ?? string.Empty)); break;
                    case "--json": dataParts.Add(("data", Value(ref i, inline) ?? string.Empty)); json = true; break;
                    case "--user": user = Value(ref i, inline); break;
                    case "--oauth2-bearer": bearer = Value(ref i, inline); break;
                    case "--cookie": cookie = Value(ref i, inline); break;
                    case "--user-agent": userAgent = Value(ref i, inline); break;
                    case "--referer": referer = Value(ref i, inline); break;
                    case "--form" or "--form-string": AddForm(formFields, Value(ref i, inline), name == "--form", result, seen); break;
                    case "--url": url = Value(ref i, inline); break;
                    case "--head": head = true; break;
                    case "--get": get = true; break;
                    case "--insecure": result.IgnoreTlsErrors = true; break;
                    default:
                        if (SilentWithValue.Contains(name))
                        {
                            Value(ref i, inline);
                        }
                        else if (WarnWithValue.Contains(name))
                        {
                            Value(ref i, inline);
                            Note(name);
                        }

                        // Anything else is treated as a flag without a value (--silent, --compressed, --http1.1 ...).
                        break;
                }

                continue;
            }

            // Short options may be bundled (-sSL) and may carry their value directly (-XPOST, -H'Accept: x').
            for (var c = 1; c < arg.Length; c++)
            {
                var option = arg[c];
                var inline = arg[(c + 1)..];
                switch (option)
                {
                    case 'X': method = Value(ref i, inline); c = arg.Length; break;
                    case 'H': AddHeader(headers, Value(ref i, inline), result); c = arg.Length; break;
                    case 'd': dataParts.Add(("data", Value(ref i, inline) ?? string.Empty)); c = arg.Length; break;
                    case 'u': user = Value(ref i, inline); c = arg.Length; break;
                    case 'b': cookie = Value(ref i, inline); c = arg.Length; break;
                    case 'A': userAgent = Value(ref i, inline); c = arg.Length; break;
                    case 'e': referer = Value(ref i, inline); c = arg.Length; break;
                    case 'F': AddForm(formFields, Value(ref i, inline), true, result, seen); c = arg.Length; break;
                    case 'I': head = true; break;
                    case 'G': get = true; break;
                    case 'k': result.IgnoreTlsErrors = true; break;
                    case 'x' or 'E':
                        Value(ref i, inline);
                        Note("-" + option);
                        c = arg.Length;
                        break;
                    case 'o' or 'O' or 'w' or 'D' or 'c' or 'C' or 'K' or 'm' or 'r' or 't' or 'z' or 'P' or 'Q' or 'U' or 'y' or 'Y':
                        Value(ref i, inline);
                        c = arg.Length;
                        break;
                    case 'T':
                        Value(ref i, inline);
                        Note("-T");
                        c = arg.Length;
                        break;
                }
            }
        }

        if (url is null)
        {
            result.Warnings.Add("A curl command without a URL was skipped.");
            return null;
        }

        var request = new RequestDefinition();
        foreach (var (name, value) in headers)
        {
            ImportHelpers.AddHeader(request, name, value);
        }

        var hasContentType = headers.Any(h => h.Name.Trim().Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        var body = BuildBody(dataParts, formFields, request, hasContentType, json, result, seen);
        if (get && body is not null)
        {
            url = ImportHelpers.AppendQuery(url, body);
            body = null;
        }

        if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("{{", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        request.Url = url;
        request.Method = (method ?? (head ? "HEAD" : body is not null && !get ? "POST" : "GET")).ToUpperInvariant();
        request.Body = body;
        if (body is null)
        {
            request.ContentType = "application/json";
        }

        if (user is not null && !ImportHelpers.HasHeader(request, "Authorization"))
        {
            var colon = user.IndexOf(':');
            var credentials = colon < 0 ? user + ":" : user;
            request.Headers.Add(new HeaderEntry { Name = "Authorization", Value = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)) });
        }

        if (bearer is not null && !ImportHelpers.HasHeader(request, "Authorization"))
        {
            request.Headers.Add(new HeaderEntry { Name = "Authorization", Value = "Bearer " + bearer });
        }

        if (cookie is not null && cookie.Contains('=') && !ImportHelpers.HasHeader(request, "Cookie"))
        {
            request.Headers.Add(new HeaderEntry { Name = "Cookie", Value = cookie });
        }
        else if (cookie is not null && !cookie.Contains('='))
        {
            result.Warnings.Add($"The cookie file '{cookie}' was not read; paste the cookies as -H 'Cookie: name=value' instead.");
        }

        if (userAgent is not null && !ImportHelpers.HasHeader(request, "User-Agent"))
        {
            request.Headers.Add(new HeaderEntry { Name = "User-Agent", Value = userAgent });
        }

        if (referer is not null && !ImportHelpers.HasHeader(request, "Referer"))
        {
            request.Headers.Add(new HeaderEntry { Name = "Referer", Value = referer });
        }

        request.Name = ImportHelpers.NameFor(request.Method, request.Url);
        return request;
    }

    private static void AddHeader(List<(string Name, string Value)> headers, string? header, ImportResult result)
    {
        if (header is null)
        {
            return;
        }

        var colon = header.IndexOf(':');
        if (colon <= 0)
        {
            // "Name;" sends an empty header and "Name:" removes one; neither can be shown as a header row.
            if (!header.EndsWith(';') && !header.EndsWith(':'))
            {
                result.Warnings.Add($"The header '{header}' has no colon and was skipped.");
            }

            return;
        }

        var value = header[(colon + 1)..].Trim();
        if (value.Length > 0)
        {
            headers.Add((header[..colon], value));
        }
    }

    private static void AddForm(List<(string Name, string Value)> fields, string? field, bool allowFiles, ImportResult result, HashSet<string> seen)
    {
        var equals = field?.IndexOf('=') ?? -1;
        if (field is null || equals <= 0)
        {
            return;
        }

        var value = field[(equals + 1)..];
        if (allowFiles && (value.StartsWith('@') || value.StartsWith('<')))
        {
            if (seen.Add("form-file:" + field))
            {
                result.Warnings.Add($"The form field '{field[..equals]}' uploads a file, which falcon-load can't do, so it was left out.");
            }

            return;
        }

        fields.Add((field[..equals], value));
    }

    private static string? BuildBody(List<(string Kind, string Value)> dataParts, List<(string Name, string Value)> formFields, RequestDefinition request, bool hasContentType, bool json, ImportResult result, HashSet<string> seen)
    {
        if (formFields.Count > 0)
        {
            var (multipart, contentType) = ImportHelpers.MultipartBody(formFields);
            request.ContentType = contentType;
            return multipart;
        }

        var parts = new List<string>();
        foreach (var (kind, value) in dataParts)
        {
            if (kind is "data" or "binary" && value.StartsWith('@'))
            {
                if (seen.Add("data-file:" + value))
                {
                    result.Warnings.Add($"The body is read from '{value[1..]}', which falcon-load can't do, so that part was left out.");
                }

                continue;
            }

            parts.Add(kind == "urlencode" ? EncodeData(value) : value);
        }

        if (parts.Count == 0)
        {
            return null;
        }

        if (!hasContentType)
        {
            request.ContentType = json ? "application/json" : ImportHelpers.FormContentType;
        }

        if (json && !ImportHelpers.HasHeader(request, "Accept"))
        {
            request.Headers.Add(new HeaderEntry { Name = "Accept", Value = "application/json" });
        }

        return string.Join('&', parts);
    }

    // curl's --data-urlencode: "name=content" encodes the content, "=content" and "content" encode everything.
    private static string EncodeData(string value)
    {
        var equals = value.IndexOf('=');
        if (equals < 0)
        {
            return ImportHelpers.Encode(value);
        }

        return equals == 0 ? ImportHelpers.Encode(value[1..]) : $"{value[..equals]}={ImportHelpers.Encode(value[(equals + 1)..])}";
    }

    /// <summary>
    /// Splits shell text into commands made of words, handling quotes, backslash line continuations, <c>$'...'</c> and
    /// comments. Newlines, <c>;</c>, <c>&amp;</c> and <c>|</c> outside quotes end a command.
    /// </summary>
    internal static List<List<string>> Tokenize(string text)
    {
        var commands = new List<List<string>>();
        var current = new List<string>();
        var word = new StringBuilder();
        var inWord = false;

        void EndWord()
        {
            if (inWord)
            {
                current.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            EndWord();
            if (current.Count > 0)
            {
                commands.Add(current);
                current = [];
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '\\' when i + 1 < text.Length && text[i + 1] == '\n':
                    i += 2;
                    break;
                case '\\' when i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n':
                    i += 3;
                    break;
                case '\\':
                    if (i + 1 < text.Length)
                    {
                        word.Append(text[i + 1]);
                        inWord = true;
                    }

                    i += 2;
                    break;
                case '\'':
                    inWord = true;
                    i++;
                    while (i < text.Length && text[i] != '\'')
                    {
                        word.Append(text[i++]);
                    }

                    i++;
                    break;
                case '"':
                    inWord = true;
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '\n')
                        {
                            i += 2;
                        }
                        else if (text[i] == '\\' && i + 1 < text.Length && "\"\\$`".Contains(text[i + 1]))
                        {
                            word.Append(text[i + 1]);
                            i += 2;
                        }
                        else
                        {
                            word.Append(text[i++]);
                        }
                    }

                    i++;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '\'':
                    inWord = true;
                    i = ReadAnsiC(text, i + 2, word);
                    break;
                case '#' when !inWord:
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    break;
                case '\n' or ';' or '&' or '|':
                    EndCommand();
                    i++;
                    break;
                case ' ' or '\t' or '\r':
                    EndWord();
                    i++;
                    break;
                default:
                    word.Append(c);
                    inWord = true;
                    i++;
                    break;
            }
        }

        EndCommand();
        return commands;
    }

    // $'...' strings, which browsers use when a value has quotes or non-ASCII characters.
    private static int ReadAnsiC(string text, int i, StringBuilder word)
    {
        while (i < text.Length && text[i] != '\'')
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                word.Append(text[i++]);
                continue;
            }

            var escape = text[i + 1];
            i += 2;
            switch (escape)
            {
                case 'n': word.Append('\n'); break;
                case 't': word.Append('\t'); break;
                case 'r': word.Append('\r'); break;
                case 'a': word.Append('\a'); break;
                case 'b': word.Append('\b'); break;
                case 'f': word.Append('\f'); break;
                case 'v': word.Append('\v'); break;
                case 'e' or 'E': word.Append('\u001b'); break;
                case 'x' or 'u' or 'U':
                {
                    var max = escape == 'x' ? 2 : escape == 'u' ? 4 : 8;
                    var digits = 0;
                    while (digits < max && i + digits < text.Length && Uri.IsHexDigit(text[i + digits]))
                    {
                        digits++;
                    }

                    if (digits > 0)
                    {
                        var code = int.Parse(text.AsSpan(i, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        word.Append(escape == 'x' ? ((char)code).ToString() : char.ConvertFromUtf32(code));
                        i += digits;
                    }

                    break;
                }

                default: word.Append(escape); break;
            }
        }

        return i + 1;
    }
}
