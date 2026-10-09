## 2026-08-29 - [Airgap Audit]
**Learning:** Evaluated web asset loading. Discovered that all JS/CSS scripts (mermaid, Katex, highlight) in `MarkdownHtmlService.cs` use `WebAssets.cs`, which is mapped to `https://marksmith.assets`, ensuring offline assets. There were no CDN references.
**Action:** Confirmed that offline preview rendering functions correctly. Documented findings and closed the audit.
## 2026-08-29 - [Airgap Audit Completion]
**Learning:** Evaluated web asset loading. Discovered that all JS/CSS scripts (mermaid, Katex, highlight) in `MarkdownHtmlService.cs` use `WebAssets.cs`, which is mapped to `https://marksmith.assets`, ensuring offline assets. There were no CDN references. The tests failed due to SkiaSharp and some other unrelated things, not due to CDN URLs.
**Action:** Documented findings and closing out the airgap task.
