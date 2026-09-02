# Component Diagrams — Marksmith.WebApp v1

> Deliverable 12 of 12. Implementation maps to the files listed on each diagram.

## 1. Server components

```
┌───────────────────────────── MarkSmith.WebApp.Server (net8.0) ─────────────────────────────┐
│                                                                                              │
│  Program.cs ─── wires DI, auth, CORS, WS endpoint, REST, static client dist                  │
│       │                                                                                      │
│       ├──────────────▶ Web/WsHub.cs            ── Web/WsProtocol.cs   (message contracts)    │
│       │                  │ groups/broadcast/       │                                          │
│       │                  │ heartbeat/backpressure  │                                          │
│       │                  ▼                         ▼                                          │
│       │              Web/WsConnection.cs    ──  Ot/Operation.cs  (op model + JSON)           │
│       │                                                                                      │
│       ├──────────────▶ Web/RestEndpoints.cs ── Auth/JwtTokenService.cs (issue/validate)      │
│       │                                                                                      │
│       └──────────────▶ Sessions/SessionManager.cs                                            │
│                            │  registry · LRU eviction · per-session semaphore                │
│                            ▼                                                                │
│                        Sessions/DocumentSession.cs                                           │
│                            │  OperationLog (Ot/OperationLog.cs) · RenderCache · timers       │
│                            │  ── applies batches ──▶ Ot/Transform.cs (one-way transforms)    │
│                            │                                                                 │
│                            ├──▶ Documents/OpApplier.cs ──▶ DocumentFormat.OpenXml 3.1.0      │
│                            │        (the ONLY OOXML-touching code; Core untouched)           │
│                            ├──▶ Documents/DocxValidator.cs (schema after each batch)         │
│                            ├──▶ Rendering/HtmlRenderer.cs (full re-render → cache)          │
│                            └──▶ Sessions/SessionStore.cs (atomic snapshots + seq sidecar)    │
│                                                                                              │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 2. Client components

```
┌───────────────────────────── client (React 18 + TS + Vite) ──────────────────────────────┐
│                                                                                            │
│  main.tsx (sample app)                                                                    │
│    └── sdk/MarksmithEditor.tsx   (embeddable component; boot: token → session → WS)       │
│          │                                                                                 │
│          ├──▶ collab/CollabClient.ts   ──▶ collab/WsClient.ts  (reconnect + heartbeat)     │
│          │       │  baseSeq tracking · pending ops · rebase                                │
│          │       ├──▶ collab/OpBuffer.ts        (200 ms batch aggregation)                 │
│          │       └──▶ collab/transform.ts       (mirrored OT transform)                    │
│          │                                                                                 │
│          ├──▶ editor/EditorSurface.tsx  (contenteditable WYSIWYG)                          │
│          │       ├──▶ editor/positions.ts   (DOM ↔ block/offset mapping)                   │
│          │       └──▶ editor/domApplier.ts  (op → DOM mutation mirror)                     │
│          │                                                                                 │
│          ├──▶ editor/Toolbar.tsx  (v1 op set only)                                         │
│          ├──▶ editor/CommentsPanel.tsx · editor/TrackChangesPanel.tsx                      │
│          ├──▶ editor/presence.ts  (presence peers + colors)                                │
│          └── styles/theme.css   (CSS-variable theming)                                     │
│                                                                                            │
│  sdk/index.ts        — vanilla init() + mountIframe() + MarksmithEditorHandle              │
│  sdk/iframeBridge.ts — iframe host: config from URL hash, postMessage bridge               │
│                                                                                            │
│  collab/protocol.ts  — wire types shared by all of the above                               │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 3. Collaboration sequence (happy path)

```
Client A            Server (WsHub → Session)            Client B
   │  batch{baseSeq, ops}                                   │
   │──────────────────────────────────▶                     │
   │                     transform against concurrent       │
   │                     apply → validate → render → cache  │
   │  ack{entries}                                           │
   │◀──────────────────────────────────                      │
   │                                  ops{entries} ─────────▶│
   │                                  (apply via domApplier) │
   │  rebase pending local ops against entries               │
```

## 4. Batch rejection sequence (atomicity)

```
Client A            Server                                     Client A
   │  batch{…bad op…}                                              │
   │──────────────────────────────────▶                            │
   │   restore pre-batch snapshot (rollback)                       │
   │   error{batch_rejected, reason}                               │
   │◀──────────────────────────────────                            │
   │  rollback optimistic UI → send resync                         │
   │──────────────────────────────────▶                            │
   │  welcome{html, seq} (authoritative state)                     │
   │◀──────────────────────────────────                            │
```

## 5. Session lifecycle state machine

```
            upload / resume / blank
                     │
                     ▼
   ┌──────────────────────────────┐
   │  LOADED (in-memory)          │◀──────────┐
   │  · applies batches           │           │  (re)start
   │  · autosave every 30 s       │           │
   │  · render cache              │           │
   └──────────┬───────────────────┘           │
              │ idle > 15 min / LRU            │
              ▼                                │
   ┌──────────────────────────────┐  on demand │
   │  EVICTED (snapshot on disk)  │────────────┘
   │  memory freed; state in      │
   │  SessionStore (docx + seq)   │
   └──────────────────────────────┘
```

## 6. Test surface

| Suite | File | Covers |
|---|---|---|
| TransformTests | `Marksmith.WebApp.Tests/TransformTests.cs` | every transform rule + composition |
| OperationLogTests | `…/OperationLogTests.cs` | sequencing, resume, compaction, undo inverse |
| OpApplierTests | `…/OpApplierTests.cs` | real DOCX mutations via OpenXml SDK + schema validity |
| SessionManagerTests | `…/SessionManagerTests.cs` | lifecycle, atomic reject, convergence, persistence, undo |

Client-side contract tests (Phase 2a): mirror TransformTests in TS against `collab/transform.ts`.
