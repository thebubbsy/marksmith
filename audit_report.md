# Marksmith Engine Comprehensive Codebase Audit Report

## Executive Summary
An exhaustive codebase audit was conducted across all core C# components of the Marksmith engine:
- `MdToPdf.Core` (Markdown Parsing, AST, Pipeline, Text Normalizers)
- DOCX Generation (`DocxExportService.cs`, OpenXML DOM, Styles, Proofing)
- HTML Rendering (`MarkdownHtmlService.cs`, Sanitizers, Diagram Exporters)
- UI ViewModels (`MdToPdf.Avalonia`, MainViewModel, Controls, Services)

A total of **36 distinct bugs, logical flaws, security vulnerabilities, and edge-case rendering issues** were identified, documented, and assigned for remediation.

---

## Detailed Audit Findings & Remediation Plan

### Category 1: Markdown Parsing & `MdToPdf.Core` (9 Findings)

| ID | File Path | Line | Severity | Description | Remediation |
|---|---|---|---|---|---|
| M1-01 | `MdToPdf.Core/Services/HtmlSanitizer.cs` | 30 | High | Event handler stripping regex (`\son\w+`) misses slash-delimited tags (e.g. `<img/onload=...>`), allowing XSS execution in preview. | Update regex to allow forward slash prefix: `[\s/]\son\w+`. |
| M1-02 | `MdToPdf.Core/Services/MarkdownHtmlService.cs` | 128 | High | Embedded Mermaid string literals extracted from code blocks are appended directly to HTML without HTML-encoding. | Wrap extracted string literals in `WebUtility.HtmlEncode()` before appending. |
| M1-03 | `MdToPdf.Core/AdvancedFeatures/AdvancedFeaturePipeline.cs` | 213 | High | `Tokenize()` lacks container nesting depth tracking; inner closing `:::` prematurely terminates outer `:::` containers. | Implement stack depth counter for container tokenization. |
| M1-04 | `MdToPdf.Core/Services/DialectNormalizer.cs` | 41 | Medium | `DialectNormalizer.Apply()` unconditionally calls `DashReplacer.NormalizeDoubleHyphens` overriding `DashMode.Keep` settings. | Check `AppSettings.DashMode` before replacing double hyphens. |
| M1-05 | `MdToPdf.Core/Services/DashReplacer.cs` | 60-85 | Medium | `NormalizeDoubleHyphens` skips fenced code blocks but replaces `--` with `—` in 4-space or tab indented code blocks. | Track indented code blocks and bypass dash replacements inside them. |
| M1-06 | `MdToPdf.Core/Services/AdmonitionNormalizer.cs` | 118 | Medium | Admonition body scanning loop matches `:::` inside code fences, truncating admonition blocks prematurely. | Maintain code block state tracking during line consuming loop. |
| M1-07 | `MdToPdf.Core/Services/EpubExportService.cs` | 76 | Medium | Void tag XHTML regex `[^>]*?` breaks on quoted attributes containing `>` (e.g., `alt="A > B"`), causing invalid XML output. | Update regex or attribute parser to be quote-aware. |
| M1-08 | `MdToPdf.Core/Services/DiagramFenceSniffer.cs` | 46, 53 | Low | Hardcoded 3-character fence marker `[..3]` causes premature fence termination when code contains 4+ backtick fences. | Extract dynamic length of opening fence marker. |
| M1-09 | `MdToPdf.Core/Services/MarkdownHtmlService.cs` | 783 | Low | `BuildToc()` tag stripping regex removes generic C# types in headings (e.g., `# List<T>` becomes `List`). | HTML-encode or selectively strip HTML tags while preserving generic syntax. |

---

### Category 2: DOCX Generation & OpenXML Schema (10 Findings)

