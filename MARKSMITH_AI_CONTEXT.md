# `MARKSMITH_AI_CONTEXT.md`
**Target Audience:** AI Coding Assistants & Autonomous Agents  
**Project:** Marksmith (C# Markdown-to-DOCX Offline Compiler)  
**Directive:** Read this document before generating C#, Markdig extensions, or OpenXML code for the Marksmith repository. This document defines the architectural boundaries, OpenXML schema traps, and feature requirements to ensure zero-corruption `.docx` generation.

---

## 1. CORE ARCHITECTURE RECAP
Marksmith is an offline compiler that translates Markdown ASTs into native Office Open XML (OOXML). It does not use Microsoft Word Interop or COM. It operates directly on the Open Packaging Conventions (OPC) ZIP structure.

**Pipelines:**
1. **Pre-Parsing & Normalization:** `TextNormalizer` → `AdmonitionNormalizer` → `DialectNormalizer` → `DiagramFenceSniffer` → `DashReplacer` → `FormattingService`.
2. **AST Compilation:** Markdig AST + `AmbiguityDetector` (Heuristic layout resolution).
3. **Layout Engine:** ShapeForge (`DocxShapeEmitter`, `MermaidChartsRenderer`, `MermaidTreesRenderer`) & `LatexToOmml` (Math).
4. **Assembly:** `DocxExportService` & `ContrastGuard` (WCAG 2.1 enforcement).

---

## 2. AI GOVERNANCE & ANTI-CORRUPTION RULES
When writing code for Marksmith, you **MUST** adhere to the following OpenXML governance rules. Word's rendering engine is notoriously fragile; a single schema violation will result in a "File Corrupted" error.

### 🚫 Anti-Pattern 1: Hardcoded Relationship IDs (`rId`)
**DO NOT** hardcode `rId` values (e.g., `rId1`, `rId2`) in generated XML.
**ALWAYS** use a dynamic relationship manager. If you add an image, hyperlink, or footnote, you must register it in the corresponding `.rels` file and inject the returned ID into the XML.
```csharp
// CORRECT: Dynamic generation
string rId = package.MainDocumentPart.AddExternalRelationship(
    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", 
    new Uri(url)
).Id;
```

### 🚫 Anti-Pattern 2: Namespace Omissions
**DO NOT** emit XML elements without their proper namespace prefixes.
**ALWAYS** ensure the root `<w:document>` contains all necessary xmlns declarations.
*   `w:` (WordprocessingML)
*   `a:` (DrawingML)
*   `wp:` (DrawingML Wordprocessing)
*   `pic:` (Picture)
*   `m:` (Math / OMML)
*   `wpg:` (WordprocessingGroup)
*   `wps:` (WordprocessingShape)

### 🚫 Anti-Pattern 3: DOM-based Memory Bloat
**DO NOT** load the entire `document.xml` into an `XmlDocument` or `XDocument` if appending large ASTs.
**ALWAYS** use `OpenXmlWriter` (SAX-style writing) for the main document body to ensure Marksmith can compile 1,000+ page Markdown files with $O(1)$ memory overhead.

### 🚫 Anti-Pattern 4: Decoding Escaped HTML Inside Mermaid Fences (XSS Vulnerability)
When transforming `<pre><code class="language-mermaid">` into `<div class="mermaid">`, **DO NOT** `HtmlDecode` the text content. Mermaid reads `.textContent` directly, which the browser automatically decodes. Decoding before injection introduces XSS risks (e.g., `<img onerror=...>`).

---

## 3. ADVANCED FEATURE IMPLEMENTATION (The "Wow" Factor)

To make Marksmith a world-class compiler, AIs must implement the following advanced features using precise OpenXML structures.

### A. The OpenXML Numbering Trap (Lists)
Markdown lists (`- item` or `1. item`) cannot simply be wrapped in a `<w:list>` tag. OpenXML separates list *content* from list *definitions*.

**AI Directive for Lists:**
1. You must maintain a `numbering.xml` part.
2. Define a `<w:abstractNum>` (the template for the list: bullet vs. decimal, indents).
3. Define a `<w:num>` (an instance of the list) that points to the `abstractNumId`.
4. In `document.xml`, apply the `numId` and `ilvl` (indent level) to the paragraph properties.

```xml
<!-- In document.xml -->
<w:p>
  <w:pPr>
    <w:numPr>
      <w:ilvl w:val="0"/> <!-- Nesting level (0-based) -->
      <w:numId w:val="1"/> <!-- Points to numbering.xml -->
    </w:numPr>
  </w:pPr>
  <w:r><w:t>List Item</w:t></w:r>
</w:p>
```

### B. Image Scaling & DPI Math
Markdown images `![alt](image.png)` must not overflow the page.
**AI Directive for Images:**
1. Read the image headers (via SkiaSharp or ImageSharp) to get native Pixel Width/Height.
2. Convert Pixels to EMUs ($1 \text{ px} = 9525 \text{ EMUs}$, $1 \text{ pt} = 12700 \text{ EMUs}$).
3. Calculate maximum page width: `Page Width (EMUs) - Left Margin - Right Margin`.
4. If `Image EMU Width > Max Page Width`, calculate a scaling ratio and apply it to both `cx` and `cy` extents to maintain aspect ratio.

### C. Native Table of Contents (TOC) Generation
Users expect a TOC if they use `[[_TOC_]]` or if the document is long.
**AI Directive for TOC:**
Do not generate static text for the TOC. Emit a Word **Structured Document Tag (SDT)** containing a Field Code (`w:instrText`). Word will automatically populate it when the user opens the file.

```xml
<w:sdt>
  <w:sdtContent>
    <w:p>
      <w:r>
        <w:fldChar w:fldCharType="begin"/>
      </w:r>
      <w:r>
        <!-- TOC \o "1-3" = Show Headings 1 through 3 -->
        <w:instrText xml:space="preserve"> TOC \o "1-3" \h \z \u </w:instrText>
      </w:r>
      <w:r>
        <w:fldChar w:fldCharType="end"/>
      </w:r>
    </w:p>
  </w:sdtContent>
</w:sdt>
```
*Note: Marksmith must set `<w:updateFields w:val="true"/>` in `settings.xml` to force Word to calculate the TOC on first open.*

### D. Footnotes & Endnotes
Markdown footnotes `[^1]` must be compiled natively.
**AI Directive for Footnotes:**
1. Extract all footnote definitions from the Markdig AST.
2. Write them to `footnotes.xml` with unique `w:id` attributes.
3. In `document.xml`, replace the inline reference with:
```xml
<w:r>
  <w:rPr><w:vertAlign w:val="superscript"/></w:rPr>
  <w:footnoteReference w:id="2"/>
</w:r>
```

### E. Code Block Syntax Highlighting & Word Spellcheck Suppression (`NoProof`)
Code blocks must be syntax-highlighted and monospaced, but Microsoft Word will display ugly red spellcheck underlines under code tokens (`import`, `gfm`, `const`, `bytemd`).

**AI Directive for Code Blocks:**
1. Use case-insensitive dictionary lookup (`StringComparer.OrdinalIgnoreCase`) for language IDs (`l.Id` and `l.Name`).
2. Attach `<w:noProof/>` (`DocumentFormat.OpenXml.Wordprocessing.NoProof`) to every code block run property (`rPr`) and paragraph property (`pPr`).
3. Set `Consolas` monospace font on all runs.

```csharp
var rPr = new RunProperties(
    new RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
    new NoProof(),
    new Color { Val = hexColor }
);
```

---

## 4. DIAGRAM ENGINE: SHAPEFORGE & NATIVE DRAWINGML (`DocxShapeEmitter.cs`)

Marksmith features **ShapeForge**, an engine that converts pure geometric diagrams (`MDiagram`, `MShape`, `MConnector`) into native Word vector shapes (`<wpg:wgp>` containing `<wps:wsp>`). Users can select, ungroup, restyle, and edit text inside shapes in Word.

### Native Diagram Engines (`IMermaidRenderer`):
- **`MermaidChartsRenderer`**: Renders `pie`, `gantt`, `quadrantChart`, `xychart-beta` into native DrawingML shapes.
- **`MermaidTreesRenderer`**: Renders `mindmap`, `timeline`, `gitGraph`, `sankey`, `kanban`, `block-beta` into native DrawingML shapes.

### 9 Oversized Layout Modes (`ScaleToFit`):
- `Mode 0 (Ask)`: Default prompt mode.
- `Mode 1 (Exact Layout)`: Keeps exact layout computed by Dagre; triggers Word Web Layout mode if bounds exceed page margins.
- `Mode 2 (Reflow)`: Reflows layout.
- `Mode 3 (MultiPageVertical)`: Keeps scale, splits tall diagrams vertically across page height bands.
- `Mode 4 (Grid)`: Enlarges canvas by grid multiplier ($N \times$ room).
- `Mode 5 (ShrinkToFit)`: Drops scaling floor to 30% to fit on one page.
- `Mode 6 (Shrink Spacing)`: Shrinks node-to-node connector distance while preserving shape font sizes.
- `Mode 7 (Shrink Shapes)`: Shrinks shape dimensions while preserving spatial distance between nodes.
- `Mode 8 (Proportional Shrink)`: Shrinks both shapes and spacing proportionally.

---

## 5. WCAG 2.1 CONTRAST GUARD (`ContrastGuard.cs`)

To mathematically prevent illegible text colors against light or dark shading backgrounds, Marksmith enforces W3C WCAG 2.1 relative luminance and contrast ratio calculations.

### Formulas:
Relative Luminance ($L$):
$$L = 0.2126 R + 0.7152 G + 0.0722 B$$

Where $C_{linear} = \frac{C}{12.92}$ if $C \le 0.03928$, else $\left(\frac{C + 0.055}{1.055}\right)^{2.4}$.

Contrast Ratio:
$$\text{Ratio} = \frac{\max(L_1, L_2) + 0.05}{\min(L_1, L_2) + 0.05}$$

**Hard Rule:** If contrast ratio is below **4.5:1** (WCAG AA), `ContrastGuard.EnsureLegibleText()` forcibly adjusts the text color to `#FFFFFF` or `#121212`.

---

## 6. CORPORATE THEMING & TEMPLATE INJECTION

Marksmith is designed for enterprise use. Users will provide a corporate `.dotx` or `.docx` template containing custom fonts, headers, footers, and styles.

**AI Directive for Template Handling:**
1. **DO NOT** generate a blank document from scratch if a `--template` argument is provided.
2. **DO** copy the template file to the output destination first.
3. Open the copied ZIP archive.
4. Locate the `w:body` tag in `word/document.xml`.
5. **Inject** the compiled Markdown OpenXML nodes *at the end* of the existing body (or replace a specific placeholder like `{{MARKSMITH_CONTENT}}`).
6. **Map Styles:** If the Markdown AST contains a `# Heading 1`, do not hardcode font sizes. Emit `<w:pStyle w:val="Heading1"/>`. Word will automatically inherit the font and color from the corporate template's `styles.xml`.

---

## 7. TESTING & VALIDATION PROTOCOLS

When writing unit tests for Marksmith features, AIs must follow this validation flow:

1. **AST Verification:** Assert that Markdig correctly parsed the specific Markdown dialect.
2. **XML Emission Verification:** Assert that the generated XML string contains the exact required tags (e.g., `<w:tbl>`, `<m:oMath>`).
3. **OPC Validation:** (Crucial) Use the `DocumentFormat.OpenXml.Validation.OpenXmlValidator` class in unit tests to ensure the generated document complies with the ECMA-376 standard.

```csharp
// AI MUST INCLUDE THIS IN EXPORT TESTS
var validator = new OpenXmlValidator();
var errors = validator.Validate(wordDocument);
Assert.Empty(errors); // Zero-corruption guarantee
```

**END OF DIRECTIVE.**
