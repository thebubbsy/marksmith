# Gemini AI Agent Learnings & Rules (GEMINI.md)

This document contains accumulated technical learnings, architecture blueprints, and engineering governance rules derived from real-world development sessions and archived project tasks.

---

## 1. OpenXML & OOXML Document Generation Governance
When generating or modifying Office OpenXML (`.docx`, `.xlsx`, `.pptx`) using C# / `DocumentFormat.OpenXml`:

- **Never Hardcode Relationship IDs (`rId`)**:
  DO NOT hardcode relationship IDs (e.g., `rId1`, `rId2`) in generated XML. ALWAYS use dynamic relationship managers (`package.MainDocumentPart.AddExternalRelationship(...)`) to register external images, hyperlinks, footnotes, or custom parts in `.rels`.
- **Enforce Root Namespaces**:
  Ensure all required XML namespace declarations (`w:`, `a:`, `wp:`, `pic:`, `m:`, `wpg:`, `wps:`, `w15:`) are declared on root elements. Missing namespaces cause Word "File Corrupted" errors upon opening.
- **Memory Overhead & SAX Writing**:
  Avoid loading full `document.xml` trees into DOM memory (`XmlDocument`/`XDocument`) for large documents. Use `OpenXmlWriter` (SAX-style streaming) for $O(1)$ memory footprint.
- **Native Word Element Reality vs AI Hallucinations**:
  - **Tabs (`:::tabs`)**: Word has no native `w15:tabSet` element. Implement using Heading outline levels (`w:outlineLvl`) or Content Controls (`w:sdt`) linked to Custom XML parts + VBA macros.
  - **Online Video (`:::embed`)**: Use `<w15:webVideoPr>` extension inside `<wp:docPr>` containing an `<a:blip>` thumbnail image.
  - **DrawingML Charts (`:::chart`)**: Requires a `ChartPart` backed by an embedded Excel spreadsheet (`EmbeddedPackagePart`) linked via `<c:f>` formulas.
  - **PivotTables / OLE Objects (`:::datagrid`)**: Embed binary `.xlsx` as `EmbeddedObjectPart` with an EMF/PNG preview image linked via `<w:object>`.
  - **Multi-Column Sections (`:::columns`)**: Enclose column blocks in Continuous Section Breaks (`<w:type w:val="continuous"/>` inside `<w:sectPr>`).
  - **Footnotes**: Store in `word/footnotes.xml` and insert `<w:footnoteReference>` inline anchors in document body.
  - **Math & OMML Whitespace**: Preserve literal whitespace inside OMML text blocks using `<m:t xml:space="preserve">`.
  - **Complex HTML Tables**: Translate `colspan` and `rowspan` into native `<w:gridSpan>` and `<w:vMerge>`.
  - **Collapsible Sections**: Map to native Word collapsible headings using `<w15:collapsed w:val="true"/>`.

---

## 2. Security & Anti-Corruption Practices
- **XSS Prevention in Code Fences / Diagrams**:
  When transforming raw code fences (e.g., `<pre><code class="language-mermaid">`) into DOM rendering containers, DO NOT `HtmlDecode` text before insertion. Browsers automatically decode `.textContent`, so pre-decoding exposes XSS execution vectors.
- **Inline Tag Processing**:
  When stripping or transforming inline HTML tags (e.g., `<u>`, `<span>`), preserve adjacent text nodes and trailing whitespace to prevent string truncation bugs.

---

## 3. Markdown AST & Parser State Machines
- **Pipeline Configuration**:
  Always verify that the `MarkdownPipelineBuilder` has explicitly enabled required extension modules (e.g., `.UseDefinitionLists()`) before assuming AST nodes will be present.
- **Parser Leaked Borders & Blockquote Traps**:
  Ensure table parsers handle nested blockquote contexts (`>`) correctly and strip table structural borders (e.g., Pandoc `+------+` lines) rather than leaking them into text runs.

---

## 4. UI/UX Architecture & Dynamic Layouts
- **In-Place Canvas Editor Coordinates**:
  Positioning floating text inputs or overlay controls on interactive canvases must calculate bounds relative to their direct `Canvas` parent wrapper rather than outer Grids or Window containers.
- **Explicit Action Affordances**:
  Provide dual commit mechanisms for inline canvas editors: explicit Commit (✔️) / Cancel (❌) action buttons alongside keyboard accelerators (`Enter` / `Escape`).
- **Dynamic Vector Shape Converters**:
  Use WPF/Avalonia value converters (`ShapeToVisibilityConverter`) on item templates to dynamically switch icon primitives (Actor, Database cylinder, Decision diamond, Root circle) based on model metadata.
- **Workspace Consolidation**:
  Consolidate crowded toolbars into smart dropdown clusters (`Text Style`, `Lists ▼`, `+ Insert ▼`) and utilize custom title bar regions (`AppTitleBar`) to reclaim vertical canvas height.

---

## 5. Multi-Agent & Subagent Orchestration
- **Specialized Role Decomposition**:
  For complex features or refactors, break tasks into specialized subagent personas (e.g., OpenXML Researcher, OpenXML Verifier, DocX Debugger, Layout Debugger, Auditor, Reviewer, Challenger).
- **Empirical Verification Gate**:
  Never declare victory based solely on code compilation. Perform end-to-end verification tests, run automated test suites, and inspect raw output file structures (XML, PDF, DOCX) to confirm fidelity.
