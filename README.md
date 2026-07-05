<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-full-dark.png">
  <img src="docs/images/logo-full-light.png" alt="Marksmith" width="260">
</picture>

# Marksmith

### Turn AI chats into polished documents.

Paste a reply from ChatGPT, Gemini, or Claude — Marksmith detects the source, cleans up the
formatting quirks each one leaves behind, and exports a professional **PDF** or **DOCX**.

![Marksmith](docs/images/hero.png)

[![CI](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml/badge.svg)](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml)
![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WinUI 3](https://img.shields.io/badge/WinUI-3-purple)
![license](https://img.shields.io/badge/license-MIT-green)

</div>

---

## Why Marksmith?

AI assistants all format the same way. It isn't *bad* formatting — it's **recognizable** formatting:
the same em‑dashes, bold on everything, `\(LaTeX\)` delimiters, `【7†source】` citation pips. Marksmith
turns that into **your** formatting — re‑theme it, shift the heading structure, tone down the emphasis,
swap the dashes — then renders it with proper math, syntax highlighting, and your choice of theme,
ready to send. (It also clears the genuine copy‑paste artifacts — citation pips, "Copy code" buttons —
automatically.)

It's a native **WinUI 3** desktop app with a left‑to‑right workflow: **Source → Style → Preview & Export.**

---

## Features

### 🧠 AI source detection & normalization

Paste or drop Markdown and Marksmith fingerprints which assistant produced it, then normalizes the
quirks unique to each — ChatGPT's LaTeX + citation artifacts, Gemini's pseudo‑headings and "Sources"
blocks, Claude's stray tags. Math is typeset with KaTeX, code is highlighted, and a source‑attribution
strip is stamped on the export.

![AI detection and normalization](docs/images/ai-detection.png)

> The badge shows the detected assistant, a confidence score, and how many fixes were applied.
> Every fix is counted — never silent.

### ✂️ Cleanup controls

One‑click switches for the things that make text look machine‑generated:

- **Em‑dash handling** — keep, replace with a hyphen, a spaced dash, or your own custom string
  (code blocks are left untouched)
- **No‑emoji mode** — strip every emoji from preview, PDF, and DOCX
- **Normalize AI quirks** — the detection/cleanup engine above, toggleable

![Em-dash and cleanup options](docs/images/emdash.png)

### ✍️ Personalize the formatting

Make the output *yours* instead of the model's default — this changes the document's **structure**,
not just its surface. Applied live to preview, PDF, and DOCX, and always code‑block‑safe:

- **Heading level shift** — an up/down control that promotes (−) or demotes (+) every heading at once
  (e.g. +1 turns `#` into `##`), clamped to 1–6
- **Bold** — keep, remove entirely, or convert to italic
- **Italic** — keep or remove

![Personalize the formatting: heading shift + bold removed, italics and code untouched](docs/images/formatting.png)

### 🎨 Themes & layout

Ten built‑in themes (GitHub Light/Dark, Solarized, Dracula, Monokai Pro, Cyberpunk, Nordic, Forest,
Obsidian) with control over page width, A4 lock, single‑continuous‑page mode, and an auto‑generated
table of contents.

### 🐚 Mermaid diagrams

Fenced ` ```mermaid ` blocks render inline and inherit whichever theme is active — nodes, edges, and
labels all recolor to match, across **every** built‑in theme. Here's the same diagram in four of them:

![The same Mermaid diagram recolored to match four of the built-in themes](docs/images/mermaid-themes.png)

And it scales: here's a single diagram of a **global datacenter network** — an AS8075 backbone
fanning out to four regions, each a Clos spine‑leaf fabric, all the way down through top‑of‑rack
switches and servers to individual **HDD bays**, with real‑world addressing throughout. It's rendered
at full resolution — **right‑click → "Open image in new tab"** to read the IP addressing:

![Massive Mermaid network topology](docs/images/mermaid-network.png)

### 📤 Export

- **PDF** via the Chromium print pipeline (WebView2) — pixel‑perfect to the preview
- **DOCX** generated natively with OpenXML — no Pandoc, no external dependencies
- Optional **table of contents** and **source‑attribution** strip
- Every export is logged to a searchable **history** you can reopen with a click

### 🤖 Automation

Marksmith can run hands‑free:

- **Clipboard watcher** — copy a reply in any AI chat and it lands in Marksmith, detected and cleaned
- **Watch folder** — drop a `.md` in a folder and it's ingested; enable **auto‑convert** and a polished
  PDF appears in your output folder with a desktop toast, zero clicks
- **System tray** — closing the window keeps the watchers and API running; the tray icon brings it back

### 🔌 Local REST API

A loopback‑only API (`127.0.0.1`) lets scripts and other tools drive Marksmith:

```bash
# Convert Markdown to a PDF over HTTP
curl -X POST http://127.0.0.1:47821/api/convert \
     -H "Content-Type: application/json" \
     -d '{"markdown":"# Hello\n\nFrom **Marksmith**.","theme":"GitHub Dark"}' \
     -o out.pdf
```

| Endpoint | Purpose |
| --- | --- |
| `GET  /api/health` | Liveness + endpoint list |
| `GET  /api/themes` | Available theme names |
| `POST /api/classify` | Detect the AI source of a Markdown blob |
| `POST /api/ingest` | Push Markdown into the app UI |
| `POST /api/convert` | Return a rendered PDF |

### 🧩 Browser extension

The **Marksmith Connector** (Chrome/Edge, MV3) adds a one‑click "send to Marksmith" button on
ChatGPT, Gemini, and Claude — it converts the reply to Markdown and posts it to the local API. It can
also **auto-send at the end of a conversation** (when you stop interacting) and carries its own
**output profile** — theme, page layout, and formatting set in the connector's Options page — so
automated PDFs use those settings independently of the app's own Style panel. See
[`extension/README.md`](extension/README.md) to load it.

### 🏢 AI Usage Governance (for organizations)

Marksmith can double as a **transparent, consent‑based AI‑usage governance tool.** In org mode the
extension shows employees a consent notice and a persistent monitoring indicator, then reports
**metadata and data‑loss‑prevention flags only** — never conversation content — to an admin dashboard.
The collector rejects any report that doesn't assert consent.

![AI Usage Governance dashboard](docs/images/governance.png)

It flags secrets, credentials, and PII (API keys, AWS keys, emails, SSNs) pasted into AI tools, and
rolls usage up per user and per assistant. See [`store/GOVERNANCE.md`](store/GOVERNANCE.md) for the
deployment guide and the legal considerations before rollout.

### ⚙️ Settings, updates & advanced mode

A settings cog (top‑right) opens **About & updates** — app version, a **Check for updates** button that
queries GitHub Releases, and quick links (repo, releases, report‑an‑issue, license). It's also where
**Advanced mode** lives: off by default to keep the Style panel simple, it reveals the power‑user
options — the cleanup controls (no‑emoji, em‑dash handling) and the formatting transforms.

![Settings — about, updates, and advanced mode](docs/images/settings.png)

---

## Examples

Real documents run through Marksmith — inputs *and* their rendered PDFs — live in
[`examples/`](examples/). Here's [`product-spec.md`](examples/product-spec.md), a design doc with a
Mermaid architecture diagram, GitHub‑style alert boxes, syntax‑highlighted code, and tables, exported
straight to PDF:

<img src="docs/images/example-product-spec.png" width="540" alt="The product-spec example rendered to PDF by Marksmith">

---

## Download

**[⬇️ Download the latest release](https://github.com/thebubbsy/marksmith/releases/latest)** —
grab `Marksmith-win-x64.zip`, unzip anywhere, and run `Marksmith.exe`. No installer, no .NET SDK,
nothing else to set up — the .NET runtime and Windows App SDK are bundled in.

Runs on Windows 10 (1809+) and Windows 11 (x64). The WebView2 runtime it uses for rendering ships
with current Windows; if yours somehow lacks it, grab it
[here](https://developer.microsoft.com/microsoft-edge/webview2/).

---

## Build from source

Only needed if you want to hack on it. Requires the
[.NET 8 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone https://github.com/thebubbsy/marksmith.git
cd marksmith
dotnet restore
dotnet build MdToPdf.sln
dotnet run --project MdToPdf/MdToPdf.csproj
```

> Plain `dotnet build`/`run` default to an x64 build automatically — the Windows App SDK's
> self‑contained mode needs a concrete architecture.

---

## Architecture

- **UI:** WinUI 3 (Windows App SDK 1.6), MVVM via the CommunityToolkit source generators
- **Rendering:** Markdig (Markdown → HTML) → WebView2 (Chromium) for preview and PDF
- **DOCX:** DocumentFormat.OpenXml, native AST‑to‑OOXML mapping
- **Math/code:** KaTeX and highlight.js, loaded only when a document needs them
- **Automation:** `FileSystemWatcher`, clipboard polling, and an `HttpListener` REST API
- **Governance:** local JSON collector with a self‑contained HTML dashboard

> Marksmith began life as a single‑file Python/Textual TUI; this repository is the ground‑up
> native rewrite.

---

## Roadmap

- Branding kit (logo/letterhead, page numbers, cover pages)
- Export presets and batch conversion queue
- Delivery connectors (SharePoint / Drive / Slack)
- EPUB and PPTX‑outline export

---

## License

MIT. See [LICENSE](LICENSE).
