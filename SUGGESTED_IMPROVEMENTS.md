# 10 Existing Feature Improvements for Marksmith

Based on the current Marksmith feature set (v2.8.0), here are 10 logical improvements to existing features to enhance capability and user experience:

1. **Enhanced ShapeForge Mermaid Parsing for Gantt Charts:** Improve `MermaidChartsRenderer.cs` to map Gantt chart timelines to native MS Project-style timeline SmartArt structures, allowing for native duration editing inside DOCX.
2. **Deep-Linked TOC Auto-Update:** Instead of just generating a Table of Contents via OpenXML, hook into Word's field calculation flag so the TOC automatically updates its page numbers on the *first open* of the document, removing the need for manual `F9` updates.
3. **Advanced AI Quirk Normalization - Inline Citations:** Extend the normalization engine to convert conversational citation markers (e.g., `[1]`, `[source]`) into proper Word footnotes linked to an automatically generated bibliography section at the document's end.
4. **Interactive Blazor WASM Editor Extensions:** Enhance the `MarkSmith.Wasm` live editor with a localized real-time collaboration cursor (via WebRTC or local network mesh) so multiple users can edit the same markdown document simultaneously before export.
5. **EPUB 3 Media Overlays (Read Aloud):** Upgrade the EPUB export pipeline to support EPUB 3 Media Overlays, mapping headers and text blocks to SSML (Speech Synthesis Markup Language) tags for accessibility and native "Read Aloud" support in e-readers.
6. **SmartArt Layout Customization Engine:** Expand the reverse-engineered SmartArt engine to allow users to upload their own custom `.glox` (SmartArt Graphic Layout) templates via the UI, matching them dynamically to Markdown lists.
7. **OCR Table Extraction with Formatting Retention:** Upgrade the Tesseract OCR implementation to not just extract text, but also infer and apply native Word cell shading, border weights, and text alignment based on the original image's pixel data.
8. **PPTX Morph Transition Support:** When appending content or generating multi-slide presentations from markdown headers, automatically inject OpenXML tags for PowerPoint "Morph" transitions between slides that share similar native shapes or SmartArt.
9. **Native Theme Extraction from PDF:** Extend the house-style automation to extract color palettes and typography not just from `.dotx` templates, but also by analyzing uploaded corporate PDF documents to instantly create a Marksmith `ThemeDefinition`.
10. **OMML Math - Live Equation Solver:** Integrate a lightweight local WASM math solver into the live Markdown editor that can evaluate LaTeX blocks and append the solved result (e.g., `= 42`) into the native Word equation output automatically.
