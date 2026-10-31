using System.Net;
using System.Net.WebSockets;

namespace FalconLoad.Core.Engine;

/// <summary>A connected WebSocket and, when the handshake was observed, the headers the server answered it with.</summary>
public sealed record WebSocketConnection(WebSocket Socket, HttpStatusCode? HandshakeStatus = null, IReadOnlyList<KeyValuePair<string, string>>? HandshakeHeaders = null);

/// <summary>
/// Opens WebSocket connections for the engine. The default implementation uses <see cref="ClientWebSocket"/>; tests
/// supply one that hands out fake sockets, so no test needs the network.
/// </summary>
public interface IWebSocketConnector
{
    /// <param name="headers">Headers to send with the handshake. <c>Sec-WebSocket-Protocol</c> asks for sub-protocols.</param>
    /// <param name="collectHandshake">Whether to keep the handshake response's status and headers, for "Send once".</param>
    Task<WebSocketConnection> ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, bool collectHandshake, CancellationToken cancellationToken);
}

/// <summary>
/// Connects with <see cref="ClientWebSocket"/> over the run's shared <see cref="HttpMessageInvoker"/>, so the handshake
/// uses the same connection settings, timeouts and TLS options as the HTTP requests.
/// </summary>
public sealed class ClientWebSocketConnector(HttpMessageInvoker invoker) : IWebSocketConnector
{
    public async Task<WebSocketConnection> ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, bool collectHandshake, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.CollectHttpResponseDetails = collectHandshake;
            foreach (var (name, value) in headers)
            {
                if (name.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var protocol in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        socket.Options.AddSubProtocol(protocol);
                    }
                }
                else
                {
                    socket.Options.SetRequestHeader(name, value);
                }
            }

            await socket.ConnectAsync(uri, invoker, cancellationToken).ConfigureAwait(false);
            if (!collectHandshake)
            {
                return new WebSocketConnection(socket);
            }

            var responseHeaders = socket.HttpResponseHeaders?
                .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))
                .ToList() ?? [];
            return new WebSocketConnection(socket, socket.HttpStatusCode, responseHeaders);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
