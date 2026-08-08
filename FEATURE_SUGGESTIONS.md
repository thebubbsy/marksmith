# Feature Improvements

Here are 10 suggestions for improving existing features in Marksmith:

1. **ShapeForge Expansion:** Extend Mermaid diagram support beyond flowcharts. Add native Word shape mapping for Sequence Diagrams, State Diagrams, and User Journeys, allowing them to be fully editable vector shapes in DOCX rather than falling back to images or code blocks.
2. **Tracked Changes for AI Normalization:** Upgrade the current AI cleanup feature (which uses margin comments to disclose changes) to utilize native Word Tracked Changes. This would allow users to individually accept or reject each formatting fix (e.g., citation pip removal) directly in Word.
3. **Advanced Branding Kit:** Expand the branding kit to inject custom logos and letterheads into the repeating header and footer of every page in the exported DOCX/PDF, not just the cover page (as hinted in the current roadmap).
4. **Export Preset Cloud Sync:** Enhance Export Presets by allowing them to be synchronized across a team or user's devices via cloud storage or the planned governance tier, ensuring a consistent visual identity across distributed workflows.
5. **Interactive SmartArt JSON Editor:** Bring the CLI SmartArt layout compiler capabilities to the Blazor WASM editor. Add a side-by-side live preview for authoring and tweaking custom SmartArt JSON constraint files before exporting them to `.glox`.
6. **Multi-page OCR Table Extraction:** Improve the Tesseract OCR engine to detect and stitch together tables that span across multiple input images or screenshots, outputting them as a single continuous repeating-header table in Markdown/DOCX.
7. **Delta Rendering for Word-Exact Preview:** Optimize the Word-Exact preview mode. Instead of re-rendering full page bands (costing ~1.7s), use a delta-update mechanism with the OpenXML SDK to only update the specific paragraphs or shapes being modified in real-time, reducing latency to match the HTML preview.
8. **PPTX Speaker Notes Generation:** Enhance the PPTX export pipeline by intelligently parsing non-bulleted paragraphs that follow headings as slide speaker notes, turning standard long-form Markdown documents naturally into presentation decks.
9. **Browser Extension Quick Preview:** Add a floating overlay preview to the Marksmith browser extension. Before pushing an AI chat to the desktop app via the API, allow users to preview how the Markdown will parse (and check AI cleanup stats) directly in the browser.
10. **Intellisense for Marksmith Components:** Enhance the Blazor live editor by adding autocomplete and syntax highlighting specifically for Marksmith's advanced OpenXML elements (like `:::tabs`, `:::chart`, and `:::datagrid`), making these powerful custom features more discoverable to users writing Markdown from scratch.
