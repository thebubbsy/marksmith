# TEST INFRASTRUCTURE & ARCHITECTURE — NATIVE COLLAPSIBLE TOGGLES

## 1. Overview & Test Architecture

This document defines the test architecture, methodology, test categories, and test tier hierarchy for the **Native Collapsible Toggles** feature in Marksmith's OpenXML DOCX exporter.

### Architecture Under Test
- **Input Pipeline**: Markdown string parsing via `AdmonitionNormalizer` and `Markdig` pipeline.
- **Normalization Phase**: Transformation of `:::toggle [Title]` or `:::toggle Title` syntax into normalized container structures (`<details><summary>Title</summary>...`).
- **OpenXML Generation**: `DocxExportService` walking AST / HTML blocks into Word OpenXML elements (`<w:p>`, `<w:pPr>`, `<w:outlineLvl w:val="8"/>`, `<w15:collapsed w:val="1"/>`, and invisible heading styling).
- **Output Artifact**: Valid `.docx` package output containing native Word expandable/collapsible accordion paragraphs.

---

## 2. Test Design Methodology & Categories

We apply four formal testing design strategies to ensure complete coverage without bloat:

1. **Category-Partition Method**:
   - Inputs are partitioned into equivalence classes based on syntax form (`:::toggle [Title]`, `:::toggle Title`, `<details><summary>Title</summary>`).
   - Container states are partitioned (empty title, default title, custom title, empty body, multiline body).

2. **Boundary Value Analysis (BVA)**:
   - Zero-length title and missing title defaults.
   - Deeply nested toggle containers (depth = 0, 1, N).
   - Fenced code blocks (` ``` ` and ` ~~~ `) containing literal `:::` inside toggle bodies.
   - Empty body vs. multi-paragraph body.

3. **Pairwise Testing (Combinatorial)**:
   - Toggles combined with other Markdown AST nodes:
     - Toggles + Callouts / Admonitions (`> [!NOTE]` or `:::note`)
     - Toggles + Headings (`# Heading 1` .. `### Heading 3`)
     - Toggles + Bulleted/Numbered List items
     - Toggles + Tables and Code Blocks

4. **Real-World Workloads**:
   - Complex technical document containing multiple toggles, callout boxes, code blocks, tables, and lists.
   - Verification of generated `.docx` output at `test_outputs/sample_toggle.docx`.
   - Structural validation of the ZIP package `word/document.xml` using OpenXML DOM assertions.

---

## 3. Enumerated Features (from ORIGINAL_REQUEST.md)

| Feature ID | Description | Primary Verification Target |
|------------|-------------|----------------------------|
| **Feature 1** | `:::toggle [Title]` parsing & container conversion | `AdmonitionNormalizer` converts syntax to `<details><summary>Title</summary>` container |
| **Feature 2** | `<details><summary>Title</summary>` parsing & container conversion | `DocxExportService` handles HTML details/summary block tags into OpenXML collapsible paragraphs |
| **Feature 3** | Native OpenXML Paragraph creation (`<w:collapsed w:val="1"/>` & `<w:outlineLvl w:val="8"/>`) | Heading paragraph has `<w:outlineLvl w:val="8"/>` and `<w15:collapsed w:val="1"/>` in `<w:pPr>` |
| **Feature 4** | Invisible Heading Styling (11pt font, body text color, bold styling) | Run properties do not use large Heading 1 font; styled with body text color and bold |
| **Feature 5** | Sample output generation to `test_outputs/sample_toggle.docx` | Real-world test generates physical `.docx` at `test_outputs/sample_toggle.docx` |

---

## 4. Test Hierarchy (Tiers 1–4)

### Tier 1: Core Feature Coverage
- **T1-1**: `:::toggle [Title]` bracketed title syntax.
- **T1-2**: `:::toggle Title` unbracketed title syntax.
- **T1-3**: `<details><summary>Title</summary>` standard HTML syntax.

### Tier 2: Boundary & Corner Cases
- **T2-1**: Empty title handling (fallback to default "Toggle").
- **T2-2**: Empty body handling.
- **T2-3**: Nested toggles (toggle inside a toggle).
- **T2-4**: Special characters in title (HTML entities, brackets, quote marks, emojis).
- **T2-5**: Multiple sequential toggles.
- **T2-6**: Toggle containing code blocks or tables.

### Tier 3: Cross-Feature Combinations
- **T3-1**: Toggles containing callout boxes (`> [!NOTE]` or `:::note`).
- **T3-2**: Toggles containing headings (`# Heading 1`).
- **T3-3**: Toggles containing list items (`- bullet 1`, `1. item 1`).

### Tier 4: E2E & Real-World OpenXML DOM Assertions
- **T4-1**: End-to-end document generation to `test_outputs/sample_toggle.docx`.
- **T4-2**: Direct OpenXML DOM inspection verifying `ParagraphProperties.OutlineLevel`, `DefaultCollapsed`, and run font/color styling.