| ID | File Path | Line | Severity | Description | Remediation |
|---|---|---|---|---|---|
| M2-01 | `MdToPdf.Core/Services/DocxExportService.cs` | 424-426 | High | OpenXML schema violation in `MathBlock` display math: `w:spacing` placed before `w:jc`. | Reorder elements so `W.Justification` precedes `W.SpacingBetweenLines`. |
| M2-02 | `MdToPdf.Core/Services/DocxExportService.cs` | 663-666 | High | Footnote label `W.Run` inserted directly into `W.ParagraphProperties` instead of parent `W.Paragraph`. | Insert run into `W.Paragraph` after properties. |
| M2-03 | `MdToPdf.Core/Services/DocxExportService.cs` | 253, 1486 | Medium | `NumberingDefinitionsPart` is not saved in append mode, losing newly created list numbering instances. | Call `NumberingDefinitionsPart.Numbering.Save()` before saving main document. |
| M2-04 | `MdToPdf.Core/Services/DocxExportService.cs` | 1519, 1855 | High | OpenXML table cell (`w:tc`) ending with non-paragraph element violates ISO 29500 (missing mandatory trailing `w:p`). | Ensure every `w:tc` ends with a `W.Paragraph`. |
| M2-05 | `MdToPdf.Core/Services/DocxExportService.cs` | 2080, 2229 | Medium | Unmapped `#tag` spans and nested formatting pops clearing `NoProof` flag, causing spell-check errors on WikiLinks/tags. | Map `md-tag` class and preserve `NoProof` property when popping formatting stack. |
| M2-06 | `MdToPdf.Core/Services/DocxExportService.cs` | 2475, 2527 | High | NullReferenceException & undisposed `JsonDocument` in `RenderChart`. | Wrap `JsonDocument` in `using`, guard null string values. |
| M2-07 | `MdToPdf.Core/Services/DocxExportService.cs` | 1318 | Medium | NullReferenceException in `RenderReferences` when `node.InnerContent` is null. | Null-guard `node.InnerContent`. |
| M2-08 | `MdToPdf.Core/Services/DocxExportService.cs` | 2443 | Medium | NullReferenceException in `RenderDatagrid` when `ctx.Theme.Primary` is null. | Safe-navigate `ctx.Theme.Primary?.TrimStart('#')`. |
| M2-09 | `MdToPdf.Core/Services/DocxExportService.cs` | 2246 | Low | OpenType `w14` properties in standard `w:rPr` trigger schema warnings in non-Word viewers. | Wrap `w14` extensions in compatibility list wrappers. |
| M2-10 | `MdToPdf.Core/Services/DocxExportService.cs` | 1861 | Low | Table column alignment skipped for paragraphs inside nested block elements. | Use `wCell.Descendants<W.Paragraph>()` to apply alignment recursively. |

---

### Category 3: HTML Rendering & Sanitization (4 Findings)

| ID | File Path | Line | Severity | Description | Remediation |
|---|---|---|---|---|---|
| M3-01 | `MdToPdf.Core/Plugins/SvgSanitizer.cs` | 24-26 | High | `SvgSanitizer` checks raw SVG attributes without HTML-decoding, allowing `java&#x73;cript:` URIs to bypass sanitization. | HTML-decode attribute values before testing against script regexes. |
| M3-02 | `MdToPdf.Core/Services/MarkdownHtmlService.cs` | 81, 161 | High | Plugin-generated SVG diagrams interpolated into HTML body without running through `SvgSanitizer`. | Pass plugin SVG output through `SvgSanitizer.Sanitize(svg)` before inserting into body. |
| M3-03 | `MdToPdf.Core/Services/DialectNormalizer.cs` | 34, 195 | Medium | `ReplaceOutsideInlineCode` regex matches single backticks only; WikiLink/Hashtag normalizers mutate multi-backtick code spans (` ``code`` `). | Update inline code regex to support multi-backtick delimiters. |
| M3-04 | `MdToPdf.Core/Services/MarkdownHtmlService.cs` | 699, 703 | Medium | `PrepareImageForInline` leaks unmanaged SkiaSharp bitmap by failing to dispose original `SKBitmap` after resizing. | Wrap `SKBitmap.Decode(raw)` in `using` block. |

---

### Category 4: UI ViewModels & Application Logic (13 Findings)

