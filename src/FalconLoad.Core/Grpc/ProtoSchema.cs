using System.Globalization;
using System.Text;

namespace FalconLoad.Core.Grpc;

/// <summary>The scalar and composite types a protobuf field can have.</summary>
public enum FieldType
{
    Double, Float, Int32, Int64, UInt32, UInt64, SInt32, SInt64, Fixed32, Fixed64, SFixed32, SFixed64, Bool, String, Bytes, Message, Enum,
}

public sealed class FieldDescriptor
{
    public required string Name { get; init; }

    /// <summary>The lowerCamelCase name the proto3 JSON mapping uses, or the <c>json_name</c> option.</summary>
    public required string JsonName { get; init; }

    public required int Number { get; init; }

    public FieldType Type { get; internal set; }

    /// <summary>The message or enum type's name as written, resolved into <see cref="Message"/> or <see cref="Enum"/> once every file is read.</summary>
    public string? TypeName { get; init; }

    public bool Repeated { get; init; }

    /// <summary>
    /// Whether a repeated field is written packed: numeric, bool and enum arrays are, by default in proto3 or with
    /// <c>[packed = true]</c> in proto2. Both forms are always read.
    /// </summary>
    public bool Packed => Repeated && Type is not (FieldType.String or FieldType.Bytes or FieldType.Message) && (PackedOption ?? Proto3);

    internal bool? PackedOption { get; init; }

    internal bool Proto3 { get; init; } = true;

    /// <summary>A map field is a repeated entry message whose fields 1 and 2 are the key and the value.</summary>
    public bool IsMap { get; init; }

    public MessageDescriptor? Message { get; internal set; }

    public EnumDescriptor? Enum { get; internal set; }

    /// <summary>Whether values live in a length-delimited record on the wire: strings, bytes, messages and packed arrays.</summary>
    public bool LengthDelimited => Type is FieldType.String or FieldType.Bytes or FieldType.Message;
}

public sealed class MessageDescriptor
{
    private readonly Dictionary<int, FieldDescriptor> _byNumber = new();
    private readonly Dictionary<string, FieldDescriptor> _byName = new(StringComparer.Ordinal);

    public required string FullName { get; init; }

    public List<FieldDescriptor> Fields { get; } = [];

    /// <summary>Whether the file that defines it is proto3, which decides packing and the meaning of unset fields.</summary>
    public bool Proto3 { get; init; } = true;

    internal void Add(FieldDescriptor field)
    {
        Fields.Add(field);
        _byNumber[field.Number] = field;
        _byName[field.Name] = field;
        _byName.TryAdd(field.JsonName, field);
    }

    public FieldDescriptor? Find(int number) => _byNumber.GetValueOrDefault(number);

    /// <summary>Finds a field by its proto name or its JSON name.</summary>
    public FieldDescriptor? Find(string name) => _byName.GetValueOrDefault(name);
}

public sealed class EnumDescriptor
{
    public required string FullName { get; init; }

    public Dictionary<string, int> ByName { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, string> ByNumber { get; } = new();
}

public sealed class MethodDescriptor
{
    public required string Name { get; init; }

    public required string ServiceName { get; init; }

    /// <summary>The request path, <c>package.Service/Method</c>, which is also how a request names the method.</summary>
    public string Path => $"{ServiceName}/{Name}";

    public required string InputTypeName { get; init; }

    public required string OutputTypeName { get; init; }

    public MessageDescriptor Input { get; internal set; } = null!;

    public MessageDescriptor Output { get; internal set; } = null!;

    public bool ClientStreaming { get; init; }

    public bool ServerStreaming { get; init; }
}

/// <summary>A <c>.proto</c> file given to a test, by name so other files can import it.</summary>
public sealed class ProtoFile
{
    public string Name { get; set; } = "service.proto";

    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// The messages, enums and services of a set of <c>.proto</c> files, read by a small parser that understands the
/// language as ordinary service definitions use it: proto2 and proto3 syntax, packages, imports between the given
/// files, nested messages and enums, oneofs, maps, repeated and optional fields, reserved ranges, options (ignored) and
/// unary or streaming rpcs. Extensions, groups and custom option syntax are skipped. The well-known
/// <c>google/protobuf/*.proto</c> imports are built in and treated as plain messages, so a <c>Timestamp</c> is
/// written as <c>{ "seconds": …, "nanos": … }</c>.
/// </summary>
public sealed class ProtoSchema
{
    private readonly Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescriptor> _enums = new(StringComparer.Ordinal);
    private readonly List<MethodDescriptor> _methods = [];

