## 2024-05-27 - Audit and Lock Down Offline Web Assets
**Learning:** `MdToPdf.Core/Services/WebAssets.cs` defines local virtual host endpoints (`https://marksmith.assets/`) for all scripts and styles. `MainWindow.xaml.cs` maps this host. `MarkdownHtmlService.cs` correctly uses these via `WebAssets`. There are no external CDN calls (`jsdelivr`, `unpkg`, `cloudflare`) found in the HTML generation logic.
**Action:** MarkSmith continues to operate 100% air-gapped without external network calls for preview rendering.
