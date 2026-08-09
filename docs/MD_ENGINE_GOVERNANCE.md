# Marksmith Engine Governance

This document is the architectural map **and** the syntax contract for Marksmith's rendering
engine. Autonomous agents modifying the engine **must** read it before touching the markdown
pipeline: it tells you (a) how the two render paths are wired, and (b) which markdown wrappers
mean what, who handles them, and the rules for adding new ones. If a construct you need is not
in the wrapper catalog, extend the catalog here first — never invent an undocumented syntax.

## The Two Pipelines

Marksmith renders the same markdown through **two separate pipelines** that must stay in sync:

### 1. OpenXML / DOCX pipeline (native Word fidelity)

1. **Pre-processing** — `TextNormalizer` (line endings, spacing), `DialectNormalizer` +
   `AdmonitionNormalizer` (Obsidian callouts, Notion/MkDocs/Docusaurus forms), then
   `ShapeMarkdownHtml.PreTransform` (the `:::shapes` MLShape DSL → inline SVG).
2. **Feature detection** — `AdvancedFeaturePipeline` (`Detectors.cs`) scans for the `:::`
   block wrappers (smartart, workflow, tabs, chart, columns, timeline, canvas, shapes). Each
   valid block is replaced with a `<!-- MARKSMITH_FEATURE:<id> -->` marker so the dispatch
   stage knows what to emit.
3. **Markdig AST** — `DocxExportService` builds its `MarkdownPipeline` (`.UseAdvancedExtensions()`,
   math, alerts, footnotes, tables, task lists) and dispatches blocks: headings, code, math,
   tables, alerts → native `w:p` / `w:r` / `w:drawing`.
4. **SmartArt** — a `:::smartart` block renders **native Word SmartArt** (`RenderNativeSmartArt`
   via embedded `.glox` layouts) or the styled fallback. `RenderHtmlBlock` handles the feature
   markers (line 2333+).
5. **Output** — packaged with styles/numbering; `.dotx` templates keep template content type.

### 2. HTML preview pipeline (live WebView2 preview, split view, Looking Glass)

1. **Normalize** — `NormalizeForRender` (normalizers above, applied the same way), then
   `ShapeMarkdownHtml.PreTransform`.
2. **Markdig → HTML** — `Markdown.ToHtml` with `.UseAdvancedExtensions().UseMathematics()`:
   `$..$` / `$$..$$` / `\(..\)` / `\[..\]` become `span.math` / `div.math` for KaTeX.
3. **Sanitize** — `HtmlSanitizer.Apply` is a **targeted filter** (script/iframe/object/on* /
   javascript: removed). **HTML comments are preserved** — they are the safe placeholder
   vehicle for post-processing.
4. **Post-inject (trusted markup only)** — after sanitize, the service injects *generated*
   markup: mermaid fences → `<div class="mermaid">`, `:::smartart` blocks (lifted to
   `<!-- SMARTART:n -->` placeholders **before** Markdig, then swapped for the generated SVG
   **after** sanitize), KaTeX, the lens/portal scripts, plugin SVGs, and the fit-to-width zoom.
   **Rule: user markup must never be re-injected after the sanitize step** — only our own
   generated SVG/div strings.

The DOCX path and the preview path must accept the **same wrappers**. Adding a syntax to one
without the other is a bug.

## Markdown Syntax & Wrapper Conventions (the contract)

