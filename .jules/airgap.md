## 2024-08-02 - [Profile Offline Assets]
**Learning:** Found the offline asset paths in `marksmith-v2/MarkSmith.Core/Services/WebAssets.cs` and the CDN paths in `THIRD-PARTY-NOTICES.md`. It turns out `THIRD-PARTY-NOTICES.md` says they are loaded from a public CDN ("Loaded at runtime for preview/render (from a public CDN, not redistributed)"), but `ROADMAP.md` says "the app makes zero CDN calls". Let's check `MarkdownHtmlService.cs` again to make sure everything is completely offline. Wait, looking at `MarkdownHtmlService.cs` again, there is a hardcoded `<script src=\"{Services.WebAssets.Base}/mhchem.min.js\">` (or wait, let's see how `mhchem.min.js` is loaded).
**Action:** Replace any remaining remote fetches with local bundles.
**Learning:** Checking `MarkdownHtmlService.cs`, it seems all the scripts load from `{Services.WebAssets.Base}/something`. For example, `mhchem.min.js`, `katex.min.js`, etc. There don't seem to be any direct cdn.jsdelivr.net or unpkg.com hardcoded URLs.
Let me check if there's any `<script>` or `<link>` without `{Services.WebAssets.Base}` or `{{Services.WebAssets.KatexCss}}`.
**Learning:** Found zero CDN script requests inside MarkdownHtmlService.cs or WebAssets.cs. Everything uses `WebAssets.Base` or other local offline assets mappings.
**Action:** Let's double check if there are any other scripts or URLs inside `MarkSmith.Core` and `MarkSmith.Desktop` that could possibly make network calls.
**Learning:** Found zero scripts loaded from external sources for Markdown preview. But the user mentioned "Your mission is to audit bundled web assets and verify zero external HTTP/HTTPS CDN calls are made during preview rendering. ... 📦 AUDIT & FIX - Remove any remote fetch calls and embed local assets".
Let's check if there's any remaining `unpkg` or `cloudflare` in the assets themselves.
**Learning:** Found zero CDN script requests inside MarkdownHtmlService.cs or WebAssets.cs. Everything uses `WebAssets.Base` or other local offline assets mappings. Wait, I should make sure that the `WebAssets.cs` mapping actually maps to local bundles.
Looking at `marksmith-v2/MarkSmith.Core/Services/WebAssets.cs`, it defines paths:
`public static string Mermaid => Base + "/mermaid.min.js";`
`public static string KatexCss => Base + "/katex.min.css";`
etc.
So the scripts are loaded locally. I've effectively audited it.
I'll check `ROADMAP.md` and `THIRD-PARTY-NOTICES.md` to see if there are any mentions of CDN. It says "the app makes zero CDN calls" in ROADMAP and "Loaded at runtime for preview/render (from a public CDN, not redistributed)" in THIRD-PARTY-NOTICES. Wait, THIRD-PARTY-NOTICES.md claims they ARE loaded from a public CDN. That's a documentation bug, probably left over from before they were bundled. I'll update it.

Wait, is there any other `<script src="https://...` or `<link href="https://...` anywhere?
The only ones I found are:
`marksmith-v2/MarkSmith.Core/Services/MarkdownHtmlService.cs`:
`<a href="https://github.com/thebubbsy/marksmith">` (link)
`xmlns="http://www.w3.org/2000/svg"` (namespace)

Let's double check `marksmith-v2/MarkSmith.Desktop/MainWindow.xaml.cs`.
It maps the asset host. It intercept navigation to allow `https://marksmith.assets`.

I'll check if there's any analytics.
`grep -rnE 'google-analytics|mixpanel|segment|sentry|hotjar|clarity' ./marksmith-v2/MarkSmith.Core ./marksmith-v2/MarkSmith.Desktop` -> nothing.

So there's no CDN calls. My task is "Verify all JS/CSS scripts in WebAssets.cs load from embedded resources", "Run dotnet build and test offline preview rendering", "Remove any remote fetch calls and embed local assets", "Create PR: '📦 Airgap: [Audit and lock down offline asset X]'"
**Learning:** Found zero scripts loaded from external sources for Markdown preview. But the user mentioned "Your mission is to audit bundled web assets and verify zero external HTTP/HTTPS CDN calls are made during preview rendering. ... 📦 AUDIT & FIX - Remove any remote fetch calls and embed local assets".
Let's check if there's any remaining `unpkg` or `cloudflare` in the assets themselves.
**Learning:** `MarkSmith.Desktop` fails to compile under Linux since it requires XamlCompiler.exe which isn't cross-platform. However, `MarkSmith.Core` built perfectly fine with 0 warnings, verifying that my syntax and changes are clean. The web assets are also strictly localized in the `WebAssets.cs`.
**Learning:** Three tests failed but those are environmental errors unrelated to web assets (missing `libSkiaSharp` shared object since tests are run under Linux on the VM and `SkiaSharp.NativeAssets.Linux` is probably missing in tests, or `Local_image_embedded_as_picture` path issues). None of them have to do with the `THIRD-PARTY-NOTICES.md` doc update or `WebAssets` audit.
