# Session Management Design — Marksmith.WebApp v1

> Deliverable 05 of 12. Implementation: `server/Marksmith.WebApp.Server/Sessions/`.

## 1. Why stateful sessions

Collaboration requires the server to hold the authoritative document between requests: the
in-memory `WordprocessingDocument`, the operation log, and the render cache. "Stateless" is not
an option (a stateless server would need to replay the whole log on every request — O(n) per
keystroke). Instead v1 accepts statefulness and controls its cost with **persistence** and
**eviction**.

## 2. Session object model

```
DocumentSession
 ├─ SessionId            (string; == the documentId the JWT binds to)
 ├─ OwnerId              (first creator; informational only)
 ├─ DocxDocument         (in-memory OOXML: MemoryStream + WordprocessingDocument)
 ├─ OperationLog         (append-only; LastSeq; entries after the last snapshot)
 ├─ RenderCache          (full HTML string + the seq it reflects)
 ├─ AutoSaveTimer        (every 30 s → SessionStore.WriteSnapshot)
 └─ IdleTimer            (every 60 s → evict when idle > 15 min)
```

Access is serialized by a per-session `SemaphoreSlim` held by the `SessionManager` (one batch at
a time per session; batches from different sessions run in parallel).

## 3. Lifecycle

### 3.1 Start

`POST /api/sessions` (or WS handshake) → `SessionManager.StartAsync(sessionId, userId, docx?)`:

1. If the session is already loaded, return it (idempotent resume).
2. If capacity is full (20), evict the least-recently-seen session first (LRU).
3. Document source precedence:
   a. **uploaded DOCX bytes** (starts fresh; snapshot written immediately),
   b. **persisted snapshot** on disk (resume; log resumes at the snapshot's seq),
   c. **blank document** (never seen before).
4. Start the timers.

### 3.2 Active

* Ops flow through `ApplyBatch` (transform → apply → validate → re-render → cache).
* `SaveToBytes()` and `RenderedHtml()` are the read surfaces.
* Every access bumps `LastActivity` (used by the idle timer and the LRU order).

### 3.3 Autosave

Every 30 s (and on close/eviction), the current bytes + tip seq are written atomically to disk
(temp file + rename, sidecar JSON with seq). Autosave never throws — persistence is
best-effort; a failed write just retries next tick.

### 3.4 Eviction (memory control)

Two triggers:

* **Idle**: no activity for 15 min → snapshot + dispose. Memory returns to the pool.
* **Capacity**: 20 concurrent sessions → the LRU session is evicted to admit the new one.

Eviction is invisible to clients: the next op/handshake **resumes from disk** (see 3.1).

### 3.5 Shutdown

On server stop, `PersistAllAsync` snapshots every live session. Cold sessions live on disk
already.

## 4. Persistence format

```
<sessionRoot>/
  <sessionId>.docx     # the DOCX snapshot (atomic writes)
  <sessionId>.json     # { sessionId, seq, savedAtUtc }
```

* Default root: `%TEMP%/marksmith-webapp/sessions` (configurable via `Sessions:Root`).
* Phase 2: object store adapter (S3/Azure Blob) behind the same `SessionStore` interface;
  deployments may mount a volume for the local adapter instead.

## 5. Crash recovery

| Failure | Recovery |
|---|---|
| Server restart | Sessions resume from the newest snapshot; log resumes after `seq`; clients reconnect and get a fresh `welcome` |
| Crash mid-snapshot-write | Atomic rename ⇒ the previous snapshot survives |
| Corrupt snapshot | `TryLoad` returns null ⇒ session starts blank (documented; the op log would have been compacted into the snapshot, so loss is bounded by the autosave interval) |
| Batch failure mid-apply | In-memory document restored from the pre-batch snapshot; batch rejected; clients resync |

## 6. Memory budget (per session)

| Component | Estimate |
|---|---|
| In-memory OOXML (medium doc, ~1 MB docx) | ~40–80 MB DOM |
| Render cache (HTML) | ~2–10 MB |
| Operation log (retained entries) | ~1–20 MB (compacted into snapshots) |
| **Total (typical)** | **~50–110 MB** — under the 250 MB ceiling |

20 sessions ⇒ ~1–2.2 GB worst case; with idle eviction the steady state is far lower. The 250
MB per-session ceiling is enforced by eviction *before* admitting new sessions, not by a hard
cap per document.

## 7. Operation log compaction

The log is append-only, but the retained tail is bounded: every autosave writes a snapshot that
captures state up to `seq`, and `OperationLog.CompactTo(seq)` drops the folded entries. The log
therefore holds only the **post-snapshot window** — enough for concurrent-batch transforms and
undo, without unbounded growth.

## 8. Undo/redo storage

Undo is server-side and uses the retained log: `RecentByClient(clientId, uptoSeq)` finds the
ops to invert. Because compaction bounds the retained tail, undo depth is bounded by the last
autosave interval's worth of ops for that client (documented v1 limitation).

## 9. Session identity & security

* `sessionId` must equal the JWT `doc` claim (tenant isolation); enforced at the WS handshake
  and implicitly by REST authorization (the `doc` claim).
* Sessions are not enumerable: `GET /api/sessions/{id}` only returns state for a valid `doc`
  claim (the sample UI's unrestricted GET is dev-only; production removes it or authorizes it).
* Eviction/close never deletes the snapshot — a document is only removed explicitly
  (`DELETE /api/sessions/{id}` + store delete) by an authorized owner (Phase 2: ACLs).
