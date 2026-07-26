# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet test` to verify 0 errors/failures, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks (Cycle 3)

### 5. Visual Mermaid Studio Connector Routing & Style Presets
- **Target Files**: `marksmith-v2/MdToPdf/Views/Mermaid/MermaidDiagramStudioControl.xaml`, `MermaidStudioViewModel.cs`
- **Goal**:
  - Add explicit connector curve controls (`Elbow / Orthogonal 90°`, `Straight`, `Curved / Bezier`) in the Visual Mermaid Studio toolbar.
  - Add preset diagram color palettes (`Catppuccin Slate`, `Nord Ocean`, `Emerald Corporate`, `Monochrome Print`) in the Diagram Studio inspector panel to quickly recolor diagram shape fills, borders, and arrow markers.

### 6. AI Quirk Normalization — DeepSeek & Perplexity AI Artifact Cleaners
- **Target Files**: `marksmith-v2/MdToPdf.Core/Services/ProviderDialectNormalizer.cs`, `DialectNormalizer.cs`
- **Goal**:
  - Add automatic detection and stripping for DeepSeek / Perplexity AI chat artifacts (e.g., `<think> ... </think>` reasoning tags, web search citation badges `[source]`, `[1]`, and raw prompt echo headers).
  - Add comprehensive unit tests in `DialectNormalizerTests.cs` for DeepSeek and Perplexity input streams.

### 7. Native EPUB Cover Image & Dublin Core Metadata Exporter
- **Target Files**: `marksmith-v2/MdToPdf.Core/Services/EpubExportService.cs`
- **Goal**:
  - Enable embedding custom book cover PNG/JPG images into EPUB3 exports when `BrandCoverPage` / `BrandLogoPath` is enabled.
  - Inject Dublin Core XML metadata (`dc:title`, `dc:creator`, `dc:language`, `dc:identifier`) into EPUB `content.opf` based on document frontmatter or `ContentLanguage` settings.

### 8. Global Keyboard Shortcuts & Live Zoom Diagnostics
- **Target Files**: `marksmith-v2/MdToPdf/MainWindow.xaml.cs`, `MainViewModel.cs`
- **Goal**:
  - Register global keyboard shortcuts (`Ctrl+Shift+E` for instant DOCX export, `Ctrl+Shift+P` for instant PDF export, `Ctrl+Shift+M` to launch Visual Mermaid Studio).
  - Surface active preview zoom level dynamically in the status bar tooltip (`Preview Zoom: 100%`).

---

## 📌 History & Completed Tasks

- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total.
- **2026-07-27 02:10**: Cycle 2 — Tasks 3 & 4 completed (toolbar consolidated into 5 dropdown clusters + title-bar quick actions; ApiServer AllowedExtensionId auth tests + multi-format export integration tests). Suite: 731 passed / 0 failed / 21 skipped / 752 total.
- **2026-07-27 03:41**: Cycle 3 queued — Tasks 5, 6, 7, 8 added.
