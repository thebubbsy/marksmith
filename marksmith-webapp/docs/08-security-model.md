# Security Model — Marksmith.WebApp v1

> Deliverable 08 of 12. v1 scope: JWT auth, session/document tenant isolation, input validation,
> and transport hygiene. Enterprise SSO, audit logs and fine-grained ACLs are explicitly
> out of scope (Phase 2 — see docs/11-roadmap.md).

## 1. Trust boundaries

```
 Browser/iframe  ──TLS──▶  Marksmith.WebApp Server  ──▶  SessionStore (disk)
      │                            │
      │  JWT (query/bearer)        │  validates every op against live OOXML
      ▼                            ▼
 SDK (host)                  MarkSmith.Core — NOT referenced, never modified
```

* The **client is untrusted**: every op is validated, transformed, applied, and schema-checked
  server-side before it is committed.
* The **session store is trusted storage**: snapshots are written atomically; the server is the
  only writer.

## 2. Authentication (JWT)

* Issued by the host app (or the dev endpoint) with claims:
  * `sub` — user id (stable identity),
  * `doc` — document id (the session tenant),
  * `jti` — token id (uniqueness / revocation hook),
  * `exp` — default 8h, `nbf` — now.
* HS256 with a server secret (`MARKSMITH_WEBAPP_JWT_SECRET`, ≥ 32 chars; dev fallback clearly
  marked). Phase 2: asymmetric RS256 keys for multi-instance deployments.
* Validation: issuer, audience, signing key, lifetime, clock skew ≤ 1 min. All REST routes
  except `/api/health` and the dev token route require `bearerAuth`.
* WebSocket handshake validates the same token from the query string (`?token=`).

## 3. Authorization (tenant isolation)

* The `doc` claim is the **only** document a user may touch. The WS handshake rejects
  `session != doc` with `1008`.
* REST endpoints require the token; the session id in the path must match the `doc` claim
  (enforced in `RestEndpoints` via the authenticated principal).
* v1 has no per-document role model: any authenticated user of a document may edit it. Roles
  (owner/editor/viewer) are Phase 2.

## 4. Input validation (defense in depth)

| Layer | Mechanism |
|---|---|
| Frame size | WS frames capped at 2 MiB; REST body capped by Kestrel defaults + base64 parse errors |
| Message shape | `WsProtocol.Parse` rejects malformed JSON (unknown type, missing id/type) with `bad_frame` |
| Op semantics | `OpApplier` validates block/offset/length against the live document before mutating |
| Batch atomicity | pre-batch snapshot; any failure restores and rejects the whole batch |
| Schema | `OpenXmlValidator` validates the OOXML after every batch; schema failure ⇒ rollback |
| HTML | The renderer escapes all text (`WebUtility.HtmlEncode`); client DOM insertion never runs scripts (contenteditable + text nodes only) |

## 5. SSRF / image handling

* `insertImage` accepts **data URIs only** in v1 — the server never fetches `url` images
  (no SSRF surface). The `url` branch exists in the applier but returns a rejection.
* Phase 2 (with an allowlisted fetch proxy + size caps) can enable remote images.

## 6. DoS protections

* Session capacity cap (20 concurrent) with LRU eviction — memory is bounded.
* Per-connection send queue (512) with kick-on-backpressure — a slow consumer cannot pin memory.
* Heartbeat (60 s receive deadline) reaps dead connections.
* Per-session serialization prevents interleaved-batch races from corrupting a document.

## 7. Data-at-rest

* Snapshots live in the session root (temp by default; configurable). Production should place
  them on an encrypted volume or object store.
* No PII beyond the user id in the log entries; snapshot sidecars hold only sessionId/seq/time.
* The dev token endpoint is **not** for production; hosts issue tokens from their own identity
  layer.

## 8. Known v1 gaps (documented, Phase 2)

* Token in WS query string may appear in proxy logs → short TTL + TLS; Phase 2 adds a
  subprotocol/cookie handshake.
* iframe mode passes config via URL hash (visible in history) → Phase 2 cookie handshake.
* No per-document ACLs, no audit trail, no rate limiting per user.
* No CSRF concern for WS (browsers don't attach cookies to WS unless same-origin policy allows;
  v1 uses explicit tokens, not cookies).
