# Marksmith Rendering Engine — Governance Reference

> **MANDATORY READING** for any agent or developer modifying `DocxExportService.cs`, `LatexToOmml.cs`,
> `MermaidDocxRenderer.cs`, or any preprocessor in `MdToPdf.Core/Services/`.
>
> This document is the single source of truth for how Markdown becomes a Word document.
> If you add a new feature, you MUST update this document.

---

## 1. Pipeline Stages (Execution Order)

Every markdown string passes through these stages IN ORDER before becoming OpenXML:

| Stage | File | Purpose |
|-------|------|---------|
| 1. Line Normalization | `TextNormalizer.cs` | Convert CR/CRLF → LF |
| 2. Admonition Normalization | `AdmonitionNormalizer.cs` | Rewrite `:::` fences, Obsidian `> [!tip]-` callouts → GitHub alerts or `<details>` |
| 3. Dialect Normalization | `DialectNormalizer.cs` | Wiki-links `[[...]]`, `#tags`, code block titles, MkDocs tab syntax, page breaks `\pagebreak`, fix glued table lines |
| 4. Dash Replacement | `DashReplacer.cs` | Em-dashes `—` → hyphens (outside code fences only) |
| 5. Emoji Stripping | `EmojiStripper.cs` | Remove emoji/ZWJ sequences (NoEmoji mode only) |
| 6. Diagram Sniffing | `DiagramFenceSniffer.cs` | Infer diagram language from bare fenced code blocks |
| 7. Markdig Parse | `DocxExportService.cs` L55-71 | Parse normalized markdown into AST using configured pipeline |
| 8. Block Dispatch | `DocxExportService.cs` `RenderBlock()` | Walk AST → OpenXML elements |
| 9. Inline Dispatch | `DocxExportService.cs` `RenderInlines()` | Walk inline AST → Word runs |
| 10. Package Assembly | `DocxExportService.cs` | Assemble `document.xml`, `styles.xml`, `numbering.xml`, `footnotes.xml`, media parts |

> **RULE**: If you add a new preprocessor, it MUST execute between stages 1-6. Never modify the raw
> string after Markdig has parsed it.

---

## 2. Markdig Pipeline Extensions

The `MarkdownPipelineBuilder` (lines 55-71) enables:

| Extension | What It Adds to the AST |
|-----------|------------------------|
| `UseAdvancedExtensions()` | Pipe tables, grid tables, task lists, auto-links, footnotes, definition lists, abbreviations, custom containers, figures, emoji, SmartyPants, generic attributes |
| `UseYamlFrontMatter()` | `YamlFrontMatterBlock` nodes |
| `UseAlertBlocks()` | `AlertBlock` nodes for `> [!NOTE]` etc. |
| `UseMathematics()` | `MathBlock` and `MathInline` nodes for `$$...$$` and `$...$` |
| `UseEmojiAndSmiley(false)` | `EmojiInline` nodes for `:rocket:` shortcodes (smileys disabled) |

> **RULE**: If a Markdig extension isn't enabled here, its AST nodes won't appear in the tree and your
> `case` branch in `RenderBlock` will NEVER fire. Check this FIRST when debugging "why doesn't X render?"

---

## 3. Block-Level Dispatch Table

The `RenderBlock` method (line ~382) is a `switch` on the Markdig `Block` type:

| AST Node Type | Line | OpenXML Output | Recursion |
|---------------|------|----------------|-----------|
| `HeadingBlock` | 386 | `W.Paragraph` with `Heading1`–`Heading6` style + bookmark | Inlines via `RenderInlines` |
| `YamlFrontMatterBlock` | 399 | Skipped (metadata only) | None |
| `MathBlock` | 401 | Centered `W.Paragraph` + OMML via `LatexToOmml.Build` | None |
| `FencedCodeBlock` (mermaid) | 420 | ShapeForge shapes → PNG snapshot → code fallback | None |
| `FencedCodeBlock` (plugin) | 470 | Plugin shapes → SVG/PNG → code fallback | None |
| `CodeBlock` | 500 | Shaded `W.Paragraph` with Consolas + diff coloring | None |
| `AlertBlock` | 600 | Single-cell `W.Table` with accent border + icon title | Recurses inner blocks into the cell |
| `QuoteBlock` | 609 | Renders children, THEN applies `ApplyQuoteFormatting` | Recurses children via `RenderBlock` |
| `ListBlock` | 620 | `RenderList` → numbered/bulleted paragraphs | Recurses list items |
| `MdTable` | 624 | `W.Table` with headers, banding, alignment | Recurses cells |
| `ThematicBreakBlock` | 628 | `W.Paragraph` with wavy bottom border | None |
| `HtmlBlock` | 636 | Defers to `RenderHtmlBlock` | See HTML dispatch |
| `DefinitionList` | 644 | Recurses child items | Recurses items |
| `DefinitionItem` | 649 | First child = `DefinitionTerm` style, rest = `Definition` + hanging indent | Recurses children |
| `ParagraphBlock` | 680 | `W.Paragraph` (intercepts `\tableofcontents` / `[TOC]`) | Inlines via `RenderInlines` |
| `Footnote` | 700 | `FootnotesPart` + `W.Footnote` + inline `W.FootnoteReference` | Recurses footnote body |
| `ContainerBlock` | 720 | Recurses children | Recurses |
| `LeafBlock` | 725 | `W.Paragraph` + inlines | Inlines via `RenderInlines` |

