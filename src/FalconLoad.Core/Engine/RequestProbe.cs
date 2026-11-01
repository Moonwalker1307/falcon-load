using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using FalconLoad.Core.Metrics;
using FalconLoad.Core.Model;

namespace FalconLoad.Core.Engine;

/// <summary>The response to a single test request, used to check a request before load testing with it.</summary>
/// <param name="Error">Why nothing came back, or, with a status code, what the protocol found wrong with the response, such as a GraphQL error.</param>
public sealed record ProbeResult(
    int? StatusCode,
    string? ReasonPhrase,
    TimeSpan Latency,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string BodyPreview,
    long BodyBytes,
    bool Success,
    string? Error,
    IReadOnlyList<ExtractedValue>? Extractions = null)
{
    /// <summary>What each of the request's checks saw in the response. Empty when it has none or nothing came back.</summary>
    public IReadOnlyList<CheckOutcome> Checks { get; init; } = [];

    /// <summary>Whether the status matched the expected codes, regardless of the checks.</summary>
    public bool StatusMatches { get; init; }

    /// <summary>
    /// What stands in for the status when the protocol has none: <see cref="Metrics.RequestResult.ReplyStatus"/> or
    /// <see cref="Metrics.RequestResult.SentStatus"/> for a WebSocket message. Null with a status code or an error.
    /// </summary>
    public string? StatusLabel { get; init; }

    /// <summary>Whether anything came back: a status code, or a WebSocket message that was sent or answered.</summary>
    public bool HasResponse => StatusCode is not null || StatusLabel is not null;
}

/// <summary>The outcome of asking the token endpoint for a token. The token itself is never part of it.</summary>
/// <param name="LifetimeSeconds">The token's <c>expires_in</c>; null when the server gave none.</param>
public sealed record TokenProbeResult(bool Success, string? Error, TimeSpan Latency, double? LifetimeSeconds);

/// <summary>
/// Sends one request and captures the response, so users can verify a request before load testing. Parameter lists
/// supply their first value, the environment, if any, supplies its variables, and the request's extraction rules are
/// tried on the response. With OAuth2 auth, a token is fetched first and sent as the test would send it. The
/// environment's secret values, the client secret and the token are hidden in what comes back, in case the server echoes them.
/// </summary>
public static class RequestProbe
{
    private const int MaxPreviewBytes = 16 * 1024;

