# Marksmith Capabilities & Feature Grid (v2.8.0)

## 🚀 Version 2.8.0 Release Highlights & Feature Matrix

Marksmith v2.8.0 is a multi-format document engine that transforms Markdown and AI outputs into native, deeply integrated Microsoft Word (`.docx`), PDF, PowerPoint (`.pptx`), EPUB, and WebAssembly applications.

### 📊 Comprehensive Feature Matrix

| Feature | PDF Export | Native DOCX (OpenXML) | PPTX Presentation | EPUB eBook | Blazor WASM Editor |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **LaTeX / OMML Math** | ✅ KaTeX | ✅ Native OMML | ✅ Vector/Math | ✅ HTML Math | ✅ Live KaTeX |
| **Mermaid / ShapeForge** | ✅ SVG | ✅ Native Vector Shapes | ✅ Slide Diagrams | ✅ Inline SVG | ✅ Live Preview |
| **SmartArt & Kanban** | ✅ Styled | ✅ Native SmartArt | ✅ Flow Layout | ✅ Styled | ✅ Visual Grid |
| **Native Tabs (`:::tabs`)**| ✅ Styled Tabs | ✅ Word Outline Tabs | ✅ Tabbed Slides | ✅ Collapsible | ✅ Live Tabs |
| **Cloud Auto-Publish** | ✅ WebDAV/Sync | ✅ WebDAV/Sync | ✅ WebDAV/Sync | ✅ WebDAV/Sync | N/A |
| **OCR Table Extraction** | ✅ Tesseract | ✅ Native Tables | ✅ Table Slides | ✅ HTML Tables | ✅ Image OCR |
| **AI Quirk Normalization**| ✅ Auto-Clean | ✅ Margin Comments | ✅ Auto-Clean | ✅ Auto-Clean | ✅ Live Counter |

---

## 🛠️ Native Word & Multi-Format Capabilities

1. **Admonitions to Native Blocks:** Intercepts GitHub-style alerts (`> [!WARNING]`) and renders native Word tables with background shading, borders, and colored glyphs.
2. **Mermaid to Native Shapes (ShapeForge™):** Converts Mermaid.js code blocks into native, fully editable Word DrawingML vector shapes and lines.
3. **SmartArt & Kanban Export:** Translates Markdown lists into native Word SmartArt parts and Kanban project board layouts.
4. **Task Lists to Interactive Checkboxes:** Maps `- [ ]` and `- [x]` directly to native Word `w14:checkbox` Content Controls.
5. **LaTeX to Native OMML Math:** Maps LaTeX blocks via `LatexToOmml` directly into editable native Word Equations.
6. **Blazor WASM Markdown Editor:** Standalone client-side web application (`MdToPdf.Wasm`) offering instant split-pane preview with full Markdig extensions.
7. **Cloud Auto-Publish:** Automatically mirrors generated exports to WebDAV endpoints or local sync folders.
8. **OCR Table Extraction:** Built-in Tesseract OCR engine parses data tables directly from embedded document images into structured Markdown tables.
9. **Multi-Format Export Pipeline:** Coordinated export engine for PDF, DOCX, PPTX, and EPUB files.
