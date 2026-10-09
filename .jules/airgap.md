## 2024-05-24 - [Initial Audit]
**Learning:** Evaluated WebAssets loading mechanisms. MarkSmith sets up local virtual hosts for web assets to prevent remote fetches, utilizing WebView2's mapping capability in `MapAssetHost`. The HTML injection in `MarkdownHtmlService.cs` correctly references `Services.WebAssets` constants which use the virtual host scheme (`https://marksmith.assets/`). Evaluated CSS and JS loads in `MarkdownHtmlService.cs` which appear correct without external script injections.
**Action:** The codebase already embeds assets properly. Verify if there are any remaining external CDN calls missed by simple grep.
**2024-05-24 - [Action Complete]**
**Learning:** Found no instance of `cdn.jsdelivr.net`, `cdnjs.cloudflare.com` or other CDNs being injected or fetched directly for web assets. All external dependencies (Mermaid, KaTeX, highlight.js, mhchem, liquid_fill, etc) are correctly bundled within `Assets/web/` directory and referenced offline via `Services.WebAssets`.
**Action:** The codebase passes the Airgap audit. The mission is verified without changes needed.
**2024-05-24 - [Verify MapAssetHost Comment]**
**Learning:** `MainWindow.xaml.cs` comment in `MapAssetHost` says `/* mapping already set or unavailable — CDN fallback in the HTML still works */`. I verified that there is no *actual* CDN fallback written into `MarkdownHtmlService.cs`. The code directly references `{{Services.WebAssets.Mermaid}}` which evaluates to the local asset path, which won't load if the mapping fails, but crucially, it won't trigger an external network call either.
**Action:** The codebase passes this check.
**2024-05-24 - [Telemetry Audit]**
**Learning:** Found no telemetry, tracking, analytics, mixpanel, amplitude, or sentry SDKs. The word "beacon" appears exclusively for a UI feature (the "radar beacon" which flashes a red circle over a markdown issue). Segment is only used for UI path geometry (LineSegment/BezierSegment) or substring operations. There are no tracking scripts injected into HTML or C# codebase.
**Action:** The codebase passes the anti-telemetry airgap check.
**2024-05-24 - [Build Check]**
**Learning:** `dotnet build` of WinUI desktop app on Linux environment fails on `XamlCompiler.exe: Exec format error` because it's trying to execute a Windows binary (`.exe`) during the build process on a Linux container. The core library (`MarkSmith.Core`) compiles successfully. Since I am auditing the web assets loaded by `MarkSmith.Core` and no changes were required, the integrity of the project hasn't been impacted.
**Action:** Proceed despite the WinUI XAML compiler error, as it's an environment limitation (Linux trying to build a Windows-specific UI project).
**2024-05-24 - [Pre-Commit Steps]**
**Learning:** Evaluated tests and ran them. `SkiaSharp.SKObject` throws `DllNotFoundException` due to `libSkiaSharp` missing on the headless Linux test host. This is a known issue when testing cross-platform drawing libraries on a headless container without the necessary native binaries (e.g. `libfontconfig`). The tests passed right up to the crash point except one `Base64DataUriImage_EmbedsAsRealDrawingInDocx` which fails likely due to the same missing underlying rendering deps on linux.
**Action:** Complete the pre-commit steps. As no code changes were made, these failures are pre-existing environmental issues on this platform and can be safely ignored for the purpose of the Airgap audit PR.
