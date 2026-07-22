# Marksmith Engine Governance

This document serves as the architectural map and governance guide for future autonomous agents modifying the Marksmith engine.

## The Markdown Pipeline

Marksmith's rendering engine executes a linear pipeline to convert raw Markdown into native OpenXML (Word/PDF):

1. **Pre-Processing (TextNormalizer, AdmonitionNormalizer, etc.)**: Raw markdown text is cleaned up and converted into a standard format (e.g., standardizing CRLF to LF, converting Obsidian callouts to standard alerts).
2. **Markdig AST Generation (MarkdownPipelineBuilder)**: The pre-processed markdown string is fed into Markdig, which builds an Abstract Syntax Tree (AST) representing blocks and inline elements.
3. **Block-Level Dispatch (RenderBlock)**: The AST is traversed. High-level blocks (Headings, CodeBlocks, Math, Tables, Alerts) are converted into their respective OpenXML Paragraphs or Tables.
4. **Inline-Level Dispatch (RenderInlines)**: Inside text blocks, inline elements (Emphasis, Code, Links, Math) are parsed into OpenXML Runs.
5. **HTML Dispatch (RenderHtmlBlock)**: Custom HTML tags (like <details>, <mark>, or structural features) are specially parsed.
6. **Output**: The OpenXML document is packaged with styles and numbering.

## Architectural Guidelines for Agents

When implementing new Markdown features or resolving bugs, follow these governance rules:

- **Do NOT modify Markdig directly**: Marksmith relies on Markdig's AST. If a construct isn't natively supported, add a pre-processor regex in the PREPROCESS stage to convert it to a supported format before it reaches Markdig, OR intercept it in the DISPATCH stage.
- **Maintain OpenXML Fidelity**: Output should always be native OpenXML (w:p, w:r, w:drawing) wherever possible. Avoid falling back to rasterized images unless absolutely necessary (e.g., as a fallback for complex SVGs).
- **Ambiguity Detection**: If a markdown construct has multiple valid visual representations (e.g., an ASCII table vs a plain text block), do NOT guess. Use the AmbiguityDetector to flag the construct, and provide RenderOptions that the user can select via the AmbiguityResolverDialog.
- **Global Settings vs Per-Document**: User preferences for ambiguity resolution are stored globally in AppSettings.AmbiguityPreferences. Always respect these preferences in DocxExportService when making decisions on how to render flagged AST nodes.

