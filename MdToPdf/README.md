# Marksmith — WinUI 3 (native Windows companion app)

A from-scratch C#/.NET 8 port of the Python/Textual TUI's core idea — Markdown in, PDF out, via a
Chromium print pipeline — built with idiomatic **WinUI 3** (Windows App SDK) instead of a
console UI: `NavigationView`-adjacent `TabView` shell, Mica backdrop, `CommunityToolkit.Mvvm`
source-generated view model, and `WebView2` as both the live preview surface and the PDF renderer.

## Prerequisites

- **Visual Studio 2022** (17.8+) with the **"Windows application development"** workload, which
  installs the Windows SDK and the WinUI 3 project templates/tooling.
- .NET 8 SDK (already present in most VS 2022 installs; `dotnet --list-sdks` to check).
- Windows 10 version 1809 (build 17763) or later to run the built app.

This project was **written but not built** in the sandbox that authored it — there's no Windows
SDK or Visual Studio available there (`dotnet workload list` shows no workloads, and
`Program Files\Windows Kits` doesn't exist). `dotnet restore` was run to sanity-check the NuGet
package graph: `CommunityToolkit.Mvvm`, `Markdig`, and `Microsoft.Web.WebView2` all resolved and
downloaded cleanly, confirming those package IDs/versions are correct. `Microsoft.WindowsAppSDK`
and `Microsoft.Windows.SDK.BuildTools` also resolved on the NuGet feed (proving the version
numbers exist) but the sandbox's network egress truncated the multi-hundred-MB downloads — that's
an environment limit, not a project error. **Treat your first `dotnet build` (or F5 in Visual
Studio) as the real verification step**, and expect to fix any typos that only a real XAML/C#
compiler pass would catch.

## Build & run

```powershell
cd winui3
dotnet restore
dotnet build MdToPdf.sln
dotnet run --project MdToPdf/MdToPdf.csproj
```

Or open `MdToPdf.sln` in Visual Studio and press F5. The app is **unpackaged**
(`WindowsPackageType=None` in the `.csproj`) — no MSIX identity, no `Package.appxmanifest`, no
Store association needed. That's a deliberate simplification for this MVP; see
[Distribute an unpackaged WinUI 3 app](https://learn.microsoft.com/windows/apps/package-and-deploy/unpackage-winui-app)
if you later want MSIX packaging for Store distribution or better OS integration (jump lists,
notifications, etc.).

## What works end-to-end

- **Convert tab**: pick a `.md` file (or paste Markdown in the **Paste & Preview** tab), pick a
  theme (all 10 from the Python app, same hex values), set page width / A4-lock / single-page
  toggles, hit **Generate PDF**. Uses `CoreWebView2.PrintToPdfAsync` under the hood — see
  `Services/PdfExportService.cs`.
- **Live preview**: the right-hand `WebView2` pane re-renders on every relevant change (file
  picked, pasted text edited, theme switched, page width changed) via `Markdig` → themed HTML,
  mirroring `create_html_content()` from the Python app. GitHub-style `> [!NOTE]` alert blocks
  render via Markdig's `UseAlertBlocks()` extension. Mermaid diagrams render live in the preview
  (WebView2 is a real Chromium engine, so `mermaid.js` just works) — the *stubbed* piece is only
  extracting a diagram to its own image file (see below), not seeing it on screen.
- **Settings persistence** (`%LocalAppData%\MdToPdf\settings.json`) and **recent files**
  (`%LocalAppData%\MdToPdf\recent_files.json`) — same shape/intent as the Python app's
  `~/.md_to_pdf/` files, kept separate so the two apps don't fight over one file.
- **Cancel button** for in-progress exports (best-effort — see the comment in
  `ViewModels/MainViewModel.cs`; `PrintToPdfAsync` has no native cancellation).

## What's deliberately stubbed (`NotImplementedException` + a design-decision comment)

Per the "solid MVP over broad-but-thin" scope: these throw a clear exception (caught and shown in
the status bar, not a crash) with a comment at the top of the file laying out the real trade-off,
rather than a fake/partial implementation.

| File | Decision you need to make |
|---|---|
| `Services/DocxExportService.cs` | Shell out to `pandoc` (matches Python, needs it installed) vs. generate `.docx` natively with `DocumentFormat.OpenXml` (zero external deps, more code to write). |
| `Services/MermaidRenderService.cs` | How to get a single diagram's PNG out of WebView2 — there's no Playwright-style `locator.screenshot()`; options are sizing the WebView2 to the element's bounding box + `CapturePreviewAsync`, or dropping to the raw CDP `Page.captureScreenshot` method via `CallDevToolsProtocolMethodAsync`. |

PNG gallery mode (render one document in all 10 themes) depends on the Mermaid piece landing
first, so it wasn't started.

## Architecture notes

- **No DI container.** Services are plain singletons exposed as static properties on `App`
  (`App.Settings`, `App.Themes`, `App.ViewModel`, etc.) — a deliberate "smallest thing that works"
  choice for an app this size. `Microsoft.Extensions.DependencyInjection` (the pattern most WinUI 3
  sample apps graduate to) is the natural next step once you add more services/pages.
- **One `WebView2`, not one per tab.** It lives at the `MainWindow` level (not inside either
  `Views/ConvertView.xaml` or `Views/EditorView.xaml`), so it survives switching between the
  Convert and Editor tabs instead of being torn down and recreated. `MainWindow.xaml.cs` listens
  to `MainViewModel.PropertyChanged` and re-renders the preview when a relevant property changes.
- **Classic `{Binding}`, not `{x:Bind}`.** `{x:Bind}` is compile-time-checked and generally
  preferred in new WinUI 3 code, but it requires careful ordering (the `DataContext`/binding
  source must exist before the generated `Bindings.Update()` call). Given this was authored without
  a compiler in the loop, classic `{Binding}` (resolved at runtime against `DataContext`) was the
  lower-risk choice. Migrating to `{x:Bind}` for compile-time safety is a reasonable follow-up once
  the project builds cleanly.
