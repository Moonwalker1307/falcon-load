using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FalconLoad.Core.Grpc;

/// <summary>Thrown when a JSON message can't be turned into protobuf bytes, or protobuf bytes can't be read with the schema.</summary>
public sealed class ProtobufException(string message) : Exception(message);

/// <summary>
/// Converts between the proto3 JSON form of a message, which is what a test edits and what checks and extractions
/// read, and the protobuf wire format that goes over gRPC. Fields are named by their JSON name or their proto name,
/// enums by name or number, bytes as base64, and 64-bit integers are accepted as numbers or strings and written as
/// numbers. Timestamps, durations and the wrapper types use their JSON forms (<c>"2026-10-31T12:00:00Z"</c>,
/// <c>"1.5s"</c>, a bare value) and also accept the plain object. Unknown fields on the wire are skipped, and unknown names in the JSON are an error, since they would mean a
/// test silently sends less than it says.
/// </summary>
public static class ProtobufCodec
{
    private const int MaxDepth = 64;

    /// <summary>Encodes the JSON object <paramref name="json"/> as a <paramref name="message"/>.</summary>
    public static byte[] Encode(MessageDescriptor message, ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ProtobufException("The message has to be a JSON object.");
            }

            var writer = new ArrayBufferWriter<byte>();
            WriteMessage(writer, message, document.RootElement, 0);
            return writer.WrittenSpan.ToArray();
        }
        catch (Exception ex) when (Model.JsonErrors.IsInvalidJson(ex))
        {
            throw new ProtobufException($"The message isn't valid JSON: {ex.Message}");
        }
    }

    public static byte[] Encode(MessageDescriptor message, string json) => Encode(message, Encoding.UTF8.GetBytes(json));

    /// <summary>Decodes <paramref name="bytes"/> as a <paramref name="message"/> and writes it as JSON into <paramref name="output"/>.</summary>
    public static void Decode(MessageDescriptor message, ReadOnlySpan<byte> bytes, IBufferWriter<byte> output)
    {
        var parsed = ReadMessage(message, bytes, 0);
        using var writer = new Utf8JsonWriter(output);
        WriteJson(writer, message, parsed);
        writer.Flush();
    }

    public static string DecodeToString(MessageDescriptor message, ReadOnlySpan<byte> bytes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Decode(message, bytes, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ---- JSON to wire ----

    private static void WriteMessage(IBufferWriter<byte> writer, MessageDescriptor message, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ProtobufException("The message is nested too deeply.");
        }

        foreach (var property in element.EnumerateObject())
        {
            var field = message.Find(property.Name)
                ?? throw new ProtobufException($"'{property.Name}' isn't a field of {message.FullName}.");
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (field.IsMap)
            {
                WriteMap(writer, field, property.Value, depth);
            }
            else if (field.Repeated)
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    throw new ProtobufException($"'{property.Name}' is repeated, so it needs a JSON array.");
                }

                WriteRepeated(writer, field, property.Value, depth);
            }
            else
            {
                WriteField(writer, field, property.Value, depth);
            }
        }
    }

    private static void WriteMap(IBufferWriter<byte> writer, FieldDescriptor field, JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ProtobufException($"'{field.Name}' is a map, so it needs a JSON object.");
        }

        var entry = field.Message!;
        var key = entry.Find(1)!;
        var value = entry.Find(2)!;
        foreach (var property in element.EnumerateObject())
        {
            var inner = new ArrayBufferWriter<byte>();
            WriteScalar(inner, key, JsonDocument.Parse(JsonSerializer.Serialize(property.Name)).RootElement, allowStringNumbers: true);
            if (property.Value.ValueKind != JsonValueKind.Null)
            {
                WriteField(inner, value, property.Value, depth + 1);
            }

            WriteTag(writer, field.Number, 2);
            WriteVarint(writer, (ulong)inner.WrittenCount);
            writer.Write(inner.WrittenSpan);
        }
    }

    private static void WriteRepeated(IBufferWriter<byte> writer, FieldDescriptor field, JsonElement array, int depth)
    {
        if (field.Packed)
        {
            var packed = new ArrayBufferWriter<byte>();
            foreach (var item in array.EnumerateArray())
            {
                WriteScalarValue(packed, field, item);
            }

            if (packed.WrittenCount > 0)
            {
                WriteTag(writer, field.Number, 2);
                WriteVarint(writer, (ulong)packed.WrittenCount);
                writer.Write(packed.WrittenSpan);
            }

            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            WriteField(writer, field, item, depth);
        }
    }

    private static void WriteField(IBufferWriter<byte> writer, FieldDescriptor field, JsonElement value, int depth)
    {
        switch (field.Type)
        {
            case FieldType.Message:
                if (value.ValueKind != JsonValueKind.Object)
                {
                    value = FromSpecialJson(field, value);
                }

                var inner = new ArrayBufferWriter<byte>();
                WriteMessage(inner, field.Message!, value, depth + 1);
                WriteTag(writer, field.Number, 2);
                WriteVarint(writer, (ulong)inner.WrittenCount);
                writer.Write(inner.WrittenSpan);
                break;
            case FieldType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new ProtobufException($"'{field.Name}' is a string, so it needs a JSON string.");
                }

                WriteTag(writer, field.Number, 2);
                var text = value.GetString()!;
                WriteVarint(writer, (ulong)Encoding.UTF8.GetByteCount(text));
                writer.Write(Encoding.UTF8.GetBytes(text));
                break;
            case FieldType.Bytes:
                if (value.ValueKind != JsonValueKind.String || !TryBase64(value.GetString()!, out var bytes))
                {
                    throw new ProtobufException($"'{field.Name}' is bytes, so it needs a base64 string.");
                }

                WriteTag(writer, field.Number, 2);
                WriteVarint(writer, (ulong)bytes.Length);
                writer.Write(bytes);
                break;
            default:
                WriteTag(writer, field.Number, WireTypeOf(field.Type));
                WriteScalarValue(writer, field, value);
                break;
        }
    }

    private const string TimestampType = "google.protobuf.Timestamp";
    private const string DurationType = "google.protobuf.Duration";
    private const long NanosPerSecond = 1_000_000_000;

    private static bool IsWrapper(string type) =>
        type is "google.protobuf.DoubleValue" or "google.protobuf.FloatValue" or "google.protobuf.Int64Value" or "google.protobuf.UInt64Value"
            or "google.protobuf.Int32Value" or "google.protobuf.UInt32Value" or "google.protobuf.BoolValue" or "google.protobuf.StringValue"
            or "google.protobuf.BytesValue";

    // Turns the JSON form of a well-known type, such as a timestamp's RFC 3339 string, into the object it stands for.
    private static JsonElement FromSpecialJson(FieldDescriptor field, JsonElement value)
    {
        var type = field.Message!.FullName;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
        string? json = type switch
        {
            TimestampType when text is not null && TryParseTimestamp(text, out var seconds, out var nanos) => $"{{\"seconds\":{seconds},\"nanos\":{nanos}}}",
            TimestampType => throw new ProtobufException($"'{field.Name}' is a timestamp, so it needs an RFC 3339 time such as \"2026-10-31T12:00:00Z\"."),
            DurationType when text is not null && TryParseDuration(text, out var seconds, out var nanos) => $"{{\"seconds\":{seconds},\"nanos\":{nanos}}}",
            DurationType => throw new ProtobufException($"'{field.Name}' is a duration, so it needs seconds with an s, such as \"1.5s\"."),
            _ when IsWrapper(type) => $"{{\"value\":{value.GetRawText()}}}",
            _ => null,
        };

        if (json is null)
        {
            throw new ProtobufException($"'{field.Name}' is a {type}, so it needs a JSON object.");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal static bool TryParseTimestamp(string text, out long seconds, out long nanos)
    {
        (seconds, nanos) = (0, 0);
        if (text.Length < 20)
        {
            return false;
        }

        // DateTimeOffset reads at most seven fractional digits, and protobuf allows nine, so the fraction is read here.
        var fractionStart = text.IndexOf('.');
        var zoneStart = text.IndexOfAny(['Z', 'z', '+'], 19);
        if (zoneStart < 0)
        {
            zoneStart = text.LastIndexOf('-');
        }

        if (zoneStart < 19)
        {
            return false;
        }

        var fraction = string.Empty;
        if (fractionStart > 0 && fractionStart < zoneStart)
        {
            fraction = text[(fractionStart + 1)..zoneStart];
            if (fraction.Length is 0 or > 9 || !fraction.All(char.IsAsciiDigit))
            {
                return false;
            }

            text = text[..fractionStart] + text[zoneStart..];
        }

        if (!DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd't'HH:mm:ssK"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return false;
        }

        seconds = time.ToUnixTimeSeconds();
        nanos = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(9, '0'), CultureInfo.InvariantCulture);
        return true;
    }

    internal static bool TryParseDuration(string text, out long seconds, out long nanos)
    {
        (seconds, nanos) = (0, 0);
        if (!text.EndsWith('s') || !decimal.TryParse(text[..^1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total)
            || Math.Abs(total) > 315_576_000_000m)
        {
            return false;
        }

        // Seconds and nanos carry the same sign, so -1.5s is -1 seconds and -500000000 nanos.
        seconds = (long)decimal.Truncate(total);
        nanos = (long)decimal.Round((total - seconds) * NanosPerSecond);
        return true;
    }

    private static void WriteScalar(IBufferWriter<byte> writer, FieldDescriptor field, JsonElement value, bool allowStringNumbers)
    {
        if (field.Type == FieldType.String)
        {
            WriteField(writer, field, value, 0);
            return;
        }

        WriteTag(writer, field.Number, WireTypeOf(field.Type));
        WriteScalarValue(writer, field, value);
    }

    /// <summary>Writes a numeric, bool or enum value without its tag, as a packed array element or after a tag.</summary>
    private static void WriteScalarValue(IBufferWriter<byte> writer, FieldDescriptor field, JsonElement value)
    {
        switch (field.Type)
        {
            case FieldType.Bool:
                WriteVarint(writer, ReadBool(field, value) ? 1UL : 0UL);
                break;
            case FieldType.Enum:
                WriteVarint(writer, (ulong)(long)ReadEnum(field, value));
                break;
            case FieldType.Int32:
            case FieldType.Int64:
                WriteVarint(writer, (ulong)ReadInt64(field, value, field.Type == FieldType.Int32 ? int.MinValue : long.MinValue, field.Type == FieldType.Int32 ? int.MaxValue : long.MaxValue));
                break;
            case FieldType.UInt32:
            case FieldType.UInt64:
                WriteVarint(writer, ReadUInt64(field, value, field.Type == FieldType.UInt32 ? uint.MaxValue : ulong.MaxValue));
                break;
            case FieldType.SInt32:
                var s32 = ReadInt64(field, value, int.MinValue, int.MaxValue);
                WriteVarint(writer, (uint)(((int)s32 << 1) ^ ((int)s32 >> 31)));
                break;
            case FieldType.SInt64:
                var s64 = ReadInt64(field, value, long.MinValue, long.MaxValue);
                WriteVarint(writer, (ulong)((s64 << 1) ^ (s64 >> 63)));
                break;
            case FieldType.Fixed32:
                WriteFixed32(writer, (uint)ReadUInt64(field, value, uint.MaxValue));
                break;
            case FieldType.SFixed32:
                WriteFixed32(writer, (uint)(int)ReadInt64(field, value, int.MinValue, int.MaxValue));
                break;
            case FieldType.Fixed64:
                WriteFixed64(writer, ReadUInt64(field, value, ulong.MaxValue));
                break;
            case FieldType.SFixed64:
                WriteFixed64(writer, (ulong)ReadInt64(field, value, long.MinValue, long.MaxValue));
                break;
            case FieldType.Float:
                WriteFixed32(writer, BitConverter.SingleToUInt32Bits((float)ReadDouble(field, value)));
                break;
            case FieldType.Double:
                WriteFixed64(writer, BitConverter.DoubleToUInt64Bits(ReadDouble(field, value)));
                break;
            default:
                throw new ProtobufException($"'{field.Name}' can't be written as a scalar.");
        }
    }

    private static bool ReadBool(FieldDescriptor field, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
        _ => throw new ProtobufException($"'{field.Name}' is a bool, so it needs true or false."),
    };

    private static int ReadEnum(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (field.Enum!.ByName.TryGetValue(text, out var named))
            {
                return named;
            }

            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }

            throw new ProtobufException($"'{text}' isn't a value of {field.Enum.FullName}; use one of {string.Join(", ", field.Enum.ByName.Keys)}.");
        }

        throw new ProtobufException($"'{field.Name}' is a {field.Enum!.FullName}, so it needs a name or a number.");
    }

    private static long ReadInt64(FieldDescriptor field, JsonElement value, long min, long max)
    {
        long result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
        }
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && d == Math.Floor(d) && d >= min && d <= max)
        {
            result = (long)d;
        }
        else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out result))
        {
        }
        else
        {
            throw new ProtobufException($"'{field.Name}' is an integer, so it needs a whole number.");
        }

        return result >= min && result <= max ? result : throw new ProtobufException($"'{field.Name}' is out of range for {field.Type}.");
    }

    private static ulong ReadUInt64(FieldDescriptor field, JsonElement value, ulong max)
    {
        ulong result;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out result))
        {
        }
        else if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out result))
        {
        }
        else
        {
            throw new ProtobufException($"'{field.Name}' is an unsigned integer, so it needs a whole number of 0 or more.");
        }

        return result <= max ? result : throw new ProtobufException($"'{field.Name}' is out of range for {field.Type}.");
    }

    private static double ReadDouble(FieldDescriptor field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text == "NaN")
            {
                return double.NaN;
            }

            if (text is "Infinity" or "-Infinity")
            {
                return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw new ProtobufException($"'{field.Name}' is a number.");
    }

    private static bool TryBase64(string text, out byte[] bytes)
    {
        var normalized = text.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
        var buffer = new byte[normalized.Length];
        if (Convert.TryFromBase64String(normalized, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }

    private static int WireTypeOf(FieldType type) => type switch
    {
        FieldType.Fixed64 or FieldType.SFixed64 or FieldType.Double => 1,
        FieldType.Fixed32 or FieldType.SFixed32 or FieldType.Float => 5,
        FieldType.String or FieldType.Bytes or FieldType.Message => 2,
        _ => 0,
    };

    private static void WriteTag(IBufferWriter<byte> writer, int number, int wireType) => WriteVarint(writer, ((ulong)number << 3) | (uint)wireType);

    private static void WriteVarint(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i++] = (byte)value;
        writer.Advance(i);
    }

    private static void WriteFixed32(IBufferWriter<byte> writer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    private static void WriteFixed64(IBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    // ---- wire to JSON ----

    /// <summary>A decoded message: each field's values in wire order, so repeated fields can be written as one array.</summary>
    private sealed class Parsed
    {
        public Dictionary<FieldDescriptor, List<object>> Values { get; } = new();

        public void Add(FieldDescriptor field, object value)
        {
            if (!Values.TryGetValue(field, out var list))
            {
                list = [];
                Values[field] = list;
            }

            list.Add(value);
        }
    }

    private static Parsed ReadMessage(MessageDescriptor message, ReadOnlySpan<byte> bytes, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ProtobufException("The response is nested too deeply.");
        }

        var parsed = new Parsed();
        while (bytes.Length > 0)
        {
            var tag = ReadVarint(ref bytes);
            var number = (int)(tag >> 3);
            var wireType = (int)(tag & 7);
            if (number == 0)
            {
                throw new ProtobufException("The response has an invalid field tag.");
            }

            // A field sent with another wire type than its declared type uses is skipped, like an unknown field, so a
            // server with a different version of the .proto can't make a number turn up where a message belongs.
            var field = message.Find(number);
            switch (wireType)
            {
                case 0:
                    var varint = ReadVarint(ref bytes);
                    if (field is not null && WireTypeOf(field.Type) == 0)
                    {
                        parsed.Add(field, FromVarint(field, varint));
                    }

                    break;
                case 1:
                    var fixed64 = BinaryPrimitives.ReadUInt64LittleEndian(Take(ref bytes, 8));
                    if (field is not null && WireTypeOf(field.Type) == 1)
                    {
                        parsed.Add(field, FromFixed64(field, fixed64));
                    }

                    break;
                case 5:
                    var fixed32 = BinaryPrimitives.ReadUInt32LittleEndian(Take(ref bytes, 4));
                    if (field is not null && WireTypeOf(field.Type) == 5)
                    {
                        parsed.Add(field, FromFixed32(field, fixed32));
                    }

                    break;
                case 2:
                    var length = ReadVarint(ref bytes);
                    if (length > (ulong)bytes.Length)
                    {
                        throw new ProtobufException("The response ends in the middle of a value.");
                    }

                    var record = Take(ref bytes, (int)length);
                    if (field is null)
                    {
                        break;
                    }

                    if (field.Type == FieldType.Message)
                    {
                        parsed.Add(field, ReadMessage(field.Message!, record, depth + 1));
                    }
                    else if (field.Type == FieldType.String)
                    {
                        parsed.Add(field, Encoding.UTF8.GetString(record));
                    }
                    else if (field.Type == FieldType.Bytes)
                    {
                        parsed.Add(field, record.ToArray());
                    }
                    else
                    {
                        // A packed array of numbers, bools or enums.
                        while (record.Length > 0)
                        {
                            parsed.Add(field, WireTypeOf(field.Type) switch
                            {
                                1 => FromFixed64(field, BinaryPrimitives.ReadUInt64LittleEndian(Take(ref record, 8))),
                                5 => FromFixed32(field, BinaryPrimitives.ReadUInt32LittleEndian(Take(ref record, 4))),
                                _ => FromVarint(field, ReadVarint(ref record)),
                            });
                        }
                    }

                    break;
                case 3:
                case 4:
                    throw new ProtobufException("The response uses groups, which aren't supported.");
                default:
                    throw new ProtobufException($"The response has an unknown wire type {wireType}.");
            }
        }

        return parsed;
    }

    private static object FromVarint(FieldDescriptor field, ulong value) => field.Type switch
    {
        FieldType.Bool => value != 0,
        FieldType.Int32 => (long)(int)value,
        FieldType.Int64 => (long)value,
        FieldType.UInt32 => (ulong)(uint)value,
        FieldType.UInt64 => value,
        FieldType.SInt32 => (long)((int)(uint)value >>> 1 ^ -((int)value & 1)),
        FieldType.SInt64 => (long)(value >> 1) ^ -(long)(value & 1),
        FieldType.Enum => (long)(int)value,
        _ => (long)value,
    };

    private static object FromFixed64(FieldDescriptor field, ulong value) => field.Type switch
    {
        FieldType.Double => BitConverter.UInt64BitsToDouble(value),
        FieldType.SFixed64 => (long)value,
        _ => value,
    };

    private static object FromFixed32(FieldDescriptor field, uint value) => field.Type switch
    {
        FieldType.Float => (double)BitConverter.UInt32BitsToSingle(value),
        FieldType.SFixed32 => (long)(int)value,
        _ => (ulong)value,
    };

    private static ulong ReadVarint(ref ReadOnlySpan<byte> bytes)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (bytes.Length == 0 || shift > 63)
            {
                throw new ProtobufException("The response ends in the middle of a value.");
            }

            var b = bytes[0];
            bytes = bytes[1..];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }
    }

    private static ReadOnlySpan<byte> Take(ref ReadOnlySpan<byte> bytes, int count)
    {
        if (count < 0 || count > bytes.Length)
        {
            throw new ProtobufException("The response ends in the middle of a value.");
        }

        var taken = bytes[..count];
        bytes = bytes[count..];
        return taken;
    }

    private static void WriteJson(Utf8JsonWriter writer, MessageDescriptor message, Parsed parsed)
    {
        writer.WriteStartObject();
        foreach (var field in message.Fields)
        {
            if (!parsed.Values.TryGetValue(field, out var values))
            {
                continue;
            }

            writer.WritePropertyName(field.JsonName);
            if (field.IsMap)
            {
                writer.WriteStartObject();
                var keyField = field.Message!.Find(1)!;
                var valueField = field.Message.Find(2)!;
                foreach (var entry in values.Cast<Parsed>())
                {
                    var key = entry.Values.TryGetValue(keyField, out var keys) ? keys[^1] : DefaultOf(keyField);
                    writer.WritePropertyName(key is string s ? s : Convert.ToString(key, CultureInfo.InvariantCulture)!);
                    if (entry.Values.TryGetValue(valueField, out var inner))
                    {
                        WriteJsonValue(writer, valueField, inner[^1]);
                    }
                    else
                    {
                        WriteJsonValue(writer, valueField, DefaultOf(valueField));
                    }
                }

                writer.WriteEndObject();
            }
            else if (field.Repeated)
            {
                writer.WriteStartArray();
                foreach (var value in values)
                {
                    WriteJsonValue(writer, field, value);
                }

                writer.WriteEndArray();
            }
            else
            {
                // When a field is repeated on the wire without being declared so, the last value wins, as protoc does.
                WriteJsonValue(writer, field, values[^1]);
            }
        }

        writer.WriteEndObject();
    }

    private const long MaxDurationSeconds = 315_576_000_000;

    // Writes a timestamp, duration or wrapper in its JSON form. A value outside what the form can show stays an object.
    private static bool TryWriteSpecialJson(Utf8JsonWriter writer, MessageDescriptor message, Parsed parsed)
    {
        if (IsWrapper(message.FullName) && message.Find(1) is { } valueField)
        {
            WriteJsonValue(writer, valueField, parsed.Values.TryGetValue(valueField, out var values) ? values[^1] : DefaultOf(valueField));
            return true;
        }

        if (message.FullName is not (TimestampType or DurationType))
        {
            return false;
        }

        var seconds = Number(1);
        var nanos = Number(2);
        if (nanos is <= -NanosPerSecond or >= NanosPerSecond)
        {
            return false;
        }

        // Outside the range protobuf allows (about 10,000 years), a duration stays an object.
        if (seconds is < -MaxDurationSeconds or > MaxDurationSeconds)
        {
            return false;
        }

        if (message.FullName == TimestampType)
        {
            if (nanos < 0 || seconds < -62_135_596_800 || seconds > 253_402_300_799)
            {
                return false;
            }

            var time = DateTimeOffset.FromUnixTimeSeconds(seconds);
            writer.WriteStringValue(time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(nanos) + "Z");
            return true;
        }

        var negative = seconds < 0 || nanos < 0;
        writer.WriteStringValue((negative ? "-" : "") + Math.Abs(seconds).ToString(CultureInfo.InvariantCulture) + Fraction(Math.Abs(nanos)) + "s");
        return true;

        long Number(int number) =>
            message.Find(number) is { } f && parsed.Values.TryGetValue(f, out var values) ? Convert.ToInt64(values[^1], CultureInfo.InvariantCulture) : 0;

        // As protoc writes it: no fraction, or 3, 6 or 9 digits.
        static string Fraction(long nanos) =>
            nanos == 0 ? string.Empty
            : nanos % 1_000_000 == 0 ? "." + (nanos / 1_000_000).ToString("D3", CultureInfo.InvariantCulture)
            : nanos % 1_000 == 0 ? "." + (nanos / 1_000).ToString("D6", CultureInfo.InvariantCulture)
            : "." + nanos.ToString("D9", CultureInfo.InvariantCulture);
    }

    private static object DefaultOf(FieldDescriptor field) => field.Type switch
    {
        FieldType.String => string.Empty,
        FieldType.Bool => false,
        FieldType.Bytes => Array.Empty<byte>(),
        FieldType.Double or FieldType.Float => 0d,
        FieldType.UInt32 or FieldType.UInt64 or FieldType.Fixed32 or FieldType.Fixed64 => 0UL,
        FieldType.Message => new Parsed(),
        _ => 0L,
    };

    private static void WriteJsonValue(Utf8JsonWriter writer, FieldDescriptor field, object value)
    {
        switch (value)
        {
            case Parsed inner when TryWriteSpecialJson(writer, field.Message!, inner):
                break;
            case Parsed inner:
                WriteJson(writer, field.Message!, inner);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case double d when double.IsNaN(d) || double.IsInfinity(d):
                writer.WriteStringValue(double.IsNaN(d) ? "NaN" : d > 0 ? "Infinity" : "-Infinity");
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case long l when field.Type == FieldType.Enum:
                if (field.Enum!.ByNumber.TryGetValue((int)l, out var name))
                {
                    writer.WriteStringValue(name);
                }
                else
                {
                    writer.WriteNumberValue(l);
                }

                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case ulong u:
                writer.WriteNumberValue(u);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
