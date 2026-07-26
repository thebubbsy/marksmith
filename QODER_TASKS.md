# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet build` to verify 0 errors/warnings, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks

### 1. Fix Browser Extension ChatGPT Mermaid Code Recovery (Issue #8) — [x] DONE 2026-07-27
- **Target Files**: `extension/background.js`, `extension/copybutton.js`
- **Problem**: When ChatGPT renders interactive SVG/canvas Mermaid widgets, `recoverMermaidSources()` fails to find the raw ` ```mermaid ` source code, causing Marksmith to ingest only the header text `"Mermaid"`.
- **Goal**:
  - Update DOM selectors in `recoverMermaidSources()` to match ChatGPT's newest widget wrappers.
  - Implement fallback logic to inspect `<code class="language-mermaid">`, `data-code` attributes, or programmatically trigger ChatGPT's "Code" toggle button prior to Markdown conversion.
  - Ensure `convertHtmlToMarkdown` replaces the widget with a clean fenced ` ```mermaid ` block.
- **Done**: Widget detection now accepts `<canvas>` renders, "diagram" class/testid variants, and header-label ("Mermaid") wrappers; source recovery adds mermaid-keyword sniffing of any `code`/`pre`/`textarea`, `data-code`/`data-content`/`data-source` attributes, and text-labeled "Code"/"Source" toggle tabs. `copybutton.js` gained the same recovery pre-pass (it previously dropped diagrams entirely). Both files pass `node --check`.

### 2. Mermaid Chained-Arrow Parser Fix & Spatial Metadata Hiding (Issue #7) — [x] DONE 2026-07-27
- **Target File**: `marksmith-v2/MdToPdf.Core/Mermaid/Parser/FlowchartParser.cs`
- **Problem**: Chained arrow lines (e.g., `A -->|Yes| B -->|No| C`) fail regex parsing because `FlowchartParser.cs` assumes only one edge per line, misparsing `B -->|No| C` as a single node ID.
- **Goal**:
  - Refactor `ParseLine()` to loop sequentially across multiple edge operators per line.
  - Hide/strip `%% {"id": ...}` spatial comments from the primary Markdown editor view while preserving them during export sync.
- **Done**: `ParseLine()` now walks every edge operator per line (with a bracket-balance guard so arrows inside node labels don't false-chain). New `MermaidSpatialMetadataService` strips `%% {"id":...}` lines from editor text into a per-block stash and re-injects them on Diagram Studio open, studio sync, and file save; wired into `MainWindow.xaml.cs`. Covered by 10 new tests (suite: 719 passed / 0 failed / 21 skipped / 740 total).

### 3. UI Layout De-Cluttering & Toolbar Consolidation (Issue #6)
- **Target File**: `marksmith-v2/MdToPdf/MainWindow.xaml`
- **Goal**:
  - Consolidate the 20+ horizontal formatting buttons into 5 dropdown clusters (`Text Style`, `Lists ▼`, `+ Insert ▼`, `Tools ▼`, `View & Zoom ▼`) to save ~100px vertical workspace.
  - Move primary export controls (`Export PDF`, `Export Word`, `Diagram Studio 🎨`, `Settings ⚙`) into the empty right half of the custom Title Bar (`AppTitleBar`).

### 4. Automated Integration & Export Test Suite Coverage
- **Target Project**: `marksmith-v2/tests/MdToPdf.Core.Tests`
- **Goal**:
  - Add unit tests for `AllowedExtensionId` authorization check in `ApiServer.cs`.
  - Add integration tests verifying multi-format exports (PDF, DOCX, PPTX, EPUB) generate non-zero-byte valid archives without throwing exceptions.

---

## 📌 Status Log
- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total. Tasks 3 & 4 queued for next cycles.
