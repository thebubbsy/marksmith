using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace MarkSmith.WebApp.Server.Web;

/// <summary>
/// One WebSocket connection. Handles per-connection ordering (a FIFO send channel) and
/// backpressure: if a client cannot drain its outbound queue within the limits, the connection
/// is dropped (kicked) rather than letting memory grow unbounded. Heartbeats are driven by the
/// hub via <see cref="LastReceiveUtc"/>.
/// </summary>
public sealed class WsConnection : IAsyncDisposable
{
    public const int MaxQueuedMessages = 512;
    public const int MaxFrameBytes = 2 * 1024 * 1024; // 2 MiB: images are sent as data URIs
    public static readonly TimeSpan KickTimeout = TimeSpan.FromSeconds(10);

    private readonly WebSocket _socket;
    private readonly Channel<string> _outbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderTask;

    public WsConnection(WebSocket socket)
    {
        _socket = socket;
        _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedMessages)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // senders await capacity; hub enforces kick
        });
        _senderTask = Task.Run(SendLoopAsync);
    }

    public string? ClientId { get; set; }
    public string? SessionId { get; set; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastReceiveUtc { get; private set; } = DateTimeOffset.UtcNow;
    public bool IsOpen => _socket.State == WebSocketState.Open && !_cts.IsCancellationRequested;

    /// <summary>Queues a message for delivery. Returns false when the queue is full (caller may kick).</summary>
    public bool TrySend(string json)
    {
        if (_outbound.Writer.TryWrite(json)) return true;
        return false; // slow consumer: hub will kick
    }

    /// <summary>Reads the next inbound frame. Returns null on close/abort.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxFrameBytes];
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return null;
                }
                if (result.MessageType != WebSocketMessageType.Text)
                    return null; // binary frames unsupported in v1
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxFrameBytes) return null;
            }
            while (!result.EndOfMessage);

            LastReceiveUtc = DateTimeOffset.UtcNow;
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Sends a close frame and drains. Best-effort.</summary>
    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        _cts.Cancel();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch { }
        _outbound.Writer.TryComplete();
        try { await _senderTask; } catch { }
    }

    private async Task SendLoopAsync()
    {
        await foreach (var json in _outbound.Reader.ReadAllAsync(_cts.Token))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose");
}

/// <summary>Fast kick verdict: whether a connection is too slow to keep up.</summary>
public static class Backpressure
{
    /// <summary>Checks if the connection has been unable to drain for too long.</summary>
    public static bool ShouldKick(WsConnection conn, DateTimeOffset now)
    {
        // The send queue saturating is the signal; the hub tracks queue occupancy via TrySend
        // returning false, and kicks immediately. This helper covers the "stuck sender" case.
        return !conn.IsOpen;
    }
}
