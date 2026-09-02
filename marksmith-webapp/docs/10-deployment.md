# Deployment Strategy — Marksmith.WebApp v1

> Deliverable 10 of 12. v1 is cloud-native, single-instance per document-shard, containerized.
> Redis-backed persistence and multi-instance scale-out are Phase 2 (docs/11-roadmap.md).

## 1. Topology (v1)

```
                 ┌─────────────────────────────────────────────┐
 Internet ──TLS──▶  LB / reverse proxy (nginx)                 │
                 │   /api/*        ──▶ collab-server:5210      │
                 │   /ws           ──▶ collab-server:5210 (WS) │
                 │   /  (static)   ──▶ collab-server:5210      │
                 └───────────────────────┬─────────────────────┘
                                         │
                                 ┌───────▼───────┐
                                 │ collab-server │  20 sessions max (in-memory)
                                 │  (1 instance) │
                                 └───────┬───────┘
                                         │ volume mount
                                 ┌───────▼───────┐
                                 │ session store │  /var/lib/marksmith/sessions
                                 └───────────────┘
```

* Single collab-server instance in v1 — sessions are in-memory, so multi-instance needs sticky
  routing + shared session store (Phase 2).
* The server statically serves `client/dist` (sample UI + SDK iframe host).

## 2. Container image

`deploy/Dockerfile.server` (multi-stage):

```
stage 1: mcr.microsoft.com/dotnet/sdk:8.0 → dotnet publish -c Release
stage 2: mcr.microsoft.com/dotnet/aspnet:8.0 → copy publish + client/dist
```

Runtime config:

| Env var | Default | Purpose |
|---|---|---|
| `MARKSMITH_WEBAPP_JWT_SECRET` | dev fallback | HS256 signing secret (≥ 32 chars) |
| `Jwt__Issuer` / `Jwt__Audience` | marksmith-webapp | token claims |
| `Sessions__Root` | `%TEMP%/…` | snapshot directory (mount a volume) |
| `ASPNETCORE_URLS` | `http://+:5210` | listen port |
| `ASPNETCORE_ENVIRONMENT` | Production | Kestrel defaults |

## 3. docker-compose (local / small deployment)

```yaml
# deploy/docker-compose.yml
services:
  collab:
    build: { context: .., dockerfile: deploy/Dockerfile.server }
    ports: ["5210:5210"]
    environment:
      MARKSMITH_WEBAPP_JWT_SECRET: ${JWT_SECRET:?set a 32+ char secret}
      Sessions__Root: /data/sessions
    volumes:
      - sessions:/data/sessions
volumes:
  sessions:
```

## 4. Production hardening checklist

* TLS termination at the LB (WS is `wss://` through the proxy; nginx needs
  `proxy_set_header Upgrade $http_upgrade; Connection "upgrade";` and a generous
  `proxy_read_timeout` ≥ 75 s to survive idle WS keepalives).
* Generate a real JWT secret (e.g. `openssl rand -base64 48`) and never use the dev fallback.
* Disable the dev token endpoint (`POST /api/auth/token`) — route it out or gate it behind a
  flag; hosts issue tokens.
* Snapshot volume: encrypted, backed up, and sized for `20 sessions × ~50 MB avg` plus churn.
* Health checks: `/api/health`; wire it into the orchestrator's liveness probe.
* Resource limits: `memory: 4g` per collab container (20 × ~150 MB working set + GC headroom).

## 5. Rolling updates

* Single instance ⇒ short downtime on deploy (or accept a brief eviction: sessions resume from
  disk on the new instance, so downtime is a reconnect, not data loss).
* `docker compose up -d --build` with a named volume keeps sessions across deploys.
* Phase 2 (blue/green with sticky sessions) removes even the reconnect blip.

## 6. Backups

* The session root is the only durable state (DOCX snapshots). Back it up on a schedule
  (e.g. hourly `restic`/`velero` snapshot of the volume).
* Documents are owned by the host app in production; the collab session store is a cache of the
  latest version — restore = re-upload + resume.

## 7. Observability (v1, basic)

* Structured console logs (`ILogger`) for: session start/close/evict, WS connect/disconnect,
  batch rejections, backpressure kicks.
* `/api/health` exposes active session count + capacity for scrape-based alerting.
* Phase 2: OpenTelemetry traces per batch + metrics (round-trip latency, re-render time, memory
  per session) and an audit log.
