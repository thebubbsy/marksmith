# What's new in Marksmith v3.4.0

Marksmith v3.4.0 introduces major new document capabilities: rebuilt PDF import with four OCR engines, native Outlook email drafting and import, lossless Diagram Studio with full Mermaid sequence diagram lifelines and frames, strict-reader EPUB publishing, and interactive canvas rendering.

---

### 📄 Rebuilt PDF Import & Multi-Engine OCR
- **Structural Document Reconstruction**: Reads native PDF vector text layers with PdfPig and reconstructs hierarchical headings, paragraphs, bullet/numbered lists, complex tables, links, code blocks, and pictures rather than dumping unformatted text.
- **Four-Engine OCR Pipeline**: Scanned PDFs and image-only pages automatically route through OCR:
  1. **MarkSmith OCR**: A pure C# convolutional neural network (CNN) letter classifier, segmenter, touching-letter splitter, and SCOWL dictionary corrector running in-process with zero native dependencies.
  2. **PaddleOCR PP-OCRv5**: High-performance detection and Latin/English recognition powered by ONNX Runtime.
  3. **Windows Media OCR**: Native, hardware-accelerated Windows OCR API (`Windows.Media.Ocr`).
  4. **Tesseract 5**: Industrial-grade LSTM engine.
  5. **Automatic Mode**: Selects the best engine based on system hardware and language.

### ✉️ Outlook Email Workflow & .eml (Free on All Plans)
- **Outlook .msg & .eml Export**: Compile Markdown straight into native Microsoft Outlook `.msg` drafts or standard RFC-822 `.eml` files.
- **Live Email Preview**: Inspect your email rendering before handing off to Outlook.
- **Reverse Email Import**: Drag-and-drop or open `.msg` and `.eml` emails to instantly convert them into clean Markdown while preserving attachments.
- **Copy as Email**: One-click clipboard copy with multipart HTML and plaintext payloads.
- **Local Conversion API**: `/api/convert` now supports `format: "eml"`.

### 🎨 Diagram Studio & Vector Visuals
- **Mermaid Sequence Lifelines & Ordered Frames**: Renders sequence diagrams the way Mermaid specifies: participants across the top, dashed lifelines, dedicated message rows, self-call loops, note boxes, and activation bars.
- **Lossless Diagram Studio Saves**: Sequence blocks (`loop`, `alt`, `opt`, `par`, `critical`, `break`, `rect`) and autonumbering (`autonumber`, `autonumber 10 5`) are preserved in written order across visual edits and saves.
- **Grouped Layouts & Subgraphs**: Subgraphs, class diagram visibility, and ER cardinalities survive saves. Cycle-safe layout ranking prevents visual tangles.
- **Interactive Canvas (`:::canvas`)**: Live canvas rendering across preview, high-DPI PDF exports, and Word documents with strict SVG sanitization.
- **Shape Studio & SmartArt Studio**: Rotated shape handles with proportion locking, high-contrast labels, and keyboard-driven SmartArt outline editing.

### 📚 Strict-Reader EPUB Publishing
- Strict XHTML output, MathML equations, high-resolution diagram image rendering, visible code blocks, and stable book identities that prevent book collisions in reader libraries.

### 🛡️ Security & Reliability
- **Local API Hardening**: Defends against sandboxed browser iframe attacks by enforcing strict origin checks for `Origin: null`.
- **Atomic File Operations**: Safe temporary file handling during batch conversions and export failures.
- **Desktop Keyboard Usability**: Working Ctrl+B/Ctrl+I shortcuts, line-aware heading toggles, unified shortcut tables, and an integrated find bar.
