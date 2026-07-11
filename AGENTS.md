# AGENTS.md — read this before you touch a single line

You are probably an AI agent. So was the author of this file, and so is at least one *other*
agent that may be editing this repo **at the same time as you**. This document exists because
this codebase looks approachable and is actually a minefield of empirically-discovered,
expensively-learned constraints. Every rule below was paid for with a real bug, a real
regression, or a real retraction. Do not relearn them at the user's expense.

---

## 1. What this is, and why it matters

**Marksmith turns pasted AI-chat Markdown into documents good enough to present as your own
work.** Live themed preview → PDF / DOCX / PPTX / EPUB, 100% offline. The one-line pitch on the
product site: *"Prompt. Copy. Paste. Present."*

The moat — the thing people will pay for — is **ShapeForge™**: diagrams (Mermaid, Graphviz,
PlantUML, D2, Vega-Lite) do not become pictures in Word. They become **native, editable Word
shapes** — a pasted 66-node Graphviz network lands as 208 real `wps:wsp` elements you can click,
move, recolor, and retext inside Word. Nothing else on the market does this. Pandoc pastes
bitmaps. Online converters upload your data. Marksmith is offline, native-output, and
fidelity-obsessed. Guard that positioning in every decision.

The second differentiator is **2026 Markdown fluency**: the app reads the dialect-rich Markdown
AI assistants actually emit — Obsidian folded callouts, wiki-links, #tags, MkDocs tabs,
chemistry (`\ce{}`), bare undelimited LaTeX glued into prose by browser copy handlers,
unlabelled ```` ``` ```` fences containing `digraph {…}` — and maps each cue to the professional
Word construct a careful human would have chosen. "Humanized output from subtle hints" is a
product promise, not a nice-to-have.

### Business context (why care about quality this much)

- A freemium skeleton **already exists in code**: `AppServices.License` gates
  `CanAutomate` (clipboard/folder/API hands-free automation is Pro) and `ShowFooter` (free tier
  stamps a "Made with Marksmith" footer on exports). Don't route around these gates; new
  automation features belong behind `CanAutomate`.
- Distribution: GitHub Releases via `.github/workflows/release.yml` (WinUI installer + portable,
  Avalonia portable for win/linux/mac), with winget/Chocolatey checksums prepared in the
  release notes. A browser extension ("Copy as Markdown") feeds the ingest pipeline.
- **One-year picture** the current owners are steering toward: the **WinUI app deepens its lead
  as the flagship** — it is Windows-native, full-featured, and stays that way indefinitely (see
  §7: WinUI is king). The Avalonia build remains a compatibility concession so Linux/macOS users
  aren't turned away empty-handed, nothing more — it is not expected to surpass the flagship and
  is not the platform bet. Meanwhile: a plugin registry grows beyond the five built-in diagram
  engines (the manifest system in `MdToPdf.Core/Plugins/` is designed for third-party
  `plugin.json` drops); Pro pricing lands on automation + ShapeForge-quality exports; the
  extension funnels chat-→-document conversions from every major AI web UI. The realistic buyer
  is the professional — overwhelmingly on Windows, where Word lives — who pastes AI output into
  status reports, design docs, and boardroom material daily and needs it to look hand-made.
  Every fidelity bug undermines the entire value proposition — a document that looks 95% right
  is a document the user has to fix, which is the product failing at its only job.
- See `ROADMAP.md` / `FIVE-YEAR-PLAN.md` for the owners' longer arc. Do not contradict them.

---

## 2. You are not alone in this repo — concurrency protocol

**At least one other AI agent session actively commits to this working tree**, historically on
plugins, the WinUI app, the extension, and fonts/manifest work. This is not hypothetical; mid-turn
build failures have repeatedly turned out to be the other agent's half-saved files.

Hard rules:

1. **`git status` before you stage. Stage by explicit path only. Never `git add .` / `git add -A`.**
   You will sweep the other agent's in-flight work into your commit and entangle two workstreams.
2. **A build error you didn't cause is probably a save race.** Before diagnosing, re-read the
   file's *current* state and simply build again. Two documented cases: a `PluginManager`
   referencing a class whose file hadn't been written yet, and a XAML `Click` handler whose
   code-behind arrived seconds later. Both "fixed themselves" on rebuild.
3. **`HEAD` moves under you.** Check `git log --oneline -3` before committing and confirm your
   staged delta is additive over their latest (e.g. `git diff --numstat` + grep that your diff
   doesn't *delete* their code).
4. **Never revert a file change you didn't make.** System reminders about "modified by user or
   linter" usually mean the other agent. Take the current state as intentional.
5. **Do not kill processes you didn't start.** Multiple `Marksmith.exe` instances run
   simultaneously: the user's installed copy (`C:\Program Files\Marksmith\`), the other agent's
   debug instance, and yours. Filter kills by path
   (`*MdToPdf.Avalonia\bin\Debug*`) and never touch the Program Files instance.

---

## 3. Architecture — the 60-second map

```
MdToPdf.Core/        net8.0   THE ENGINE. Platform-agnostic. Everything important lives here.
MdToPdf/             net8.0-windows  WinUI 3 app (the original, full-featured shipping app)
MdToPdf.Avalonia/    net10.0  Cross-platform build — a Linux/macOS compatibility afterthought, NOT the future
tests/MdToPdf.Core.Tests/  net8.0  xunit accuracy suite — 167 tests. THE gate. Keep it green.
```

- **`AppServices`** (`MdToPdf.Core/AppServices.cs`, namespace `MdToPdf`) is a hand-rolled static
  composition root. Both apps consume it.
- **`IWebRenderHost` / `IUiPrompts`** (`MdToPdf.Core/Rendering/IWebRenderHost.cs`) is the seam
  between the engine and each app's web view. WinUI implements it over WebView2; Avalonia over
  `Avalonia.Controls.WebView`'s `NativeWebView`. Core never references a UI framework.
- **Render pipeline (preview + PDF)**: `MarkdownHtmlService.Render` →
  themed HTML → WebView (`NavigateToStringAsync`) → print (`PrintToPdfAsync`).
- **DOCX pipeline**: `DocxExportService` hand-walks the Markdig AST and emits OOXML. Anything the
  walk doesn't have a `case` for is silently dropped or textified — historically THE main source
  of fidelity bugs. When you add a Markdown capability, add it to BOTH pipelines.
- **ShapeForge stack** (`MdToPdf.Core/Services/Mermaid/`): bespoke per-family mermaid renderers →
  `HarvestedDiagram` (exact geometry harvested from the live WebView) → `GenericDiagram` (SVG
  primitives) → `DocxShapeEmitter` (native shapes, EMU math). **`SvgShapeForge.cs`** parses a
  diagram *plugin's* SVG string into `GenericDiagram` directly — no browser needed.
- **Plugins** (`MdToPdf.Core/Plugins/`): manifest-driven (`plugin.json`) external renderers
  (Graphviz, D2, PlantUML, Typst, Vega-Lite). They shell out and return SVG. The *other agent*
  has been the primary author here — coordinate, don't refactor unilaterally.

### The normalizer chain — ORDER IS LOAD-BEARING

Both `MarkdownHtmlService.Render` and both `DocxExportService` entry points run, in this order:

```
TextNormalizer.Newlines        (bare-CR → LF; WinUI TextBox emits bare CR — skipping this
                                collapses whole documents onto one line)
