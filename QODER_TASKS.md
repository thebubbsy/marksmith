# Qoder Task Queue & Feature Backlog — Marksmith v2

> **Instructions for Qoder**: This file is polled periodically to assign next technical priorities. Pick the next uncompleted task (`[ ]`), execute changes, run `dotnet test` to verify 0 errors/failures, and mark it complete (`[x]`).

---

## 🚀 Active Priority Tasks (Cycle 5)

### [ ] 9. Multi-Cloud Storage Provider & Auto-Sync Engine (OneDrive, Google Drive, Dropbox, Box, WebDAV)
- **Target Files**:
  - `marksmith-v2/MdToPdf.Core/Services/CloudStorageService.cs` (new core service)
  - `marksmith-v2/MdToPdf.Core/Models/CloudProviderInfo.cs` (provider metadata POCO)
  - `marksmith-v2/MdToPdf/Views/SettingsView.xaml` (Cloud Storage panel under Automation)
  - `marksmith-v2/tests/MdToPdf.Core.Tests/CloudStorageServiceTests.cs` (unit tests)
- **Goal**:
  - Build a unified `CloudStorageService` auto-detecting and integrating local cloud drive sync directories across Windows:
    1. **Microsoft OneDrive**: `%USERPROFILE%\OneDrive`, `%USERPROFILE%\OneDrive - [Business]`
    2. **Google Drive**: `%USERPROFILE%\Google Drive`, `G:\My Drive` (Google Drive for Desktop volume)
    3. **Dropbox**: `%USERPROFILE%\Dropbox`, `%APPDATA%\Dropbox\info.json`
    4. **Box Sync / iCloud Drive**: `%USERPROFILE%\Box`, `%USERPROFILE%\iCloudDrive`
    5. **Nextcloud / OwnCloud / WebDAV**: Standard WebDAV REST client fallback with endpoint, username, password/token auth.
  - Features:
    - Auto-detect active cloud storage folders on system startup and display detected drives in Settings -> Automation -> Cloud Sync.
    - Add "Save & Export to Cloud Storage" option in the main export split button and folder ingest menu.
    - Enable background auto-publishing of exported PDF/DOCX/PPTX/EPUB files directly into designated Cloud Drive folders upon document export.
  - Verification: Add unit tests verifying path resolution, provider detection, and mock upload/sync dispatching across all 5 provider targets.

---

## 📌 History & Completed Tasks

- **2026-07-27 00:26**: Queue initialized for Qoder polling.
- **2026-07-27 01:15**: Cycle 1 — Tasks 1 & 2 completed (extension mermaid recovery + chained-arrow parser fix / spatial metadata hiding). Suite: 719 passed / 0 failed / 21 skipped / 740 total.
- **2026-07-27 02:10**: Cycle 2 — Tasks 3 & 4 completed (toolbar consolidated into 5 dropdown clusters + title-bar quick actions; ApiServer AllowedExtensionId auth tests + multi-format export integration tests). Suite: 731 passed / 0 failed / 21 skipped / 752 total.
- **2026-07-27 03:41**: Cycle 3 queued — Tasks 5, 6, 7, 8 added.
- **2026-07-27 04:32**: Cycle 4 — Tasks 5, 6, 7, 8 completed (Mermaid Studio connector curve routing + 4 style presets; DeepSeek `<think>` / Perplexity cleaners; EPUB3 cover image + Dublin Core metadata; Ctrl+Shift+E/P/M shortcuts + zoom tooltip). Suite: 763 passed / 0 failed / 21 skipped / 784 total.
- **2026-07-27 05:03**: Cycle 5 queued — Task 9 (Multi-Cloud Storage & Auto-Sync Engine for OneDrive, Google Drive, Dropbox, Box, Nextcloud / WebDAV) added.
