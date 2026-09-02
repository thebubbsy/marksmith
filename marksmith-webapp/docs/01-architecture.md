# Marksmith.WebApp v1 — Architecture

> Deliverable 01 of 12. Companion docs: [OT specification](02-ot-spec.md), [WebSocket protocol](03-websocket-protocol.md), [OpenAPI](04-openapi.yaml), [Session management](05-session-management.md), [SDK API](06-sdk-api.md), [Wireframes](07-wireframes.md), [Security model](08-security-model.md), [Performance budget](09-performance-budget.md), [Deployment](10-deployment.md), [Phase 2 roadmap](11-roadmap.md), [Component diagrams](12-component-diagrams.md).

## 1. Design goal

Marksmith.WebApp v1 is a browser-based DOCX editor with real-time collaboration, built on **server-sequenced Operational Transformation (OT)** — deliberately **not** CRDT, **not** Yjs — with the existing MarkSmith Core engine treated as a **black box that is never modified**.

The system accepts the hard truths that a naive architecture would fight:

| Old assumption | Hard truth | v1 decision |
|---|---|---|
| CRDT + OOXML can coexist | They fight each other (CRDT wants a flat mutable model; OOXML is a hierarchical XML package) | **OT with server sequencing**; the server is the single orderer |
| Session must be stateless | Impossible for collaboration | **Stateful sessions** with persistence + eviction |
| Delta rendering exists | It doesn't — the engine renders the whole doc | **Full re-render** after each batch, cached; batched client edits keep it rare |
| Mutation API can be everything | Too complex without Core surgery | **Limited op set** applied via `DocumentFormat.OpenXml` public API only |
| WebSockets are an extension | They're a new subsystem | **Dedicated WS hub** as a first-class part of the server |
| iframe mode is easy | It's a nightmare | **Two modes**: standalone (full) + iframe (documented limitations) |
| Undo/redo is client-side | Must be server-side (shared state) | **Server-side inverse ops** from the operation log |

## 2. System overview

```
┌─────────────────────────────┐        ┌──────────────────────────────────────────────────────┐
│  Browser                     │        │  Marksmith.WebApp Server (.NET 8)                    │
│                              │  REST  │                                                      │
│  React 18 + TS client        │ ─────▶ │  ┌───────────────────────────────────────────────┐  │
│  ┌────────────────────────┐  │  JWT   │  │  REST API (session lifecycle, upload, save)   │  │
│  │ <MarksmithEditor />    │  │        │  └───────────────────────────────────────────────┘  │
│  │  EditorSurface (DOM)   │  │        │                                                      │
│  │  OpBuffer (200ms)      │  │  WS    │  ┌───────────────────────────────────────────────┐  │
│  │  CollabClient (OT rebase)│ ─────▶ │  │  WebSocket Hub (auth, groups, broadcast,       │  │
│  │  presence / comments / │  │        │  │  heartbeat, backpressure)                     │  │
│  │  track changes          │  │        │  └────────────────────────┬──────────────────────┘  │
│  └────────────────────────┘  │        │                           │                         │
│                              │        │  ┌────────────────────────▼──────────────────────┐  │
│  (optimistic ops applied     │        │  │  Session Manager (20 sessions, LRU eviction) │  │
│   locally; rebased against   │        │  │  ┌─────────────────────────────────────────┐ │  │
│   remote ops via mirrored    │        │  │  │ DocumentSession (per document)          │ │  │
│   transform)                 │        │  │  │  in-memory WordprocessingDocument      │ │  │
│                              │        │  │  │  OperationLog (append-only, seq'd)     │ │  │
│                              │        │  │  │  RenderCache (full HTML)               │ │  │
│                              │        │  │  │  timers: autosave 30s / idle 15min     │ │  │
│                              │        │  │  └──────────────┬──────────────────────────┘ │  │
│                              │        │  └─────────────────┼─────────────────────────────┘  │
│                              │        │                    │                               │
│                              │        │  ┌─────────────────▼─────────────────────────────┐  │
│                              │        │  │ SessionStore (atomic DOCX snapshots on disk) │  │
│                              │        │  └───────────────────────────────────────────────┘  │
│                              │        │                                                      │
│                              │        │  MarkSmith.Core — untouched black box.              │
│                              │        │  (v1 never references it; OOXML mutations go       │
│                              │        │   through DocumentFormat.OpenXml 3.1.0 directly)   │
└─────────────────────────────┘        └──────────────────────────────────────────────────────┘
```

## 3. Layer responsibilities

### 3.1 Client (React 18 + TypeScript, vanilla fallback)

