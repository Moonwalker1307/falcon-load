using System.Text;
using System.Text.Json;

namespace FalconLoad.Core.Engine;

/// <summary>
/// The HTTP shape of a GraphQL operation: a POST whose JSON body carries the query, its variables and the operation
/// name, and a response that reports failures in an <c>errors</c> array rather than in the status code.
/// </summary>
public static class GraphQLRequest
{
    public const string ContentType = "application/json";

    /// <summary>
    /// Builds the request body. <paramref name="variables"/> is written as it is, so it has to be a JSON object already;
    /// <see cref="IsJsonObject"/> checks that when the plan is validated.
    /// </summary>
    public static byte[] BuildBody(string query, string? variables, string? operationName)
    {
        var buffer = new MemoryStream(query.Length + (variables?.Length ?? 0) + 64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query"u8, query);
            if (!string.IsNullOrWhiteSpace(variables))
            {
                writer.WritePropertyName("variables"u8);
                writer.WriteRawValue(variables, skipInputValidation: false);
            }

            if (!string.IsNullOrWhiteSpace(operationName))
            {
                writer.WriteString("operationName"u8, operationName.Trim());
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    public static bool IsJsonObject(string text)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Looks for a top-level <c>errors</c> array with at least one entry and returns the first error's message as the
    /// request's error text, or null when the response reports no error. Only the top level is read, so the work is a
    /// scan of the body without building a document, and nothing is allocated for a response without errors.
    /// </summary>
    public static string? FindError(ReadOnlySpan<byte> body)
    {
        if (body.StartsWith("﻿"u8))
        {
            body = body[3..];
        }

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return "GraphQL error: the response isn't a JSON object";
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (!reader.ValueTextEquals("errors"u8))
                {
                    reader.Skip();
                    continue;
                }

                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    return reader.TokenType == JsonTokenType.Null ? null : "GraphQL error: \"errors\" isn't an array";
                }

                reader.Read();
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    return null;
                }

                return "GraphQL error: " + FirstMessage(ref reader);
            }

            return null;
        }
        catch (JsonException)
        {
            return "GraphQL error: the response isn't valid JSON";
        }
    }

    /// <summary>Reads the first element of the errors array, positioned on its first token, and returns its message.</summary>
    private static string FirstMessage(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            return "the response reports an error";
        }

        string? message = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isMessage = reader.ValueTextEquals("message"u8);
            reader.Read();
            if (isMessage && reader.TokenType == JsonTokenType.String)
            {
                message = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return string.IsNullOrWhiteSpace(message) ? "the response reports an error" : message;
    }
}
