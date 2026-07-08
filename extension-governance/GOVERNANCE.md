# AI Usage Governance — Marksmith Governance Monitor

A transparent way for organizations to understand how staff use AI chat tools
(ChatGPT, Gemini, Claude, Copilot), how much time they spend in them, and to catch — and start
remediating — sensitive-data leaks. **This is a standalone extension, separate from "Marksmith
Connector."** It is not distributed via the Chrome Web Store; it's deployed by an organization to
its own managed devices.

## What it is (and deliberately is not)

| It DOES | It does NOT |
| --- | --- |
| Record which AI tool was used, when, page title/topic, message size, and active time spent | Capture the text of prompts or replies for ordinary messages |
| Flag *categories* of sensitive data (API keys, credentials, PII) with a **masked, remediation-identifying preview** of each match | Store or transmit the full matched secret — a private key or credential shows as a flat "redacted, value not stored" marker, a card/SSN as `•••• •••• •••• 1234` (PCI convention) |
| When something IS flagged, also keep the **surrounding message with the sensitive span(s) blanked in place**, plus a **density score** (what share of the message the match made up) | Keep that context for messages that triggered no flag — the 99% of clean traffic gets zero content capture, same as before |
| Show an AWS Access Key ID **in full** — it's an identifier, not a secret (see below) | Reveal any *actual* secret: the paired AWS Secret Access Key, passwords, private keys, API keys, GitHub tokens |
| Require the employee to acknowledge a monitoring notice before anything is recorded | Operate covertly or silently |
| Show a persistent, un-hideable on-screen indicator while active | Track browsing outside the configured AI sites |

This is a **data-loss-prevention / compliance** tool, not surveillance. The masked-capture design
is what makes it *useful* for remediation ("go rotate key `...MNOP`") without creating a second
copy of the actual secret sitting in a dashboard database — the same discipline commercial DLP
tools (Microsoft Purview, Netskope) use. The always-visible-badge design is what keeps
it lawful and defensible; covert capture of chat content would breach wiretap / two-party-consent
and GDPR/CCPA rules in most jurisdictions, so the product does not support it.

### Why context + density, not just a category count

A category-only signal can't tell "an AWS key accidentally buried in 2,000 characters of a
deployment-notes paste" (negligence) apart from "an AWS key submitted alone, as the entire
message" (looks deliberate) — and that distinction matters for triage and is exactly the kind of
signal a security buyer wants. Density (`matched chars / message length`) and the redacted
context capture that distinction directly, **without needing the raw secret value**: a message
that's 8% key and 92% legitimate deployment notes reads as "Likely accidental"; a message that's
100% key reads as "Likely deliberate." The context is capped at 500 characters and only ever
captured for messages that tripped a rule.

### Why the AWS Access Key ID is the one exception to masking

An AWS Access Key ID (`AKIA`/`ASIA`-prefixed) is an *identifier*, not the secret half of the
credential pair — it's already recorded in every CloudTrail event and visible in the IAM console.
The actual secret is the paired **Secret Access Key** (a 40-character value with no fixed prefix),
which is not reliably regex-matchable and, if labelled, falls under the fully-masked
"Credential"/"API key" rules instead. Revealing the Access Key ID costs nothing — the org already
has it indexed — and lets an investigator search CloudTrail immediately. Every other category
(private keys, passwords/credentials, generic API keys, GitHub tokens, cards, SSNs) stays masked
or fully redacted; none of those have a "the identifier half is already public" structure.

## How it works

```
Managed browser (this extension)               Marksmith collector (or a central service)
┌────────────────────────────────┐             ┌───────────────────────────────────────┐
│ 1. Persistent "monitored by X"  │─ metadata ─▶│                                 │
│    badge on AI sites            │  + time     │   → server-side safety net: re-applies  │
│ 3. Client-side DLP scan; only   │  + MASKED   │     masking/redaction to everything      │
│    masked previews + redacted   │  findings   │     received, so a bug or tamper on the  │
│    context leave the device     │  + context  │     client can't smuggle a raw value in  │
│    (dlp-mask.js)                │  + density  │ GET  /api/governance/summary  (rollup)  │
│ 4. Active-time tracking         │             │ GET  /api/governance/events   (recent)  │
│    (visible + focused seconds)  │             └───────────────────────────────────────┘
└────────────────────────────────┘                        dashboard.html reads these
```

The raw secret value never leaves the browser tab, at any granularity — only the masked preview,
the category, the density score, and context with the value itself blanked. The collector
re-applies the same masking/redaction rules to everything it receives before storage, so even a
buggy or tampered client can't smuggle a raw value — of a finding OR of the context string — into
the store.

## Deploying to an organization

1. **Distribute the extension** as a managed, force-installed extension — via
   `ExtensionInstallForcelist` (Chrome/Edge) pointing at a self-hosted `.crx`, or Chrome Web Store
   **private** visibility scoped to your Workspace. It is not published publicly.
2. **Push config via managed policy** (Intune / GPO / Google Admin) matching `managed_schema.json`:
   ```json
   {
     "orgName": "Acme Corp",
     "orgId": "acme",
     "collectorUrl": "https://ai-governance.acme.internal",
     "policyUrl": "https://acme.internal/ai-policy",
     "userEmail": "${user.email}"
   }
   ```
   With no policy pushed, the extension is entirely inert (see `governance.js`'s `getConfig()`).
3. **Run a collector.** For a pilot, a Marksmith desktop instance on a shared/always-on machine is
   the collector (enable the local REST API). For production, `collectorUrl` can point at any
   service implementing the same three endpoints — a central hosted collector is a natural next
   step but is not shipped today; nothing about the extension assumes 127.0.0.1.
4. **Publish your AI-usage policy** and link it via `policyUrl`.

## Legal note (read before deploying)

Monitoring employees — even transparently, even with masked capture — is regulated and varies by
jurisdiction. Before rollout: notify staff per local law, publish an
AI-usage/monitoring policy, limit access to the dashboard to those with a legitimate need (it
contains remediation-identifying data), and set a data-retention period. This document is not
legal advice; involve your legal / HR / works-council stakeholders.

## API reference

| Endpoint | Purpose |
| --- | --- |
| `POST /api/governance/report` | Reports one usage/DLP/time event. Response includes `droppedUnmasked` — a non-zero count means the safety net rejected a finding that looked unmasked. |
| `GET /api/governance/summary` | 30-day rollup: totals, time-on-tool, per-user, per-assistant, top DLP flags, `likelyDeliberate` count, and a `recentIncidents` remediation worklist (masked matches + redacted context + density per incident). |
| `GET /api/governance/events` | Recent raw events for the activity table. |

Open `dashboard.html` and point the port box at your collector to view it.

