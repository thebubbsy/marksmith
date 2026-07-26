# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet test` to verify 0 errors/failures, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks (Cycle 6)

### [ ] 10. PDF Page Numbering & Custom Header/Footer Engine
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/PdfExportService.cs`
  - `marksmith-v2/MdToPdf.Core/Models/AppSettings.cs`
  - `marksmith-v2/MdToPdf/Views/SettingsView.xaml`
- **Goal**:
  - Add configurable header and footer template strings for PDF exports (tokens: `{title}`, `{page}`, `{pages}`, `{date}`).
  - Add page number position options (Bottom Right, Bottom Center, Top Right) in PDF export settings.
  - Verification: Add unit tests in `PdfExportServiceTests.cs` verifying header/footer string token substitution.

### [ ] 11. Multi-File Drag & Drop Batch Queue & Watcher Debounce
- **Target Files**:
  - `marksmith-v2/MdToPdf/MainWindow.xaml.cs`
  - `marksmith-v2/MdToPdf/Services/FolderIngestService.cs`
- **Goal**:
  - Support dragging multiple `.md` files onto the editor canvas, opening an interactive multi-file batch conversion queue card.
  - Implement file-lock retry and 300ms debounce in `FolderIngestService` to prevent `IOException` when AI tools overwrite files continuously.

### [ ] 12. Visual Mermaid Diagram Standalone Graphic Exporter (SVG / PNG)
- **Target Files**:
  - `marksmith-v2/MdToPdf/ViewModels/Mermaid/MermaidStudioViewModel.cs`
  - `marksmith-v2/MdToPdf/Views/Mermaid/MermaidDiagramStudioControl.xaml`
- **Goal**:
  - Add "Export as SVG" and "Export as PNG" standalone graphic export buttons in the Visual Mermaid Studio toolbar.
  - Allow users to save rendered diagrams as high-res images for external presentation decks or web pages.

---

## 📌 History & Completed Tasks

- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total.
- **2026-07-27 02:10**: Cycle 2 — Tasks 3 & 4 completed (toolbar consolidated into 5 dropdown clusters + title-bar quick actions; ApiServer AllowedExtensionId auth tests + multi-format export integration tests). Suite: 731 passed / 0 failed / 21 skipped / 752 total.
- **2026-07-27 03:41**: Cycle 3 queued — Tasks 5, 6, 7, 8 added.
- **2026-07-27 04:32**: Cycle 4 — Tasks 5, 6, 7, 8 completed (Mermaid Studio connector curve routing + 4 style presets; DeepSeek `<think>` / Perplexity cleaners; EPUB3 cover image + Dublin Core metadata; Ctrl+Shift+E/P/M shortcuts + zoom tooltip). Suite: 763 passed / 0 failed / 21 skipped / 784 total.
- **2026-07-27 05:03**: Cycle 5 queued — Task 9 (Multi-Cloud Storage & Auto-Sync Engine) added.
- **2026-07-27 06:01**: Cycle 5 — Task 9 completed (CloudStorageService auto-detecting OneDrive / Google Drive / Dropbox / Box / iCloud sync folders + WebDAV endpoint client; Settings -> Automation Cloud Sync panel; background auto-publish of exports; 21 new unit tests). Suite: 784 passed / 0 failed / 21 skipped / 805 total.
- **2026-07-27 06:52**: Scheduled 15-min check verified Task 9 commit (`917d498`), 100% test pass rate (784 passed). Cycle 6 queued — Tasks 10, 11, 12 added.
