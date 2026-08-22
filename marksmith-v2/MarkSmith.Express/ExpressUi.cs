using System;

namespace MarkSmith.Express;

public static class ExpressUi
{
    public const string Html = """
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Marksmith Express — Universal Markdown &amp; Office Converter</title>
  <style>
    :root {
      --bg: #090d16;
      --card-bg: #111827;
      --card-border: #1f2937;
      --input-bg: #0b1120;
      --accent: #38bdf8;
      --accent-hover: #0ea5e9;
      --accent-grad: linear-gradient(135deg, #38bdf8 0%, #6366f1 100%);
      --text: #f3f4f6;
      --text-muted: #9ca3af;
      --border: #374151;
      --success: #10b981;
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
      background-color: var(--bg);
      color: var(--text);
      line-height: 1.5;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
    }
    header {
      border-bottom: 1px solid var(--card-border);
      background: rgba(17, 24, 39, 0.8);
      backdrop-filter: blur(8px);
      padding: 0.875rem 1.5rem;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      font-weight: 700;
      font-size: 1.15rem;
      letter-spacing: -0.02em;
    }
    .brand-badge {
      background: var(--accent-grad);
      color: #fff;
      font-size: 0.7rem;
      font-weight: 800;
      padding: 0.2rem 0.5rem;
      border-radius: 9999px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .status-badge {
      display: flex;
      align-items: center;
      gap: 0.4rem;
      font-size: 0.8rem;
      color: var(--text-muted);
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      padding: 0.3rem 0.75rem;
      border-radius: 9999px;
    }
    .status-dot {
      width: 7px;
      height: 7px;
      background-color: var(--success);
      border-radius: 50%;
      box-shadow: 0 0 8px var(--success);
    }
    main {
      flex: 1;
      max-width: 1280px;
      width: 100%;
      margin: 0 auto;
      padding: 1.5rem;
      display: grid;
      grid-template-columns: 1fr 340px;
      gap: 1.5rem;
    }
    @media (max-width: 900px) {
      main { grid-template-columns: 1fr; }
    }
    .card {
      background: var(--card-bg);
      border: 1px solid var(--card-border);
      border-radius: 12px;
      padding: 1.25rem;
      display: flex;
      flex-direction: column;
      gap: 1rem;
    }
    .card-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .card-title {
      font-size: 0.95rem;
      font-weight: 600;
      color: var(--text);
    }
    .editor-container {
      position: relative;
      flex: 1;
      display: flex;
      flex-direction: column;
    }
    textarea {
      width: 100%;
      min-height: 480px;
      flex: 1;
      background: var(--input-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      color: #f8fafc;
      font-family: "Cascadia Code", "Fira Code", Consolas, "Courier New", monospace;
      font-size: 0.9rem;
      line-height: 1.6;
      padding: 1rem;
      resize: vertical;
      outline: none;
      transition: border-color 0.2s;
    }
    textarea:focus {
      border-color: var(--accent);
      box-shadow: 0 0 0 2px rgba(56, 189, 248, 0.2);
    }
    .editor-toolbar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-top: 0.5rem;
      font-size: 0.8rem;
      color: var(--text-muted);
    }
    .btn-group {
      display: flex;
      gap: 0.5rem;
    }
    .btn-sm {
      background: #1e293b;
      border: 1px solid var(--border);
      color: var(--text);
      font-size: 0.75rem;
      padding: 0.35rem 0.65rem;
      border-radius: 6px;
      cursor: pointer;
      transition: all 0.2s;
    }
    .btn-sm:hover {
      background: #334155;
      border-color: var(--accent);
    }
    .option-group {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
    }
    .option-label {
      font-size: 0.8rem;
      font-weight: 600;
      color: var(--text-muted);
      text-transform: uppercase;
      letter-spacing: 0.05em;
    }
    .format-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 0.5rem;
    }
    .format-btn {
      background: #1e293b;
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 0.75rem 0.5rem;
      color: var(--text);
      text-align: center;
      cursor: pointer;
      font-size: 0.85rem;
      font-weight: 500;
      transition: all 0.2s;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 0.25rem;
    }
    .format-btn:hover {
      border-color: var(--accent);
      background: #273549;
    }
    .format-btn.active {
      border-color: var(--accent);
      background: rgba(56, 189, 248, 0.15);
      color: var(--accent);
      font-weight: 700;
    }
    .format-icon {
      font-size: 1.25rem;
    }
    select {
      width: 100%;
      background: #1e293b;
      border: 1px solid var(--border);
      color: var(--text);
      padding: 0.6rem 0.75rem;
      border-radius: 8px;
      font-size: 0.85rem;
      outline: none;
      cursor: pointer;
    }
    select:focus {
      border-color: var(--accent);
    }
    .checkbox-item {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-size: 0.85rem;
      color: var(--text);
      cursor: pointer;
      user-select: none;
    }
    .checkbox-item input {
      accent-color: var(--accent);
      cursor: pointer;
      width: 16px;
      height: 16px;
    }
    .cta-btn {
      background: var(--accent-grad);
      color: #fff;
      border: none;
      border-radius: 8px;
      padding: 0.9rem 1.25rem;
      font-size: 1rem;
      font-weight: 700;
      cursor: pointer;
      transition: transform 0.15s, opacity 0.2s;
      display: flex;
      align-items: center;
      justify-content: center;
      gap: 0.5rem;
      box-shadow: 0 4px 14px rgba(56, 189, 248, 0.3);
    }
    .cta-btn:hover {
      opacity: 0.95;
      transform: translateY(-1px);
    }
    .cta-btn:active {
      transform: translateY(1px);
    }
    .cta-btn:disabled {
      opacity: 0.5;
      cursor: not-allowed;
      transform: none;
    }
    .api-box {
      background: var(--input-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 0.75rem;
      font-family: monospace;
      font-size: 0.75rem;
      color: #38bdf8;
      overflow-x: auto;
      white-space: pre-wrap;
    }
    .dropzone-overlay {
      position: absolute;
      top: 0; left: 0; right: 0; bottom: 0;
      background: rgba(56, 189, 248, 0.15);
      border: 2px dashed var(--accent);
      border-radius: 8px;
      display: none;
      align-items: center;
      justify-content: center;
      font-size: 1.25rem;
      font-weight: 700;
      color: var(--accent);
      pointer-events: none;
    }
    footer {
      border-top: 1px solid var(--card-border);
      padding: 1rem;
      text-align: center;
      font-size: 0.8rem;
      color: var(--text-muted);
    }
  </style>
</head>
<body>
  <header>
    <div class="brand">
      <span>⚡ Marksmith Express</span>
      <span class="brand-badge">Cross-Platform</span>
    </div>
    <div class="status-badge">
      <span class="status-dot"></span>
      <span id="apiStatus">Local API Online</span>
    </div>
  </header>

  <main>
    <!-- Left: Input Editor -->
    <div class="card">
      <div class="card-header">
        <span class="card-title">Markdown Input</span>
        <div class="btn-group">
          <button class="btn-sm" onclick="pasteClipboard()">📋 Paste</button>
          <button class="btn-sm" onclick="loadSample()">📄 Load Sample</button>
          <button class="btn-sm" onclick="clearEditor()">🗑️ Clear</button>
        </div>
      </div>

      <div class="editor-container" id="dropArea">
        <textarea id="markdownInput" placeholder="Paste your Markdown here, or drag &amp; drop a .md file..."></textarea>
        <div class="dropzone-overlay" id="dropOverlay">Drop Markdown file here</div>
      </div>

      <div class="editor-toolbar">
        <span id="statCounts">0 words • 0 characters</span>
        <span>Drag &amp; drop supported</span>
      </div>
    </div>

    <!-- Right: Options & CTA -->
    <div class="card">
      <div class="card-header">
        <span class="card-title">Output Options</span>
      </div>

      <div class="option-group">
        <label class="option-label">Format</label>
        <div class="format-grid">
          <div class="format-btn active" data-format="docx" onclick="selectFormat(this)">
            <span class="format-icon">📄</span>
            <span>Word (.docx)</span>
          </div>
          <div class="format-btn" data-format="html" onclick="selectFormat(this)">
            <span class="format-icon">🌐</span>
            <span>HTML (.html)</span>
          </div>
          <div class="format-btn" data-format="pptx" onclick="selectFormat(this)">
            <span class="format-icon">📊</span>
            <span>Slides (.pptx)</span>
          </div>
          <div class="format-btn" data-format="epub" onclick="selectFormat(this)">
            <span class="format-icon">📚</span>
            <span>eBook (.epub)</span>
          </div>
        </div>
      </div>

      <div class="option-group">
        <label class="option-label">Theme / Styling</label>
        <select id="themeSelect">
          <option value="Modern Clean" selected>Modern Clean</option>
          <option value="Academic Formal">Academic Formal</option>
          <option value="Corporate Executive">Corporate Executive</option>
          <option value="Dark Mode">High-Contrast Dark</option>
          <option value="Minimalist">Minimalist</option>
          <option value="Engineering Spec">Engineering Specification</option>
        </select>
      </div>

      <div class="option-group">
        <label class="option-label">Enhancements</label>
        <label class="checkbox-item">
          <input type="checkbox" id="chkSmartArt" checked>
          <span>Native SmartArt &amp; Flowcharts</span>
        </label>
        <label class="checkbox-item">
          <input type="checkbox" id="chkMath" checked>
          <span>Native OMML Math (LaTeX)</span>
        </label>
        <label class="checkbox-item">
          <input type="checkbox" id="chkCallouts" checked>
          <span>GitHub Alert Callouts</span>
        </label>
      </div>

      <button class="cta-btn" id="convertBtn" onclick="convertAndDownload()">
        <span>⚡ Convert &amp; Download</span>
      </button>

      <div class="option-group" style="margin-top: 0.5rem;">
        <label class="option-label">Developer REST API</label>
        <div class="api-box" id="apiSnippet">curl -X POST http://localhost:PORT/api/convert \
  -H "Content-Type: application/json" \
  -d '{"markdown":"# Hello","format":"docx"}' \
  -o document.docx</div>
      </div>
    </div>
  </main>

  <footer>
    Marksmith Express v2.18.0 — Cross-Platform Universal Markdown Converter &bull; For the full IDE experience, use Marksmith Studio for Windows.
  </footer>

  <script>
    let selectedFormat = 'docx';
    const textarea = document.getElementById('markdownInput');
    const statCounts = document.getElementById('statCounts');
    const convertBtn = document.getElementById('convertBtn');
    const dropArea = document.getElementById('dropArea');
    const dropOverlay = document.getElementById('dropOverlay');

    // Update word and character counts
    textarea.addEventListener('input', updateStats);

    function updateStats() {
      const text = textarea.value;
      const chars = text.length;
      const words = text.trim() ? text.trim().split(/\s+/).length : 0;
      statCounts.textContent = `${words.toLocaleString()} words • ${chars.toLocaleString()} characters`;
    }

    function selectFormat(el) {
      document.querySelectorAll('.format-btn').forEach(b => b.classList.remove('active'));
      el.classList.add('active');
      selectedFormat = el.getAttribute('data-format');
      updateApiSnippet();
    }

    function updateApiSnippet() {
      const port = window.location.port || '5000';
      const host = window.location.hostname || 'localhost';
      document.getElementById('apiSnippet').textContent = 
`curl -X POST http://${host}:${port}/api/convert \\
  -H "Content-Type: application/json" \\
  -d '{"markdown":"# Hello","format":"${selectedFormat}"}' \\
  -o output.${selectedFormat}`;
    }

    async function pasteClipboard() {
      try {
        const text = await navigator.clipboard.readText();
        if (text) {
          textarea.value = text;
          updateStats();
        }
      } catch (err) {
        alert('Could not access clipboard directly. Please use Ctrl+V / Cmd+V in the box.');
      }
    }

    function clearEditor() {
      textarea.value = '';
      updateStats();
    }

    function loadSample() {
      textarea.value = `# Executive Summary & Strategic Proposal

> [!NOTE]
> This document was automatically compiled using **Marksmith Express** with native Office OpenXML components.

## 1. Key Performance Indicators

The quarterly growth model follows the standard exponential compounding rate:

$$P(t) = P_0 \\cdot e^{r \\cdot t} + \\int_0^t \\lambda(\\tau) \\, d\\tau$$

| Quarter | Projected Revenue | Actual Yield | Growth (%) | Status |
| :--- | :--- | :--- | :--- | :--- |
| **Q1 2026** | $1,250,000 | $1,420,000 | +13.6% | Complete |
| **Q2 2026** | $1,500,000 | $1,680,000 | +12.0% | Complete |
| **Q3 2026** | $1,800,000 | $1,950,000 | +8.3% | Target |
| **Q4 2026** | $2,200,000 | $2,450,000 | +11.4% | Projected |

## 2. Process Architecture

:::workflow "Production Pipeline"
[Intake] -> [Parse AST] -> [Transpile OMML] -> [OpenXML Packaging] -> [Delivery]
:::

* 100% native Word DrawingML vector graphics
* Real editable OMML equations (no raster images)
* Full typography & accessible table formatting
`;
      updateStats();
    }

    // Drag & Drop
    ['dragenter', 'dragover'].forEach(eventName => {
      dropArea.addEventListener(eventName, (e) => { e.preventDefault(); dropOverlay.style.display = 'flex'; }, false);
    });
    ['dragleave', 'drop'].forEach(eventName => {
      dropArea.addEventListener(eventName, (e) => { e.preventDefault(); dropOverlay.style.display = 'none'; }, false);
    });
    dropArea.addEventListener('drop', (e) => {
      const dt = e.dataTransfer;
      const files = dt.files;
      if (files.length > 0) {
        const reader = new FileReader();
        reader.onload = (event) => {
          textarea.value = event.target.result;
          updateStats();
        };
        reader.readAsText(files[0]);
      }
    });

    async function convertAndDownload() {
      const markdown = textarea.value.trim();
      if (!markdown) {
        alert('Please enter or paste some Markdown content first.');
        return;
      }

      convertBtn.disabled = true;
      convertBtn.innerHTML = '<span>⏳ Converting...</span>';

      try {
        const theme = document.getElementById('themeSelect').value;
        const payload = {
          markdown: markdown,
          format: selectedFormat,
          theme: theme
        };

        const res = await fetch('/api/convert', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload)
        });

        if (!res.ok) {
          const err = await res.json().catch(() => ({ error: res.statusText }));
          throw new Error(err.error || `HTTP ${res.status}`);
        }

        const blob = await res.blob();
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = url;
        a.download = `document.${selectedFormat}`;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(url);
      } catch (ex) {
        alert('Conversion error: ' + ex.message);
      } finally {
        convertBtn.disabled = false;
        convertBtn.innerHTML = '<span>⚡ Convert &amp; Download</span>';
      }
    }

    updateApiSnippet();
  </script>
</body>
</html>
""";
}