---

## 4. Inline-Level Dispatch Table

The `RenderInlines` method (line ~2038) is a `foreach` + `switch` on `Inline` type:

| AST Node Type | Line | OpenXML Output | Notes |
|---------------|------|----------------|-------|
| `EmojiInline` | 2045 | `W.Text` (skipped in NoEmoji) | Uses `Segoe UI Emoji` font |
| `LiteralInline` | 2055 | `W.Text` with current `Fmt` | Main text emitter |
| `EmphasisInline` | 2063 | Recurses with toggled `Fmt` | `*`→italic, `**`→bold, `~~`→strike, `~`→sub, `^`→sup, `==`→highlight, `++`→underline |
| `CodeInline` | 2070 | `W.Text` with `Code=true` | Consolas font. Supports `~~strike~~` inside code |
| `LineBreakInline` | 2090 | `W.Break` (hard) or space (soft) | |
| `LinkInline` | 2100 | `RenderLink` → bookmark/image/hyperlink | Images embedded via `TryEmbedLocalImage` |
| `AutolinkInline` | 2110 | `W.Hyperlink` | Falls back to plain text if invalid |
| `TaskList` | 2120 | `w14:checkbox` content control | Uses `MS Gothic` font for glyphs |
| `MathInline` | 2130 | OMML equation via `LatexToOmml.Build` | Editable in Word |
| `FootnoteLink` | 2135 | Superscript `W.FootnoteReference` | |
| `HtmlEntityInline` | 2146 | Decoded text | `&amp;` → `&` etc. |
| `HtmlInline` | 2150 | `ApplyHtmlInlineTag` → format stack mutation | See state machine below |
| `ContainerInline` | 2157 | Recurses | |

---

## 5. HTML Block Dispatch Table

The `RenderHtmlBlock` method (line ~1695) uses regex pattern matching:

| Pattern | Line | Output | Priority |
|---------|------|--------|----------|
| `<!-- MARKSMITH_FEATURE:... -->` | 1700 | `RenderAdvancedFeature` (Columns, Tabs, AI, Chart, Canvas, Datagrid) | 1st |
| `class="page-break"` or `page-break-after` div | 1714 | `W.Break` type Page | 2nd |
| `div.code-title` or `div.tab-label` | 1722 | Bold styled one-liner paragraph | 3rd |
| `<hr>` anywhere in block | 1737 | Split segments + wavy borders between | 4th |
| `<table>...</table>` | 1752 | `RenderHtmlTable` with colspan/rowspan | 5th |
| `<iframe>`, `<video>`, `<svg>` | 1755 | Italicized hyperlink placeholder | 6th |
| `<details><summary>` (split opener) | 1782 | Collapsible heading (outline level 4, `w15:collapsed`) | 7th |
| `</details>` (split closer) | 1792 | Dropped/ignored | 8th |
| `<details>...(complete)...</details>` | 1801 | Collapsible heading + re-parse body through Markdig | 9th |
| Everything else | 1820 | `StripHtmlToText` → plain text paragraph | Catch-all |

> **RULE**: Pattern matching is ORDER-DEPENDENT. The first match wins and returns. If you add a new
> HTML pattern, place it ABOVE the catch-all but consider where it sits relative to existing patterns.

---

## 6. Inline HTML Format State Machine

`ApplyHtmlInlineTag` (line ~2181) uses a stack-based state machine:

