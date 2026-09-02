# WebSocket Protocol — Marksmith.WebApp v1

> Deliverable 03 of 12. The message vocabulary here is implemented by
> `server/Marksmith.WebApp.Server/Web/WsProtocol.cs` (C#) and mirrored in
> `client/src/collab/protocol.ts` (TypeScript).

## 1. Transport

* Endpoint: `ws(s)://host/ws?token=<JWT>&session=<sessionId>`
* The token is passed in the **query string** because the browser WebSocket API cannot set an
  `Authorization` header. The server validates the JWT during the handshake; a missing/invalid
  token is closed with `1008 (Policy Violation)`.
* Frames are **text JSON**, UTF-8, single message per frame (no fragmentation required; the
  server accepts fragmented text frames but not binary).
* Max frame size: **2 MiB** (images are sent as data URIs inside ops).
* The server also accepts the token in the `Authorization: Bearer` header for tooling that can
  set headers; the query param wins.

## 2. Handshake

```
Client                                Server
  |  GET /ws?token=…&session=…           |
  | ───────────────────────────────────▶ |  validate JWT + session match
  |                                      |  start/resume session
  |  welcome { sessionId, clientId,      |
  |           seq, html, docUrl? }       |
  | ◀─────────────────────────────────── |
```

`welcome.seq` is the current tip of the operation log; `welcome.html` is the full rendered
document (the client's initial state and resync payload).

## 3. Message catalog

### 3.1 Client → Server

**batch** — the core message.

```json
{
  "type": "batch",
  "batchId": "b-u1-7",
  "baseSeq": 42,
  "ops": [ { "id": "op-…", "clientId": "u1", "type": "insertText", "block": 0, "offset": 3, "text": "hi" } ]
}
```

`baseSeq` must be the client's last acked seq. The server transforms against the concurrent
window, sequences, applies, validates, re-renders.

**undo**

```json
{ "type": "undo", "uptoSeq": 45 }
```

Server derives inverse ops for the caller down to `uptoSeq` and sequences them.

**presence** (throttled by the client to ~200ms)

```json
{ "type": "presence", "caret": { "block": 2, "offset": 5 }, "selection": { "start": {…}, "end": {…} } }
```

**resync**

```json
{ "type": "resync" }
```

Requests a fresh `welcome` (drift recovery after a rejected batch, or after reconnect).

**ping**

```json
{ "type": "ping" }
```

### 3.2 Server → Client

**welcome** — initial state (on connect and on resync).

```json
{
  "type": "welcome",
  "sessionId": "doc-abc",
  "clientId": "u1",
  "seq": 42,
  "html": "<p>…</p>"
}
```

**ack** — to the origin client only, confirming its own batch with the exact sequenced entries.

```json
{
  "type": "ack",
  "batchId": "b-u1-7",
  "baseSeq": 42,
  "entries": [
    { "seq": 43, "op": { "id": "op-…", "clientId": "u1", "type": "insertText", "block": 0, "offset": 3, "text": "hi" }, "noOp": false }
  ]
}
```

The client drops the acked ops from its pending queue and advances `baseSeq` to the max acked
seq.

**ops** — broadcast to every other client in the session group.

```json
{
  "type": "ops",
  "entries": [ { "seq": 43, "clientId": "u1", "op": { … } } ]
}
```

Peers apply the ops to their local DOM via the mirrored transform.

**error**

```json
{ "type": "error", "code": "batch_rejected", "message": "deleteText: range out of bounds" }
```

Codes: `bad_frame`, `batch_rejected`, `batch_error`, `undo_rejected`, `undo_error`,
`kicked`.

**pong** — reply to ping.

**kicked** — sent just before the server closes a slow consumer (backpressure drop).

**presence** (server → peers) — presence rebroadcast:

```json
{ "type": "presence", "clientId": "u2", "caret": {…}, "selection": {…} }
```

## 4. Ordering and delivery guarantees

* The server sequences ops **per session** under a per-session semaphore; a batch's ops are
  applied atomically and appear in the log in order.
* `ack` and `ops` frames for a session are sent in seq order; each connection has a FIFO send
  queue, so a client never observes ops out of order.
* Presence frames are **not** ordered relative to ops and may be dropped under load (they are
  transient state, never authoritative).

## 5. Heartbeat

* The server pings every 30 s (`WsHub` heartbeat timer) — actually v1 uses the client-initiated
  ping + a receive-deadline: if no inbound frame arrives within 60 s, the connection is closed
  and reaped. The client sends `ping` every 30 s and expects `pong` within 40 s, else it forces
  a reconnect.
* KeepAlive frames are also set at the WebSocket layer (`KeepAliveInterval = 30s`) to defeat
  idle-proxies.

## 6. Backpressure

* Each connection has a bounded send queue (`MaxQueuedMessages = 512`).
* `TrySend` returning false means the consumer is too slow; the server kicks that connection
  (close code `1008`, reason `slow consumer`) so one slow client cannot pin the session's memory.
* Clients must therefore treat the socket as reconnectable: on reconnect the server sends a
  fresh `welcome`, and the client drops its pending queue (v1 policy — no replay across
  reconnect; ops lost in flight are recovered by the next user action and the server's
  authoritative re-render).

## 7. Reconnect sequence (client)

1. `close` event → schedule reconnect (exponential backoff 1s→15s, cap 8 attempts).
2. On `open` → server sends `welcome` (fresh html + seq).
3. Client replaces its DOM with the welcome html, resets `baseSeq`, clears pending ops.
4. Client re-sends presence; user edits resume the batch loop.

## 8. Security notes

* Token in query string can leak into logs/proxies; mitigate with short-lived tokens (8h) and
  TLS. Phase 2: `Sec-WebSocket-Protocol` subprotocol token or cookie-based handshake.
* Session id must match the JWT `doc` claim — enforced server-side at the handshake.
* Frame size capped at 2 MiB; ops are validated and schema-checked before commit.
