# 12-Month Enterprise Platform & Hybrid GUI/CLI Architecture Plan

> [!IMPORTANT]
> **Why the Previous PyWebView Experience Failed**: Rendering static `rich` terminal tables into an HTML container with 14 flat sidebar buttons creates a "terminal screenshot viewer," not a desktop workbench. Meanwhile, while the Canary module had deep state, scheduling, and customization, the broader M365/Entra ID modules were treated as one-shot summary dumps rather than deep, interactive workflows.

---

## 1. Executive Vision: The Hybrid CLI + Workbench Paradigm

Instead of a rudimentary wrapper around terminal HTML output, the platform will be architected as a **Dual-Surface Security Engineering Workbench** (modeled after tools like Burp Suite, Wireshark, and Postman, paired with a first-class CLI engine like `nuclei` / `az` / `gh`):

1. **Headless Core Engine & Structured CLI (`m365-workbench-cli`)**:
   - Every capability in the platform is exposed first as a composable sub-command (`recon`, `federation`, `dns`, `posture`, `deception`, `graph`, `scheduler`, `diff`) with structured JSONL streaming, SQLite/DuckDB local state persistence, and Unix-pipe compatibility.
   - Power users can script, chain, and automate every single feature from the terminal without touching the GUI.
2. **True Native-Grade Desktop Workbench + Embedded CLI Dock**:
   - **Technology Stack**: **Tauri v2 (Rust shell) + React/TypeScript + Tailwind + Cytoscape.js / React Flow + xterm.js** communicating with the Python FastAPI/gRPC core engine (or a modular **Textual + Native Qt/QML** dual frontend).
   - **Bi-Directional CLI/GUI Sync ("Harnessing the CLI")**:
     - Every action clicked in the GUI displays and executes the exact underlying CLI command in a dockable **Live Quake-Style Terminal Drawer (`Ctrl+\``)** and **Command Palette (`Ctrl+K`)**.
     - Running a command in the embedded terminal automatically updates the GUI's interactive tables, network topology graphs, and tenant state inspector in real time.

---

## 2. Expanding Platform Functionality Beyond Canary Tooling (6 Deep Pillars)

To bring every capability up to—and beyond—the depth of the Canary module, the platform will expand across six dedicated engineering pillars:

### Pillar 1: Deep Federation, B2B Trust & Cross-Tenant Topology Engine
*Moves beyond basic OpenID/GetUserRealm dumps into interactive multi-tenant relationship mapping.*
- **Recursive Federation & Partner Discovery**: Automatically walk federated domains, parent/sibling namespaces, and M&A subsidiary domains linked to the same Tenant ID.
- **IdP Infrastructure Fingerprinting**: Deep parsing of ADFS, Okta, PingFederate, Duo, and Entra Cloud-Managed endpoints, extracting signing certificates, token-signing key rollover dates, WS-Trust/WS-Federation endpoints, and SAML metadata bindings.
- **Cross-Tenant Access & B2B Posture Mapping**: Resolve inbound/outbound B2B collaboration configurations, verified publisher linkages, and guest tenant trust boundaries.
- **Interactive Visual Graph**: Render the target tenant, all custom domains, federated IdPs, and partner tenants as an interactive node graph with click-to-inspect metadata panes.

### Pillar 2: Continuous DNS, Mail Hygiene & Shadow Cloud Asset Discovery
*Replaces a single MX/TXT table with a full Attack Surface Management (ASM) inspector.*
- **SPF Lookup Tree & Flatten Analyzer**: Recursively resolve `include:`, `redirect=`, `a:`, and `mx:` mechanisms, count the 10-lookup RFC 7208 limit, highlight broken includes, and detect dangling IP ranges.
- **DMARC, DKIM & BIMI Selector Auditor**: Probe common and historical DKIM selectors, evaluate DMARC policy enforcement (`p=none` vs `quarantine`/`reject`), and inspect TLS-RPT and MTA-STS policies.
- **Subdomain & Shadow Azure Asset Discovery**: Correlate Certificate Transparency (CT) logs with Azure resource CNAME suffixes (`*.azurewebsites.net`, `*.blob.core.windows.net`, `*.trafficmanager.net`, `*.azurefd.net`, `*.cloudapp.net`) to identify orphaned records and unconfigured cloud services.

### Pillar 3: Identity, Authentication Surface & Protocol Posture Lab
*Transforms basic endpoint checks into a comprehensive protocol and authentication policy analyzer.*
- **Granular Protocol Matrix**: Inspect status and configuration nuances across Exchange Online (Autodiscover v1/v2, EWS, MAPI/HTTP, OAB, ActiveSync), SharePoint Online, OneDrive, Teams, and Skype for Business (Lyncdiscover).
- **Login Branding & CSS Telemetry Diffing**: Historical snapshot diffing of Entra Company Branding assets (illustrations, backgrounds, custom CSS, favicons, boilerplates), tracking exact byte changes, CDN origins, and embedded telemetry rules over time.
- **OPSEC & Telemetry Profile Simulator**: An interactive request builder that calculates exact Entra Sign-In Log, Defender for Cloud Apps, and Azure WAF visibility before a query is ever sent, allowing operators to tune request pacing, DNS-over-HTTPS resolvers, and header profiles.

