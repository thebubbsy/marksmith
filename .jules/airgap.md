## 2025-02-14 - Initial Airgap Audit
**Learning:** Found that `marksmith-v2/MarkSmith.Core/Services/MarkdownHtmlService.cs` does not contain any remote fetches (`cdn.jsdelivr.net` or `cdnjs.cloudflare.com`). It successfully uses `WebAssets.cs` to load local assets via `https://marksmith.assets`. The only `https://` links are for `github.com` footer links and youtube embeds.
**Action:** No actions needed as the airgap policy is currently upheld regarding web assets.
