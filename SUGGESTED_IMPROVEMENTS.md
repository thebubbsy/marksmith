# 10 Suggested Feature Improvements for Marksmith

Based on the existing capabilities of Marksmith (AI normalization, native document exports, SmartArt engine, Mermaid integration, live editor, etc.), here are 10 concrete feature improvements:

1.  **AI Detection Model Confidence Feedback Loop**:
    Enhance the "AI source detection & normalization" feature by allowing users to report false positives/negatives in the fingerprinting of ChatGPT, Gemini, Claude, or Copilot. This data can locally tune the detection heuristics over time to improve the confidence score.

2.  **Mermaid Shape Context Menus in Word**:
    Extend the `ShapeForge` native Word rendering of Mermaid diagrams so that the generated DrawingML shapes contain OpenXML Alt Text or hidden data linking back to the original Mermaid source code. This would allow a Marksmith reverse-importer or a Word Add-in to reconstruct the Markdown block from the DOCX diagram.

3.  **SmartArt / Kanban Two-Way Sync via API**:
    Build upon the "Bidirectional SmartArt layout engine" and the REST API. Allow a user to drag and drop nodes in a generated Kanban Word document, and have an API endpoint or watcher listen to save those structural changes back to the original Markdown file as reordered list items.

4.  **Custom AI Clean-Up Regex Profiles**:
    Expand the "Cleanup controls". Currently it normalizes known quirks (LaTeX delimiters, pseudo-headings, citation artifacts). Add a feature to allow users to define their own custom regular expressions (e.g., stripping specific proprietary disclaimers or unique phrases their company's custom GPT always outputs) and save them as cleanup presets.

5.  **Live Editor "Ghost" Auto-Complete**:
    Leverage the local WASM capabilities in the Blazor editor to add a lightweight, local "Ghost text" (tab-to-complete) feature for Markdown syntax, predicting things like closing code fences, continuing table rows, or suggesting the next number in an ordered list based on the current context.

6.  **Interactive Math Equation Tester**:
    Improve the "Editable Word Equations (OMML)" and live KaTeX preview by adding a dedicated Math inspector panel. When a user clicks on a `$$` block, it isolates the equation and provides a visual symbol palette to inject LaTeX commands directly into that block, seeing the KaTeX render update instantly before exporting to DOCX.

7.  **Theme Inheritance for Custom CSS**:
    Build on the "Custom CSS Injection & User Theme Stylesheet Manager". Allow users to create custom themes that *inherit* from the 10 built-in themes (e.g., "Dracula"). This way, a user only needs to specify the CSS variables they want to override (like `--accent-color`), rather than rewriting an entire stylesheet.

8.  **Context-Aware Footnote Reordering**:
    Enhance the "Advanced Footnote & Endnote Cross-Referencing Engine". If a user cuts and pastes paragraphs with inline footnotes (e.g., moving `[^3]` before `[^1]`), automatically re-number the inline references and the bottom definition list sequentially upon the next render or export, keeping the document clean.

9.  **Granular Image Export Options per Format**:
    Improve the image handling in the multi-format pipeline. Allow users to define format-specific image settings—for instance, keep high-resolution PNGs for the PPTX and DOCX exports, but automatically downsample and convert images to WEBP or compress them significantly for the EPUB export to save space.

10. **Browser Extension "Extract Only" Mode**:
    Expand the Marksmith Connector browser extension. In addition to sending the AI's reply to Marksmith, add a mode to "Extract Code Blocks Only" or "Extract Diagrams Only". This would push only the Mermaid or code fences to the local API, instantly generating a focused PDF or DOCX of just the architecture diagram or code snippet, ignoring conversational filler.