### Pillar 4: Authenticated Microsoft Graph, Conditional Access & Entitlement Analyzer
*Turns Option `[14]` from a 5-endpoint summary into a full posture analyzer.*
- **Conditional Access Policy Matrix & Gap Finder**: Parse all Conditional Access policies and visualize coverage matrices across user groups, client apps, device platforms, Sign-in Risk levels, and grant controls (identifying legacy auth gaps or missing MFA exclusions).
- **Service Principal, App Registration & OAuth Consent Graph**: Map high-privilege Graph API application permissions (`Application.ReadWrite.All`, `RoleManagement.ReadWrite.Directory`, `Mail.ReadWrite`), expiring client secrets/certificates, and multi-tenant third-party OAuth grants.
- **Privileged Role & PIM Assignment Auditor**: Enumerate eligible vs. permanent Entra directory roles, administrative units, and group-based role assignments, with native import/export compatibility for `ROADrecon` and GraphHound databases.

### Pillar 5: Enterprise Deception & Canary Telemetry Lab
*Elevates the existing `[1]`–`[9]` canary features into a dedicated visual workbench.*
- **Visual CSS AST & Deception Inspector**: Parse custom CSS into a full syntax tree highlighting hidden selectors, off-screen positioning, zero-opacity rules, `@import` chains, and hex/unicode obfuscation layers side-by-side with live DOM rendering previews.
- **Telemetry & Alert Verification Studio**: Manage custom header profiles, compare response timings/fingerprints, and correlate scheduled verification ticks in a timeline view.

### Pillar 6: Continuous Monitoring, Historical Diffing & Automation Hub
*Generalizes Option `[8]`/`[9]` across the entire platform—not just canary URLs.*
- **Universal Scheduled Monitors & Drift Detection**: Schedule recurring background jobs for *any* pillar (e.g., alert when a tenant adds a new federated domain, modifies SPF/DMARC, rotates branding CSS, or changes Conditional Access policies).
- **Time-Travel Snapshot Diffing**: Every scan is stored in a local DuckDB/SQLite workspace database, allowing side-by-side historical diffs between any two timestamps.

---

## 3. 12-Month Phased Engineering Roadmap (Q1 – Q4)

| Quarter | Months | Theme | Key Deliverables & Milestones |
| :--- | :--- | :--- | :--- |
| **Q1** | **Months 1–3** | **Core Engine Refactor, Structured CLI & Persistence Layer** | • Decompose monolithic script into modular Python package (`core/`, `cli/`, `daemon/`, `storage/`).<br>• Build structured CLI with sub-commands, `--json`/`--jsonl` streaming, and interactive REPL.<br>• Implement embedded SQLite/DuckDB workspace database for multi-tenant profiles, historical snapshots, and diffing.<br>• Build local FastAPI + WebSocket daemon to bridge CLI execution with the new GUI. |
| **Q2** | **Months 4–6** | **Next-Gen Desktop Workbench GUI & Bi-Directional CLI Harness** | • Scaffold Tauri v2 + React/TypeScript desktop shell (replacing PyWebView + Rich HTML).<br>• Build the **Embedded `xterm.js` Terminal Dock**, **Live CLI Command Builder Bar**, and **`Ctrl+K` Command Palette**.<br>• Implement multi-tab Tenant Workspaces, interactive data grids (sorting, filtering, regex search, column pinning), and side-by-side diff viewers.<br>• Ship interactive Cytoscape.js / React Flow canvas for Tenant, Domain & Federation topology graphs. |
| **Q3** | **Months 7–9** | **Deep Pillar Expansion (Federation, ASM/DNS, Identity & Graph)** | • **Pillar 1**: Recursive federation walker, IdP certificate/metadata inspector, and B2B trust mapper.<br>• **Pillar 2**: Recursive SPF tree visualizer, DKIM/DMARC/BIMI auditor, CT log correlator, and Azure CNAME asset discovery.<br>• **Pillar 3**: Full protocol posture matrix, branding time-machine diffing, and OPSEC telemetry calculator.<br>• **Pillar 4**: Authenticated Graph workbench, Conditional Access coverage matrix, and OAuth/Service Principal privilege graph. |
| **Q4** | **Months 10–12** | **Continuous Drift Engine, Reporting Studio & Enterprise Packaging** | • **Pillar 6**: Universal cross-platform scheduler daemon (Windows Task Scheduler, systemd, launchd) with auto-heal and drift webhooks (Slack, Teams, Webhook, SIEM).<br>• **Plugin & Workflow SDK**: YAML-based workflow runner (similar to Nuclei templates) for custom M365 posture checks.<br>• **Executive & Technical Reporting Studio**: One-click PDF/HTML/Markdown/SARIF report generation.<br>• Signed cross-platform installers (`.msi`/`.exe`, `.dmg`, `.AppImage`) and automated CI/CD release pipeline. |

---

## 4. How the New GUI Harnesses the CLI (UX Blueprint)

1. **The "CLI Mirror Bar" (Top of Every Workspace View)**:
   - Every toggle, filter, header preset, or scan option selected in the GUI dynamically constructs the exact CLI command string in a persistent bar at the top of the workspace (e.g., `m365-wb recon federation --domain contoso.com --recursive --verify-certs`).
   - Users can click **Run in Workbench**, **Send to Embedded Terminal**, or **Copy CLI One-Liner** for scripts/CI pipelines.
2. **Quake-Style Embedded Terminal (`Ctrl+\``)**:
   - A full `xterm.js` terminal docked at the bottom of the window running the platform's interactive CLI shell.
   - Typing `workspace use contoso.com` or `scan dns --deep` in the terminal immediately populates the visual graphs and data tables in the upper GUI panes.
3. **Interactive Visual Explorers Instead of Static Text Tables**:
   - **Topology Canvas**: Drag, zoom, group, and filter tenant domains, MX hosts, cloud CNAMEs, and federated IdPs.
   - **Data Grid Inspector**: Every table supports instant multi-column filtering, cell-level copy, JSON drawer inspection, and one-click pivot actions (e.g., right-clicking a discovered domain to immediately run DNS/SPF analysis or Branding CSS inspection).
