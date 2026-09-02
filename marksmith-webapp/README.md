# Marksmith.WebApp v1

Browser-based DOCX editor with **real-time collaboration**, built on **server-sequenced
Operational Transformation (OT)** — no CRDT, no Yjs — with MarkSmith.Core treated as an
**untouched black box**.

> The pragmatic v1: OT instead of CRDT, stateful persistent sessions with eviction, full
> HTML re-render with 200 ms client-side op batching, a limited op set applied through the
> OpenXml SDK's public API, a dedicated WebSocket hub, and both standalone + iframe embedding.

## Repository layout

```
marksmith-webapp/
  server/
    Marksmith.WebApp.sln
    Marksmith.WebApp.Server/     # .NET 8 ASP.NET Core: REST + WebSocket + OT + sessions
    Marksmith.WebApp.Tests/      # xUnit: transforms, op log, applier, session lifecycle
  client/
    src/
      collab/                    # WS client, 200ms op buffer, OT rebase (TS mirror), protocol types
      editor/                    # WYSIWYG surface, toolbar, comments, track changes, presence
      sdk/                       # <MarksmithEditor />, vanilla init(), iframe bridge
      styles/theme.css           # CSS-variable theming
    package.json  vite.config.ts tsconfig.json
  docs/                          # the 12 design deliverables (below)
  deploy/                        # Dockerfile.server + docker-compose.yml
```

The existing `marksmith-v2/` tree (MarkSmith.Core, Desktop, Express, …) is **not touched** by
this project. The WebApp pins the same `DocumentFormat.OpenXml` version Core uses (3.1.0) and
mutates documents through its public API only.

## Build & run

### Prerequisites

* .NET SDK 8.0
* Node.js 20+ (for the client)

### 1. Server

```bash
dotnet build server/Marksmith.WebApp.sln -c Release
dotnet run --project server/Marksmith.WebApp.Server -c Release --urls http://localhost:5210
```

Set `MARKSMITH_WEBAPP_JWT_SECRET` to a ≥32-char secret in production (a dev fallback is baked
in for local runs). Session snapshots land in `%TEMP%/marksmith-webapp/sessions` unless
`Sessions__Root` is set.

### 2. Client (dev)

```bash
cd client
npm install
npm run dev          # http://localhost:5173 (proxies /api and /ws to :5210)
```

### 3. Client (production bundle, served by the server)

```bash
cd client && npm run build     # client/dist
# server/Program.cs serves client/dist statically when present
```

### 4. Tests

```bash
dotnet test server/Marksmith.WebApp.sln -c Release
```

### 5. Docker

```bash
docker build -f deploy/Dockerfile.server -t marksmith-webapp .
docker compose -f deploy/docker-compose.yml up   # requires JWT_SECRET env
```

## Try it

1. Start the server (dev secret is fine).
2. Open `http://localhost:5210` (serves the sample UI from `client/dist` after a build) — or
   `http://localhost:5173` in dev mode.
3. Open a second tab with `?user=alice` to watch two users converge on the same document.

Quick REST smoke:

```bash
curl -s -X POST localhost:5210/api/auth/token -H "Content-Type: application/json" \
     -d '{"userId":"u1","documentId":"doc-demo"}' | tee /tmp/token.json
# token=$(jq -r .token /tmp/token.json)
curl -s localhost:5210/api/health
```

## Design deliverables (`docs/`)

| # | Document | File |
|---|---|---|
| 1 | Architecture | [01-architecture.md](docs/01-architecture.md) |
| 2 | OT specification | [02-ot-spec.md](docs/02-ot-spec.md) |
| 3 | WebSocket protocol | [03-websocket-protocol.md](docs/03-websocket-protocol.md) |
| 4 | OpenAPI specification | [04-openapi.yaml](docs/04-openapi.yaml) |
| 5 | Session management design | [05-session-management.md](docs/05-session-management.md) |
| 6 | Embeddable SDK API | [06-sdk-api.md](docs/06-sdk-api.md) |
| 7 | UI wireframes | [07-wireframes.md](docs/07-wireframes.md) |
| 8 | Security model | [08-security-model.md](docs/08-security-model.md) |
| 9 | Performance budget | [09-performance-budget.md](docs/09-performance-budget.md) |
| 10 | Deployment strategy | [10-deployment.md](docs/10-deployment.md) |
| 11 | Phase 2 roadmap | [11-roadmap.md](docs/11-roadmap.md) |
| 12 | Component diagrams | [12-component-diagrams.md](docs/12-component-diagrams.md) |

## Acceptance criteria status

| Criterion | Status |
|---|---|
| OT converges for 10 users editing the same paragraph | Implemented — server sequencing + one-way transforms (`Ot/Transform.cs`), exercised in `TransformTests.cs` (same-offset inserts, overlapping deletes, block ops, id collisions) |
| Sessions reload from disk after server restart | Implemented — `SessionStore` snapshots + `SessionManager.StartAsync` resume path, tested in `SessionManagerTests.Session_PersistsAndResumes_AfterRestart` |
| Re-render < 250 ms for a 5 MB doc | Budgeted (docs/09) — full re-render cached per session; batching limits frequency; benchmark harness is a Phase 2a integration test |
| Memory < 250 MB/session + idle eviction | Budgeted + implemented — LRU cap at 20 sessions, 15-min idle sweep, log compaction |
| Embeddable standalone flawless; iframe works with documented limits | Implemented — SDK (React + vanilla + iframe bridge), limitations in docs/06 |
| Core untouched | Guaranteed by construction — the WebApp server references only `DocumentFormat.OpenXml`, never MarkSmith.Core |

## v1 scope boundaries (prohibited list)

❌ WASM / browser-side rendering engine · ❌ modifying MarkSmith.Core · ❌ CRDT/Yjs · ❌ delta
rendering · ❌ multi-level lists / table merge-split / nested tables / OMML / DrawingML /
diagrams / charts · ❌ plugin system · ❌ on-prem (cloud-native only) · ❌ enterprise SSO
(just JWT) · ❌ audit logs (basic logging only).

## Notes on this build

* The session's in-memory document is mutated exclusively through `DocumentFormat.OpenXml`'s
  public API (`Documents/OpApplier.cs`), then schema-validated after every batch — this is the
  "Core as a black box" boundary in code.
* Client and server share the operation/transform contract: `collab/transform.ts` is a strict
  TS mirror of `Ot/Transform.cs`, and `collab/protocol.ts` mirrors `Ot/Operation.cs` /
  `Web/WsProtocol.cs`.
* See `docs/03-websocket-protocol.md` §7 for the reconnect policy (drop pending ops + resync)
  and §6 for backpressure (kick slow consumers) — both are v1 pragmatic choices, documented.
