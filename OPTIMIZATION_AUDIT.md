# Marksmith Performance Audit — 23 Findings

## Batch 1: Findings #1–#13

| # | Location | Inefficiency | Feature Risk |
|---|----------|-------------|--------------|
| ~~1~~ | ~~`TextNormalizer` / `DashReplacer` / `DocumentStatsService`~~ | ~~Redundant full-document normalization passes — same markdown re-scanned multiple times by independent services that each walk the whole string~~ ✅ | ~~ZERO~~ |
| ~~2~~ | ~~`AdmonitionNormalizer` / `DialectNormalizer` / `KanbanNormalizer`~~ | ~~Each normalizer does its own `Split('\n')` → process → `Join('\n')` — the document is split into a string array and reassembled 5+ times in sequence~~ ✅ | ~~ZERO~~ |
| ~~3~~ | ~~`DocxExportService` (lines 529–622)~~ | ~~`Console.WriteLine` diagnostic output left in the export hot path — string interpolation + console I/O on every export~~ ✅ | ~~ZERO~~ |
| ~~4~~ | ~~`MermaidHarvestService` polling loop~~ | ~~`File.WriteAllText(Path.GetTempPath(), "msvg.txt")` inside a 130-iteration `Task.Delay(150)` loop — disk I/O every 150ms for a debug artifact nobody reads~~ ✅ | ~~ZERO~~ |
| ~~5~~ | ~~`OpenXmlSyntaxHighlighter.ResolveProfile`~~ | ~~15–20 `new Regex(...)` allocations per call — builds the same compiled regex set from scratch on every code block~~ ✅ | ~~ZERO~~ |
| ~~6~~ | ~~`LlmSourceService`~~ | ~~`Collect(FencedCode()); Collect(InlineCode()); Collect(DollarMath()); Collect(DisplayMathBlock());` executed **twice** — once in `RecoverMatrixEnvironments`, again in `WrapBareLatexMath` — 4 full-document regex scans duplicated~~ ✅ | ~~ZERO~~ |
| ~~7~~ | ~~`AdvancedFeaturePipeline`~~ | ~~Instantiated fresh per-export (`new AdvancedFeaturePipeline()`) instead of reused — constructor wires up 12 detectors each time~~ ✅ | ~~ZERO~~ |
| ~~8~~ | ~~`AdvancedFeaturePipeline`~~ | ~~`SHA256.Create()` called per invocation for stable-ID hashing — disposable crypto object allocated and disposed repeatedly~~ ✅ | ~~ZERO~~ |
| ~~9~~ | ~~`DocxExportService` (lines 120–125)~~ | ~~O(n²) feature-marker insertion: `Substring(0, start) + marker + Substring(end)` in a loop, rebuilding the entire markdown string per feature node~~ ✅ | ~~ZERO~~ |
| ~~10~~ | ~~`MermaidHarvestService` (line 225)~~ | ~~`new JsonSerializerOptions { PropertyNameCaseInsensitive = true }` allocated **inside** the polling loop — identical object recreated every 150ms iteration~~ ✅ | ~~ZERO~~ |
| ~~11~~ | ~~`MermaidHarvestService`~~ | ~~Triple `ExtractFences` parsing — the same markdown is fence-extracted 3 times in sequence for mermaid/plantuml/chart detection instead of once~~ ✅ | ~~ZERO~~ |
| ~~12~~ | ~~`MarkdownHtmlService` / `UpdatePreviewCanvasLiveAsync`~~ | ~~Full 50KB+ page HTML (including all static JS/CSS shell) generated per debounced update, then `IndexOf`/`Substring` extracts just the canvas body and discards the shell~~ ✅ | ~~ZERO~~ |
| ~~13~~ | ~~`MarkdownHtmlService`~~ | ~~Duplicate normalization pipeline — `Render()` re-runs the same normalizer chain that `PrepareMarkdown` already applied upstream~~ ✅ | ~~VERY LOW~~ |

## Batch 2: Findings #14–#23

| # | Location | Inefficiency | Feature Risk |
|---|----------|-------------|--------------|
| ~~14~~ | ~~`ThemeCatalog.All` / `GetOrDefault`~~ | ~~`Builtin.Concat(CustomThemeStore.All).ToList()` allocates two new lists on **every** call — and `GetOrDefault` calls `All` twice (FirstOrDefault + fallback `[0]`)~~ ✅ | ~~ZERO~~ |
| ~~15~~ | ~~`DocxExportService` / `PptxExportService` / `EpubExportService`~~ | ~~Each declares its own `private static ThemeCatalog` instead of using the `AppServices.Themes` singleton — 3 extra instances, inconsistent custom-theme state~~ ✅ | ~~LOW~~ |
| ~~16~~ | ~~`PptxExportService.BuildSlides`~~ | ~~3 `new Regex(...)` per slide line — heading/bullet/code patterns rebuilt for every line of every slide~~ ✅ | ~~ZERO~~ |
| ~~17~~ | ~~`EpubExportService.XhtmlSafe`~~ | ~~14 sequential `Regex.Replace` passes (one per void HTML tag) over the entire document — each allocates a new Regex and scans the full string~~ ✅ | ~~ZERO~~ |
| ~~18~~ | ~~`HistoryEntry`~~ | ~~Non-cached `new Regex(...)` per history line + redundant normalization call — allocates on every history list render~~ ✅ | ~~ZERO~~ |
| ~~19~~ | ~~`Detectors.cs` (12 detectors)~~ | ~~Each detector calls `GetInnerLines(block)` which re-splits the raw block text — the same block is split into lines up to 12 times~~ ✅ | ~~ZERO~~ |
| ~~20~~ | ~~`DocxExportService` (lines 1092/1098/1118)~~ | ~~`info?.Trim().Split(' ', '\t')[0].TrimStart('.')` computed **twice** for the same code block + `new Regex(...)` per call for font-color stripping~~ ✅ | ~~ZERO~~ |
| ~~21~~ | ~~`DocxExportService` (lines 1052–1066)~~ | ~~`CloneNode(true)` deep-copies the entire run-properties XML node **per line** of a code block — hundreds of deep clones for a long listing~~ ✅ | ~~ZERO~~ |
| ~~22~~ | ~~`LlmSourceService`~~ | ~~`Regex.IsMatch(markdown, ...)` with inline patterns instead of `[GeneratedRegex]` — misses source-gen optimization on a hot classification path~~ ✅ | ~~ZERO~~ |
| ~~23~~ | ~~`ExportCoordinator` (line 370)~~ | ~~`ThemeCatalog.GetOrDefault(themeName)` called **inside** the per-file batch loop — re-resolves the same theme for every file in a batch export~~ ✅ | ~~ZERO~~ |

## Status

| Status | Findings |
|--------|----------|
| **Implemented** | #1–#23 — All findings resolved ✅ |
| **Remaining** | None |

## Biggest Bang-for-Buck Remaining

1. **#4** — delete the debug file I/O (one-line removal, instant win)
2. **#3** — strip the Console.WriteLine diagnostics (dead weight on every export)
3. **#5 + #16 + #20 + #22** — cache/source-gen all the per-call Regex allocations (one pattern, many sites)
4. **#14** — cache `ThemeCatalog.All` as a field, invalidate on custom-theme change
5. **#2** — single-pass normalization (split once, run all normalizers over the line array, join once)
