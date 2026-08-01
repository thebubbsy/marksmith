# Marksmith v2 — The Offline Document Engine 🚀

![Marksmith Hero Architecture Banner](https://raw.githubusercontent.com/antigravity/marksmith/main/assets/hero-banner.png)

> **Turn AI Style Into Your Style.**  
> Marksmith v2 is a 100% offline, native Windows 11 desktop engine and REST API designed to transform raw AI outputs (from ChatGPT, Gemini, Claude, and local LLMs) into publication-ready, professional Microsoft Office documents, PDFs, PowerPoint decks, and EPUB eBooks.

---

## 🌟 What's New in Marksmith v2 (Release Highlights)

### 1. 📐 Native Editable Word Equations (OMML)
- **Zero Image Rendering**: Unlike traditional converters that render math formulas as flat PNG images, Marksmith converts raw LaTeX math (`$`, `$$`, `\begin{matrix}`, integrals, fractions, roots, limits) directly into **native Microsoft Word OMML (Office Math Markup Language)**.
- **100% Editable in Word**: Opening an exported `.docx` file in Microsoft Word allows users to click directly into any matrix or formula—illuminating the **Word Equation Tools** ribbon to edit numbers and variables natively.

### 2. 🎨 Visual Mermaid Diagram Studio & Grouped Word Vector Shapes
- **Interactive Visual Editor**: Built-in drag-and-drop diagram studio with snap-to-grid alignment, minimap navigation, force-directed **Auto Layout (`⚡ Auto Layout`)**, orientation switching (`LR` Left-to-Right, `TD` Top-Down, `BT`, `RL`), and full undo/redo (`Ctrl+Z` / `Ctrl+Y`).
- **Grouped Vector Shapes in Word**: Flowcharts exported to `.docx` are **not flat screenshots**. Marksmith converts Mermaid diagrams into **native grouped Word vector shapes** (rectangles, diamonds, connectors, and text runs). Teams can drag boxes, edit text labels, recolor borders, and adjust arrow paths directly inside Word.

### 3. 📄 Multi-Format Export Engine (PDF • DOCX • PPTX • EPUB)
- **Microsoft Word (.docx)**: Styled OpenType typography, dark theme document defaults (`w:background`), custom table borders & zebra shading, single-cell callout cards for GitHub alerts (`> [!NOTE]`, `> [!WARNING]`), drop caps, bookmarks, and automatic self-updating Table of Contents.
- **PowerPoint Decks (.pptx)**: Converts Markdown header structures (`# Slide 1`, `## Slide 2`) into formatted presentation decks.
- **EPUB eBooks**: Generates reflowable EPUB3 eBooks complete with automatic table of contents and chapter navigation.
- **High-Resolution PDF**: Deterministic PDF generation powered by a local Chromium engine (`CoreWebView2`).

### 4. 🛡️ 100% Air-Gapped Local REST API & Enterprise DLP Governance
- **Local Loopback REST API (`http://127.0.0.1:47821`)**: Full programmatic conversion endpoints (`/api/convert`, `/api/governance/report`, `/api/governance/summary`) allowing local scripts, terminal commands, and watch-folder daemons to compile Markdown into PDF/DOCX/PPTX/EPUB silently.
- **1-Click Browser Connector**: Chrome/Edge browser extension that streams replies from ChatGPT, Gemini, and Claude directly into Marksmith in one click.
- **Data Loss Prevention (DLP)**: Local governance script monitors AI prompt ingestion and automatically masks sensitive credentials, passwords, and API keys (`sk-proj-[redacted]`) before saving local audit logs (`%LOCALAPPDATA%\MdToPdf\governance.json`). Zero remote cloud server calls.

### 5. 🧹 AI Source Normalization & Artifact Cleanup Engine
- **Quirk Normalization**: Automatically detects source models (ChatGPT, Gemini, Claude) and strips machine-generated tells:
  - Excessive emojis and em-dashes (`—`)
  - Citation pips (`[1]`)
  - Redundant `"Copy code"` HTML button remnants
  - Conversational lead-in chatter (*"Sure, here is your requested document:"*)
- **Real-Time Fix Counter**: Displays exact count of normalized formatting quirks applied to the document.

### 6. 🔍 Looking Glass Inspection Lens & Live 60 FPS Preview
- **Dual-Pane Preview**: Live preview updates at 60 FPS without flicker as you edit Markdown text.
- **Looking Glass Lens**: Hovering over rendered HTML elements activates a circular high-tech portal lens that reveals the raw underlying Markdown syntax.

### 7. 💎 Windows 11 Polish & Native Shell Integration
- **Modern WinUI 3 Architecture**: Mica backdrop blur, custom 48px title bar (`AppTitleBar`), official Marksmith taskbar icon (`Assets/app.ico`), suppressed WinUI accelerator hover tooltips (`KeyboardAcceleratorPlacementMode="Hidden"`), and clean Segoe MDL2 Fluent icons.

---

## 🛠️ Project Structure

```text
marksmith-v2/
├── MdToPdf/                                # WinUI 3 Presentation App
│   ├── Assets/                             # App icons, tray icons, brand assets (app.ico)
│   ├── ViewModels/                         # CommunityToolkit.Mvvm ViewModels
│   ├── Views/                              # XAML UI Views & Controls
│   │   └── Mermaid/                        # Visual Diagram Studio (MermaidDiagramStudioControl.xaml)
│   └── MainWindow.xaml                     # Main Application Window shell & title bar
├── MdToPdf.Core/                           # Core Document & Rendering Engine
│   ├── Mermaid/                            # Mermaid AST, Lexer, Parser & Code Generator
│   ├── Plugins/                            # Extensible Plugin System
│   └── Services/                           # Native Exporters:
│       ├── DocxExportService.cs            # OpenXML DOCX & OMML Math generator
│       ├── PptxExportService.cs            # PowerPoint (.pptx) deck generator
│       ├── EpubExportService.cs            # EPUB eBook generator
│       ├── PdfExportService.cs             # Chromium WebView2 PDF printer
│       └── GovernanceService.cs            # Local DLP & Governance audit logger
└── tests/                                  # Empirical Test Suite
    └── MdToPdf.Core.Tests/                 # Unit tests & OpenXML validation checks
```

---

## 🚀 Building & Running

### Requirements
- **Windows 11 (x64)**
- **.NET 8 SDK** (`dotnet --list-sdks`)
- **Visual Studio 2022** (17.8+) with the *Windows application development* workload.

### Command Line Build

```powershell
# Clone the repository
git clone https://github.com/antigravity/marksmith.git
cd marksmith/marksmith-v2/MdToPdf

# Restore dependencies & build Release x64
dotnet restore
dotnet build MdToPdf.csproj -c Release /p:Platform=x64

# Run locally
dotnet run --project MdToPdf.csproj -c Release /p:Platform=x64
```

### Publishing Standalone Single-File Executable (`Marksmith.exe`)

To publish a self-contained, zero-dependency executable bundled with the .NET 8 runtime, WinUI 3, and native WebView2 binaries:

```powershell
dotnet publish MdToPdf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output binary: `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish\Marksmith.exe` (~180 MB standalone).

---

## 🔌 Local REST API Reference

When Marksmith is running, the local REST API listens on `http://127.0.0.1:47821`:

### 1. Convert Markdown to Output Format
`POST /api/convert`

**Request Payload (JSON)**:
```json
{
  "markdown": "# Financial Report\n$$\\sum_{i=1}^n x_i = Y$$\n",
  "format": "docx",
  "theme": "Dracula"
}
```

**Supported Formats**: `"pdf"`, `"docx"`, `"pptx"`, `"epub"`

### 2. Ingest Extension Report & DLP Scan
`POST /api/governance/report`

### 3. Get Governance Audit Summary
`GET /api/governance/summary`

---

## 📜 License

Distributed under the MIT License. See `LICENSE` for details.
