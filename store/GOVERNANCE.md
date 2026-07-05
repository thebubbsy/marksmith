# AI Usage Governance — Marksmith

A transparent, consent-based way for organizations to understand how staff use AI chat tools
(ChatGPT, Gemini, Claude) and to catch sensitive-data leaks — **without reading anyone's chats**.

## What it is (and deliberately is not)

| It DOES | It does NOT |
| --- | --- |
| Record which AI tool was used, when, page title/topic, and message size | Capture the text of prompts or replies |
| Flag *categories* of sensitive data (API keys, credentials, PII) with a count | Store or transmit the matched secret values |
| Require the employee to acknowledge a monitoring notice before anything is recorded | Operate covertly or silently |
| Show a persistent, un-hideable on-screen indicator while active | Track browsing outside the configured AI sites |

This is a **data-loss-prevention / compliance** tool, not surveillance. The design choices above
are what keep it lawful (consent, data minimization) and enterprise-sellable. Covert capture of
chat content would breach wiretap / two-party-consent and GDPR/CCPA rules in most jurisdictions,
so the product does not support it — the collector rejects any report that doesn't assert consent.

## How it works

```
Managed browser (extension, org mode)          Marksmith collector (or central service)
┌───────────────────────────────┐              ┌──────────────────────────────────────┐
│ 1. One-time consent notice     │              │ POST /api/governance/report            │
│ 2. Persistent "monitored by X" │─ metadata ─▶ │   → rejects if consent flag missing    │
│    badge on AI sites           │   + DLP      │ GET  /api/governance/summary  (rollup) │
│ 3. Client-side DLP scan;       │   flags      │ GET  /api/governance/events   (recent) │
│    only labels+counts leave    │              │                                        │
└───────────────────────────────┘              └──────────────────────────────────────┘
                                                 governance-dashboard.html reads these
```

DLP scanning runs **in the browser**, so the sensitive value never leaves the device — only the
category label ("AWS key") and a hit count are reported.

## Deploying to an organization

1. **Package & distribute the extension** privately (Chrome Web Store "private" visibility for your
   Google Workspace, or self-hosted `.crx` via `ExtensionInstallForcelist` policy).
2. **Push config via managed policy** (Intune / GPO / Google Admin) matching `managed_schema.json`:
   ```json
   {
     "orgMode": true,
     "orgName": "Acme Corp",
     "orgId": "acme",
     "collectorUrl": "https://ai-governance.acme.internal",
     "policyUrl": "https://acme.internal/ai-policy",
     "userEmail": "${user.email}"
   }
   ```
   With no policy, `orgMode` is false and the extension behaves as the personal connector — governance
   is entirely opt-in.
3. **Run a collector.** For a pilot, the Marksmith desktop app is the collector (enable the REST API).
   For production, stand up a central service exposing the same three endpoints.
4. **Publish your AI-usage policy** and link it via `policyUrl` so it appears in the consent notice.

## Legal note (read before deploying)

Monitoring employees — even transparently — is regulated and varies by jurisdiction. Before rollout:
notify staff and obtain consent per local law, publish an AI-usage/monitoring policy, limit access to
the dashboard, and set a data-retention period. This document is not legal advice; involve your legal
/ HR / works-council stakeholders.

## API reference

| Endpoint | Purpose |
| --- | --- |
| `POST /api/governance/report` | Extension reports one usage event. **403 if `consentAcknowledged` ≠ true.** |
| `GET /api/governance/summary` | 30-day rollup: totals, per-user, per-assistant, top DLP flags. |
| `GET /api/governance/events` | Recent raw events for the activity table. |

Open `governance-dashboard.html` and point the port box at your collector to view it.