    private ProtoSchema()
    {
    }

    public IReadOnlyCollection<MessageDescriptor> Messages => _messages.Values;

    public IReadOnlyList<MethodDescriptor> Methods => _methods;

    /// <summary>Finds a method by <c>package.Service/Method</c>, or by <c>Service/Method</c> when that is unambiguous.</summary>
    public MethodDescriptor? FindMethod(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = path.Trim().TrimStart('/');
        var exact = _methods.FirstOrDefault(m => m.Path == path);
        if (exact is not null)
        {
            return exact;
        }

        var matches = _methods.Where(m => m.Path.EndsWith("." + path, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public MessageDescriptor? FindMessage(string fullName) => _messages.GetValueOrDefault(fullName.TrimStart('.'));

    /// <summary>Reads the files. Returns false with the problems when they don't parse or refer to types that aren't defined.</summary>
    public static bool TryParse(IReadOnlyList<ProtoFile> files, out ProtoSchema? schema, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var result = new ProtoSchema();
        var pending = new List<(FieldDescriptor Field, string Scope, string File)>();
        var pendingMethods = new List<(MethodDescriptor Method, string Scope, string File)>();
        var parsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ProtoFile>(files.Where(f => !string.IsNullOrWhiteSpace(f.Content)));
        var byName = files.ToDictionary(f => f.Name.Trim(), f => f, StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!parsed.Add(file.Name.Trim()))
            {
                continue;
            }

            try
            {
                var parser = new Parser(file.Name, file.Content, result, pending, pendingMethods);
                foreach (var import in parser.ParseFile())
                {
                    if (byName.TryGetValue(import, out var imported))
                    {
                        queue.Enqueue(imported);
                    }
                    else if (WellKnown.TryGet(import, out var builtIn))
                    {
                        queue.Enqueue(builtIn);
                    }
                    else
                    {
                        problems.Add($"{file.Name}: imports '{import}', which isn't one of the test's proto files. Add it, or remove the import if its types aren't used.");
                    }
                }
            }
            catch (ProtoParseException ex)
            {
                problems.Add($"{file.Name}: {ex.Message}");
            }
        }

        foreach (var (field, scope, fileName) in pending)
        {
            if (result.Resolve(field.TypeName!, scope) is { } message)
            {
                field.Type = FieldType.Message;
                field.Message = message;
            }
            else if (result.ResolveEnum(field.TypeName!, scope) is { } @enum)
            {
                field.Type = FieldType.Enum;
                field.Enum = @enum;
            }
            else
            {
                problems.Add($"{fileName}: the type '{field.TypeName}' of field '{field.Name}' isn't defined in the proto files.");
            }
        }

        foreach (var (method, scope, fileName) in pendingMethods)
        {
            method.Input = result.Resolve(method.InputTypeName, scope)!;
            method.Output = result.Resolve(method.OutputTypeName, scope)!;
            if (method.Input is null)
            {
                problems.Add($"{fileName}: the request type '{method.InputTypeName}' of rpc {method.Path} isn't defined in the proto files.");
            }

            if (method.Output is null)
            {
                problems.Add($"{fileName}: the response type '{method.OutputTypeName}' of rpc {method.Path} isn't defined in the proto files.");
            }
        }

        if (problems.Count == 0 && result._methods.Count == 0)
        {
            problems.Add("The proto files define no service, so there is no rpc to call.");
        }

        errors = problems;
        schema = problems.Count == 0 ? result : null;
        return problems.Count == 0;
    }

    // A type name is looked up from the innermost scope outwards, as protoc does; a leading dot makes it absolute.
    private MessageDescriptor? Resolve(string name, string scope)
    {
        foreach (var candidate in Candidates(name, scope))
        {
            if (_messages.TryGetValue(candidate, out var found))
            {
                return found;
            }
        }

        return null;
    }

    private EnumDescriptor? ResolveEnum(string name, string scope)
    {
        foreach (var candidate in Candidates(name, scope))
        {
            if (_enums.TryGetValue(candidate, out var found))
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string name, string scope)
    {
        if (name.StartsWith('.'))
        {
            yield return name[1..];
            yield break;
        }

        while (true)
        {
            yield return scope.Length == 0 ? name : $"{scope}.{name}";
            if (scope.Length == 0)
            {
                yield break;
            }

            var dot = scope.LastIndexOf('.');
            scope = dot < 0 ? string.Empty : scope[..dot];
        }
    }

    internal static string ToJsonName(string name)
    {
        var builder = new StringBuilder(name.Length);
        var upper = false;
        foreach (var c in name)
        {
            if (c == '_')
            {
                upper = true;
            }
            else
            {
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
        }

        return builder.ToString();
    }

    private sealed class ProtoParseException(string message) : Exception(message);

    /// <summary>A recursive-descent reader over the tokens of one file.</summary>
    private sealed class Parser(string fileName, string text, ProtoSchema schema, List<(FieldDescriptor, string, string)> pending, List<(MethodDescriptor, string, string)> pendingMethods)
    {
        private static readonly Dictionary<string, FieldType> Scalars = new(StringComparer.Ordinal)
        {
            ["double"] = FieldType.Double, ["float"] = FieldType.Float, ["int32"] = FieldType.Int32, ["int64"] = FieldType.Int64,
            ["uint32"] = FieldType.UInt32, ["uint64"] = FieldType.UInt64, ["sint32"] = FieldType.SInt32, ["sint64"] = FieldType.SInt64,
            ["fixed32"] = FieldType.Fixed32, ["fixed64"] = FieldType.Fixed64, ["sfixed32"] = FieldType.SFixed32, ["sfixed64"] = FieldType.SFixed64,
            ["bool"] = FieldType.Bool, ["string"] = FieldType.String, ["bytes"] = FieldType.Bytes,
        };

        private readonly List<string> _tokens = Tokenize(text);
        private int _position;
        private string _package = string.Empty;
        private bool _proto3 = true;

        public List<string> ParseFile()
        {
            var imports = new List<string>();
            while (!AtEnd)
            {
                var token = Next();
                switch (token)
                {
                    case ";":
                        break;
                    case "syntax":
                    case "edition":
                        Expect("=");
                        var syntax = Unquote(Next());
                        _proto3 = syntax != "proto2";
                        Expect(";");
                        break;
                    case "package":
                        _package = Next();
                        Expect(";");
                        break;
                    case "import":
                        if (Peek() is "public" or "weak")
                        {
                            Next();
                        }

                        imports.Add(Unquote(Next()));
                        Expect(";");
                        break;
                    case "option":
                        SkipStatement();
                        break;
                    case "message":
                        ParseMessage(_package);
                        break;
                    case "enum":
                        ParseEnum(_package);
                        break;
                    case "service":
                        ParseService();
                        break;
                    case "extend":
                        Next();
                        SkipBlock();
                        break;
                    default:
                        throw new ProtoParseException($"unexpected '{token}' at the top level.");
                }
            }

            return imports;
        }

        private void ParseMessage(string scope)
        {
            var name = Next();
            var fullName = scope.Length == 0 ? name : $"{scope}.{name}";
            var message = new MessageDescriptor { FullName = fullName, Proto3 = _proto3 };
            if (!schema._messages.TryAdd(fullName, message))
            {
                throw new ProtoParseException($"the message '{fullName}' is defined twice.");
            }

            Expect("{");
            while (Peek() != "}")
            {
                var token = Next();
                switch (token)
                {
                    case ";":
                        break;
                    case "message":
                        ParseMessage(fullName);
                        break;
                    case "enum":
                        ParseEnum(fullName);
                        break;
                    case "oneof":
                        Next();
                        Expect("{");
                        while (Peek() != "}")
                        {
                            if (Peek() == "option")
                            {
                                Next();
                                SkipStatement();
                            }
                            else
                            {
                                ParseField(message, fullName, Next());
                            }
                        }

                        Expect("}");
                        break;
                    case "option":
                    case "reserved":
                    case "extensions":
                        SkipStatement();
                        break;
                    case "extend":
                        Next();
                        SkipBlock();
                        break;
                    case "map":
                        ParseMap(message, fullName);
                        break;
                    default:
                        ParseField(message, fullName, token);
                        break;
                }
            }

            Expect("}");
        }

        private void ParseField(MessageDescriptor message, string scope, string first)
        {
            var repeated = false;
            var typeToken = first;
            while (typeToken is "repeated" or "optional" or "required")
            {
                repeated |= typeToken == "repeated";
                typeToken = Next();
            }

            if (typeToken == "group")
            {
                throw new ProtoParseException("groups aren't supported.");
            }

            var name = Next();
            Expect("=");
            var number = ParseInt(Next());
            var (jsonName, packed) = ParseFieldOptions();
            Expect(";");

            var scalar = Scalars.TryGetValue(typeToken, out var type);
            var field = new FieldDescriptor
            {
                Name = name,
                JsonName = jsonName ?? ToJsonName(name),
                Number = number,
                Type = scalar ? type : FieldType.Message,
                TypeName = scalar ? null : typeToken,
                Repeated = repeated,
                PackedOption = packed,
                Proto3 = _proto3,
            };
            message.Add(field);
            if (!scalar)
            {
                pending.Add((field, scope, fileName));
            }
        }

        private void ParseMap(MessageDescriptor message, string scope)
        {
            Expect("<");
            var keyType = Next();
            Expect(",");
            var valueType = Next();
            Expect(">");
            var name = Next();
            Expect("=");
            var number = ParseInt(Next());
            var (jsonName, _) = ParseFieldOptions();
            Expect(";");

            if (!Scalars.TryGetValue(keyType, out var keyScalar) || keyScalar is FieldType.Double or FieldType.Float or FieldType.Bytes)
            {
                throw new ProtoParseException($"map field '{name}' has a key type '{keyType}' that a map can't have.");
            }

            var entry = new MessageDescriptor { FullName = $"{scope}.{name}Entry", Proto3 = _proto3 };
            entry.Add(new FieldDescriptor { Name = "key", JsonName = "key", Number = 1, Type = keyScalar });
            var valueScalar = Scalars.TryGetValue(valueType, out var valueFieldType);
            var value = new FieldDescriptor { Name = "value", JsonName = "value", Number = 2, Type = valueScalar ? valueFieldType : FieldType.Message, TypeName = valueScalar ? null : valueType };
            entry.Add(value);
            if (!valueScalar)
            {
                pending.Add((value, scope, fileName));
            }

            message.Add(new FieldDescriptor
            {
                Name = name,
                JsonName = jsonName ?? ToJsonName(name),
                Number = number,
                Type = FieldType.Message,
                Repeated = true,
                IsMap = true,
                Message = entry,
            });
        }

        private (string? JsonName, bool? Packed) ParseFieldOptions()
        {
            string? jsonName = null;
            bool? packed = null;
            if (Peek() != "[")
            {
                return (null, null);
            }

            Next();
            while (true)
            {
                var option = Next();
                Expect("=");
                var value = ReadOptionValue();
                if (option == "json_name")
                {
                    jsonName = Unquote(value);
                }
                else if (option == "packed")
                {
                    packed = value == "true";
                }

                if (Next() == "]")
                {
                    return (jsonName, packed);
                }
            }
        }

        private string ReadOptionValue()
        {
            var value = Next();
            if (value == "{")
            {
                var depth = 1;
                while (depth > 0)
                {
                    var t = Next();
                    depth += t == "{" ? 1 : t == "}" ? -1 : 0;
                }

                return "{}";
            }

            return value == "-" ? "-" + Next() : value;
        }

        private void ParseEnum(string scope)
        {
            var name = Next();
            var fullName = scope.Length == 0 ? name : $"{scope}.{name}";
            var @enum = new EnumDescriptor { FullName = fullName };
            if (!schema._enums.TryAdd(fullName, @enum))
            {
                throw new ProtoParseException($"the enum '{fullName}' is defined twice.");
            }

            Expect("{");
            while (Peek() != "}")
            {
                var token = Next();
                if (token is ";")
                {
                    continue;
                }

                if (token is "option" or "reserved")
                {
                    SkipStatement();
                    continue;
                }

                Expect("=");
                var numberToken = Next();
                var number = ParseInt(numberToken == "-" ? "-" + Next() : numberToken);
                if (Peek() == "[")
                {
                    ParseFieldOptions();
                }

                Expect(";");
                @enum.ByName[token] = number;
                @enum.ByNumber.TryAdd(number, token);
            }

            Expect("}");
        }

        private void ParseService()
        {
            var name = Next();
            var fullName = _package.Length == 0 ? name : $"{_package}.{name}";
            Expect("{");
            while (Peek() != "}")
            {
                var token = Next();
                if (token is ";")
                {
                    continue;
                }

                if (token == "option")
                {
                    SkipStatement();
                    continue;
                }

                if (token != "rpc")
                {
                    throw new ProtoParseException($"unexpected '{token}' in service {name}.");
                }

                var methodName = Next();
                Expect("(");
                var clientStreaming = Peek() == "stream";
                if (clientStreaming)
                {
                    Next();
                }

                var input = Next();
                Expect(")");
                Expect("returns");
                Expect("(");
                var serverStreaming = Peek() == "stream";
                if (serverStreaming)
                {
                    Next();
                }

                var output = Next();
                Expect(")");
                if (Peek() == "{")
                {
                    SkipBlock();
                }
                else
                {
                    Expect(";");
                }

                var method = new MethodDescriptor
                {
                    Name = methodName,
                    ServiceName = fullName,
                    InputTypeName = input,
                    OutputTypeName = output,
                    ClientStreaming = clientStreaming,
                    ServerStreaming = serverStreaming,
                };
                schema._methods.Add(method);
                pendingMethods.Add((method, _package, fileName));
            }

            Expect("}");
        }

        private void SkipStatement()
        {
            while (true)
            {
                var token = Next();
                if (token == ";")
                {
                    return;
                }

                if (token == "{")
                {
                    _position--;
                    SkipBlock();
                    if (Peek() == ";")
                    {
                        Next();
                    }

                    return;
                }
            }
        }

        private void SkipBlock()
        {
            Expect("{");
            var depth = 1;
            while (depth > 0)
            {
                var token = Next();
                depth += token == "{" ? 1 : token == "}" ? -1 : 0;
            }
        }

        private bool AtEnd => _position >= _tokens.Count;

        private string Peek() => AtEnd ? throw new ProtoParseException("the file ends unexpectedly.") : _tokens[_position];

        private string Next() => AtEnd ? throw new ProtoParseException("the file ends unexpectedly.") : _tokens[_position++];

        private void Expect(string token)
        {
            var actual = Next();
            if (actual != token)
            {
                throw new ProtoParseException($"expected '{token}' but found '{actual}'.");
            }
        }

        private static int ParseInt(string token)
        {
            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            {
                return hex;
            }

            return int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new ProtoParseException($"expected a number but found '{token}'.");
        }

        private static string Unquote(string token) =>
            token.Length >= 2 && (token[0] == '"' || token[0] == '\'') ? token[1..^1] : throw new ProtoParseException($"expected a quoted string but found '{token}'.");

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
                else if (c is '"' or '\'')
                {
                    var start = i++;
                    while (i < text.Length && text[i] != c)
                    {
                        i += text[i] == '\\' ? 2 : 1;
                    }

                    i = Math.Min(i + 1, text.Length);
                    tokens.Add(text[start..i]);
                }
                else if (char.IsLetterOrDigit(c) || c is '_' or '.')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                    {
                        i++;
                    }

                    tokens.Add(text[start..i]);
                }
                else
                {
                    tokens.Add(c.ToString());
                    i++;
                }
            }

            return tokens;
        }
    }
}
