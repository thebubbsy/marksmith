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
