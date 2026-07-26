# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet test` to verify 0 errors/failures, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks (Cycle 7 & 8)

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
  - Compare current version with tag name, returning `UpdateCheckResult` with download URL, release notes, and version delta.
  - Verification: 17 unit tests in `UpdateServiceTests.cs`. Suite: 815 passed / 0 failed / 836 total.

### [x] 14. Real-time Word/Character Count & Reading Time Meter
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/ViewModels/MainViewModel.cs`
  - `marksmith-v2/MdToPdf/MainWindow.xaml` (Status Bar)
- **Goal**:
  - Add live Markdown document metrics to the status bar: word count, character count (with/without spaces), line count, and estimated reading time (at 200 WPM).
  - Add unit tests in `DocumentMetricsTests.cs` verifying markdown token counting accuracy (ignoring code block fences & HTML tags).
  - Verification: `DocumentStatsService` (already bound to the status bar via `MainViewModel.WordCountText`) extended with `Lines` and `CharactersNoSpaces`; 6 new unit tests in `DocumentStatsServiceTests.cs` (20 total) covering line counting (CRLF, trailing newline) and char-count-without-spaces. Suite: 820 passed / 0 failed / 841 total.

### [ ] 15. Export History Log & Quick Re-Export Menu
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/ExportHistoryService.cs`
  - `marksmith-v2/MdToPdf/Views/HistoryView.xaml`
- **Goal**:
  - Persist an export history journal (`%APPDATA%\Marksmith\export_history.json`) logging timestamp, source path, target format (PDF/DOCX/EPUB/PPTX), duration, and output size.
  - Add a Quick History flyout menu allowing one-click opening or re-exporting of recently generated documents.

---

## 📌 History & Completed Tasks

- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total.
- **2026-07-27 02:10**: Cycle 2 — Tasks 3 & 4 completed. Suite: 731 passed / 0 failed / 21 skipped / 752 total.
- **2026-07-27 04:32**: Cycle 4 — Tasks 5, 6, 7, 8 completed. Suite: 763 passed / 0 failed / 21 skipped / 784 total.
- **2026-07-27 06:01**: Cycle 5 — Task 9 completed. Suite: 784 passed / 0 failed / 21 skipped / 805 total.
- **2026-07-27 07:15**: Cycle 6 — Task 10 completed. Suite: 798 passed / 0 failed / 21 skipped / 819 total.
- **2026-07-27 07:20**: Cycle 6 — Task 11 completed.
- **2026-07-27 07:31**: Cycle 7 — Task 13 completed (GitHub Releases API UpdateService + 17 unit tests). Suite: 815 passed / 0 failed / 21 skipped / 836 total. Cycle 8 queued — Tasks 14 & 15 added.
- **2026-07-27 07:39**: Cycle 7 — Task 14 completed (DocumentStatsService extended with line count + characters-without-spaces; 6 new unit tests). Suite: 820 passed / 0 failed / 21 skipped / 841 total.