| Opening Tag | Fmt Field Toggled | Closing Pops Stack |
|-------------|-------------------|--------------------|
| `<b>`, `<strong>` | `Bold = true` | Yes |
| `<i>`, `<em>`, `<cite>`, `<var>`, `<dfn>` | `Italic = true` | Yes |
| `<u>`, `<ins>` | `Underline = true` | Yes |
| `<del>`, `<s>`, `<strike>` | `Strike = true` | Yes |
| `<kbd>`, `<code>`, `<samp>`, `<tt>` | `Code = true` | Yes |
| `<mark>` | `Highlight = true` | Yes |
| `<sub>` | `Subscript = true` | Yes |
| `<sup>` | `Superscript = true` | Yes |
| `<span>`, `<font>` | `Color`, `Background`, `FontWeight` extracted from `style=` | Yes |
| `<br>` | Emits `W.Break` immediately | No (void tag) |
| Unknown tags | No format change | Yes (keeps stack balanced) |

> **RULE**: The stack MUST remain balanced. Every opening tag pushes the previous state. Every closing
> tag pops. If you add a new tag, follow this pattern exactly or you will corrupt formatting for
> all subsequent inline content.

---

## 7. Helper Methods Quick Reference

| Method | Line | Purpose |
|--------|------|---------|
| `Hex()` | 350 | CSS hex → Word hex (strip `#`) |
| `FirstHeadingText()` | 351 | Extract document title from first heading |
| `CollectAnchors()` | 363 | Build bookmark name lookup for heading IDs |
| `TryRenderDropCap()` | 738 | First letter → framed drop cap (min 60 chars) |
| `AppendCoverPage()` | 774 | Title page with date and optional logo |
| `JpegDimensions()` | 838 | Parse JPEG headers for width/height |
| `PngDimensions()` | 980 | Parse PNG headers for width/height |
| `CodeParagraph()` | 1001 | Shaded code block with diff line coloring |
| `ApplyQuoteFormatting()` | 1076 | Blockquote border + shading + indent |
| `RenderAdvancedFeature()` | 1094 | Dispatch for `:::columns`, `:::tabs`, `:::ai-context`, etc. |
| `TryEmbedLocalImage()` | 1606 | Read, resize, embed image via SkiaSharp |
| `StripHtmlToText()` | 1683 | Remove all HTML tags (sanitize script/style) |
| `RenderHtmlTable()` | 1825 | Raw HTML `<table>` → `W.Table` with span support |
| `RenderTable()` | 1925 | Markdig pipe table → `W.Table` with banding |
| `AppendTocField()` | 2021 | Emit auto-updating TOC field code |
| `ApplyHtmlInlineTag()` | 2181 | Stack-based inline format toggle |
| `ExtractHtmlColor()` | 2234 | Parse CSS color from style attribute |
| `ExtractHtmlFontWeight()` | 2249 | Parse CSS font-weight from style attribute |
| `ApplyEmphasis()` | 2256 | Map Markdig emphasis delimiters → Fmt toggles |
| `RenderLink()` | 2266 | Route bookmark / image / external hyperlink |
| `AddText()` | 2350 | Emit `W.Run` with emoji font isolation |
| `BuildRunProperties()` | 2371 | Convert `Fmt` struct → `W.RunProperties` |
| `AddStyles()` | 2411 | Define global Word stylesheet |
| `AddSettings()` | 2492 | Document view mode, zoom, hyphenation |
| `BuildSectionProperties()` | 2510 | Page geometry, headers, footers, borders |
| `AddNumbering()` | 3040 | Inject Word numbering definitions |

---

## 8. Known Ambiguities & Edge Cases

These are places where the same input can produce different outputs depending on context or settings:

| Ambiguity | Current Behavior | Risk |
|-----------|-----------------|------|
| **Mermaid rendering mode** | ShapeForge (native shapes) → PNG snapshot → code fallback. Mode depends on settings + parse success | User may not know why output changed |
| **Web Layout coercion** | Oversized ShapeForge diagrams force the ENTIRE document into Web Layout view | Changes reading experience for everything |
| **Drop cap skip** | Silently skipped if first paragraph < 60 chars or doesn't start with a letter | No user feedback |
| **`<details>` split vs complete** | Split opener creates collapsible heading that "captures" subsequent blocks until `</details>` | Blank lines between summary and body change behavior |
| **`<hr>` + trailing text** | Markdig swallows text after `<hr>` into the same HTML block | `RenderHtmlBlock` splits on `<hr>` to recover |
| **Definition list syntax** | Requires term alone on line, `: ` prefix on next line. Easy to confuse with blockquote `:` | Preprocessors may interfere |
| **Grid table bottom border** | `+---+---+` line may leak as plain text if parser doesn't fully consume it | Visual artifact in document |
| **Inline code + strikethrough** | `~~text~~` inside backtick code: currently processed for strike | Violates CommonMark spec (code = literal) |
| **Footnote numbering** | Auto-numbered in document order. Reordering paragraphs breaks references | No cross-reference validation |
| **Task checkbox font** | Requires `MS Gothic` font. Missing font = broken glyphs | Platform-dependent |

