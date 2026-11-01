namespace FalconLoad.Core.Grpc;

/// <summary>
/// The <c>google/protobuf/*.proto</c> files services commonly import, so a test's proto files don't have to include
/// them. They are plain messages here: a <c>Timestamp</c> is written and read as <c>{ "seconds": …, "nanos": … }</c>
/// rather than as an RFC 3339 string, and <c>Any</c> keeps its <c>value</c> as base64 bytes.
/// </summary>
internal static class WellKnown
{
    private static readonly Dictionary<string, string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google/protobuf/empty.proto"] = "syntax = \"proto3\"; package google.protobuf; message Empty {}",
        ["google/protobuf/timestamp.proto"] = "syntax = \"proto3\"; package google.protobuf; message Timestamp { int64 seconds = 1; int32 nanos = 2; }",
        ["google/protobuf/duration.proto"] = "syntax = \"proto3\"; package google.protobuf; message Duration { int64 seconds = 1; int32 nanos = 2; }",
        ["google/protobuf/wrappers.proto"] = """
            syntax = "proto3"; package google.protobuf;
            message DoubleValue { double value = 1; } message FloatValue { float value = 1; }
            message Int64Value { int64 value = 1; } message UInt64Value { uint64 value = 1; }
            message Int32Value { int32 value = 1; } message UInt32Value { uint32 value = 1; }
            message BoolValue { bool value = 1; } message StringValue { string value = 1; } message BytesValue { bytes value = 1; }
            """,
        ["google/protobuf/any.proto"] = "syntax = \"proto3\"; package google.protobuf; message Any { string type_url = 1; bytes value = 2; }",
        ["google/protobuf/field_mask.proto"] = "syntax = \"proto3\"; package google.protobuf; message FieldMask { repeated string paths = 1; }",
        ["google/protobuf/struct.proto"] = """
            syntax = "proto3"; package google.protobuf;
            message Struct { map<string, Value> fields = 1; }
            message Value { oneof kind { NullValue null_value = 1; double number_value = 2; string string_value = 3; bool bool_value = 4; Struct struct_value = 5; ListValue list_value = 6; } }
            message ListValue { repeated Value values = 1; }
            enum NullValue { NULL_VALUE = 0; }
            """,
        ["google/protobuf/descriptor.proto"] = "syntax = \"proto2\"; package google.protobuf; message FileDescriptorSet {}",
    };

    public static bool TryGet(string import, out ProtoFile file)
    {
        if (Files.TryGetValue(import.Trim(), out var content))
        {
            file = new ProtoFile { Name = import.Trim(), Content = content };
            return true;
        }

        file = null!;
        return false;
    }
}
