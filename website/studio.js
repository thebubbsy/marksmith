/**
 * MarkSmith Interactive Document Gauntlet & Live Playground
 * Fast, rock-solid, client-side Markdown preview with KaTeX, Mermaid,
 * GitHub callout alerts, AI paste sanitization, and instant verified DOCX downloads.
 */

(() => {
  'use strict';

  // 1. Curated Gauntlet Scenarios
  const SCENARIOS = {
    math: {
      title: 'LaTeX & OMML Math',
      badge: '<i class="fas fa-circle-check"></i> ISO/IEC 29500-1 Verified',
      desc: 'Compiles LaTeX directly into Microsoft Word native OMML equations. Every symbol, matrix, and integral is editable in Word with built-in Equation Tools.',
      downloadFile: 'media/quantum-mechanics.docx',
      downloadName: 'quantum-mechanics.docx',
      markdown: `# Quantum Mechanics & Linear Transformations

The transformation operator $\\mathbf{M}$ acts upon the state vector $|\\psi\\rangle$ in Hilbert space:

$$
\\mathbf{M} = \\begin{bmatrix}
\\frac{1}{\\sqrt{2}} & \\frac{1}{\\sqrt{2}} \\\\
\\frac{1}{\\sqrt{2}} & -\\frac{1}{\\sqrt{2}}
\\end{bmatrix}
$$

The characteristic polynomial and eigenvalue equation are derived as:

$$
\\det(\\mathbf{A} - \\lambda \\mathbf{I}) = \\lambda^2 - \\text{tr}(\\mathbf{A})\\lambda + \\det(\\mathbf{A}) = 0
$$

The probability density satisfies normalization across all bounds:

$$
\\int_{-\\infty}^{+\\infty} |\\Psi(x, t)|^2 \\, dx = 1
$$

> [!NOTE]
> MarkSmith compiles the LaTeX math above into **native Microsoft Word OMML equations**. When opened in Word, every symbol and matrix entry can be edited directly using Word's built-in Equation Tools. Zero rasterization.`
    },

    diagram: {
      title: 'Mermaid & ShapeForge',
      badge: '<i class="fas fa-circle-check"></i> ECMA-376 DrawingML Verified',
      desc: 'Translates Mermaid code fences into native grouped vector DrawingML shapes. Ungroup and style individual shapes directly inside Word.',
      downloadFile: 'media/enterprise-architecture.docx',
      downloadName: 'enterprise-architecture.docx',
      markdown: `# Enterprise Distributed Architecture

The MarkSmith compiler transforms Markdown ASTs into native Office OpenXML packages with zero COM dependency:

\`\`\`mermaid
flowchart LR
    A[Raw AI Prompt] --> B{MarkSmith Compiler}
    B -->|Native OMML| C[Editable Word Equations]
    B -->|ShapeForge| D[Vector DrawingML Shapes]
    B -->|ContrastGuard| E[WCAG 2.1 AA Document]
\`\`\`

Unlike legacy tools that embed blurry raster images, MarkSmith translates Mermaid code fences into **native grouped vector shapes**:

> [!TIP]
> In Microsoft Word, you can click on each diagram box, change its fill color, drag connector lines, and edit label text. Real Office DrawingML vectors.`
    },

    aiclean: {
      title: 'AI Paste Sanitizer',
      badge: '<i class="fas fa-wand-magic-sparkles"></i> AI Normalizer Active',
      desc: 'Automatically detects AI output, strips citation pips ([12†source]), converts \\( \\) and \\[ \\] math delimiters, and sanitizes leaked tokens.',
      downloadFile: 'media/chatgpt-export-with-marksmith.docx',
      downloadName: 'chatgpt-export-with-marksmith.docx',
      downloadRawFile: 'media/chatgpt-export-without-marksmith.docx',
      downloadRawName: 'chatgpt-export-without-marksmith.docx',
      markdown: `# Understanding Gradient Descent

Great question! Here's a clear walkthrough of how gradient descent actually works 【12†source】.

**The core idea:** we want to minimise a loss function \\( L(\\theta) \\) by repeatedly stepping in the direction that reduces it fastest — the negative gradient. Each update looks like:

\\[
\\theta_{t+1} = \\theta_t - \\eta \\nabla_\\theta L(\\theta_t)
\\]

where \\( \\eta \\) is the **learning rate**.

### A minimal implementation

\`\`\`python
import numpy as np

def gradient_descent(grad, theta, lr=0.1, steps=100):
    for _ in range(steps):
        theta = theta - lr * grad(theta)
    return theta
\`\`\`
Copy code

### Choosing the learning rate 【3†source】
- Too **small** → training crawls and may never converge in reasonable time
- Too **large** → the loss oscillates or diverges entirely :contentReference[oaicite:0]{index=0}
- A good default is to start around \`1e-3\` and tune from there

### Variants worth knowing

| Variant | What changes | Good for |
| :--- | :--- | :--- |
| SGD | one sample per step | large datasets |
| Momentum | accumulates past gradients | ravines / plateaus |
| Adam | per-parameter adaptive rates | most deep-learning defaults |

ChatGPT can make mistakes. Check important info.`
    },

    report: {
      title: 'Tables & Callouts',
      badge: '<i class="fas fa-circle-check"></i> Strict Schema Compliant',
      desc: 'Compiles complex Markdown tables into strict ISO/IEC 29500-1 table structures with borders, zebra striping, and cell padding.',
      downloadFile: 'media/compliance-audit.docx',
      downloadName: 'compliance-audit.docx',
      markdown: `# Platform Performance & Compliance Audit

| Feature Area | Legacy Markdown Converters | MarkSmith Studio | Schema Compliance |
| :--- | :--- | :--- | :--- |
| **Mathematical Typesetting** | Static PNG / MathJax | **Native OMML Equations** | ISO/IEC 29500-1 |
| **Diagram Generation** | Blurry Bitmaps | **ShapeForge™ DrawingML Shapes** | ECMA-376 Part 1 |
| **Enterprise Governance** | Unmonitored Web Paste | **Client-Side Secret Masking** | Localhost Air-Gapped |
| **Document Integrity** | Frequent XML Corruptions | **Zero Corruption Guarantee** | Strict OOXML Validation |

> [!IMPORTANT]
> **Enterprise DLP Guarantee**: All processing in MarkSmith runs strictly offline. Trade secrets, financial tables, and code never touch third-party servers.

> [!WARNING]
> Legacy tools often emit mismatched \`<w:rId>\` relationships that trigger Microsoft Word "File Corrupted" repair dialogs upon opening.`
    }
  };

  // DOM Elements
  const tabs = document.querySelectorAll('.gauntlet-tab');
  const badgePill = document.getElementById('scenario-badge-pill');
  const descText = document.getElementById('scenario-desc-text');
  const btnDownload = document.getElementById('btn-scenario-download');
  const btnDownloadRaw = document.getElementById('btn-scenario-download-raw');
  const btnCopy = document.getElementById('btn-copy-markdown');
  const aiCleanBanner = document.getElementById('ai-clean-banner');
  const textarea = document.getElementById('gauntlet-markdown-input');
  const renderedHtml = document.getElementById('gauntlet-rendered-html');
  const wordCountDisplay = document.getElementById('editor-word-count');
  const insertButtons = document.querySelectorAll('.btn-insert-tag');

  let currentScenarioKey = 'math';
  let renderDebounceTimer = null;

  // Initialize Mermaid with Dark Theme
  if (window.mermaid) {
    try {
      window.mermaid.initialize({
        startOnLoad: false,
        theme: 'dark',
        themeVariables: {
          darkMode: true,
          background: '#090d14',
          primaryColor: '#1e293b',
          primaryTextColor: '#f8fafc',
          primaryBorderColor: '#38bdf8',
          lineColor: '#0ea5e9',
          secondaryColor: '#0f172a',
          tertiaryColor: '#1e1b4b'
        },
        securityLevel: 'loose'
      });
    } catch (e) {
      console.warn('Mermaid initialization note:', e);
    }
  }

  // HTML Escape Helper
  function escapeHtml(str) {
    return str.replace(/&/g, '&amp;')
              .replace(/</g, '&lt;')
              .replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;')
              .replace(/'/g, '&#039;');
  }

  // KaTeX Math Transpiler Helper
  function renderKaTeX(tex, isDisplay) {
    if (window.katex && typeof window.katex.renderToString === 'function') {
      try {
        return window.katex.renderToString(tex, {
          displayMode: isDisplay,
          throwOnError: false,
          output: 'htmlAndMathml'
        });
      } catch (err) {
        console.warn('KaTeX renderToString error:', err);
        return `<span class="katex-error" title="${escapeHtml(err.message)}">${escapeHtml(tex)}</span>`;
      }
    }
    const escaped = escapeHtml(tex);
    return isDisplay
      ? `<div class="studio-math-display"><span class="katex-fallback">$$${escaped}$$</span></div>`
      : `<span class="studio-math-inline"><span class="katex-fallback">$${escaped}$</span></span>`;
  }

  // Fast Markdown AST Parser & Renderer with First-Class KaTeX
  function renderMarkdown(md) {
    if (!md || !md.trim()) {
      return '<p style="color: #64748b; font-style: italic;">No content. Enter Markdown in the editor...</p>';
    }

    let html = md;

    // 1. Extract code blocks & Mermaid diagrams FIRST
    const codeBlocks = [];
    html = html.replace(/```(mermaid|[\w-]+)?\r?\n([\s\S]*?)```/g, (match, lang, code) => {
      const id = 'CODE_BLOCK_' + codeBlocks.length;
      if (lang === 'mermaid') {
        codeBlocks.push({ id, type: 'mermaid', content: code.trim() });
      } else {
        codeBlocks.push({ id, type: 'code', content: escapeHtml(code.trim()) });
      }
      return `\n\n<!--${id}-->\n\n`;
    });

    // 2. Protect literal escaped dollar signs (\$50)
    html = html.replace(/\\(\$)/g, '<!--ESCAPED_DOLLAR-->');

    // 3. Extract Display Math: $$ ... $$ and \[ ... \]
    const displayMathBlocks = [];
    html = html.replace(/\$\$([\s\S]*?)\$\$/g, (match, tex) => {
      const id = 'MATH_DISP_' + displayMathBlocks.length;
      displayMathBlocks.push({ id, tex: tex.trim() });
      return `\n\n<!--${id}-->\n\n`;
    });
    html = html.replace(/\\\[([\s\S]*?)\\\]/g, (match, tex) => {
      const id = 'MATH_DISP_' + displayMathBlocks.length;
      displayMathBlocks.push({ id, tex: tex.trim() });
      return `\n\n<!--${id}-->\n\n`;
    });

    // 4. Extract Inline Math: \( ... \) and $ ... $
    const inlineMathBlocks = [];
    html = html.replace(/\\\(([\s\S]*?)\\\)/g, (match, tex) => {
      if (!tex.trim()) return match;
      const id = 'MATH_INLINE_' + inlineMathBlocks.length;
      inlineMathBlocks.push({ id, tex: tex.trim() });
      return `<!--${id}-->`;
    });
    html = html.replace(/\$([^\$\n\r]+?)\$/g, (match, tex) => {
      if (!tex.trim()) return match;
      const id = 'MATH_INLINE_' + inlineMathBlocks.length;
      inlineMathBlocks.push({ id, tex: tex.trim() });
      return `<!--${id}-->`;
    });

    // Restore literal dollar signs
    html = html.replace(/<!--ESCAPED_DOLLAR-->/g, '$');

    // 5. GitHub Callout Alerts: > [!NOTE], > [!TIP], > [!IMPORTANT], > [!WARNING], > [!CAUTION]
    html = html.replace(/^>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*\r?\n((?:>.*\r?\n?)*)/gim, (match, type, content) => {
      const cleanContent = content.replace(/^>\s?/gm, '').trim();
      const badgeType = type.toUpperCase();
      const iconMap = {
        'NOTE': 'fa-circle-info',
        'TIP': 'fa-lightbulb',
        'IMPORTANT': 'fa-thumbtack',
        'WARNING': 'fa-triangle-exclamation',
        'CAUTION': 'fa-octagon-exclamation'
      };
      const icon = iconMap[badgeType] || 'fa-circle-info';
      return `<div class="studio-alert alert-${type.toLowerCase()}">
        <div class="alert-title"><i class="fas ${icon}"></i> ${badgeType}</div>
        <div class="alert-body">${cleanContent}</div>
      </div>\n\n`;
    });

    // 6. Tables
    html = html.replace(/^\|(.+)\|\r?\n\|([-: |]+)\|\r?\n((?:\|.*\|\r?\n?)*)/gm, (match, header, divider, body) => {
      const headers = header.split('|').map(h => h.trim()).filter(Boolean);
      const rows = body.trim().split('\n').map(r => r.split('|').map(c => c.trim()).filter(Boolean));

      let tableHtml = '<div class="table-wrapper"><table class="studio-table"><thead><tr>';
      headers.forEach(h => { tableHtml += `<th>${h}</th>`; });
      tableHtml += '</tr></thead><tbody>';
      rows.forEach(r => {
        tableHtml += '<tr>';
        r.forEach(c => {
          let cell = c.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
          tableHtml += `<td>${cell}</td>`;
        });
        tableHtml += '</tr>';
      });
      tableHtml += '</tbody></table></div>\n\n';
      return tableHtml;
    });

    // 7. Headings
    html = html.replace(/^# (.*$)/gim, '<h1>$1</h1>');
    html = html.replace(/^## (.*$)/gim, '<h2>$1</h2>');
    html = html.replace(/^### (.*$)/gim, '<h3>$1</h3>');

    // 8. Bold, Italics, Code
    html = html.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/\*(.*?)\*/g, '<em>$1</em>');
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

    // 9. Paragraphs
    const paragraphs = html.split(/\n{2,}/);
    html = paragraphs.map(p => {
      p = p.trim();
      if (!p) return '';
      if (p.startsWith('<h') || p.startsWith('<div') || p.startsWith('<!--CODE_BLOCK') || p.startsWith('<!--MATH_DISP')) return p;
      return `<p>${p.replace(/\n/g, '<br>')}</p>`;
    }).join('\n');

    // 10. Restore Display Math (Rendered directly with KaTeX display mode)
    displayMathBlocks.forEach(m => {
      const rendered = renderKaTeX(m.tex, true);
      html = html.replace(`<!--${m.id}-->`, `<div class="studio-math-display">${rendered}</div>`);
    });

    // 11. Restore Inline Math (Rendered directly with KaTeX inline mode)
    inlineMathBlocks.forEach(m => {
      const rendered = renderKaTeX(m.tex, false);
      html = html.replace(`<!--${m.id}-->`, `<span class="studio-math-inline">${rendered}</span>`);
    });

    // 12. Restore Code Blocks & Diagrams
    codeBlocks.forEach(b => {
      if (b.type === 'mermaid') {
        html = html.replace(`<!--${b.id}-->`, `<div class="mermaid-diagram-container"><pre class="mermaid">${b.content}</pre></div>`);
      } else {
        html = html.replace(`<!--${b.id}-->`, `<pre class="studio-code-block"><code>${b.content}</code></pre>`);
      }
    });

    return html;
  }

  // Update Live Preview Surface
  async function updatePreview() {
    if (!textarea || !renderedHtml) return;
    const text = textarea.value;

    // Check if AI citation pips are present and show normalizer banner
    const hasAiMarkers = /\[\d+†source\]|\\\([^\)]+\\\)|\\\[[\s\S]+?\\\]/.test(text);
    if (aiCleanBanner) {
      aiCleanBanner.style.display = (hasAiMarkers || currentScenarioKey === 'aiclean') ? 'flex' : 'none';
    }

    // Render HTML with embedded KaTeX equations
    renderedHtml.innerHTML = renderMarkdown(text);

    // Mermaid Diagram Rendering
    if (window.mermaid) {
      try {
        const mermaidElements = renderedHtml.querySelectorAll('.mermaid');
        if (mermaidElements.length > 0) {
          await window.mermaid.run({ nodes: mermaidElements });
        }
      } catch (err) {
        console.warn('Mermaid rendering notice:', err);
      }
    }

    // Update Word & Line Counts
    updateMetrics();
  }

  // Update Metrics Display
  function updateMetrics() {
    if (!textarea || !wordCountDisplay) return;
    const val = textarea.value;
    const words = val.trim() ? val.trim().split(/\s+/).length : 0;
    const lines = (val.match(/\n/g) || []).length + 1;
    wordCountDisplay.textContent = `${words.toLocaleString()} words • ${lines} line${lines === 1 ? '' : 's'}`;
  }

  // Switch Scenario
  function setScenario(key) {
    if (!SCENARIOS[key]) return;
    currentScenarioKey = key;
    const scenario = SCENARIOS[key];

    // Update Tabs Active State
    tabs.forEach(tab => {
      const isMatch = tab.getAttribute('data-scenario') === key;
      tab.classList.toggle('active', isMatch);
      tab.setAttribute('aria-selected', isMatch ? 'true' : 'false');
    });

    // Update Info Bar & Download Actions
    if (badgePill) badgePill.innerHTML = scenario.badge;
    if (descText) descText.textContent = scenario.desc;
    if (btnDownload) {
      btnDownload.href = scenario.downloadFile;
      btnDownload.setAttribute('download', scenario.downloadName);
      if (key === 'aiclean') {
        btnDownload.innerHTML = '<i class="fa-solid fa-file-word"></i> With MarkSmith (.docx)';
        btnDownload.title = 'Download clean sanitized export with native OMML equations';
      } else {
        btnDownload.innerHTML = '<i class="fa-solid fa-file-word"></i> Download Verified .docx';
        btnDownload.title = 'Download genuine Word document';
      }
    }
    if (btnDownloadRaw) {
      if (key === 'aiclean' && scenario.downloadRawFile) {
        btnDownloadRaw.style.display = 'inline-flex';
        btnDownloadRaw.href = scenario.downloadRawFile;
        btnDownloadRaw.setAttribute('download', scenario.downloadRawName || 'chatgpt-export-without-marksmith.docx');
      } else {
        btnDownloadRaw.style.display = 'none';
      }
    }

    if (aiCleanBanner) {
      aiCleanBanner.style.display = key === 'aiclean' ? 'flex' : 'none';
    }

    // Update Textarea & Preview
    if (textarea) {
      textarea.value = scenario.markdown;
      updatePreview();
    }
  }

  // Tab Click Handlers
  tabs.forEach(tab => {
    tab.addEventListener('click', () => {
      const scenarioKey = tab.getAttribute('data-scenario');
      setScenario(scenarioKey);
    });
  });

  // Textarea Input Listener with Debounce
  if (textarea) {
    textarea.addEventListener('input', () => {
      clearTimeout(renderDebounceTimer);
      renderDebounceTimer = setTimeout(updatePreview, 120);
    });

    // Tab key support (2 spaces)
    textarea.addEventListener('keydown', (e) => {
      if (e.key === 'Tab') {
        e.preventDefault();
        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        textarea.value = textarea.value.substring(0, start) + '  ' + textarea.value.substring(end);
        textarea.selectionStart = textarea.selectionEnd = start + 2;
        updatePreview();
      }
    });

    // Smart AI Paste Sanitization
    textarea.addEventListener('paste', (e) => {
      const pasteData = (e.clipboardData || window.clipboardData).getData('text');
      if (!pasteData) return;

      if (/\[\d+†source\]|:contentReference/.test(pasteData)) {
        e.preventDefault();
        const cleaned = pasteData
          .replace(/\[\d+†source\]/g, '')
          .replace(/:contentReference\[.*?\]/g, '')
          .replace(/\\\((.*?)\\\)/g, '$$$1$$')
          .replace(/\\\[([\s\S]*?)\\\]/g, '$$$$\n$1\n$$$$');

        const start = textarea.selectionStart;
        const end = textarea.selectionEnd;
        textarea.value = textarea.value.substring(0, start) + cleaned + textarea.value.substring(end);
        textarea.selectionStart = textarea.selectionEnd = start + cleaned.length;
        if (aiCleanBanner) aiCleanBanner.style.display = 'flex';
        updatePreview();
      }
    });
  }

  // Quick Insert Snippet Buttons
  insertButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      if (!textarea) return;
      const tag = btn.getAttribute('data-tag');
      let snippet = '';

      switch (tag) {
        case 'math':
          snippet = '\n\n$$\n\\mathbf{H}|\\psi\\rangle = E|\\psi\\rangle\n$$\n\n';
          break;
        case 'mermaid':
          snippet = '\n\n```mermaid\nflowchart TD\n    A[Input] --> B{Process}\n    B -->|Success| C[Native Word DOCX]\n```\n\n';
          break;
        case 'alert':
          snippet = '\n\n> [!NOTE]\n> Native OMML equation typesetting enabled without rasterization.\n\n';
          break;
        case 'table':
          snippet = '\n\n| Item | Baseline | MarkSmith |\n| :--- | :--- | :--- |\n| Integrity | Risky | 100% Guaranteed |\n\n';
          break;
      }

      const start = textarea.selectionStart;
      const end = textarea.selectionEnd;
      textarea.value = textarea.value.substring(0, start) + snippet + textarea.value.substring(end);
      textarea.selectionStart = textarea.selectionEnd = start + snippet.length;
      textarea.focus();
      updatePreview();
    });
  });

  // 1-Click Copy Raw Markdown Button
  if (btnCopy && textarea) {
    btnCopy.addEventListener('click', () => {
      navigator.clipboard.writeText(textarea.value).then(() => {
        const originalHtml = btnCopy.innerHTML;
        btnCopy.innerHTML = '<i class="fa-solid fa-check" style="color: #34d399;"></i> Copied!';
        setTimeout(() => {
          btnCopy.innerHTML = originalHtml;
        }, 1500);
      });
    });
  }

  // If KaTeX finishes loading asynchronously from CDN, refresh preview once ready
  if (!window.katex) {
    const checkKatex = setInterval(() => {
      if (window.katex) {
        clearInterval(checkKatex);
        updatePreview();
      }
    }, 50);
    setTimeout(() => clearInterval(checkKatex), 3000);
  }

  // Initial Load with 'math' scenario
  setScenario('math');
})();