| ID | File Path | Line | Severity | Description | Remediation |
|---|---|---|---|---|---|
| M4-01 | `MdToPdf.Avalonia/Hosting/ClipboardWatcherService.cs` | 28 | High | `async void` lambda on timer tick (`_timer.Tick += async ...`) crashes process on clipboard exception. | Wrap async timer delegate body in `try/catch`. |
| M4-02 | `MdToPdf.Avalonia/Views/MainWindow.axaml.cs` | 120 | High | `async void` on `Loaded` event handler crashes app on startup errors. | Wrap `Loaded` handler body in top-level `try/catch` and present error UI. |
| M4-03 | `MdToPdf.Avalonia/Views/MainWindow.axaml.cs` | 324, 414 | High | `async void` delegates in `Dispatcher.UIThread.Post` crash UI thread if REST API calls fail. | Wrap UI thread posted delegates in `try/catch` with `tcs.SetException`. |
| M4-04 | `MdToPdf.Avalonia/Hosting/FolderWatcherService.cs` | 47 | High | Fire-and-forget `Task.Run` in file system watcher callback silently drops errors or crashes worker thread. | Add `try/catch` logging block inside `Task.Run`. |
| M4-05 | `MdToPdf.Avalonia/Hosting/LocalAssetServer.cs` | 81 | High | Port discovery race condition in `GetFreeLoopbackPort()` causes `HttpListenerException`. | Bind to port 0 directly on `HttpListener` or retry socket binding loop. |
| M4-06 | `MdToPdf.Avalonia/Controls/AmbiguityColorizer.cs` | 15, 30 | High | Non-thread-safe `_ambiguousLines` dictionary accessed concurrently between text change and UI render thread. | Use `lock` object or `ConcurrentDictionary` to guard access. |
| M4-07 | `MdToPdf.Avalonia/App.axaml.cs` | 19 | High | `TrayIcon.GetIcons(this)![0]` throws `ArgumentOutOfRangeException` / `NullReferenceException` if tray icons empty. | Add null and count bounds check before array indexing. |
| M4-08 | `MdToPdf.Core/ViewModels/MainViewModel.cs` | 269-311 | High | Synchronous `_settingsService.Save()` on UI thread on every settings property change notification. | Debounce or execute settings save asynchronously on background thread. |
| M4-09 | `MdToPdf.Core/ViewModels/MainViewModel.cs` | 114 | Medium | Synchronous `File.ReadAllText` inside `CurrentMarkdown` property getter blocks UI thread. | Cache file content or read asynchronously during file selection. |
| M4-10 | `MdToPdf.Core/ViewModels/MainViewModel.cs` | 623 | Medium | Overwriting `_conversionCts` without disposing old CTS causes memory/handle leak and cancels signal tracking. | Dispose and cancel existing `_conversionCts` prior to assignment. |
| M4-11 | `MdToPdf.Core/ViewModels/MainViewModel.cs` | 692 | Medium | `Path.GetDirectoryName(InputFilePath)!` assuming non-null throws `ArgumentNullException` on root paths. | Fall back to fallback folder if directory name is null. |
| M4-12 | `MdToPdf.Avalonia/Controls/Polyfills.cs` | 101 | Medium | `ContentDialog.ShowAsync()` hangs indefinitely (deadlock) when `owner` is null. | Set `tcs.SetResult` on dialog close even if `owner` is null. |
| M4-13 | `MdToPdf.Avalonia/Views/MainWindow.axaml.cs` | 20, 675, 1068, 1089, 1282 | Medium | Multiple `async void` UI event handlers lacking exception boundary. | Wrap all event handler async bodies in defensive `try/catch`. |

---

## Remediation Workflow & Acceptance Criteria
1. **Fix Implementation**: Each bug fix will be implemented by Worker subagents in the C# source code.
2. **Regression Testing**: A corresponding unit test will be written for each fix, ensuring bug scenario is covered.
3. **Build & Test Verification**: `dotnet test` must be executed and achieve 100% pass rate.
4. **Forensic Integrity Verification**: `teamwork_preview_auditor` will run static and dynamic analysis to confirm clean implementation.
