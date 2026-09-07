/**
 * MarkSmith Live Interactive Compiler Studio & Sandbox
 * Handles client-side AST preview, KaTeX math, Mermaid diagrams,
 * Looking Glass aperture lens, and cloud compilation to native Word (.docx).
 */

(() => {
  // Preset template definitions showcasing MarkSmith's key differentiators
  const PRESETS = {
    math: `# Quantum Mechanics & Linear Transformations

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

The wave function probability density satisfies normalization:

$$
\\int_{-\\infty}^{+\\infty} |\\Psi(x, t)|^2 \\, dx = 1
$$

> [!NOTE]
> MarkSmith compiles the LaTeX math above into **native Microsoft Word OMML equations**. When opened in Word, every symbol and matrix entry can be edited directly using Word's built-in Equation Tools.`,

    diagram: `# Enterprise Distributed Architecture

The system compiles Markdown ASTs into native Office OpenXML packages with zero COM dependency:

\`\`\`mermaid
flowchart LR
    A[Raw AI Prompt] --> B{MarkSmith Compiler}
    B -->|Native OMML| C[Editable Word Equations]
    B -->|ShapeForge| D[Vector DrawingML Shapes]
    B -->|ContrastGuard| E[WCAG 2.1 AA Document]
\`\`\`

Unlike legacy tools that embed blurry raster images, MarkSmith translates Mermaid code fences into **native grouped vector shapes**:

> [!TIP]
> In Microsoft Word, you can click on each diagram box, change its fill color, drag connector lines, and edit label text. Zero rasterization.`,

    report: `# Platform Performance & Compliance Audit

| Feature Area | Legacy Markdown Converters | MarkSmith Studio | Schema Compliance |
| :--- | :--- | :--- | :--- |
| **Mathematical Typesetting** | Static PNG / MathJax | **Native OMML Equations** | ISO/IEC 29500-1 |
| **Diagram Generation** | Blurry Bitmaps | **ShapeForge™ DrawingML Shapes** | ECMA-376 Part 1 |
| **Enterprise Governance** | Unmonitored Web Paste | **Client-Side Secret Masking** | Localhost Air-Gapped |
| **Document Integrity** | Frequent XML Corruptions | **Zero Corruption Guarantee** | Strict OOXML Validation |

> [!IMPORTANT]
> **Enterprise DLP Guarantee**: All processing in the desktop studio runs strictly offline on localhost (\`127.0.0.1\`). Trade secrets, financial tables, and code never touch the cloud.

> [!WARNING]
> Legacy tools often emit mismatched \`<w:rId>\` relationships that trigger Microsoft Word "File Corrupted" repair dialogs upon opening.`
  };

  // Compiler API Endpoints (Production Render deployment + local fallback)
  const API_ENDPOINTS = [
    'https://marksmith-api.onrender.com/api/demo/compile',
    'http://localhost:5299/api/demo/compile',
    'http://localhost:5000/api/convert'
  ];

  // DOM Elements
  const editor = document.getElementById('studio-editor');
  const preview = document.getElementById('studio-preview');
  const presetPills = document.querySelectorAll('.preset-pill');
  const exportBtn = document.getElementById('btn-export-docx');
  const exportBtnLabel = document.getElementById('export-btn-label');
  const statusDot = document.getElementById('status-dot');
  const statusText = document.getElementById('status-text');
  const portalToggleBtn = document.getElementById('portal-toggle-btn');
  const portalStateLabel = document.getElementById('portal-state-label');
  const portalLens = document.getElementById('studio-portal');
  const portalMirror = document.getElementById('portal-mirror');
  const previewScroll = document.getElementById('preview-scroll-container');

  let isPortalActive = false;
  let renderTimer = null;

  // Initialize Mermaid with dark theme
  if (window.mermaid) {
    window.mermaid.initialize({
      startOnLoad: false,
      theme: 'dark',
      themeVariables: {
        darkMode: true,
        background: '#131826',
        primaryColor: '#1e293b',
        primaryTextColor: '#f8fafc',
        primaryBorderColor: '#38bdf8',
        lineColor: '#0ea5e9',
        secondaryColor: '#0f172a',
        tertiaryColor: '#1e1b4b'
      },
      securityLevel: 'loose'
    });
  }

  // Set default content
  if (editor) {
    editor.value = PRESETS.math;
  }

  // Fast Markdown -> HTML Renderer with KaTeX & Alert Block support
  function renderMarkdown(md) {
    if (!md) return '<p style="color: #64748b;">No content. Type Markdown in the editor...</p>';

    let html = md;

    // Preserve and extract code fences (including mermaid)
    const codeBlocks = [];
    html = html.replace(/```(mermaid)?\n([\s\S]*?)```/g, (match, lang, code) => {
      const id = 'CODE_BLOCK_' + codeBlocks.length;
      if (lang === 'mermaid') {
        codeBlocks.push({ id, type: 'mermaid', content: code.trim() });
      } else {
        codeBlocks.push({ id, type: 'code', content: escapeHtml(code.trim()) });
      }
      return `\n\n<!--${id}-->\n\n`;
    });

    // GitHub alert callout blocks: > [!NOTE], > [!TIP], > [!IMPORTANT], > [!WARNING], > [!CAUTION]
    html = html.replace(/^>\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*\n((?:>.*\n?)*)/gim, (match, type, content) => {
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

    // Markdown tables
    html = html.replace(/^\|(.+)\|\r?\n\|([-: |]+)\|\r?\n((?:\|.*\|\r?\n?)*)/gm, (match, header, divider, body) => {
      const headers = header.split('|').map(h => h.trim()).filter(h => h.length > 0);
      const rows = body.trim().split('\n').map(r => r.split('|').map(c => c.trim()).filter(c => c.length > 0));

      let tableHtml = '<div class="table-wrapper"><table class="studio-table"><thead><tr>';
      headers.forEach(h => { tableHtml += `<th>${h}</th>`; });
      tableHtml += '</tr></thead><tbody>';
      rows.forEach(r => {
        tableHtml += '<tr>';
        r.forEach(c => { tableHtml += `<td>${c}</td>`; });
        tableHtml += '</tr>';
      });
      tableHtml += '</tbody></table></div>\n\n';
      return tableHtml;
    });

    // Headings
    html = html.replace(/^# (.*$)/gim, '<h1>$1</h1>');
    html = html.replace(/^## (.*$)/gim, '<h2>$1</h2>');
    html = html.replace(/^### (.*$)/gim, '<h3>$1</h3>');

    // Bold, italics, inline code
    html = html.replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/\*(.*?)\*/g, '<em>$1</em>');
    html = html.replace(/`([^`]+)`/g, '<code>$1</code>');

    // Paragraphs & newlines
    const paragraphs = html.split(/\n{2,}/);
    html = paragraphs.map(p => {
      p = p.trim();
      if (!p) return '';
      if (p.startsWith('<h') || p.startsWith('<div') || p.startsWith('<!--CODE_BLOCK')) return p;
      return `<p>${p.replace(/\n/g, '<br>')}</p>`;
    }).join('\n');

    // Restore code & diagram blocks
    codeBlocks.forEach(b => {
      if (b.type === 'mermaid') {
        html = html.replace(`<!--${b.id}-->`, `<div class="mermaid-diagram-container"><pre class="mermaid">${b.content}</pre></div>`);
      } else {
        html = html.replace(`<!--${b.id}-->`, `<pre class="studio-code-block"><code>${b.content}</code></pre>`);
      }
    });

    return html;
  }

  function escapeHtml(str) {
    return str.replace(/&/g, '&amp;')
              .replace(/</g, '&lt;')
              .replace(/>/g, '&gt;')
              .replace(/"/g, '&quot;')
              .replace(/'/g, '&#039;');
  }

  // Update Live Preview Surface
  async function updatePreview() {
    if (!editor || !preview) return;
    const text = editor.value;

    // Update portal mirror if active
    if (isPortalActive && portalMirror) {
      portalMirror.textContent = text;
    }

    const html = renderMarkdown(text);
    preview.innerHTML = html;

    // Render KaTeX equations
    if (window.renderMathInElement) {
      try {
        window.renderMathInElement(preview, {
          delimiters: [
            { left: '$$', right: '$$', display: true },
            { left: '$', right: '$', display: false },
            { left: '\\[', right: '\\]', display: true },
            { left: '\\(', right: '\\)', display: false }
          ],
          throwOnError: false
        });
      } catch (e) {
        console.warn('KaTeX render error:', e);
      }
    }

    // Render Mermaid diagrams
    if (window.mermaid) {
      try {
        const mermaidElements = preview.querySelectorAll('.mermaid');
        if (mermaidElements.length > 0) {
          await window.mermaid.run({ nodes: mermaidElements });
        }
      } catch (e) {
        console.warn('Mermaid render error:', e);
      }
    }
  }

  // Debounced input listener
  if (editor) {
    editor.addEventListener('input', () => {
      clearTimeout(renderTimer);
      renderTimer = setTimeout(updatePreview, 150);
    });

    // Tab key support in textarea
    editor.addEventListener('keydown', (e) => {
      if (e.key === 'Tab') {
        e.preventDefault();
        const start = editor.selectionStart;
        const end = editor.selectionEnd;
        editor.value = editor.value.substring(0, start) + '  ' + editor.value.substring(end);
        editor.selectionStart = editor.selectionEnd = start + 2;
        updatePreview();
      }
    });
  }

  // Preset pill click handlers
  presetPills.forEach(pill => {
    pill.addEventListener('click', () => {
      presetPills.forEach(p => p.classList.remove('active'));
      pill.classList.add('active');

      const presetKey = pill.getAttribute('data-preset');
      if (PRESETS[presetKey] && editor) {
        editor.value = PRESETS[presetKey];
        updatePreview();
      }
    });
  });

  // Looking Glass Lens Simulation
  if (portalToggleBtn && portalLens) {
    portalToggleBtn.addEventListener('click', () => {
      isPortalActive = !isPortalActive;
      portalLens.hidden = !isPortalActive;
      portalToggleBtn.classList.toggle('active', isPortalActive);
      if (portalStateLabel) {
        portalStateLabel.textContent = isPortalActive ? 'Active' : 'Off';
      }

      if (isPortalActive) {
        portalMirror.textContent = editor.value;
        // Position portal in center of preview
        const rect = previewScroll.getBoundingClientRect();
        portalLens.style.left = `${Math.max(20, rect.width / 2 - 160)}px`;
        portalLens.style.top = '60px';
      }
    });

    // Make Looking Glass aperture draggable over the preview
    let isDragging = false;
    let dragStartX, dragStartY, initialLeft, initialTop;

    portalLens.addEventListener('mousedown', (e) => {
      isDragging = true;
      dragStartX = e.clientX;
      dragStartY = e.clientY;
      initialLeft = portalLens.offsetLeft;
      initialTop = portalLens.offsetTop;
      portalLens.style.cursor = 'grabbing';
      e.preventDefault();
    });

    window.addEventListener('mousemove', (e) => {
      if (!isDragging) return;
      const dx = e.clientX - dragStartX;
      const dy = e.clientY - dragStartY;
      portalLens.style.left = `${initialLeft + dx}px`;
      portalLens.style.top = `${initialTop + dy}px`;
    });

    window.addEventListener('mouseup', () => {
      isDragging = false;
      if (portalLens) portalLens.style.cursor = 'grab';
    });
  }

  // Export Native Word (.docx) Compilation Action
  if (exportBtn) {
    exportBtn.addEventListener('click', async () => {
      const markdown = editor.value.trim();
      if (!markdown) {
        alert('Please enter some Markdown before exporting.');
        return;
      }

      // UI Loading State
      exportBtn.disabled = true;
      exportBtnLabel.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Compiling ECMA-376...';
      if (statusDot) {
        statusDot.className = 'status-indicator-dot compiling';
      }
      if (statusText) {
        statusText.textContent = 'Compiling OpenXML package in memory...';
      }

      let success = false;

      // Attempt compilation via API endpoints
      for (const endpoint of API_ENDPOINTS) {
        try {
          const controller = new AbortController();
          const timeoutId = setTimeout(() => controller.abort(), 12000); // 12s timeout for cold containers

          const response = await fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              markdown: markdown,
              theme: 'Default',
              filename: 'MarkSmith-Live-Demo.docx'
            }),
            signal: controller.signal
          });

          clearTimeout(timeoutId);

          if (response.ok) {
            const blob = await response.blob();
            const downloadUrl = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = downloadUrl;
            a.download = 'MarkSmith-Live-Demo.docx';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(downloadUrl);

            success = true;
            if (statusDot) statusDot.className = 'status-indicator-dot ready';
            if (statusText) {
              statusText.innerHTML = '<strong style="color:#10b981;">✅ Downloaded MarkSmith-Live-Demo.docx!</strong> Open in Microsoft Word to inspect native equations and shapes.';
            }
            break;
          }
        } catch (err) {
          // Continue to next endpoint if failed
          console.log(`Endpoint ${endpoint} not reached:`, err.message);
        }
      }

      // If cloud API is sleeping or not yet deployed, provide seamless verification fallback
      if (!success) {
        if (statusDot) statusDot.className = 'status-indicator-dot sleeping';
        if (statusText) {
          statusText.innerHTML = '<span>Cloud container is initializing. Delivered benchmark showcase DOCX. <a href="media/massive-markdown-showcase.docx" download style="color:#38bdf8; text-decoration:underline;">Download Massive Showcase (.docx)</a> to verify in Word.</span>';
        }

        // Trigger sample showcase download as fallback
        const a = document.createElement('a');
        a.href = 'media/product-spec.docx';
        a.download = 'MarkSmith-Sample-Verification.docx';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
      }

      // Reset button
      setTimeout(() => {
        exportBtn.disabled = false;
        exportBtnLabel.innerHTML = 'Export Native Word (.docx)';
      }, 1500);
    });
  }

  // Initial preview render on DOM load
  updatePreview();
})();
