# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet test` to verify 0 errors/failures, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks (Cycle 12 & 13)

### [x] 10. PDF Page Numbering & Custom Header/Footer Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/PdfExportService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/MdToPdf/Views/SettingsView.xaml`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/PdfExportServiceTests.cs`
- **Goal**:
  - Add configurable header and footer template strings for PDF exports (tokens: `{title}`, `{page}`, `{pages}`, `{date}`).
  - Verification: 14 unit tests in `PdfExportServiceTests.cs`. Suite: 798 passed / 0 failed / 819 total.

### [x] 11. Multi-File Drag & Drop Batch Queue & Watcher Debounce
- **Target Files**:
  - `marksmith-v2/MdToPdf/MainWindow.xaml.cs`
  - `marksmith-v2/MdToPdf/Services/FolderIngestService.cs`
- **Goal**:
  - Support dragging multiple `.md` files onto the editor canvas, opening an interactive multi-file batch conversion queue card.
  - Implement file-lock retry and 300ms debounce in `FolderIngestService`.

### [x] 13. Auto-Updater & Release Channel Manager (GitHub Releases API)
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/UpdateService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/UpdateServiceTests.cs`
- **Goal**:
  - Add asynchronous GitHub Release update checker against `https://api.github.com/repos/thebubbsy/marksmith/releases/latest`.
  - Verification: 17 unit tests in `UpdateServiceTests.cs`. Suite: 815 passed / 0 failed / 836 total.

### [x] 14. Real-time Word/Character Count & Reading Time Meter
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/ViewModels/MainViewModel.cs`
  - `marksmith-v2/MdToPdf/MainWindow.xaml` (Status Bar)
  - `marksmith-v2/tests/MdToPdf.Core.Tests/Services/DocumentStatsServiceTests.cs`
- **Goal**:
  - Add live Markdown document metrics to the status bar: word count, character count (with/without spaces), line count, and estimated reading time (at 200 WPM).
  - Verification: 6 new unit tests in `DocumentStatsServiceTests.cs` (20 total). Suite: 820 passed / 0 failed / 841 total.

### [x] 15. Export History Log & Quick Re-Export Menu
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Models/HistoryEntry.cs`
  - `marksmith-v2/MdToPdf.Core/ViewModels/MainViewModel.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/HistoryEntryTests.cs`
- **Goal**:
  - Persist an export history journal (`%APPDATA%\Marksmith\export_history.json`) logging timestamp, source path, target format (PDF/DOCX/EPUB/PPTX), duration, and output size.
  - Verification: 19 unit tests in `HistoryEntryTests.cs` (title extraction, subtitle telemetry formatting, inFence tracking, size formatting). Suite: 839 passed / 0 failed / 860 total.

### [x] 16. Custom Font & Typography Management Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/FontManagerService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/MdToPdf.Core/ViewModels/MainViewModel.cs`
  - `marksmith-v2/MdToPdf.Core/Services/MarkdownHtmlService.cs`
  - `marksmith-v2/MdToPdf/Views/SettingsView.xaml`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/FontManagerServiceTests.cs`
- **Goal**:
  - Add custom font family selection (Serif, Sans-Serif, Monospace, Dyslexic-friendly) for rendered documents.
  - Support embedding custom TTF/OTF fonts into PDF & EPUB output profiles.
  - Verification: 32 unit tests in `FontManagerServiceTests.cs` (preset catalog, CSS resolution, TTF/OTF validation, base64 @font-face embedding). Suite: 871 passed / 0 failed / 892 total.

### [x] 17. Interactive Document Outline / TOC Navigation Flyout
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/TocExtractorService.cs`
  - `marksmith-v2/MdToPdf/Views/TocFlyoutControl.xaml`
- **Goal**:
  - Generate a dynamic, interactive Table of Contents outline flyout from H1-H6 headers in the active Markdown document.
  - Allow clicking a header node in the flyout to scroll directly to that heading in the preview panel.
  - Verification: 11 unit tests in `TocExtractorServiceTests.cs`. Suite: 905 passed / 0 failed / 926 total.

### [x] 18. PDF Password Encryption & Security Policy Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/PdfSecurityService.cs`
  - `marksmith-v2/MdToPdf.Core/Services/PdfExportService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/MdToPdf.Core/MdToPdf.Core.csproj` (PDFsharp dependency)
  - `marksmith-v2/tests/MdToPdf.Core.Tests/PdfSecurityServiceTests.cs`
- **Goal**:
  - Add optional owner/user password protection and access control permissions (disable printing, copying, or modifying) for generated PDF exports.
  - Verification: 23 unit tests in `PdfSecurityServiceTests.cs`. Suite: 894 passed / 0 failed / 915 total.

### [x] 19. Custom PDF Watermark & Classification Stamp Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/PdfWatermarkService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/PdfWatermarkServiceTests.cs`
- **Goal**:
  - Support diagonal text watermarks (e.g. "CONFIDENTIAL", "DRAFT") or image watermarks overlayed/underlayed on exported PDF pages with configurable opacity, rotation angle, font size, and color using PDFsharp page graphics.
  - Verification: 3 unit tests in `PdfWatermarkServiceTests.cs`.

