using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace FalconLoad.Core.Grpc;

/// <summary>
/// The gRPC framing over HTTP/2: every message is a five-byte prefix (a compressed flag and a big-endian length) and its
/// protobuf bytes. A request body is one frame, or one per element of a JSON array for a client-streaming method, and a
/// response body is one frame, or any number for a server-streaming method.
/// </summary>
public static class GrpcMessages
{
    public const string ContentType = "application/grpc";

    /// <summary>Encodes the JSON message (or JSON array of messages, for a client-streaming method) as framed protobuf.</summary>
    public static byte[] EncodeRequest(MethodDescriptor method, string json)
    {
        var trimmed = json.AsSpan().TrimStart();
        if (trimmed.Length == 0)
        {
            return Frame([ProtobufCodec.Encode(method.Input, "{}")]);
        }

        if (trimmed[0] == '[')
        {
            if (!method.ClientStreaming)
            {
                throw new ProtobufException($"{method.Path} takes one {method.Input.FullName}, so the message has to be a JSON object, not an array.");
            }

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return Frame([.. document.RootElement.EnumerateArray().Select(e => ProtobufCodec.Encode(method.Input, e.GetRawText()))]);
        }

        return Frame([ProtobufCodec.Encode(method.Input, json)]);
    }

    private static byte[] Frame(IReadOnlyList<byte[]> messages)
    {
        var result = new byte[messages.Sum(m => m.Length + 5)];
        var offset = 0;
        foreach (var message in messages)
        {
            result[offset] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 1), (uint)message.Length);
            message.CopyTo(result.AsSpan(offset + 5));
            offset += message.Length + 5;
        }

        return result;
    }

    /// <summary>
    /// Decodes a response body to JSON: the message as an object, or for a server-streaming method an array of every
    /// message, so checks and extractions read <c>$.name</c> or <c>$[0].name</c>.
    /// </summary>
    public static void DecodeResponse(MethodDescriptor method, ReadOnlySpan<byte> body, ArrayBufferWriter<byte> output)
    {
        var frames = new List<(int Start, int Length)>();
        var position = 0;
        while (position < body.Length)
        {
            if (body.Length - position < 5)
            {
                throw new ProtobufException("The response ends in the middle of a message prefix.");
            }

            if (body[position] != 0)
            {
                throw new ProtobufException("The response message is compressed, which isn't supported; the server shouldn't compress since falcon-load doesn't ask for it.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(body[(position + 1)..]);
            if (length > body.Length - position - 5)
            {
                throw new ProtobufException("The response ends in the middle of a message.");
            }

            frames.Add((position + 5, (int)length));
            position += 5 + (int)length;
        }

        if (!method.ServerStreaming)
        {
            // A unary or client-streaming call answers with one message; an empty answer is the message with every field unset.
            if (frames.Count == 0)
            {
                output.Write("{}"u8);
                return;
            }

            var (start, length) = frames[^1];
            ProtobufCodec.Decode(method.Output, body.Slice(start, length), output);
            return;
        }

        output.Write("["u8);
        for (var i = 0; i < frames.Count; i++)
        {
            if (i > 0)
            {
                output.Write(","u8);
            }

            ProtobufCodec.Decode(method.Output, body.Slice(frames[i].Start, frames[i].Length), output);
        }

        output.Write("]"u8);
    }

    public static string DecodeResponseToString(MethodDescriptor method, ReadOnlySpan<byte> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        DecodeResponse(method, body, buffer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Reads <c>grpc-status</c> and <c>grpc-message</c> from the trailers, or from the headers of a trailers-only
    /// response, which is how servers answer a call that fails before sending a message. Null when neither has a status.
    /// </summary>
    public static (int Status, string? Message)? ReadStatus(HttpResponseMessage response)
    {
        foreach (var headers in new System.Net.Http.Headers.HttpHeaders[] { response.TrailingHeaders, response.Headers })
        {
            if (headers.TryGetValues("grpc-status", out var values) && int.TryParse(values.FirstOrDefault(), out var status))
            {
                var message = headers.TryGetValues("grpc-message", out var messages) ? Uri.UnescapeDataString(messages.FirstOrDefault() ?? string.Empty) : null;
                return (status, string.IsNullOrEmpty(message) ? null : message);
            }
        }

        return null;
    }
}
