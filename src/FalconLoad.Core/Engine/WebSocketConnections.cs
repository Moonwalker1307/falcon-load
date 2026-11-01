using System.Net.WebSockets;

namespace FalconLoad.Core.Engine;

/// <summary>
/// The WebSocket connections one virtual user holds, one per address, kept open across its requests and iterations
/// like a browser keeps a socket open. Virtual users don't share connections, so each one's messages and replies stay
/// in order on its own socket.
/// </summary>
internal sealed class WebSocketConnections(IWebSocketConnector connector) : IAsyncDisposable
{
    /// <summary>How long a clean close is given before the socket is dropped; a server that doesn't answer shouldn't hold up the run.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    // A test rarely talks to more than one or two WebSocket addresses, so a list beats a dictionary here.
    private readonly List<(Uri Uri, WebSocket Socket)> _open = [];

    /// <summary>The open connection to <paramref name="uri"/>, or a new one. A connection that closed is replaced.</summary>
    public async ValueTask<WebSocket> GetAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Uri == uri)
            {
                if (_open[i].Socket.State == WebSocketState.Open)
                {
                    return _open[i].Socket;
                }

                _open[i].Socket.Dispose();
                _open.RemoveAt(i);
                break;
            }
        }

        var connection = await connector.ConnectAsync(uri, headers, collectHandshake: false, cancellationToken).ConfigureAwait(false);
        _open.Add((uri, connection.Socket));
        return connection.Socket;
    }

    /// <summary>Closes the connection to <paramref name="uri"/> cleanly, or drops it when the server doesn't answer the close in time.</summary>
    public async ValueTask CloseAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Uri == uri)
            {
                var socket = _open[i].Socket;
                _open.RemoveAt(i);
                await CloseAsync(socket, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Forgets a connection that failed, so the next request opens a new one.</summary>
    public void Drop(Uri uri)
    {
        for (var i = 0; i < _open.Count; i++)
        {
            if (_open[i].Uri == uri)
            {
                _open[i].Socket.Dispose();
                _open.RemoveAt(i);
                return;
            }
        }
    }

    public static async ValueTask CloseAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(CloseTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The connection is being thrown away either way.
        }
        finally
        {
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, socket) in _open)
        {
            await CloseAsync(socket, CancellationToken.None).ConfigureAwait(false);
        }

        _open.Clear();
    }
}
