# Marksmith: Proof, Not Hype

We know the claims on our landing page sound a bit too good to be true. "Native editable equations" and "Word shapes from Mermaid diagrams" usually mean a tool is secretly just pasting a PNG image and calling it a day.

Here is the concrete proof that Marksmith does exactly what it says on the tin. We encourage you to try these repros yourself.

---

## Claim 1: Natively Editable Word Equations (Even Matrices)

**The Claim:** Marksmith converts LaTeX and KaTeX math into true, native Word OMML equations. This includes complex environments like matrices and piecewise functions, binomials, overbraces, limits, and fractions. 

**The Repro:**
1. Open Marksmith.
2. Paste the following LaTeX into the editor:
   ```latex
   \begin{pmatrix} a & b \\ c & d \end{pmatrix}
   ```
3. Export to Word (DOCX).
4. Open the document in Microsoft Word.

**The Proof:**
Click on the matrix. You will see Word's native "Equation" box outline appear. You can place your cursor next to the `a` and change it to an `x`. It is not an image; it is an OOXML `<m:oMath>` element generated locally on your machine.

---

## Claim 2: Mermaid Diagrams as Native Word Shapes (ShapeForge™)

**The Claim:** Marksmith converts *every* Mermaid diagram type (flowcharts, sequence, class, state, C4, sankey, mindmap, architecture, and more) into native, grouped Word shapes that you can click into, edit, and recolor.

**The Repro:**
1. Open Marksmith.
2. Paste the following Mermaid code:
   ```mermaid
   stateDiagram-v2
       [*] --> Still
       Still --> [*]
   ```
3. Export to Word (DOCX).
4. Open the document in Microsoft Word.

**The Proof:**
Click on the "Still" box. You will see Word's "Shape Format" ribbon appear. You can change the fill color, drag the box around, or edit the text. The diagram is built from native Word rectangles, lines, and text boxes, perfectly retaining Mermaid's actual fill and stroke colors. *(Note: Sankey flow ribbons are currently rendered as connection lines, and architecture-beta icon glyphs are not reproduced, but the structure, connectivity, text, and colors are fully intact).*

---

## Claim 3: 100% Local and Private (Zero Cloud)

**The Claim:** Marksmith runs entirely on your device and makes zero external network calls.

**The Repro:**
1. Turn off your Wi-Fi or unplug your ethernet cable.
2. Open Marksmith and paste in a complex ChatGPT response containing both math and diagrams.
3. Export it to PDF or Word.

**The Proof:**
The document exports instantly and perfectly. Marksmith bundles its own Mermaid and KaTeX rendering engines, so it never has to ping a cloud server to convert your data. Your proprietary work documents never leave your PC.

---

## Claim 4: Strips the "AI Tells" and Uses Your House Style

**The Claim:** Marksmith removes obvious AI artifacts like pseudo-headings, citation pips, and em-dash spray, replacing them with standard formatting that matches your company's style.

**The Repro:**
1. Ask ChatGPT a question that requires citations (e.g., using a web search).
2. Paste the result into Marksmith, complete with `[7]` citation pips and bolded text everywhere.
3. Apply a custom theme in Marksmith and export to PDF.

**The Proof:**
The output document contains a professional Cover Page and Table of Contents. The `[7]` pips are cleaned up, the typography matches your chosen font (e.g., Inter or Garamond), and the document looks like a human crafted it in a dedicated word processor.
