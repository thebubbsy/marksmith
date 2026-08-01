<div align="center">

![Marksmith](docs/images/logo-full-dark.png#gh-dark-mode-only)
![Marksmith](docs/images/logo-full-light.png#gh-light-mode-only)

# Marksmith

### Turn AI chats into polished documents.

Paste a reply from ChatGPT, Gemini, Claude, or Copilot — Marksmith detects the source, cleans up
the formatting quirks each one leaves behind, and exports a professional **PDF** or **DOCX**.
Import your company's `.dotx` template and it even matches your brand — using the AI you already
have, at zero extra cost.

![Marksmith](docs/images/hero.png)

[![CI](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml/badge.svg)](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml)
![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WinUI 3](https://img.shields.io/badge/WinUI-3-purple)
![license](https://img.shields.io/badge/license-Proprietary-black)

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

### ⌨️ A live Markdown editor

You don't need an AI chat to use Marksmith — switch to the **Paste** tab and just start writing.
Every keystroke re-renders the document on the right, so you're always looking at the finished
page, not the source. You can use the fully functional **Visual Markdown Toolbar** at the bottom of the editor to quickly format text (bold, italic, headings, lists, tables) or inject advanced Marksmith elements (tabs, columns, charts, drawings) directly at your cursor. When it looks right, hit **Generate PDF** (or **Export DOCX**) and it lands in your output folder.

![Typing Markdown into Marksmith with the preview updating live, then exporting a PDF](docs/images/editor-demo.gif)

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
switches and servers to individual **HDD bays**, with real‑world addressing throughout. By default, large diagrams are rendered in their exact proportions using the **Keep original size (web layout)** mode, prompting Word to open in Web Layout view for side-to-side scrolling rather than squeezing or reflowing the layout. Since every box, line, and connection is generated as a native DrawingML shape, you can click on any sub-shape and edit or move it directly inside Word:

![Full global network topology rendered into Microsoft Word as native DrawingML vector shapes](docs/images/global-network-word-render.png)

### 🧩 Reverse-Engineered SmartArt Engine & Bidirectional GLOX Builder Suite

Marksmith is no longer just a Markdown-to-DOCX converter — it is a **bidirectional SmartArt layout engine**:

- **176 Standard Office SmartArt Layouts**: Marksmith embeds standard Office `.glox` definitions and reverse-engineers Word's spatial layout constraint solver (`linear`, `tx`, `hier`, `snake`) to generate native OpenXML `DiagramDataPart` (`drawingml/2006/diagram`).
- **Custom SmartArt Authoring**: Build entirely custom SmartArt topologies from scratch using simple JSON definitions or the fluent C# `GloxBuilder` API.
- **CLI Layout Compiler**: Compile custom JSON layout definitions into native Microsoft `.glox` package archives ready for sideloading into Microsoft Word's `%APPDATA%\Microsoft\Templates\SmartArt Graphics` gallery:

```pwsh
# Compile a custom JSON definition into a native Microsoft Word SmartArt .glox package
marksmith build-layout custom_kanban.json custom_kanban.glox
```

### 📄 Advanced Word & OpenXML Capabilities

- **Editable Word Equations (OMML)** — LaTeX equations (`$x^2 + y^2 = z^2$`) are converted natively to Microsoft Word OMML. Clicking any formula opens Word's native Equation ribbon for full editing.
- **Native Lists done right** — Proper `numbering.xml` linking with `ilvl` indents and `abstractNum` definitions.
- **Auto-Updating Table of Contents** — Field codes (`w:instrText`) emitted inside Word SDT content controls.
- **Advanced OpenXML Elements**:
  - **Tabs (`:::tabs`)** — Native Word outline tabs and content controls.
  - **Link Panels (`:::embed`)** — Web link cards with site icons.
  - **Charts (`:::chart`)** — Native Word Pie, Line, and Bar charts backed by embedded Excel binary parts.
  - **Data Grids (`:::datagrid`)** — Styled tabular grids with repeating headers.
  - **Columns (`:::columns`)** — Section multi-column structures with continuous breaks.
  - **Task Lists (`- [x]`)** — Mapped directly to native Word `w14:checkbox` Content Controls.

---

## 🤖 Automation & CLI Tools

### 💻 Standalone Marksmith CLI

Marksmith includes a standalone command-line compiler for terminal workflows and automated build scripts:

```pwsh
# Convert Markdown to Word DOCX
marksmith <input.md> <output.docx> [layout_alias]

# Build a custom SmartArt GLOX package
marksmith build-layout <input_layout.json> <output.glox>
```

### 🔌 Local REST API

A loopback‑only API (`127.0.0.1:47821`) lets scripts and other tools drive Marksmith:

```bash
# Convert Markdown to a PDF or DOCX over HTTP
curl -X POST http://127.0.0.1:47821/api/convert \
     -H "Content-Type: application/json" \
     -d '{"markdown":"# Hello\n\nFrom **Marksmith**.","theme":"GitHub Dark"}' \
     -o out.pdf
```

![REST API Conversion Process and Outputs](docs/images/api-conversion-process.png)

| Endpoint | Purpose |
| --- | --- |
| `GET  /api/health` | Liveness + endpoint list |
| `GET  /api/themes` | Available theme names |
| `GET  /api/settings` | Retrieve active application settings |
| `POST /api/settings` | Update persistent settings |
| `POST /api/classify` | Detect the AI source of a Markdown blob |
| `POST /api/ingest` | Push Markdown into the app UI |
| `POST /api/convert` | Return a rendered PDF, DOCX, PPTX, or EPUB |
| `POST /api/batch` | Batch convert all Markdown files in a folder |
| `GET  /api/commands` | Pending jobs for the browser extension (Zero‑Click Pipeline) |
| `POST /api/commands/result` | Extension posts a completed job's reply |

### 🧩 Browser extension

The **Marksmith Connector** (Chrome/Edge, MV3) adds a one‑click "send to Marksmith" button on
ChatGPT, Gemini, Claude, and Copilot — it converts the reply to Markdown and posts it to the local
API. It can also **auto-send at the end of a conversation** (when you stop interacting) and carries
its own **output profile** — theme, page layout, and formatting set in the connector's Options page —
so automated PDFs use those settings independently of the app's own Style panel.

### 🏢 House‑Style Automation (Zero‑Click Pipeline)

Make every export match your company's brand — **without touching an API key or paying a token.**
Marksmith stays 100% offline; the AI you *already use* in the browser does the creative work.

1. **Import a `.dotx` template** — Settings → Automation → *Import Company Template*. Marksmith
   parses the OOXML locally: body & heading fonts, heading colors, and the full `theme1.xml` color
   palette (accents, hyperlinks, page background).
2. **Zero‑click prompt delivery** — the style summary is engineered into an unambiguous prompt and
   pushed to the browser extension over the loopback command channel. The extension finds your
   active ChatGPT / Gemini / Claude / Copilot tab, pastes the prompt into the composer, and hits
   send.

![The engineered prompt auto-injected into a web AI](docs/images/housestyle-ai-prompt.png)

3. **Your AI answers, Marksmith listens** — the reply is a strict `ThemeDefinition` JSON. Marksmith
   parses it, saves it as a custom theme, and applies it to preview, PDF, and DOCX.

![A document rendered in Marksmith using the brand-matched custom theme](docs/images/housestyle-themed-preview.png)

### 🏢 AI Usage Governance (for organizations)

Marksmith can double as a **configurable AI‑usage governance and DLP tool.** In org mode the
extension monitors employee interactions with AI chats, reporting metadata and data-loss-prevention flags to a self-hosted admin dashboard. 

![AI Usage Governance dashboard](docs/images/governance.png)

---

## Examples

Real documents run through Marksmith — inputs *and* their rendered PDFs **and DOCXs** — live in [`examples/`](examples/).

What sets Marksmith apart is its **proprietary native Word rendering**. Our unique converter takes Markdown and builds native, schema-valid OOXML under the hood. No other software on the market can do this:

- **[`product-spec.md`](examples/product-spec.md)** features a **massive, complex Mermaid architecture diagram** that is reconstructed into *native, editable Word shapes* in the resulting DOCX.
- **[`math-cheatsheet.md`](examples/math-cheatsheet.md)** demonstrates our **Clean Math** rendering, translating complex LaTeX blocks natively into Word's OMML equation editor.
- **[`chatgpt-export.md`](examples/chatgpt-export.md)** shows off how messy conversational artifacts are automatically cleansed.

<img src="docs/images/example-product-spec.png" width="540" alt="The product-spec example rendered to PDF by Marksmith">

---

## Download

**[⬇️ Download the latest release](https://github.com/thebubbsy/marksmith/releases/latest)** —
grab `Marksmith-win-x64.zip`, unzip anywhere, and run `Marksmith.exe`. No installer, no .NET SDK,
nothing else to set up — the .NET runtime and Windows App SDK are bundled in.

---

## Architecture

- **UI:** WinUI 3 (Windows App SDK 1.6), MVVM via CommunityToolkit.Mvvm in `marksmith-v2/MarkSmith.Desktop`
- **WebAssembly:** Client-side Blazor editor in `marksmith-v2/MarkSmith.Wasm`
- **Core Engine (`marksmith-v2/MarkSmith.Core`):** Reverse-engineered SmartArt layout engine, constraint solver, GLOX Builder Suite, AST parsers, & document exporters
- **CLI (`marksmith-v2/MarkSmith.Cli`):** Standalone zero-dependency command-line compiler and `.glox` layout builder
- **REST API Daemon (`marksmith-v2/MarkSmith.Api`):** Local loopback REST API server
- **Test Suite (`marksmith-v2/MarkSmith.Tests`):** Consolidated empirical test suite and OpenXML schema validator
- **Rendering:** Markdig (Markdown → HTML) → WebView2 (Chromium) for preview and PDF
- **DOCX Exporter:** DocumentFormat.OpenXml, native AST‑to‑OOXML mapping & OMML math
- **Automation:** `FileSystemWatcher`, clipboard polling, and `HttpListener` REST API
- **Governance:** local JSON collector with a self‑contained HTML dashboard

---

## Roadmap

See [ROADMAP.md](ROADMAP.md) for the full Now / Next / Later.

---

## License

**Proprietary — © 2026 thebubbsy. All rights reserved.** Marksmith is commercial software, not
open source. Use is governed by the [End-User License Agreement](LICENSE); redistribution,
modification, and reverse engineering are not permitted except as allowed by law. Third-party
components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
