# Marksmith Capabilities & Feature Grid (v2.17)

## 🚀 Version 2.17 Release Highlights & Feature Matrix

Marksmith v2.17 is a multi-format document engine that transforms Markdown and AI outputs into native, deeply integrated Microsoft Word (`.docx`), PDF, PowerPoint (`.pptx`), EPUB, and Google Docs documents.

> A standalone browser editor (MarkSmith.Wasm) shipped as a spike and was retired on 2026-08-13; a web/cross-platform path remains on the ROADMAP's "Later" list.

### 📊 Comprehensive Feature Matrix

| Feature | PDF Export | Native DOCX (OpenXML) | PPTX Presentation | EPUB eBook |
| :--- | :---: | :---: | :---: | :---: |
| **LaTeX / OMML Math** | ✅ KaTeX | ✅ Native OMML | ✅ Vector/Math | ✅ HTML Math |
| **Mermaid / ShapeForge** | ✅ SVG | ✅ Native Vector Shapes | ✅ Slide Diagrams | ✅ Inline SVG |
| **SmartArt & Kanban** | ✅ Styled | ✅ Native SmartArt | ✅ Flow Layout | ✅ Styled |
| **Native Tabs (`:::tabs`)**| ✅ Styled Tabs | ✅ Word Outline Tabs | ✅ Tabbed Slides | ✅ Collapsible |
| **Cloud Auto-Publish** | ✅ WebDAV/Sync | ✅ WebDAV/Sync | ✅ WebDAV/Sync | ✅ WebDAV/Sync |
| **OCR Table Extraction** | ✅ Windows OCR | ✅ Native Tables | ✅ Table Slides | ✅ HTML Tables |
| **AI Quirk Normalization**| ✅ Auto-Clean | ✅ Margin Comments | ✅ Auto-Clean | ✅ Auto-Clean |

---

## 🛠️ Native Word & Multi-Format Capabilities

1. **Admonitions to Native Blocks:** Intercepts GitHub-style alerts (`> [!WARNING]`) and renders native Word tables with background shading, borders, and colored glyphs.
2. **Mermaid to Native Shapes (ShapeForge™):** Converts Mermaid.js code blocks into native, fully editable Word DrawingML vector shapes and lines.
3. **SmartArt & Kanban Export:** Translates Markdown lists into native Word SmartArt parts and Kanban project board layouts.
4. **Task Lists to Interactive Checkboxes:** Maps `- [ ]` and `- [x]` directly to native Word `w14:checkbox` Content Controls.
5. **LaTeX to Native OMML Math:** Maps LaTeX blocks via `LatexToOmml` directly into editable native Word Equations.
6. **Native Google Docs Export:** Builds Google Docs-native documents (headings, tables, lists) from the same pipeline, with export branding and source round-trip.
7. **Cloud Auto-Publish:** Automatically mirrors generated exports to WebDAV endpoints or local sync folders.
8. **OCR Table Extraction:** Local OCR engine (`OcrEngineService` over a pluggable `IOcrProvider` — Windows.Media.Ocr on desktop) parses data tables directly from embedded document images into structured Markdown tables.
9. **Multi-Format Export Pipeline:** Coordinated export engine for PDF, DOCX, PPTX, and EPUB files.

---

## 🌌 Document Galaxy — filing by relationship instead of by folder

A folder tree makes you answer "where does this live?" exactly once. A research PDF that fed a
proposal, got quoted in a deck and started an argument in your notes has four right answers, so it
gets filed under the wrong one. The Document Galaxy keeps each document in one place and lets it
carry as many *named* relationships as it earned.

- **Every file is a node.** `.md`, `.docx`, `.pdf`, `.pptx`, `.epub`, `.rtf`, `.txt` and `.html`
  each get a card with its format badge, progress, tags, connection count and version history.
  Double-click opens the real file in the MarkSmith editor.
- **Import a vault.** Point it at a folder and it builds the map: `[[wikilinks]]`, relative
  Markdown links, image embeds, YAML front matter and `#tags` all become edges, and subdirectories
  become folder nodes so a large vault stays legible. Link strength is ranked — an edge you wrote
  always outranks one the scanner inferred.
- **Name the relationship.** `grew out of`, `evidence for`, `supersedes`. Solid lines are the
  hierarchy; dashed lines are the cross-links a folder tree cannot express.
- **Five layouts.** Horizontal tree, top-down hierarchy, radial galaxy, force-directed, and
  Separate Constellations, which packs each connected island so they never overlap.
- **Find things.** Search spans titles, notes, tags and file paths; tag pills filter; focus mode
  dims everything the selected document is not connected to.
- **Topology report.** Hubs, unconnected documents, cluster count, link density and a format
  breakdown — a read on the shape of your library, not just its size.
- **It leaves.** Export an editable Word DrawingML diagram with a full relationship ledger, or copy
  the map out as a Mermaid `flowchart` / `mindmap` and paste it into any document.

The map is a plain-JSON `.msmap` file under the MarkSmith config directory. It is repaired on load
(dangling links, parent cycles, duplicate edges, invalid colours) and an unreadable file is set
aside as `.corrupt` rather than replaced. First run shows a guided tour; saving replaces it with
your own galaxy for good.