* `EditorSurface` — contenteditable WYSIWYG surface. Converts every user edit to operations; applies them optimistically to the DOM.
* `CollabClient` — the OT pipeline client side: batches ops every 200ms (`OpBuffer`), tracks `baseSeq`, rebases pending local ops against incoming remote ops (mirrored transform), handles acks/rejections, sends presence.
* `Toolbar` / `CommentsPanel` / `TrackChangesPanel` / `PresenceRail` — the v1 operation set only; anything not in the set is not in the UI.
* SDK (`sdk/index.ts`) — `<MarksmithEditor />` React component, `MarksmithEditor.init(el, opts)` vanilla entry, and `mountIframe` with a postMessage bridge.

### 3.2 Collaboration server (.NET 8, ASP.NET Core)

* **WebSocket Hub** (`Web/WsHub.cs`) — connection groups per session, JWT handshake, heartbeat, per-connection FIFO send queues with kick-on-backpressure.
* **Session Manager** (`Sessions/SessionManager.cs`) — registry with per-session semaphores (serialization), LRU eviction at 20 concurrent sessions, idle sweep at 15 min.
* **DocumentSession** (`Sessions/DocumentSession.cs`) — the stateful unit: in-memory OOXML + operation log + render cache + persistence timers.
* **OpApplier** (`Documents/OpApplier.cs`) — the only place that touches the document; every op type maps to OpenXml SDK public API calls.
* **HtmlRenderer** (`Rendering/HtmlRenderer.cs`) — full DOCX→HTML render (the v1 rendering strategy), cached per session.
* **DocxValidator** (`Documents/DocxValidator.cs`) — schema validation after every batch; rollback on failure.
* **SessionStore** (`Sessions/SessionStore.cs`) — atomic snapshot writes (temp + rename), sidecar JSON with the captured seq.

### 3.3 Core Engine — untouched

MarkSmith.Core is not referenced by the WebApp server. Its only role in this architecture is as the product's existing pipeline (markdown→docx generation, desktop UI). The WebApp uses the **same package** Core pins (`DocumentFormat.OpenXml` 3.1.0) and the SDK's public API directly — that is the entire "core as a black box" boundary: we call the SDK, never Core internals, and never modify Core.

## 4. The collaboration model (OT, server-sequenced)

1. The server owns one **append-only operation log** per session, with strictly increasing sequence numbers.
2. A client sends a **batch** of ops with the `baseSeq` it was built against.
3. The server transforms each incoming op against every op in the log with `seq > baseSeq` (the concurrent window) — one-way transform (`Ot/Transform.cs`).
4. Transformed ops are applied to the in-memory OOXML, validated, appended to the log, and the full HTML is re-rendered into the cache.
5. The origin client gets an **ack** with the exact sequenced entries; all other clients get an **ops broadcast**; they apply the same ops to their local DOM via the mirrored transform, so everyone converges on the same order.
6. Undo/redo are server-side: the client sends an `undo` message, the server derives inverse ops from the log and sequences them like any other batch.

Rationale for OT over CRDT (see also `docs/02-ot-spec.md` §8): the op set is small and well-understood; the server can validate every mutation against the live OOXML before committing; DOCX is an opaque package, so a CRDT that replicates fine-grained text state would still need a full OOXML reconciliation step — the server-sequenced log makes that step the only source of truth.

## 5. Consistency, atomicity, and failure semantics

* **Batch atomicity**: a batch either fully sequences or is rejected; on any mid-batch failure the in-memory document is restored from the pre-batch snapshot.
* **Validation**: every batch is schema-validated (`OpenXmlValidator`) after applying; a schema failure also rolls back and rejects.
* **No-ops**: ops that are satisfied by concurrency (deleting an already-deleted paragraph, accepting an already-accepted change) are sequenced as no-ops, not rejected — intent is satisfied, clients converge.
* **Rejection**: genuinely malformed ops (out-of-range indices, missing payloads) reject the batch with an error; the client rolls back its optimistic UI and resyncs.
* **Crash recovery**: the SessionStore keeps the latest snapshot + seq; on restart, sessions resume from the newest snapshot with the log resuming after that seq.

## 6. Key files map

| Concern | Server | Client |
|---|---|---|
| Op model / wire contract | `Ot/Operation.cs` | `collab/protocol.ts` |
| Transform functions | `Ot/Transform.cs` | `collab/transform.ts` |
| Operation log | `Ot/OperationLog.cs` | — (server-owned) |
| OOXML mutation | `Documents/OpApplier.cs` | `editor/domApplier.ts` |
| Full render | `Rendering/HtmlRenderer.cs` | — (server HTML) |
| Sessions | `Sessions/DocumentSession.cs`, `SessionManager.cs` | — |
| Persistence | `Sessions/SessionStore.cs` | — |
| WS protocol | `Web/WsProtocol.cs`, `WsHub.cs`, `WsConnection.cs` | `collab/WsClient.ts` |
| REST | `Web/RestEndpoints.cs` | `sdk/index.ts` |
| Editor UI | — | `editor/*`, `sdk/MarksmithEditor.tsx` |
| Theme | — | `styles/theme.css` |