    /// <summary>Asks the token endpoint for a token, to check the auth settings before a run.</summary>
    public static async Task<TokenProbeResult> GetTokenAsync(
        AuthSettings auth,
        LoadProfile profile,
        TestEnvironment? environment = null,
        HttpMessageInvoker? invoker = null,
        CancellationToken cancellationToken = default)
    {
        var problems = auth.Validate(environment?.ToValues(), forRun: true).ToList();
        if (!auth.IsEnabled || problems.Count > 0)
        {
            return new TokenProbeResult(false, auth.IsEnabled ? string.Join(" ", problems) : "Auth is off for this test.", TimeSpan.Zero, null);
        }

        var owned = invoker is null;
        invoker ??= new HttpMessageInvoker(HttpHandlerFactory.Create(profile), disposeHandler: true);
        var masker = environment?.CreateMasker() ?? SecretMasker.None;
        using var tokens = new OAuth2TokenSource(auth.Resolve(environment?.ToValues()), invoker, TimeSpan.FromSeconds(profile.TimeoutSeconds), masker);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var token = await tokens.FetchFirstAsync(cancellationToken).ConfigureAwait(false);
            return new TokenProbeResult(true, null, Stopwatch.GetElapsedTime(start), token.Lifetime?.TotalSeconds);
        }
        catch (OAuth2TokenException ex)
        {
            return new TokenProbeResult(false, ex.Message, Stopwatch.GetElapsedTime(start), null);
        }
        finally
        {
            if (owned)
            {
                invoker.Dispose();
            }
        }
    }

    /// <param name="variables">
    /// Extracted variables for placeholders, compared without regard to case. Values this request extracts are added.
    /// </param>
    /// <param name="webSockets">Opens the connection of a WebSocket request. By default it goes through <paramref name="invoker"/>.</param>
    public static async Task<ProbeResult> SendAsync(
        RequestDefinition definition,
        LoadProfile profile,
        HttpMessageInvoker? invoker = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ParameterList>? parameters = null,
        Dictionary<string, string>? variables = null,
        TestEnvironment? environment = null,
        IReadOnlyList<DataFile>? dataFiles = null,
        AuthSettings? auth = null,
        IWebSocketConnector? webSockets = null,
        IReadOnlyList<Grpc.ProtoFile>? protoFiles = null)
    {
        var set = new ParameterSet(parameters ?? [], dataFiles);
        var secrets = environment?.CreateMasker() ?? SecretMasker.None;
        auth = auth is { IsEnabled: true } ? auth : null;

        // Parameter lists and variables already extracted win over an environment variable of the same name.
        var values = new Dictionary<string, string>(environment?.ToValues() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var name in set.Names.Concat(variables?.Keys ?? Enumerable.Empty<string>()))
        {
            values.Remove(name);
        }

        var problems = definition.Validate(set, variables?.Keys.ToList(), values, forRun: true).ToList();
        Grpc.ProtoSchema? schema = null;
        if (definition.Protocol == RequestProtocol.Grpc)
        {
            // A gRPC request is checked against the test's proto files like it is when the test is saved.
            problems.AddRange(new LoadTestPlan { Requests = [definition], ProtoFiles = [.. protoFiles ?? []] }.ValidateGrpc()
                .Select(p => p.StartsWith("Request 1 (", StringComparison.Ordinal) ? p[(p.IndexOf("): ", StringComparison.Ordinal) + 3)..] : p));
            if (problems.Count > 0)
            {
                return new ProbeResult(null, null, TimeSpan.Zero, [], string.Empty, 0, false, string.Join(" ", problems));
            }

            Grpc.ProtoSchema.TryParse(protoFiles!, out schema, out _);
        }

        var prepared = PreparedRequest.From(definition, values, auth is not null, schema);
        if (prepared.UsesAuth)
        {
            problems.AddRange(auth!.Validate(environment?.ToValues(), forRun: true));
        }

        if (problems.Count > 0)
        {
            return new ProbeResult(null, null, TimeSpan.Zero, [], string.Empty, 0, false, string.Join(" ", problems));
        }

        var owned = invoker is null;
        invoker ??= new HttpMessageInvoker(HttpHandlerFactory.Create(profile), disposeHandler: true);
        using var tokens = prepared.UsesAuth
            ? new OAuth2TokenSource(auth!.Resolve(environment?.ToValues()), invoker, TimeSpan.FromSeconds(profile.TimeoutSeconds), secrets)
            : null;
        Func<string?, string?> masker = text => text is null ? null : tokens?.Mask(text) ?? secrets.Apply(text);
        var start = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));

        try
        {
            // Variables from earlier probes fill in placeholders, and this probe's extractions are added to them, so
            // sending a login request once lets the next "Send once" use its token.
            variables ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var context = new TemplateContext(1, 1, set.First(), variables);
            var token = tokens is null ? null : await tokens.FetchFirstAsync(timeout.Token).ConfigureAwait(false);
            start = Stopwatch.GetTimestamp();
            if (definition.Protocol == RequestProtocol.WebSocket)
            {
                return await SendWebSocketAsync(prepared, context, token?.Header, webSockets ?? new ClientWebSocketConnector(invoker), masker, variables, start, timeout.Token).ConfigureAwait(false);
            }

            using var request = prepared.Build(context, out _, token?.Header);
            using var response = await invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(start);

            var headers = response.Headers.Concat(response.Content.Headers).Concat(response.TrailingHeaders)
                .Select(h => new KeyValuePair<string, string>(h.Key, masker(string.Join(", ", h.Value))!))
                .ToList();

            // A gRPC response is shown, checked and searched as JSON, like it is during a run.
            string? decodeError = null;
            var raw = bytes;
            if (definition.Protocol == RequestProtocol.Grpc && response.StatusCode == System.Net.HttpStatusCode.OK)
            {
                try
                {
                    bytes = prepared.ReadableBody(bytes, new System.Buffers.ArrayBufferWriter<byte>()).ToArray();
                }
                catch (Grpc.ProtobufException ex)
                {
                    decodeError = $"gRPC: couldn't read the response: {ex.Message}";
                    bytes = [];
                }
            }

            var preview = masker(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPreviewBytes)))!;
            if (bytes.Length > MaxPreviewBytes)
            {
                preview += $"\n… ({bytes.Length - MaxPreviewBytes:N0} more bytes)";
            }

            var code = (int)response.StatusCode;
            var statusMatches = prepared.HttpStatusMatches(code);
            var statusLabel = prepared.StatusLabel(response);
            var protocolError = statusMatches ? prepared.ProtocolError(response, raw, raw.Length > Assertion.MaxBodyBytes) ?? decodeError : null;
            var checks = prepared.Checker.Explain(response, bytes);
            List<ExtractedValue>? extractions = null;
            if (prepared.Extractor is { } extractor)
            {
                extractions = [];
                extractor.Apply(response, bytes.AsSpan(0, Math.Min(bytes.Length, ResponseExtractor.MaxBodyBytes)), variables, extractions);
            }

            return new ProbeResult(statusLabel is null ? code : null, statusLabel is null ? masker(response.ReasonPhrase) : null, latency, headers, preview, raw.Length,
                statusMatches && protocolError is null && checks.All(c => c.Passed), masker(protocolError), extractions)
            {
                Checks = [.. checks.Select(c => c with { Actual = masker(c.Actual) })],
                StatusMatches = statusMatches,
                StatusLabel = statusLabel,
            };
        }
        catch (OAuth2TokenException ex)
        {
            return Failed(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"Timeout after {profile.TimeoutSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return Failed(ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})");
        }
        catch (WebSocketException ex)
        {
            return Failed(LoadTestRunner.DescribeWebSocketError(ex));
        }
        catch (Grpc.ProtobufException ex)
        {
            return Failed($"gRPC: couldn't encode the message: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or FormatException or UriFormatException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }
        finally
        {
            if (owned)
            {
                invoker.Dispose();
            }
        }

        ProbeResult Failed(string error) =>
            new(null, null, Stopwatch.GetElapsedTime(start), [], string.Empty, 0, false, masker(error));
    }

    /// <summary>
    /// Connects, sends the message, waits for the reply when the request does, and closes again, so a single "Send once"
    /// leaves nothing open. The handshake response's headers are shown in place of response headers.
    /// </summary>
    private static async Task<ProbeResult> SendWebSocketAsync(
        PreparedRequest prepared,
        TemplateContext context,
        string? authorization,
        IWebSocketConnector connector,
        Func<string?, string?> masker,
        Dictionary<string, string> variables,
        long start,
        CancellationToken cancellationToken)
    {
        var connection = await connector.ConnectAsync(prepared.ResolveUri(context), prepared.HandshakeHeaders(context, authorization), collectHandshake: true, cancellationToken).ConfigureAwait(false);
        var socket = connection.Socket;
        try
        {
            var message = prepared.Message(context);
            if (message.Length > 0)
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }

            var reply = new MemoryStream();
            if (prepared.WaitForReply)
            {
                var buffer = new byte[16 * 1024];
                ValueWebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close)
                    {
                        var closed = $"Server closed the connection: {socket.CloseStatus} {socket.CloseStatusDescription}".TrimEnd();
                        return new ProbeResult(null, null, Stopwatch.GetElapsedTime(start), Headers(), string.Empty, 0, false, masker(closed));
                    }

                    reply.Write(buffer, 0, frame.Count);
                }
                while (!frame.EndOfMessage);
            }

            var latency = Stopwatch.GetElapsedTime(start);
            var bytes = reply.ToArray();
            var preview = masker(Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, MaxPreviewBytes)))!;
            if (bytes.Length > MaxPreviewBytes)
            {
                preview += $"\n… ({bytes.Length - MaxPreviewBytes:N0} more bytes)";
            }

            IReadOnlyList<CheckOutcome> checks = [];
            List<ExtractedValue>? extractions = null;
            if (prepared.WaitForReply)
            {
                checks = prepared.Checker.Explain(null, bytes);
                if (prepared.Extractor is { } extractor)
                {
                    extractions = [];
                    extractor.Apply(null, bytes.AsSpan(0, Math.Min(bytes.Length, ResponseExtractor.MaxBodyBytes)), variables, extractions);
                }
            }

            return new ProbeResult(null, null, latency, Headers(), preview, bytes.Length, checks.All(c => c.Passed), null, extractions)
            {
                Checks = [.. checks.Select(c => c with { Actual = masker(c.Actual) })],
                StatusMatches = true,
                StatusLabel = prepared.WaitForReply ? RequestResult.ReplyStatus : RequestResult.SentStatus,
            };
        }
        finally
        {
            await WebSocketConnections.CloseAsync(socket, CancellationToken.None).ConfigureAwait(false);
        }

        List<KeyValuePair<string, string>> Headers() =>
            (connection.HandshakeHeaders ?? []).Select(h => new KeyValuePair<string, string>(h.Key, masker(h.Value)!)).ToList();
    }
}

