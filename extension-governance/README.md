# Marksmith Governance Monitor

A **standalone** browser extension — separate from "Marksmith Connector" (`extension/`) — for
transparent AI-usage governance: which AI tools are used, how much time, and
whether sensitive data (keys, credentials, PII) is being leaked, with enough masked detail to
begin remediation. See [GOVERNANCE.md](GOVERNANCE.md) for the full design and deployment guide.

**Not distributed via the Chrome Web Store.** This is deployed by an organization to its own
managed devices (force-installed via Intune/GPO/Google Admin), pointed at a collector the org
runs itself. It ships nothing and reports nothing until an admin pushes configuration.

## Files

| File | Purpose |
| --- | --- |
| `manifest.json` | MV3 manifest. |
| `dlp-mask.js` | Client-side DLP scan + masking — mirrors `MdToPdf/Services/DlpScanService.cs`. |
| `governance.js` | Content script: consent notice, persistent badge, composer watch (DLP), time-on-page tracking. |
| `background.js` | Service worker: relays reports to the configured `collectorUrl`. |
| `options.html` / `options.js` | Config UI for a personal pilot (real orgs push policy via `managed_schema.json`). |
| `managed_schema.json` | Enterprise managed-policy schema. |
| `dashboard.html` | Admin dashboard — usage, time, and the masked remediation worklist. Point it at any collector. |
| `popup.html` | Toolbar popup (status + link to options). |

## Local test / pilot

1. `chrome://extensions` → Developer mode → **Load unpacked** → select this folder.
2. Open the extension's **Options** page, fill in `orgName`, `orgId`, and `collectorUrl` (a
   Marksmith desktop instance with the local API enabled, e.g. `http://127.0.0.1:47821`).
3. Visit an AI chat site — you'll see the persistent monitoring badge.
4. Open `dashboard.html` in a browser, point the port box at your collector, and watch events land.

## Design principles carried over from `extension/governance.js`

The persistent on-page indicator, and the "category label, not the raw value"
DLP discipline are unchanged. What's new here: the extension is a dedicated product (not bundled
into the public connector), it captures a **masked, remediation-identifying preview** of each DLP
match instead of only a count, and it tracks **active time spent** per AI tool.