AdmonitionNormalizer.Apply     (:::note → GitHub alerts; [!TIP]- folded callouts → <details>)
DialectNormalizer.Apply        (wiki-links, #tags, fence titles, MkDocs tabs, page breaks,
                                table-glue tolerance)
DiagramFenceSniffer.Apply      (bare ``` fences with digraph{}/@startuml/vega → labeled fences)
EmojiStripper (if NoEmoji) → DashReplacer → FormattingService
```

If you add a normalizer, add it to **all three call sites** (Render, ExportAsync,
ExportAppendAsync) in the same position, and guard fenced-code regions like the others do.
`LlmSourceService.Normalize` (vendor cleanup + **bare-LaTeX recovery**) is separate: it runs on
ingest/`PrepareMarkdown` when the "Normalize AI formatting quirks" toggle is on — and it must
NOT be gated on vendor detection (that was a real bug: generic text skipped normalization).

---

## 4. The invariants — break these and you regress shipped fixes

Each of these is a scar. The test suite pins most of them; read it (`tests/MdToPdf.Core.Tests`)
as executable documentation.

### Rendering / preview

- **`NavigateToString` silently fails past ~2 MB.** Both WebView hosts. Symptom: WinUI spinner
  forever, Avalonia blank pane. This took the whole preview down once when local images were
  inlined un-downscaled (4.9 MB HTML). Anything that grows preview HTML must respect the budget:
  images are downscaled via SkiaSharp (max 1400px long side) and capped
  (`MaxInlineBudgetBytes`). Never inline unbounded content.
- **`HtmlSanitizer.Apply` runs on the Markdig body BEFORE our own scripts are appended.** Pasted
  content must never execute (`<script>`, `on*=`, `javascript:`, iframes). Our own mermaid/KaTeX
  /lens scripts are trusted and appended after. Do not reorder.
- **Mermaid div content stays HTML-escaped** (mermaid reads `.textContent`). HtmlDecode-ing it
  reintroduces XSS. There's a comment at the site; believe it.
- **Two Markdig pipelines exist on purpose** (`Pipeline` / `PipelineNoEmoji`). Emoji-shortcode
  conversion happens during parse, *after* EmojiStripper already ran, so NoEmoji mode needs the
  extension absent. Add any new Markdig extension to BOTH.
- The **zoom lens** script is shared by `.mermaid` and `.plugin-diagram` and must not live inside
  the mermaid-only script block (Graphviz-only docs need the lens without 2.5 MB of mermaid.js).
- The **focused-diagram viewer** hijacks the preview for "one diagram + a title" docs
  (`AnalyzeDiagramFocus`, prose < 320 chars) and has its own pan/zoom. A missing lens in a tiny
  test doc is THIS, not a regression. Test with >320 chars of prose.

### Markdig quirks (verified behaviors, not guesses)

- Inline `$...$` math parses **only with flanking whitespace**. Wrapping recovered bare LaTeX in
  `$` without inserting surrounding spaces produces literal `$\frac...$` text. See
  `WrapBareLatexMath` — it inserts the spaces deliberately.
- A pipe table with a non-table line **glued directly under the last row is rejected entirely**
  (GitHub tolerates it). `DialectNormalizer` inserts the blank line. Don't remove that.
- An HTML block (type 6) **swallows subsequent lines until a blank line** — `<hr>\ntext` arrives
  as ONE block. `RenderHtmlBlock` splits on `<hr>` for this reason.
- Markdig **percent-encodes link destinations** (`C:\Users\...` → `C:%5CUsers...`). Decode
  before local-path detection; do NOT decode remote URLs on pass-through.
- `:::name` containers are parsed by Markdig's CustomContainers when our TypeMap doesn't claim
  them — unknown admonitions render as plain divs, content preserved. That's correct.

### WebView2 / printing — the retraction story

- Everything WebView2 is **COM STA**: touch `PreviewWebView` only on the UI thread. The API
  server marshals via `Dispatcher.UIThread.Post` + `TaskCompletionSource`
  (`ConvertForApiAsync`). Copy that pattern.
- **PDF page size on the Avalonia host goes through the native WebView2 bridge**
  (`TryGetPlatformHandle()` → `CoreWebView2.CreateFromComICoreWebView2` → `CreatePrintSettings`)
  — real print-API parameters, verified against two distinct page widths by measuring MediaBox.
  The earlier `@page` CSS approach **looked confirmed once and then never reproduced**; the
  claim was publicly retracted in code comments, README, and release notes. Lesson enforced
  here: **never claim "confirmed working" from one measurement.** Verify twice, with different
  inputs, before writing the word "verified".
- `Avalonia.Controls.WebView`'s own `WebViewPrintSettings` has **no page size at all** and its
  Windows margin handling passes **pixels where WebView2 expects inches**. Don't use it for
  print geometry. Linux/macOS still ride the CSS fallback — labeled unverified; keep it labeled.
- Avalonia's WebView **does not paint in desktop screenshots** (GPU compositing) — the pane reads
  black even when fine. Verify preview rendering by generating the identical HTML via a harness
  and inspecting it in a real browser (recipe in §6), not by screenshotting the app.

### Word / OOXML truths

- Task lists = **`w14:checkbox` content controls** (clickable), not glyphs. NoEmoji must not
  change this.
- Folded callouts / `<details>` in Word = **outlineLvl 4 + `w15:collapsed`** (native collapse
  triangle; level 4 keeps it out of the TOC field which collects 1–3).
- SVG-in-Word = PNG blip + **`asvg:svgBlip` extension** (the exact markup Word itself writes).
  GUID ext URIs must be emitted via variables — raw `{...}` in interpolated raw strings breaks.
- EMU math: `1pt = 12700 EMU`; printable window is ~**460pt wide**; `DocxShapeEmitter` scales to
  fit with a **75% floor** (below that labels are unreadable) and flags `oversized` →
  `ctx.ForceWebLayout` → `<w:view w:val="web"/>`. That flag is the answer to "diagram doesn't
  fit the page"; Word may still override the view — that's Word, not us.
- `GEdge.Arrow` **defaults to `true`** because the mermaid harvest never sets it. `SvgShapeForge`
  sets it precisely (endpoint arrowhead polygons or `marker-end`). Flip the default and every
  mermaid connector loses its arrow.
- `SvgShapeForge` **skips `defs`/`marker`/`clipPath`/`symbol`/`style`** subtrees. Walking into
  them turns marker innards into phantom shapes.
- The OpenXml validator reports **2 pre-existing `w14:ligatures` schema warnings** — known noise
  from an upstream template choice, not your bug. Filter it when validating (the tests do).
- Scale checks: the canonical ShapeForge benchmark is the 66-node enterprise network → exactly
  **66 nodes / 76 edges / 66 texts / 208 `wps:wsp`**. If those numbers move, you changed parsing.

### Engine / plugin assumptions

- Plugins return **SVG only** (manifest `render` spec). Typst output is glyph-outline soup —
  `SvgShapeForge.Parse` intentionally returns null for it and the SVG-picture path takes over.
  A page of prose as 500 letter-shaped polygons is worse than a picture; don't "fix" that.
- Plugin availability is machine-dependent. Tests must tolerate both installed and
  not-installed (`plugin-diagram` OR `plugin-diagram-missing`).

---

## 5. Verification discipline — how work gets trusted here

The standing rule this project runs on: **nothing is "done" until it's been observed working,
and nothing is "confirmed" from one observation.**

1. **`dotnet test tests/MdToPdf.Core.Tests` is the gate.** 167 tests, ~1s. Run before AND after
   your change. A new capability without new tests is half-shipped.
2. **DOCX inspection recipe** (don't trust the exporter, read its output):
   ```bash
   unzip -q out.docx -d x && grep -o '<w:sdt>' x/word/document.xml | wc -l
   ```
   plus the OpenXml validator for schema errors (see `DocxExportTests` for the pattern).
   Caveat: bash `grep` can't see emoji/unicode reliably — use PowerShell for those assertions
   (a 🚀-counting grep once reported 0 against a file that contained two).
3. **Preview inspection recipe**: render via a scratch harness with
   `WebAssets.Base = "http://127.0.0.1:<port>/…/Assets/web"`, serve with `npx http-server`,
   open in a browser, assert via DOM queries (`readyState`, `.katex-error` count, image
   `naturalWidth`), not by eyeballing. `file://` URLs are blocked; mixed-content blocks file
   images from http pages — which authentically reproduces the app's own behavior.
4. **PDF geometry**: measure `MediaBox` from the bytes (`strings out.pdf | grep MediaBox`)
   against the expected `px * 72/96`. Two different widths minimum.
5. **File locks**: running Marksmith instances hold `MdToPdf.Core.dll`. Build to an isolated
   `-o` dir when the app must stay up, or kill only your own debug-path instances.
6. **The API is a test surface**: `POST 127.0.0.1:47821/api/convert` (port from user settings)
   exercises ingest→normalize→render→print end-to-end. Beware: the port may be owned by a
   *different* running instance than the build you think you're testing — check
   `Get-Process Marksmith | Select Path` and `netstat -ano` first. This produced a
   wrong-conclusion incident once.

---

## 6. Environment landmines (this specific dev machine)

- NuGet cache lives on **`O:\packages\NuGet\cache`** — a mapped drive that has *disappeared
  mid-session*. Symptom: every restore fails with "Could not find a part of the path 'O:\…'".
  Nothing you can fix; tell the user, wait, retry.
- SDK is a **.NET 11 preview**; targets are net8.0 (Core/tests/WinUI) and net10.0 (Avalonia).
  WinUI builds need `-p:Platform=x64`.
- User settings: `%LOCALAPPDATA%\MdToPdf\settings.json` (the user historically runs
  **NoEmoji=true** and MermaidDocxMode=1/ShapeForge — two past "bugs" were actually these
  settings). Custom themes: `custom-themes.json` beside it. Plugins:
  `%LOCALAPPDATA%\MdToPdf\Plugins\<id>\`.
- WSL exists but its network/sudo state is unreliable; don't build verification plans on it.
- Known pre-existing warnings that are NOT yours: `System.IO.Packaging` NU1903 vulnerability
  advisories, `WindowsBase` MSB3277 conflicts, CA1416 in `MarkdownDiscoveryService`.

---

## 7. Current parity debts and the near-term backlog (don't duplicate, don't silently drop)

- **WinUI is king, and stays king.** Owner's explicit direction: the WinUI app is the flagship
  **indefinitely** — not "until Avalonia catches up." Avalonia exists as an afterthought, a
  compatibility concession for the comparatively tiny Linux/macOS audience, and it is considered
  unlikely to ever surpass the WinUI build. Practical consequences for you:
  - **Always launch, demo, and verify against the WinUI app first.** Avalonia gets checked
    second, as a "does it still build/run" concern, not as the primary target.
  - New UI features land in **WinUI first** (or simultaneously). The theme-editor button is
    currently Avalonia-only — that is a **parity bug against the flagship**, backwards from the
    intended order; port it to WinUI rather than treating Avalonia as the lead.
  - Engine work in `MdToPdf.Core` serves both automatically — that's the right layer for most
    features precisely so WinUI never waits on cross-platform plumbing.
  - **Never delete or de-prioritize WinUI code.** Any earlier note suggesting a future "WinUI
    cleanup once Avalonia reaches parity" is obsolete — disregard it.
- Real Word **footnotes** (`footnotes.xml` part) — currently inline `[n]` superscript.
- **KaTeX copy-duplication dedup** — browser-copied math arrives as
  `[rendered][TeX][rendered]` triplets; the middle now renders, the flanking duplicates remain.
  Genuinely hard heuristic; needs many tests before trusting.
- **Landscape-section rung** between "fits portrait" and "Web Layout" for medium diagrams.
- Serving arbitrary local images through each host's asset server (removes the inline budget
  entirely); both servers are currently locked to one root dir — that lock is a security
  feature, extend carefully.
- macOS/Linux native print bridges exist (`MacNativePrint.cs`, `LinuxNativePrint.cs`) but are
  **unverified on real hardware** — keep them labeled as such until someone actually runs them.

---

## 8. The prime directives, restated

1. **Fidelity is the product.** A feature that outputs 95%-right Word is a bug factory, not a
   feature.
2. **Both pipelines, always.** Preview/PDF and DOCX must agree. The gauntlet docs
   (user-supplied stress documents) exist because they diverged.
3. **Verify empirically, twice, with different inputs.** The repo's history contains one public
   retraction of an overclaimed fix; there should never be a second.
4. **Respect the other agent.** Explicit-path staging, additive diffs, no unilateral refactors
   of their subsystems (plugins, WinUI shell, extension).
5. **Keep the tests green and growing.** 167 today. If you found a bug the suite missed, the
   fix isn't done until the suite would have caught it.
6. **WinUI first, always.** Launch it first, verify against it first, land UI features in it
   first. Avalonia is the Linux/macOS compatibility afterthought — build-check it, don't lead
   with it.
7. **Never push without the user's explicit word.** Commit locally with detailed messages;
   the user says "push" when they mean it.
