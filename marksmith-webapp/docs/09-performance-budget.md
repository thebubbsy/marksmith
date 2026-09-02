# Performance Budget — Marksmith.WebApp v1

> Deliverable 09 of 12. Targets are deliberately grounded (the spec's "realistic edition"):
> full re-renders, batching, and eviction are accepted as the cost of correctness.

## 1. Targets

| Metric | Target | Measured assumption |
|---|---|---|
| Batch round-trip (client edit → ack) | **< 300 ms** | local network, medium doc |
| Server re-render (full HTML) | **< 250 ms** | 5 MB docx, "reasonable server" |
| Memory per session | **< 250 MB** | medium doc |
| Concurrent sessions per server | **20** | bounded by eviction, not hardware |
| Concurrent users per doc | **> 50** | ops broadcast + presence throttling |
| Client edit → local visible | **< 10 ms** | optimistic apply (no server hop) |
| Idle eviction | 15 min | frees memory automatically |

## 2. Latency breakdown (single batch)

```
client keystroke → optimistic DOM apply           ~1-10 ms   (no network)
  ... 200 ms batch window (flurry → one batch) ...
batch → WS send                                    ~1-5 ms
server: transform (concurrent window)              < 1 ms     (log tail only)
server: apply ops to OOXML                         1-20 ms    (per op, small)
server: validate (OpenXmlValidator)                10-80 ms   (whole package)
server: full HTML re-render                        50-250 ms  (the dominant term)
server: broadcast + ack                            1-5 ms
client: apply remote ops + caret restore           1-10 ms
────────────────────────────────────────────────────────────
Total (batch of N ops)                            ~270-380 ms worst case; < 300 ms typical
```

The 200 ms batch window is the single most important lever: a 10-keystroke flurry produces one
re-render, not ten.

## 3. Render budget

* Full re-render is the v1 strategy (no delta rendering — the engine has none). The cache
  (`RenderCache`) serves reads without re-rendering; re-render happens only after a batch that
  changed the document.
* `RenderedHtml()` is O(doc) on first call and O(1) after (cached string).
* Optimization path (Phase 2): render only dirty blocks when the change is text-only; keep full
  re-render for structural changes.

## 4. Memory budget (per session)

| Component | Estimate |
|---|---|
| In-memory OOXML DOM (5 MB docx) | 80–150 MB |
| Render cache (HTML) | 5–20 MB |
| Operation log retained tail | 1–20 MB |
| **Total** | **~90–190 MB < 250 MB ✓** |

Controls: LRU eviction at 20 sessions, idle eviction at 15 min, log compaction at every
autosave.

## 5. Broadcast cost

* Ops broadcast is O(peers × ops) JSON, tiny per entry (flat op serialization, no HTML in the
  broadcast — peers apply ops locally; only `welcome`/`resync` carry HTML).
* Presence: client throttles to one frame per 200 ms; server rebroadcasts verbatim. 50 users ⇒
  ~250 msg/s worst case, well within a single hub's capacity.
* Backpressure kicks slow consumers instead of queueing unboundedly.

## 6. Server capacity model

```
per instance: 20 sessions × ~150 MB ≈ 3 GB working set (evicted down aggressively)
concurrency:  per-session semaphore serializes one doc's batches; sessions run in parallel
scale-out:    Phase 2 (Redis session store + sticky routing) — v1 is single-instance
```

## 7. Client budget

* Bundle: React 18 + Vite, no editor framework (contenteditable baseline) ⇒ ~90–130 KB gz.
* JS main-thread work per batch: rebase (O(pending × remote)), DOM apply (O(range)), caret
  restore — all ≪ 5 ms for typical batches.
* Presence throttle and WS reconnect backoff keep idle traffic ≈ 1 frame / 30 s per client.

## 8. Test methodology

* Benchmarks are integration tests with a stopwatch around `ApplyBatch` on a generated 5 MB
  docx (`docs/12-component-diagrams.md` lists the test surface). The acceptance criteria in the
  README map 1:1 to the targets above.
* Load test: 50 WS clients × 200 ms typing loop against one session; assert p95 round-trip and
  memory via `/api/health`.
