using System.Text;
using FalconLoad.Core.Metrics;

namespace FalconLoad.Core.Engine;

/// <summary>
/// Turns a failed request into an <see cref="ErrorSample"/>. Only runs for the few failures the
/// <see cref="ErrorSampler"/> keeps, so it may allocate freely; every text it keeps goes through the run's mask.
/// </summary>
internal static class ErrorSampleBuilder
{
    /// <summary>How many bytes of a body are kept for a sample; enough for <see cref="ErrorSample.MaxBodyChars"/> characters of UTF-8.</summary>
    public const int MaxBodyBytes = ErrorSample.MaxBodyChars * 4;

    private const int MaxHeaders = 40;
    private const int MaxHeaderChars = 500;

    public static ErrorSample Build(
        PreparedRequest prepared,
        int step,
        TimeSpan startedAt,
        TimeSpan latency,
        string status,
        string error,
        Func<string, string> mask,
        string? url,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>>? requestHeaders,
        SampleBody? requestBody,
        HttpResponseMessage? response = null,
        SampleBody? responseBody = null) => new()
        {
            StepIndex = step,
            Method = prepared.Label,
            Route = prepared.Route,
            Url = url is null ? null : mask(url),
            AtSeconds = Math.Round(startedAt.TotalSeconds, 3),
            LatencyMs = Math.Round(latency.TotalMilliseconds, 3),
            Status = status,
            Error = error,
            RequestHeaders = Headers(requestHeaders, mask),
            RequestBody = requestBody,
            ResponseHeaders = response is null ? [] : Headers(ResponseHeaders(response), mask),
            ResponseBody = responseBody,
        };

    /// <summary>The headers of a request as it was sent, content headers included.</summary>
    public static IEnumerable<KeyValuePair<string, IEnumerable<string>>> RequestHeaders(HttpRequestMessage request) =>
        request.Content is null ? request.Headers : request.Headers.Concat(request.Content.Headers);

    /// <summary>The body a request sent, read back from its content; gRPC messages are protobuf, so only their size is kept.</summary>
    public static SampleBody? RequestBody(HttpRequestMessage? request, long bytesSent, bool binary, Func<string, string> mask)
    {
        if (request?.Content is not { } content)
        {
            return null;
        }

        if (binary)
        {
            return new SampleBody(null, bytesSent, false);
        }

        try
        {
            using var stream = content.ReadAsStream();
            var buffer = new byte[(int)Math.Min(MaxBodyBytes, Math.Max(bytesSent, 0))];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Body(buffer.AsSpan(0, read), bytesSent, read < bytesSent, mask);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or IOException)
        {
            return new SampleBody(null, bytesSent, false);
        }
    }

    /// <summary>
    /// The start of a body as masked text, cut to <see cref="ErrorSample.MaxBodyChars"/> characters. A body that isn't
    /// valid UTF-8, or holds control characters text doesn't have, is kept as binary: its size only.
    /// </summary>
    /// <param name="bytes">The size of the whole body.</param>
    /// <param name="cut">True when <paramref name="data"/> is only the start of the body.</param>
    public static SampleBody Body(ReadOnlySpan<byte> data, long bytes, bool cut, Func<string, string> mask)
    {
        if (data.Length > MaxBodyBytes)
        {
            data = data[..MaxBodyBytes];
            cut = true;
        }

        // A cut body may end in the middle of a character, which is dropped rather than shown as garbage.
        if (cut)
        {
            data = data[..CompleteLength(data)];
        }

        string text;
        try
        {
            text = Strict.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return new SampleBody(null, bytes, cut);
        }

        if (text.AsSpan().ContainsAnyInRange('\0', '\u0008') || text.Contains('\u007f'))
        {
            return new SampleBody(null, bytes, cut);
        }

        // Secrets are hidden before the text is cut, so a secret that straddles the cut is hidden whole.
        text = mask(text);
        if (text.Length > ErrorSample.MaxBodyChars)
        {
            // Cutting between the halves of a surrogate pair would leave text that can't be written as JSON.
            var length = char.IsHighSurrogate(text[ErrorSample.MaxBodyChars - 1]) ? ErrorSample.MaxBodyChars - 1 : ErrorSample.MaxBodyChars;
            text = text[..length];
            cut = true;
        }

        return new SampleBody(text, bytes, cut);
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The length of <paramref name="data"/> without a character cut off at its end.</summary>
    private static int CompleteLength(ReadOnlySpan<byte> data)
    {
        // A UTF-8 character is at most four bytes, so only the last three can belong to an unfinished one.
        for (var back = 1; back <= Math.Min(3, data.Length); back++)
        {
            var b = data[^back];
            if ((b & 0xC0) == 0x80)
            {
                continue;
            }

            var needed = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needed > back ? data.Length - back : data.Length;
        }

        return data.Length;
    }

    private static IEnumerable<KeyValuePair<string, IEnumerable<string>>> ResponseHeaders(HttpResponseMessage response)
    {
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = response.Headers;
        if (response.Content is not null)
        {
            headers = headers.Concat(response.Content.Headers);
        }

        // gRPC reports its status in trailers, which is often the most useful part of a failed call.
        return headers.Concat(response.TrailingHeaders);
    }

    /// <summary>Header values, masked; the values of headers that carry credentials are never kept.</summary>
    public static IReadOnlyList<SampleHeader> Headers(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers, Func<string, string> mask)
    {
        if (headers is null)
        {
            return [];
        }

        var list = new List<SampleHeader>();
        foreach (var (name, values) in headers)
        {
            if (list.Count == MaxHeaders)
            {
                break;
            }

            var value = ErrorSample.CredentialHeaders.Contains(name) ? Model.SecretMasker.Mask : mask(string.Join(", ", values));
            if (value.Length > MaxHeaderChars)
            {
                value = value[..(char.IsHighSurrogate(value[MaxHeaderChars - 1]) ? MaxHeaderChars - 1 : MaxHeaderChars)] + "…";
            }

            list.Add(new SampleHeader(name, value));
        }

        return list;
    }

    /// <summary>The headers of a WebSocket handshake, in the shape <see cref="Headers"/> reads.</summary>
    public static IEnumerable<KeyValuePair<string, IEnumerable<string>>> Pairs(IReadOnlyList<KeyValuePair<string, string>> headers) =>
        headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, [h.Value]));
}
