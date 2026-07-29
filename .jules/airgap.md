## 2024-03-24 - Initial Audit
**Learning:** Evaluated all scripts and links loaded in MarkdownHtmlService.cs and WebAssets.cs.
**Action:** Confirmed that all loaded assets point to WebAssets.* fields. WebAssets.Host points to marksmith.assets. No CDN URLs exist.
## 2024-03-24 - Pre-commit Verification
**Learning:** Evaluated the entire codebase and verified `WebAssets.cs` and `MarkdownHtmlService.cs`. The only external dependencies found were in `website/index.html` (fonts) and `marketing/mock_gui.html` (scripts), which do not run as part of the core app's preview logic and are thus irrelevant to the airgap requirement for the Markdown preview engine. There are no external network calls made during preview rendering.
**Action:** Proceeded with tests and code review.
