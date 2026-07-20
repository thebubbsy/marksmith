<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/images/logo-full-dark.png">
  <img src="docs/images/logo-full-light.png" alt="Marksmith Logo" width="280">
</picture>

# Marksmith

### Transform AI Chats and Markdown into Executive-Ready Documents

Paste your conversations from ChatGPT, Claude, or Gemini. Marksmith instantly fingerprints the source AI, strips away copy-paste clutter, normalizes formatting discrepancies, and renders gorgeous, professional **PDFs** and **DOCX** files with complete thematic styling.

[![CI](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml/badge.svg)](https://github.com/thebubbsy/marksmith/actions/workflows/ci.yml)
![platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![WinUI 3](https://img.shields.io/badge/WinUI-3-purple)

</div>

---

## 🌟 Why Marksmith?

AI assistants write great content, but they output raw, unpolished Markdown: repetitive bolding, unformatted LaTeX markers (`\( \)`), citation footnotes (`【4†source】`), and code block borders. Sharing these directly looks unpolished.

Marksmith bridges the gap between raw AI responses and boardroom-ready documents. It acts as an automated typography pipeline that:
- Cleans citation artifacts, stray tags, and markdown metadata automatically.
- Re-structures heading levels to match your document hierarchy.
- Translates formatting (like bolding and spacing) into elegant layouts.
- Compiles math equations and diagrams into native, editable Microsoft Word elements.

Available as a native, ultra-responsive **WinUI 3** desktop application for Windows, and an **Avalonia UI** preview build for Windows, Linux, and macOS.

---

## 🚀 Key Features

### 1. ⌨️ Premium Live Markdown Editor
Write, edit, or paste. The dual-pane interface re-renders your document in real-time on the right. 
- **Visual Markdown Toolbar**: Instantly format text, insert lists, headings, links, and tables, or inject advanced components at the click of a button.
- **Modern Layout**: Designed with premium acrylic and mica brushes to match modern OS aesthetics.

### 2. 🧠 ShapeForge™ — Native Editable Diagrams
Unlike standard converters that paste low-resolution screenshots of diagrams, Marksmith’s **ShapeForge™ engine** translates Mermaid blocks (` ```mermaid `) into **native Microsoft Word shapes** (`DrawingML`):
- **100% Editable**: Change shapes, resize boxes, drag lines, or edit text labels directly inside Microsoft Word.
- **Accented Theme Immersion**: Flowcharts, Gantt charts, sequence diagrams, and mindmaps automatically inherit your selected document theme's color palette.
- **Smart Connectors**: Upgraded line connectors stay glued to their parent shapes when dragged around in Word, supporting custom arrowheads, diamond/circle heads, curved/elbow routing, and dashed styles.

### 3. 📊 Native Charts & Vector Canvas
- **DrawingML Charts**: Convert simple CSV/TSV data or JSON blocks (`:::chart`) into native Word charts (Bar, Line, Pie) backed by an embedded, editable Excel worksheet. Click "Edit Data" in Word to tweak the numbers.
- **Vector Canvas Rendering**: Translates standard SVG vector paths (`:::canvas`) directly into native Word custom geometries (`<a:custGeom>`) rather than static raster images.

### 4. 🗃️ Datagrids & Repeatable Tables
- **Theme-Accented Datagrids**: Render JSON/CSV/TSV tables (`:::datagrid`) with repeated headers across page breaks (`tblHeader`), zebra striping, and no mid-page row splits.
- **Accessibility Alt Text**: Automatically embeds captions and descriptions in tables for full screen-reader compliance.

### 5. 📚 Native Citations & AI Context
- **Citations & Bibliography**: Paste BibTeX-style citation blocks (`:::references`). Marksmith registers them into Word's native Sources database and generates a standard Microsoft Word Bibliography field.
- **Document Variables (AI Context)**: Store AI models, timestamps, and prompts (`:::ai-context`) directly in the Word file metadata (`w:docVars`) for version auditing and compliance.

### 6. ⚙️ Layout Personalization
- **Theme immersion**: Dracula, Cyberpunk, GitHub Light/Dark, Nordic, Solarized, and more. Set background colors and styling variables globally.
- **Attribution Control**: Hide or show AI source badges, strip emojis, or customize em-dash formatting to remove "AI signatures".
- **Dynamic Table of Contents**: Inject a live Table of Contents field that updates automatically in Word.

---

## 🔌 Local REST API

Marksmith runs a local loopback API (`127.0.0.1:47821`) allowing other tools, script pipelines, or extensions to drive document compilation hands-free.

```bash
# Convert Markdown to a polished PDF via local REST API
curl -X POST http://127.0.0.1:47821/api/convert \
     -H "Content-Type: application/json" \
     -d '{"markdown":"# Hello World\nConverted via local API.","theme":"Nordic"}' \
     -o output.pdf

# Convert Markdown to an editable DOCX via local REST API
curl -X POST http://127.0.0.1:47821/api/convert \
     -H "Content-Type: application/json" \
     -d '{"markdown":"# Hello World\nConverted via local API.","format":"docx"}' \
     -o output.docx
```

| Method | Endpoint | Description |
| --- | --- | --- |
| **GET** | `/api/health` | Liveness check & endpoint listing |
| **GET** | `/api/themes` | List all available styles |
| **POST** | `/api/classify` | Fingerprints the AI model source from a text snippet |
| **POST** | `/api/ingest` | Pushes Markdown text straight into the active app GUI |
| **POST** | `/api/convert` | Compiles Markdown to PDF or DOCX and returns file bytes |

---

## 📸 API Conversion & Output Examples

Below is a demonstration of the REST API converting this very README into both PDF and DOCX formats simultaneously, capturing the local command line request, the generated files, and the rendered layouts side-by-side:

![REST API Conversion Process and Outputs](docs/images/api-conversion-process.png)

### Themed ShapeForge™ Mermaid Output Example
Here is how ShapeForge™ rebuilds and renders Mermaid flowcharts inside Word, fully immersive and styled matching Dracula and GitHub Light themes:

![Dracula vs Light ShapeForge Layouts](docs/images/mermaid-themes.png)

---

## 🛠️ Developer Setup & Compilation

### Requirements
- **.NET 8.0 SDK** (or later)
- **Windows App SDK** 1.6+ (for WinUI 3 desktop)

### Clone & Build
```powershell
# Clone the repository
git clone https://github.com/thebubbsy/marksmith.git
cd marksmith

# Rebuild the solution
dotnet build -p:Platform=x64
```

### Run
```powershell
# Start the WinUI 3 Desktop App
dotnet run --project MdToPdf/MdToPdf.csproj
```