| Wrapper | Meaning | Handled by |
|---|---|---|
| `` ```lang `` / `` ``` `` | Fenced code. `language-mermaid` → preview diagram div; `plantuml` / `d2` / `sequence` → diagram engines; `highlight.js` in preview. No language = bare fence. | Markdig code blocks; `MarkdownHtmlService` (fence → div, line ~170); plugin engines; `InsertSnippetBuilder.Fenced` |
| `:::smartart type="hierarchy\|process\|cycle\|…"` + nested `- ` bullets + `:::` | Native Word SmartArt / preview SVG diagram. Indentation = hierarchy depth. | `Detectors.cs:172`, `DocxExportService.RenderNativeSmartArt`, `MarkdownHtmlService` (preview SVG), `SmartArtDesignStudioViewModel.InsertIntoDocument`, `InsertSnippetBuilder.SmartArt` |
| `:::workflow` … `:::` | Mermaid-based workflow diagram | `Detectors.cs:281`, AdvancedFeaturePipeline |
| `:::tabs` + `=== "Tab label"` … `:::` | Tabbed content (Word-ribbon styled in export; ARIA tabs in preview) | `Detectors.cs:353`, `DialectNormalizer.cs:62`, task 21 |
| `:::chart type="bar\|…"` + `label,value` lines + `:::` | Native chart | `Detectors.cs:131`, `InsertSnippetBuilder.Chart` |
| `:::columns count="2-4"` + `===` separators + `:::` | Multi-column section | `Detectors.cs:480`, `InsertSnippetBuilder.Columns` |
| `:::timeline` + `year: label` lines + `:::` | Timeline diagram | `Detectors.cs:317`, `InsertSnippetBuilder.Timeline` |
| `:::canvas` … `:::` | Drawing canvas | `Detectors.cs:451` |
| `:::shapes …` | MLShape DSL (line items → DrawingML `wps` shapes) | `ShapeMarkdownCodec`, `ShapeMarkdownHtml.PreTransform` |
| `$…$` / `$$…$$` / `\(…\)` / `\[…\]` | Inline / display math → KaTeX (`div.math`/`span.math`); mhchem for `\ce{}` | `Markdig .UseMathematics()`, KaTeX in preview |
| `> [!NOTE]` / `> [!WARNING]` … | Obsidian-style callouts (+ Notion/MkDocs/Docusaurus dialect forms) | `AdmonitionNormalizer`, `DialectNormalizer` |
| `<!-- MARKSMITH_FEATURE:<id> -->` | **Internal** marker emitted by the feature pipeline (not user syntax) | `AdvancedFeaturePipeline`, `DocxExportService.RenderHtmlBlock` |
| `<!-- SMARTART:n -->` | **Internal** preview placeholder (pre-Markdig lift, post-sanitize swap) | `MarkdownHtmlService` |
| `{{token}}` / `$$"""…"""$` | **Not markdown.** C# raw-interpolated-string templating inside `MarkdownHtmlService` (the `{{…}}` are C# interpolation holes that produce the literal `{token}` then get substituted) | — |
| `- [ ]` / `- [x]` | Task lists | Markdig `TaskLists` |
| `[^note]` | Footnotes | Markdig `Footnotes` |

The `:::` container family shares one convention: an opening `:::kind …` line (with attributes
on the same line), body lines, and a bare `:::` closer. A block must be preceded by a blank
line (or document start) to be treated as a block. The `InsertSnippetBuilder` is the
single source of truth for the *emitted* shapes of every snippet the UI inserts — keep it in
sync when changing a syntax.

## Architectural Guidelines for Agents

- **Do NOT modify Markdig directly.** Marksmith relies on Markdig's AST for both pipelines. A
  construct Markdig doesn't support belongs in a **pre-process stage** (regex/normalizer before
  Markdig) or an **intercept in the dispatch/post-inject stage** — never by forking Markdig.
- **Both pipelines, always.** Any new or changed wrapper must land in: the pre-process/normalizer,
  the `Detectors` feature scan (if block-level), the DOCX dispatch (`DocxExportService`), the
  preview post-inject (`MarkdownHtmlService`), and the `InsertSnippetBuilder` snippet. Missing
  one = the preview and the exported DOCX disagree.
- **Native OpenXML fidelity.** Output should be native `w:p` / `w:r` / `w:drawing` wherever
  possible; rasterized images are a last-resort fallback. SmartArt must stay native (`.glox`
  layouts), never a screenshot.
- **Preview injection is trusted-markup-only.** Everything injected after `HtmlSanitizer.Apply`
  must be generated by our code (SVG builders, known div structures). Never splice user markdown
  or user HTML into the post-sanitize output. Use `<!-- … -->` comment placeholders (comments
  survive the targeted sanitizer) or unlikely text tokens for pre/post swaps.
- **Ambiguity detection.** If a construct has multiple valid visual representations, do not
  guess — flag it via `AmbiguityDetector` and resolve through `AmbiguityResolverDialog`,
  honoring `AppSettings.AmbiguityPreferences`.
- **Wrappers are a contract.** Before adding a syntax, check the catalog above. If you extend it,
  update this document in the same change — the wrapper table is the shared vocabulary between
  the engine, the insert UI, and the preview.

## Where to look

- `MarkSmith.Core/Services/MarkdownHtmlService.cs` — the HTML preview pipeline (normalize,
  sanitize, post-inject, KaTeX/lens/portal/fit-width).
- `MarkSmith.Core/Services/DocxExportService.cs` — the OpenXML pipeline + native SmartArt.
- `MarkSmith.Core/AdvancedFeatures/Detectors.cs` + `AdvancedFeaturePipeline.cs` — `:::` block
  detection and feature markers.
- `MarkSmith.Core/Services/DialectNormalizer.cs` / `AdmonitionNormalizer` — callouts/dialects.
- `MarkSmith.Core/Services/InsertSnippetBuilder.cs` — canonical snippet shapes for the UI.
- `MarkSmith.Core/Composer/ShapeMarkdownCodec.cs` + `ShapeMarkdownHtml.cs` — the `:::shapes` DSL.
