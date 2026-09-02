# Phase 2 Roadmap — Marksmith.WebApp

> Deliverable 11 of 12. Everything here was deliberately excluded from v1. Each item states the
> v1 blocker it removes, in rough priority order.

## 1. Correctness & collaboration depth

| Item | Removes v1 limitation | Notes |
|---|---|---|
| Split-span formatting transforms | A partially deleted range collapses to one span | Transform algebra for non-contiguous ranges; tests for each pair |
| Redo stack (server-side) | `redo` is a client re-send | Store per-client forward/inverse pairs in the log |
| Deeper undo (across autosave compaction) | Undo depth bounded by retained log tail | Archive compacted ops to disk; undo replays inverse from archive |
| Offline editing | No offline support (reconnect drops pending ops) | Client-side op queue with sync on reconnect; transform against server state (careful: this reintroduces CRDT-grade complexity — do it last) |
| Multi-level lists | v1 lists are simple | numbering.xml structure + transforms for list ops |

## 2. OOXML surface expansion

| Item | v1 blocker | Notes |
|---|---|---|
| Table cell editing | Text ops address body blocks only | Extend position model to (table, row, col, offset) + transforms |
| Table merge/split | Excluded | vMerge/vMergeContinue, gridSpan + transforms |
| Track-changes format decisions | Accept/reject only insert/delete | rPrChange-aware accept/reject per change |
| Remote image URLs | data-URI only (no SSRF surface) | Allowlisted fetch proxy, size caps, caching |
| Page layout (margins, page breaks, headers/footers) | Not editable in v1 | sectPr ops + renderer support |
| Comments replies | Single comment + resolve | Threaded replies (comment part already supports them) |
| DrawingML / diagrams (SmartArt, charts) | Explicitly out of scope | Render + select-only in v2a; editing via Core's diagram pipeline is a v2b question (Core remains a black box) |

## 3. Rendering & performance

| Item | v1 blocker | Notes |
|---|---|---|
| Incremental block rendering | Full re-render every batch | Cache per block; invalidate only dirty blocks; text-only edits skip structural render |
| Render worker offload | Render blocks the request pipeline | Background render queue with ETag on the HTML cache |
| Image thumbnailing | Full-size data URIs in ops and HTML | Server-side thumbnail part; op carries thumbnail + fetch URL |
| Large-doc pagination | 5 MB doc renders whole | Viewport-based block rendering with placeholder skeletons |
| Compression | None | WS permessage-deflate; gzip static assets |

## 4. Scale & operations

| Item | v1 blocker | Notes |
|---|---|---|
| Redis session store | Single-instance, disk-local snapshots | `SessionStore` interface already abstracts this; implement Redis-backed store; sessions resume from Redis on any instance |
| Sticky routing + blue/green | Single instance | LB cookie pinning by sessionId; zero-downtime deploys |
| Multi-region | None | Document sharding by `doc` claim; per-shard collab cluster |
| Horizontal WS fan-out | One hub | Redis pub/sub for cross-instance ops broadcast (Phase 2a) |
| Observability | Basic logs + health | OpenTelemetry: batch latency, re-render time, per-session memory, WS drop reasons; audit log of all ops |
| Object store snapshots | Local volume | S3/Azure Blob adapter for `SessionStore` (encryption at rest) |

## 5. Governance & identity

| Item | v1 blocker | Notes |
|---|---|---|
| Enterprise SSO | Dev token endpoint only | OIDC/SAML federation; token exchange with the collab server |
| Roles & ACLs | Any authenticated user edits | owner/editor/viewer per document; enforced in `SessionManager` and `WsHub` |
| Sharing & invites | No sharing model | Host-app concept; collab server consumes host-issued tokens with roles |
| Audit log | Basic logging only | Append-only op + access log, exportable |
| Rate limiting | None | Per-user token bucket on batch/WS messages |
| Tenant isolation at scale | `doc` claim only | Namespace sessions by tenant id in the store path/Redis keys |

## 6. SDK & UX

| Item | v1 blocker | Notes |
|---|---|---|
| Cookie/subprotocol WS handshake | Token in query string | Avoids proxy-log leakage; enables iframe without URL-hash config |
| iframe clipboard + drag-drop bridge | Documented limitations | Host-side event capture → op bridge; permission policy alignment |
| Inline presence cursors/selection shading | Rail + caret markers | DOM overlay mapped via positions.ts (already position-aware) |
| Keyboard shortcuts | Minimal | Full shortcut map (Ctrl+B etc. → ops) |
| Accessibility pass | Basic semantics | ARIA roles, focus management, live regions for remote changes |
| IME composition robustness | Single-op commit | Composing range → one insertText per commit (already; expand to composition-aware batching) |
| Editor framework upgrade | contenteditable baseline | Evaluate ProseMirror/Lexical as the DOM layer *behind* the same OT/op pipeline (the OT contract does not change) |

## 7. Sequencing guidance

1. **Phase 2a (correctness first):** split-span transforms, redo, deeper undo, table cell
   editing, incremental rendering, Redis session store + sticky routing.
2. **Phase 2b (scale):** object store snapshots, WS fan-out, multi-region, observability.
3. **Phase 2c (governance):** SSO, ACLs, audit log, rate limiting.
4. **Phase 2d (UX):** presence shading, shortcuts, a11y, editor-framework evaluation.

Each phase keeps the v1 invariants: Core is a black box, the server sequences ops, batches are
atomic, and the transform algebra is the single source of convergence truth.
