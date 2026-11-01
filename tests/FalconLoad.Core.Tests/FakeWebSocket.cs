using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using FalconLoad.Core.Engine;

namespace FalconLoad.Core.Tests;

/// <summary>
/// Hands out <see cref="FakeWebSocket"/>s instead of connecting anywhere. <paramref name="respond"/> turns each message
/// a test sends into the server's reply (null for no reply at all), and <paramref name="connect"/> may throw to fail the handshake.
/// </summary>
internal sealed class FakeWebSocketConnector(Func<string, string?> respond, Action<Uri>? connect = null) : IWebSocketConnector
{
    public ConcurrentQueue<(Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Headers)> Connections { get; } = new();

    public ConcurrentQueue<string> Sent { get; } = new();

    public ConcurrentBag<FakeWebSocket> Sockets { get; } = [];

    /// <summary>Replies are cut into frames of this many bytes, to exercise reassembly. 0 sends whole messages.</summary>
    public int FrameSize { get; set; }

    /// <summary>Replies take this long, so a timeout test doesn't need a server that never answers.</summary>
    public TimeSpan ReplyDelay { get; set; }

    /// <summary>When set, the server closes the connection with this status instead of replying.</summary>
    public WebSocketCloseStatus? CloseWith { get; set; }

    public Task<WebSocketConnection> ConnectAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, bool collectHandshake, CancellationToken cancellationToken)
    {
        connect?.Invoke(uri);
        Connections.Enqueue((uri, headers));
        var socket = new FakeWebSocket(this, respond);
        Sockets.Add(socket);
        var handshake = collectHandshake
            ? new WebSocketConnection(socket, HttpStatusCode.SwitchingProtocols, [new("Upgrade", "websocket"), new("Sec-WebSocket-Accept", "fake")])
            : new WebSocketConnection(socket);
        return Task.FromResult(handshake);
    }
}

internal sealed class FakeWebSocket(FakeWebSocketConnector owner, Func<string, string?> respond) : WebSocket
{
    private readonly Queue<byte[]> _pending = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly MemoryStream _partial = new();
    private byte[]? _current;
    private int _offset;
    private WebSocketState _state = WebSocketState.Open;
    private bool _closeQueued;

    public int Closed { get; private set; }

    private WebSocketCloseStatus? _closeStatus;
    private string? _closeDescription;

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;

    public override string? CloseStatusDescription => _closeDescription;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        _partial.Write(buffer.Array!, buffer.Offset, buffer.Count);
        if (!endOfMessage)
        {
            return Task.CompletedTask;
        }

        var text = Encoding.UTF8.GetString(_partial.ToArray());
        _partial.SetLength(0);
        owner.Sent.Enqueue(text);
        if (owner.CloseWith is not null)
        {
            _closeQueued = true;
            _available.Release();
        }
        else if (respond(text) is { } reply)
        {
            _pending.Enqueue(Encoding.UTF8.GetBytes(reply));
            _available.Release();
        }

        return Task.CompletedTask;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null)
        {
            await _available.WaitAsync(cancellationToken);
            if (owner.ReplyDelay > TimeSpan.Zero)
            {
                await Task.Delay(owner.ReplyDelay, cancellationToken);
            }

            if (_closeQueued)
            {
                _state = WebSocketState.CloseReceived;
                _closeStatus = owner.CloseWith;
                _closeDescription = "Going away";
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, owner.CloseWith, "Going away");
            }

            _current = _pending.Dequeue();
            _offset = 0;
        }

        var remaining = _current.Length - _offset;
        var count = Math.Min(remaining, buffer.Count);
        if (owner.FrameSize > 0)
        {
            count = Math.Min(count, owner.FrameSize);
        }

        _current.AsSpan(_offset, count).CopyTo(buffer.AsSpan());
        _offset += count;
        var end = _offset == _current.Length;
        if (end)
        {
            _current = null;
        }

        return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        Closed++;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Abort() => _state = WebSocketState.Aborted;

    public override void Dispose()
    {
        if (_state is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            _state = WebSocketState.Aborted;
        }
    }
}
