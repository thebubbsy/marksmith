# Marksmith Capabilities & Feature Roadmap

## 🚀 Currently Implemented (What We Already Do)

Marksmith is a powerful engine that takes AI-generated Markdown and turns it into native, deeply integrated Microsoft Word (.docx) documents. We don't just paste text; we **"Wordify"** it. 

### Native Word Integration
1. **Admonitions to Native Blocks:** We intercept GitHub-style alerts (`> [!WARNING]`) and turn them into native Word tables with background shading, borders, and colored glyphs.
2. **Mermaid to Native Shapes (ShapeForge):** We intercept Mermaid.js code blocks and use our "ShapeForge" engine to draw them as native, editable Word shapes and lines, rather than just rasterized images.
3. **Task Lists to Interactive Checkboxes:** We map `- [ ]` and `- [x]` directly to native Word `w14:checkbox` Content Controls so they are clickable natively in Word.
4. **LaTeX to Native OMML Math:** We intercept Math blocks and use `LatexToOmml` to inject fully editable native Word Equations.
5. **Syntax Highlighting & Diffs:** We parse HTML color spans inside code blocks and apply them to `w:r` text runs, and we detect `diff` blocks to automatically color lines green/red.
6. **AI Quirk Cleanup:** We sniff out and strip annoying AI formatting remnants (e.g., citation pips, pseudo-headings) and anchor a genuine native Word Comment (in the margin bubble) detailing the AI cleanup modifications.
7. **Theme Injection:** We map visual app themes (Monokai Pro, GitHub Dark) directly into the native Word document's background colors, link colors, and text styles.
8. **Smart Typography & Headings:** We conditionally map double hyphens to native em-dashes and allow dynamic promotion/demotion of Markdown headings to map perfectly to Word's Outline Levels.
9. **Frontmatter to Cover Pages & Auto ToC:** We intercept YAML frontmatter to generate a professional Word Cover Page and insert a native `{ TOC }` field that auto-updates.
10. **Append Mode & Oversized Diagram Fallback:** We can append new Markdown exports as dated sections to an existing running Word document, and if a ShapeForge diagram is too wide, we dynamically flip the DOCX into `Web Layout` mode so it scrolls horizontally.

---

## 🔮 The 2026 Native Feature Roadmap (To Be Added)

These are **weapon-grade, 2026-era Markdown → Native Word transformations** that will make Marksmith a completely unparalleled tool. Each one is engineered to feel like a *Word feature that already existed but was secretly waiting for Marksmith to unlock it*.

### 1. `:::tabs` → Native Word Tabbed Content Controls (W15 Multi-Section Containers)
*   **Concept:** Generate a **single w15:tabSet** control with each tab mapped to a child section. Users can click between tabs *inside Word* exactly like a web UI.
*   **Use Cases:** API docs with multi-language tabs, product docs, AI-generated tutorials.

### 2. `:::embed` (YouTube, Loom, Figma) → Native Word Online Video / WebEmbed OLE
*   **Concept:** Intercept embed blocks and inject **Native Word Online Video** (playable inside Word), Figma prototypes as live web embeds, or Loom recordings as interactive video frames.

### 3. `:::chart` (Vega-Lite, Plotly) → Native Word DrawingML Charts
*   **Concept:** Parse chart specs and generate a real Excel worksheet behind the chart, creating a fully native Word chart (editable, styleable, animatable) with auto-linked data series.

### 4. `:::datagrid` → Native Excel PivotTable OLE
*   **Concept:** If a Markdown datagrid contains grouping, totals, or filters, generate an embedded **PivotTable OLE** instead of a flat Word table. Double-clicking opens Excel with full pivot functionality.

### 5. `:::columns` → Native Word Multi-Column Section Breaks
*   **Concept:** Detect column blocks and create a new section with N columns, balanced/unbalanced flow, and column rules, turning Markdown into a magazine layout natively.

### 6. `:::smartart` → Native Word SmartArt Diagrams
*   **Concept:** Convert structured lists into native SmartArt process diagrams, hierarchy trees, cycle diagrams, and relationship charts, giving AI-generated business docs corporate polish.

### 7. `:::references` → Native Word Bibliography + Citation Manager
*   **Concept:** Parse CSL JSON / BibTeX and inject native Word citation sources, an auto-formatted bibliography section, and clickable citation fields (`<w:fldSimple w:instr="CITATION ...">`).

### 8. `:::timeline` → Native Word Timeline SmartArt + Date Metadata
*   **Concept:** Convert timeline blocks into SmartArt timeline diagrams with auto-scaled date spacing and custom document properties storing event metadata.

### 9. `:::canvas` (HTML Canvas / SVG) → Native Word InkCanvas + DrawingML Paths
*   **Concept:** Intercept canvas/SVG blocks and convert paths to DrawingML geometry or InkCanvas for freehand strokes, preserving layers and transforms.

### 10. `:::workflow` → Native Word Flowchart SmartArt + Action Metadata
*   **Concept:** Generate SmartArt flowcharts with custom XML storing workflow metadata and Action Pane add-in integration, allowing steps to trigger macros or open linked docs.

### 11. `:::ai-context` → Native Word Document Variables + Auto-Refresh Regions
*   **Concept:** Store AI metadata (model, temperature, purpose) in Custom XML parts, Document Variables, and Linked Content Controls, allowing for audited, AI-native Word documents with auto-refresh regions.
