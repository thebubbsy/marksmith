# The Proof: Test Marksmith Yourself

We make very specific claims about what Marksmith can do, but we don't want you to take our word for it. 

Below, you'll find the exact steps to verify each feature on your own machine in about 30 seconds. No tricks, no hidden network calls, just the raw output. Here is how to test our native Word equations, fully editable diagrams, offline processing, and document restyling.

(And further down, we list exactly what the app does *not* do yet. Honesty saves everyone's time.)

---

## 1. Editable Word Equations

When an AI gives you LaTeX or KaTeX, pasting it into Microsoft Word usually results in broken slashes or static images. Marksmith translates it directly into Word's native OMML format.

**Before:** Raw `\sum_{i=1}^{n} i^2` or a broken picture of an equation.  
**After:** A real, clickable, native Cambria Math equation in Word. 

**Try it yourself in 30 seconds:**
1. Open Marksmith.
2. Paste this text: `Here is a sum: \sum_{i=1}^{n} i^2`
3. Click **Export DOCX**.
4. Open the generated file in Word. Click on the equation—you can edit the variables, limits, and structure using Word's own native equation tools. It's the math and diagrams AI actually produces, fully integrated into your document.

## 2. Editable Word Diagrams

Mermaid diagrams from AI usually have to be exported as flat PNGs. Marksmith redraws them as native, editable Word shape groups, matching the actual colors of the diagram. We support every Mermaid diagram type (flowchart, sequence, class, ER, state, C4, block, kanban, packet, sankey, gantt, pie, mindmap, timeline, journey, gitgraph, quadrant, xychart, requirement, architecture).

**Before:** A static image where fixing a single typo means regenerating the whole chart.  
**After:** A grouped set of native Word shapes (boxes, arrows, wedges) that you can individually recolor, move, or edit text inside.

**Try it yourself in 30 seconds:**
1. In Marksmith, paste a standard Mermaid flowchart snippet.
2. Click **Export DOCX**.
3. Open the file in Word. Click on any box or arrow in the diagram. You can drag the shapes, change the text, or re-theme them natively in Word.

## 3. Strip the AI Tells (Your House Style)

AI assistants leave behind recognizable formatting quirks: em-dash spray, citation pips like `[7]`, and pseudo-headings. Marksmith isn't designed to "beat" AI detectors—it's designed to make the document look like *your* house style, ready to send.

**Before:** A document that screams "I copied this from a chatbot," complete with `【7†source】` and heavy, relentless bolding.  
**After:** A clean, professional document using your chosen theme, with AI artifacts transparently normalized and removed.

**Try it yourself in 30 seconds:**
1. Copy a messy response directly from ChatGPT (ensure it has some citation pips or excessive bolding).
2. Paste it into Marksmith and toggle on **Normalize AI quirks**.
3. Export. The resulting document is clean, the quirks are gone, and a Word comment explicitly lists the changes made so nothing happens silently. 

## 4. 100% Local and Offline

Many tools claim privacy but still phone home to render math or diagrams. Marksmith runs entirely on-device and makes zero external network calls. Mermaid, KaTeX, and fonts are all bundled in the Windows 11 app.

**Before:** Uploading sensitive company data to a third-party server just to get a PDF.  
**After:** Processing strictly on your local hardware.

**Try it yourself in 30 seconds:**
1. Turn off your Wi-Fi or unplug your Ethernet cable.
2. Open Marksmith, paste in a complex Markdown document with equations and Mermaid charts.
3. Export to PDF or DOCX. It works instantly and flawlessly. 

---

## What it does NOT do yet

We believe in being upfront about our technical limitations so you know exactly what you are getting.

- **Sankey flow-ribbon widths:** While we reproduce the structure, color, connectivity, and text of Sankey diagrams perfectly in Word, we cannot currently reproduce the exact variable ribbon widths. 
- **Architecture diagram icon glyphs:** For Mermaid architecture diagrams, the structural layout and text are fully converted to editable shapes, but the specific icon glyphs are not yet reproduced. 
- **Platform support:** Marksmith is strictly for **Windows 11 only**. We do not support Windows 10, Mac, or Linux. 

*(Note: The free tier of Marksmith handles all PDF exports. Exporting to DOCX, PPTX, and EPUB, along with the Branding Kit, is part of Marksmith Pro—a one-time purchase.)*