---

## 9. Rules for Adding New Features

1. **Update this document** before writing code. Add your new AST node to the dispatch table.
2. **Check the Markdig pipeline** — if your feature needs a Markdig extension, add it to BOTH `Pipeline` and `PipelineNoEmoji`.
3. **Respect the preprocessor order** — normalizers run BEFORE Markdig parsing. Don't transform syntax that a later normalizer also transforms.
4. **Test nesting** — your feature may appear inside a blockquote, table cell, list item, alert, or `<details>`. Test all five.
5. **Test with ALL themes** — dark themes invert colors. Your hardcoded hex values will be invisible on Dracula.
6. **Update `RENDERING_ENGINE_DIAGRAM.md`** with any new nodes or branches.
7. **Run the 5-file Markdown Torture Test gauntlet** and verify the extracted `document.xml`.

8. Versioning and Compatibility
The rendering engine strictly adheres to Semantic Versioning (SemVer).

Backward Compatibility: All minor and patch releases must preserve existing AST parsing behavior and OpenXML/PDF outputs. Documents generated on v1.0.0 must look identical when generated on v1.x.x.
Breaking Changes: Any change that modifies the underlying OpenXML schema hierarchy, alters default theme colors, or deprecates a public API will trigger a major version bump. Breaking changes will be announced in the CHANGELOG.md with migration paths.
Feature Detection: Downstream consumers should inspect the engine's capability flags (e.g., EngineCapabilities.SupportsShapeForge) rather than relying on version strings to determine feature availability.

9. Security and Sanitization
Because the engine may process untrusted, user-provided Markdown, strict sanitization guardrails are enforced at the AST level before rendering:

HTML/Script Stripping: All <script>, <style>, <link>, and <iframe> tags are aggressively stripped. Inline event handlers (e.g., onclick) and javascript: URIs are neutralized.
Remote Assets: The engine operates offline-first. Remote image loading is blocked by default and requires explicit opt-in via AppSettings.AllowRemoteAssets.
SVG Safety: Embedded SVGs undergo XML sanitization to prevent XXE (XML External Entity) attacks and strip embedded scripts before being passed to ShapeForge or the HTML preview.

10. Test Requirements for New Features
New features and bug fixes must not degrade the engine's reliability.

The Gauntlet: All pull requests must successfully pass the "Markdown Torture Test gauntlet"—a rigorous suite of edge-case Markdown files. A passing test means zero exceptions thrown, and the output OpenXML validates cleanly against the Microsoft Productivity Tool schema.
Feature Coverage: Any new Markdown extension or rendering feature must be accompanied by unit tests covering the standard use case, malformed inputs, and edge cases.
Regression Tracking: Visual and structural regressions are tracked by comparing the generated document.xml output against known-good baseline snapshots during CI runs.

11. Performance Budgets & Constraints
To prevent architectural bloat and ensure fast exports, the engine enforces strict performance budgets:

AST Size Limits: The engine must parse and render a 10,000-line Markdown file in under 500ms.
Memory Constraints: Diagram rendering (especially ShapeForge) must not exceed 50MB of allocated memory per complex diagram to prevent spikes during bulk exports.
OpenXML Bloat: Part count must be minimized. Shared styles and themes must be referenced globally rather than duplicated per run or paragraph. Images must be deduplicated in the media/ directory if the same source URL/path is used multiple times.

12. Theme Abstraction Layer
Developers must never hardcode hex values in the export engine (e.g., Fill = "FFFFFF"). Doing so breaks dark themes like Dracula and Cyberpunk.

The Theme API: All color requests must route through the DocxExportContext.Theme (or HTML equivalent).
Palette Usage:
Use Theme.Background for page and container fills.
Use Theme.Text for primary typography.
Use Theme.Primary or Theme.Heading for accents and titles.
Use Theme.Secondary for subtle contrast areas (e.g., table headers, blockquotes).
Use Theme.Border for all lines, table grids, and thematic breaks.
Light vs. Strong Influence: Features must respect the ThemeLightInfluence toggle, ensuring that if the user requests a white-gradient page, text luminance is automatically inverted or clamped to ensure readability.