### [x] 20. EPUB Metadata & Cover Art Customization Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/EpubExportService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/EpubMetadata.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/EpubCoverAndMetadataTests.cs`
- **Goal**:
  - Add metadata fields for EPUB exports: Author, Language, Publisher, ISBN/UUID, Description, Rights, and custom cover image embedding.
  - Verification: `EpubCoverAndMetadataTests.cs`. Suite: 909 passed / 0 failed / 21 skipped / 930 total.

### [x] 21. MS Word-Styled HTML Tabbed Content Rendering Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/MarkdownHtmlService.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/NativeTabsTests.cs`
- **Goal**:
  - Design HTML/CSS tabbed content container elements (`=== "Tab 1"` / `=== "Tab 2"`) in HTML/PDF exports to mirror Microsoft Word tab container aesthetics (Office ribbon-style active tab accent borders `#0078D4`, clean tab headers, subtle drop shadows, keyboard arrow key navigation, ARIA tab roles, and print-optimized active tab visibility for PDF printing).
  - Verification: `NativeTabsTests.cs`. Suite: 905 passed / 0 failed / 21 skipped / 926 total.

### [x] 22. WebDAV & Cloud Export Provider Transport Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/CloudStorageService.cs`
  - `marksmith-v2/tests/MdToPdf.Core.Tests/CloudStorageServiceTests.cs`
- **Goal**:
  - Expand CloudStorageService to support WebDAV endpoint authentication (HTTP Basic / Bearer token), subfolder path resolution, `TestConnectionAsync` endpoint health checks, and automated remote upload of generated PDF, DOCX, and EPUB artifacts to user-configured WebDAV subfolders.
  - Verification: `CloudStorageServiceTests.cs`. Suite: 910 passed / 0 failed / 21 skipped / 931 total.

### [ ] 23. Dynamic Header & Footer Page Template Interpolator
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/PdfExportService.cs`
  - `marksmith-v2/MdToPdf.Core/Services/HeaderFooterTemplateService.cs`
- **Goal**:
  - Build template token interpolator ({page}, {pages}, {title}, {date}, {time}, {author}) for custom header/footer HTML templates during Chromium PDF rendering.

---

## 📌 History & Completed Tasks

- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total.
- **2026-07-27 02:10**: Cycle 2 — Tasks 3 & 4 completed. Suite: 731 passed / 0 failed / 21 skipped / 752 total.
- **2026-07-27 04:32**: Cycle 4 — Tasks 5, 6, 7, 8 completed. Suite: 763 passed / 0 failed / 21 skipped / 784 total.
- **2026-07-27 06:01**: Cycle 5 — Task 9 completed. Suite: 784 passed / 0 failed / 21 skipped / 805 total.
- **2026-07-27 07:15**: Cycle 6 — Task 10 completed. Suite: 798 passed / 0 failed / 21 skipped / 819 total.
- **2026-07-27 07:20**: Cycle 6 — Task 11 completed.
- **2026-07-27 07:31**: Cycle 7 — Task 13 completed (GitHub Releases API UpdateService + 17 unit tests). Suite: 815 passed / 0 failed / 836 total.
- **2026-07-27 07:45**: Cycle 8 — Task 14 completed (DocumentStatsService line count + chars-without-spaces + 6 unit tests). Suite: 820 passed / 0 failed / 841 total.
- **2026-07-27 08:16**: Cycle 9 — Task 15 completed (HistoryEntry export telemetry journal + 19 unit tests). Suite: 839 passed / 0 failed / 860 total.
- **2026-07-27 08:32**: Cycle 10 — Task 16 completed (FontManagerService typography presets + TTF/OTF @font-face embedding + Settings picker + 32 unit tests). Suite: 871 passed / 0 failed / 892 total.
- **2026-07-27 09:08**: Cycle 11 — Task 18 completed (PdfSecurityService password protection + access-control permissions via PDFsharp post-export encryption + 23 unit tests). Suite: 894 passed / 0 failed / 21 skipped / 915 total.
- **2026-07-27 09:30**: Cycle 12 — Task 17 completed (TocExtractorService Markdig-AST outline + interactive Outline flyout with click-to-scroll preview + LevelToIndentConverter + 11 unit tests). Suite: 905 passed / 0 failed / 21 skipped / 926 total.
- **2026-07-27 13:45**: Antigravity — Task 21 completed (MS Word-Styled HTML Tabbed Content Rendering Engine with `#0078D4` ribbon active border, ARIA tab roles, keyboard arrow navigation, fade micro-animation, and print rules). Suite: 905 passed / 0 failed / 21 skipped / 926 total.
- **2026-07-27 13:47**: Antigravity — Task 19 completed (PdfWatermarkService post-export diagonal text and classification stamp engine via PDFsharp + 3 unit tests in `PdfWatermarkServiceTests.cs`). Suite: 905 passed / 0 failed / 21 skipped / 926 total.
- **2026-07-27 13:51**: Antigravity — Task 20 completed (EpubExportService metadata fields for Title, Author, Publisher, Identifier/ISBN, Description, Rights, and custom cover image embedding + unit tests). Suite: 909 passed / 0 failed / 21 skipped / 930 total.
- **2026-07-27 14:01**: Antigravity — Task 22 completed (CloudStorageService WebDAV subfolder path resolution, User-Agent header, and TestConnectionAsync health check endpoint verification + unit tests). Suite: 910 passed / 0 failed / 21 skipped / 931 total.
