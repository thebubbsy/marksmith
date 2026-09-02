using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using MarkSmith.WebApp.Server.Auth;
using MarkSmith.WebApp.Server.Ot;
using MarkSmith.WebApp.Server.Sessions;

namespace MarkSmith.WebApp.Server.Web;

/// <summary>
/// WebSocket hub: accepts connections, authenticates via JWT (query param), binds each socket to
/// a session, routes inbound messages to the session pipeline, and broadcasts sequenced
/// operations to every client in the session group. Also owns the heartbeat loop.
///
/// v1 concurrency model: one <see cref="SessionManager"/> instance is shared; each session has
/// its own gate, so batches from different sessions run in parallel and batches within a session
/// are serialized. Presence messages bypass the session gate (they mutate no document state).
/// </summary>
public sealed class WsHub : IAsyncDisposable
{
    private readonly SessionManager _sessions;
    private readonly JwtTokenService _jwt;
    private readonly ILogger<WsHub> _log;

    // sessionId -> connections (all users in that document)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WsConnection>> _groups = new();
    private readonly ConcurrentDictionary<WsConnection, byte> _all = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _heartbeatTimer;

    public WsHub(SessionManager sessions, JwtTokenService jwt, ILogger<WsHub> log)
    {
        _sessions = sessions;
        _jwt = jwt;
        _log = log;
        _heartbeatTimer = new Timer(_ => HeartbeatAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>Handles a single WebSocket session (Runs the full lifecycle for one connection).</summary>
    public async Task HandleAsync(WebSocket socket, string? token, string? sessionId)
    {
        var principal = _jwt.Validate(token ?? "");
        var userId = JwtTokenService.UserId(principal);
        var docId = JwtTokenService.DocumentId(principal);
        if (userId is null || docId is null || sessionId != docId)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", CancellationToken.None);
            return;
        }

        // Bind the socket to its session (starting/resuming the session as needed).
        var conn = new WsConnection(socket) { ClientId = userId, SessionId = sessionId };

        try
        {
            var session = await _sessions.StartAsync(sessionId, userId, uploadedDocx: null);
            var group = _groups.GetOrAdd(sessionId, _ => new ConcurrentDictionary<string, WsConnection>());
            group[userId] = conn; // one socket per user per session (v1)
            _all[conn] = 0;

            // Send the welcome frame with the current full render + sequence.
            var html = await _sessions.WithSessionAsync(sessionId, s => s.RenderedHtml());
            var seq = await _sessions.WithSessionAsync(sessionId, s => s.RenderedHtmlAtSeq());
            var welcome = new WelcomeMessage(sessionId, userId, seq, html, null);
            conn.TrySend(WsProtocol.Serialize(welcome));

            _log.LogInformation("WS connected: user={User} session={Session} groupSize={GroupSize}",
                userId, sessionId, group.Count);

            // Inbound loop.
            while (conn.IsOpen)
            {
                var frame = await conn.ReceiveAsync(_cts.Token);
                if (frame is null) break;

                var json = Encoding.UTF8.GetString(frame);
                var message = WsProtocol.Parse(json);
                if (message is null)
                {
                    SendError(conn, "bad_frame", "malformed message");
                    continue;
                }

                switch (message)
                {
                    case BatchMessage batch:
                        await HandleBatchAsync(sessionId, userId, batch, conn);
                        break;
                    case UndoMessage undo:
                        await HandleUndoAsync(sessionId, userId, undo, conn);
                        break;
                    case PresenceMessage presence:
                        BroadcastPresence(sessionId, userId, presence);
                        break;
                    case ResyncMessage:
                        await HandleResyncAsync(sessionId, conn);
                        break;
                    case PingMessage:
                        conn.TrySend(WsProtocol.Serialize(new PongMessage()));
                        break;
                }
            }
        }
        finally
        {
            await DetachAsync(sessionId, conn);
            await conn.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ handlers

    private async Task HandleBatchAsync(string sessionId, string userId, BatchMessage batch, WsConnection origin)
    {
        try
        {
            var result = await _sessions.WithSessionAsync(sessionId, s =>
                s.ApplyBatch(userId, batch.BaseSeq, batch.Ops));

            if (!result.Ok)
            {
                SendError(origin, "batch_rejected", result.Error ?? "batch rejected");
                return;
            }

            // Ack to the origin with the exact sequenced entries (transformed).
            origin.TrySend(WsProtocol.Serialize(new AckMessage(batch.BatchId, batch.BaseSeq,
                result.Entries.Select(e => new AckEntry(e.Seq, e.Op, e.WasNoOp)).ToList())));

            // Broadcast to everyone else in the group.
            if (result.Entries.Count > 0)
            {
                BroadcastOps(sessionId, origin, new OpsMessage(
                    result.Entries.Select(e => new OpsEntry(e.Seq, e.ClientId, e.Op)).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "batch failed for session {Session}", sessionId);
            SendError(origin, "batch_error", "server error applying batch");
        }
    }

    private async Task HandleUndoAsync(string sessionId, string userId, UndoMessage undo, WsConnection origin)
    {
        try
        {
            var result = await _sessions.WithSessionAsync(sessionId, s => s.ApplyBatch(
                userId, /*baseSeq*/ 0, new[]
                {
                    new Operation { Id = $"undo-{Guid.NewGuid():N}", ClientId = userId, Type = OpType.Undo, UptoSeq = undo.UptoSeq },
                }));
            if (!result.Ok)
            {
                SendError(origin, "undo_rejected", result.Error ?? "nothing to undo");
                return;
            }
            origin.TrySend(WsProtocol.Serialize(new AckMessage("undo", 0,
                result.Entries.Select(e => new AckEntry(e.Seq, e.Op, e.WasNoOp)).ToList())));
            if (result.Entries.Count > 0)
            {
                BroadcastOps(sessionId, origin, new OpsMessage(
                    result.Entries.Select(e => new OpsEntry(e.Seq, e.ClientId, e.Op)).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "undo failed for session {Session}", sessionId);
            SendError(origin, "undo_error", "server error");
        }
    }

    private async Task HandleResyncAsync(string sessionId, WsConnection conn)
    {
        try
        {
            var html = await _sessions.WithSessionAsync(sessionId, s => s.RenderedHtml());
            var seq = await _sessions.WithSessionAsync(sessionId, s => s.RenderedHtmlAtSeq());
            conn.TrySend(WsProtocol.Serialize(new WelcomeMessage(sessionId, conn.ClientId ?? "", seq, html, null)));
        }
        catch { }
    }

    private void BroadcastPresence(string sessionId, string userId, PresenceMessage presence)
    {
        if (_groups.TryGetValue(sessionId, out var group))
        {
            var frame = WsProtocol.Serialize(new PresenceBroadcast(userId, presence.Caret, presence.Selection));
            foreach (var (_, conn) in group)
            {
                if (conn.ClientId != userId) conn.TrySend(frame);
            }
        }
    }

    private void BroadcastOps(string sessionId, WsConnection origin, OpsMessage message)
    {
        if (!_groups.TryGetValue(sessionId, out var group)) return;
        var frame = WsProtocol.Serialize(message);
        foreach (var (_, conn) in group)
        {
            if (ReferenceEquals(conn, origin)) continue;
            if (!conn.TrySend(frame))
            {
                _log.LogWarning("kicking slow consumer {User} in session {Session}", conn.ClientId, sessionId);
                _ = conn.CloseAsync(WebSocketCloseStatus.PolicyViolation, "slow consumer");
            }
        }
    }

    private static void SendError(WsConnection conn, string code, string message)
    {
        conn.TrySend(WsProtocol.Serialize(new ErrorMessage(code, message)));
    }

    private Task DetachAsync(string? sessionId, WsConnection conn)
    {
        _all.TryRemove(conn, out _);
        if (sessionId is not null && _groups.TryGetValue(sessionId, out var group))
        {
            if (conn.ClientId is not null) group.TryRemove(conn.ClientId, out _);
            if (group.IsEmpty) _groups.TryRemove(sessionId, out _);
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ heartbeat

    private void HeartbeatAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var conn in _all.Keys)
        {
            // No inbound frame for 60s => dead peer.
            if (now - conn.LastReceiveUtc > TimeSpan.FromSeconds(60))
            {
                _ = conn.CloseAsync(WebSocketCloseStatus.NormalClosure, "heartbeat timeout");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _heartbeatTimer.Dispose();
        _cts.Cancel();
        foreach (var conn in _all.Keys)
        {
            await conn.DisposeAsync();
        }
    }
}

/// <summary>Presence broadcast to peers (not part of the client->server vocabulary).</summary>
public sealed record PresenceBroadcast(string ClientId, CaretPosition? Caret, SelectionSpan? Selection)
    : WsMessageBase("presence");
