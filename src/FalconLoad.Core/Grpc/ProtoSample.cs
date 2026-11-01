using System.Text;
using System.Text.Json;

namespace FalconLoad.Core.Grpc;

/// <summary>
/// Writes a JSON message with every field of a type filled in with a value of the right kind, as a starting point for a
/// request's message: numbers are 0, strings empty, enums their first value, and nested messages filled in the same way.
/// </summary>
public static class ProtoSample
{
    /// <summary>A sample message for <paramref name="method"/>'s request, or an array of one for a client-streaming method.</summary>
    public static string For(MethodDescriptor method)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            if (method.ClientStreaming)
            {
                writer.WriteStartArray();
                Write(writer, method.Input, [method.Input.FullName]);
                writer.WriteEndArray();
            }
            else
            {
                Write(writer, method.Input, [method.Input.FullName]);
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // A type that contains itself, such as a tree node, is written once and left empty where it would repeat.
    private static void Write(Utf8JsonWriter writer, MessageDescriptor message, HashSet<string> seen)
    {
        writer.WriteStartObject();
        foreach (var field in message.Fields)
        {
            if (field.Type == FieldType.Message && !field.IsMap && seen.Contains(field.Message!.FullName))
            {
                continue;
            }

            writer.WritePropertyName(field.JsonName);
            if (field.IsMap)
            {
                writer.WriteStartObject();
                writer.WritePropertyName(field.Message!.Find(1)!.Type == FieldType.String ? "key" : "0");
                WriteValue(writer, field.Message.Find(2)!, seen);
                writer.WriteEndObject();
            }
            else if (field.Repeated)
            {
                writer.WriteStartArray();
                WriteValue(writer, field, seen);
                writer.WriteEndArray();
            }
            else
            {
                WriteValue(writer, field, seen);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, FieldDescriptor field, HashSet<string> seen)
    {
        switch (field.Type)
        {
            case FieldType.Message when field.Message!.FullName == "google.protobuf.Timestamp":
                writer.WriteStringValue("2026-01-01T00:00:00Z");
                break;
            case FieldType.Message when field.Message!.FullName == "google.protobuf.Duration":
                writer.WriteStringValue("1s");
                break;
            case FieldType.Message when field.Message!.FullName.StartsWith("google.protobuf.", StringComparison.Ordinal)
                && field.Message.FullName.EndsWith("Value", StringComparison.Ordinal) && field.Message.Fields.Count == 1:
                // A wrapper such as StringValue is written as the bare value it holds.
                WriteValue(writer, field.Message.Fields[0], seen);
                break;
            case FieldType.Message:
                Write(writer, field.Message!, [.. seen, field.Message!.FullName]);
                break;
            case FieldType.String:
                writer.WriteStringValue(string.Empty);
                break;
            case FieldType.Bytes:
                writer.WriteStringValue(string.Empty);
                break;
            case FieldType.Bool:
                writer.WriteBooleanValue(false);
                break;
            case FieldType.Enum:
                writer.WriteStringValue(field.Enum!.ByNumber.OrderBy(e => e.Key).Select(e => e.Value).FirstOrDefault() ?? "0");
                break;
            default:
                writer.WriteNumberValue(0);
                break;
        }
    }
}
