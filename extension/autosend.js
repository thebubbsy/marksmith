// Marksmith Connector — end-of-conversation auto-send.
//
// OPT-IN (Options page). When enabled, watches the AI chat page and, once the assistant has
// finished replying and you've stopped interacting for a few seconds, sends the whole conversation
// to the Marksmith app. Pair it with the app's "Auto-generate PDF from AI-chat ingests" toggle and
// each finished conversation becomes a polished PDF with no clicks.
//
// It never sends mid-stream: any DOM change or keystroke resets the idle timer, so a reply that is
// still streaming keeps the timer alive. A content hash prevents re-sending an unchanged conversation.

(async () => {
  const cfg = await chrome.storage.sync.get({ autoSendIdle: false, idleSeconds: 20, port: 47821, output: null });
  if (!cfg.autoSendIdle) return;

  let lastActivity = Date.now();
  let lastHash = null;
  const bump = () => (lastActivity = Date.now());

  document.addEventListener("keydown", bump, true);
  document.addEventListener("input", bump, true);
  document.addEventListener("pointerdown", bump, true);
  new MutationObserver(bump).observe(document.body, { childList: true, subtree: true, characterData: true });

  setInterval(async () => {
    if (Date.now() - lastActivity < cfg.idleSeconds * 1000) return; // still active / streaming
    const md = extractConversation();
    if (!md || md.length < 60) return;
    const h = hash(md);
    if (h === lastHash) return; // nothing new since last send
    lastHash = h;
    try {
      await fetch(`http://127.0.0.1:${cfg.port}/api/ingest`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ markdown: md, output: cfg.output || undefined }),
      });
    } catch { /* app not running — try again next idle window */ }
  }, 4000);

  function hash(s) { let h = 0; for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) | 0; return h; }

  function extractConversation() {
    const host = location.hostname;
    let roots = [];
    if (host.includes("chatgpt.com") || host.includes("chat.openai.com"))
      roots = [...document.querySelectorAll('[data-message-author-role="assistant"] .markdown, [data-message-author-role="assistant"] .prose')];
    else if (host.includes("gemini.google.com")) {
      roots = [...document.querySelectorAll("message-content .markdown")];
      if (!roots.length) roots = [...document.querySelectorAll("message-content, model-response")];
    } else if (host.includes("claude.ai"))
      roots = [...document.querySelectorAll('[data-testid="assistant-message"], .font-claude-message')];
    if (!roots.length) return "";
    recoverMermaid(roots);
    return roots.map(conv).join("\n\n---\n\n").replace(/\n{3,}/g, "\n\n").trim();
  }

  // HTML -> Markdown walker (same conversion the toolbar button uses).
  function conv(n) {
    if (n.nodeType === Node.TEXT_NODE) return n.textContent;
    if (n.nodeType !== Node.ELEMENT_NODE) return "";

    if (n.dataset && n.dataset.mkMermaid) {
      const f = String.fromCharCode(96, 96, 96), nl = String.fromCharCode(10);
      return nl + f + "mermaid" + nl + n.dataset.mkMermaid + nl + f + nl;
    }

    const cls = typeof n.className === "string" ? n.className : (n.className?.baseVal || "");
    if (cls.includes("katex") || cls.includes("math-inline") || cls.includes("math-display")) {
        const tex = n.querySelector('annotation[encoding="application/x-tex"], script[type="math/tex"]');
        if (tex) {
            const t = tex.textContent.trim();
            return cls.includes("-display") ? `\n$$${t}$$\n` : `$${t}$`;
        }
    }

    const tag = n.tagName.toLowerCase();
    const kids = () => [...n.childNodes].map(conv).join("");
    switch (tag) {
      case "h1": return `\n# ${kids().trim()}\n`;
      case "h2": return `\n## ${kids().trim()}\n`;
      case "h3": return `\n### ${kids().trim()}\n`;
      case "h4": return `\n#### ${kids().trim()}\n`;
      case "p": return `\n${kids()}\n`;
      case "br": return "\n";
      case "strong": case "b": return `**${kids()}**`;
      case "em": case "i": return `*${kids()}*`;
      case "code": return n.closest("pre") ? kids() : "`" + kids() + "`";
      case "pre": {
        const code = n.querySelector("code");
        const lang = [...(code?.classList || [])].find((c) => c.startsWith("language-"))?.slice(9) || "";
        return `\n\`\`\`${lang}\n${(code || n).textContent.replace(/\n$/, "")}\n\`\`\`\n`;
      }
      case "ul": return "\n" + [...n.children].filter((c) => c.tagName === "LI").map((li) => "- " + conv(li).trim().replace(/\n/g, "\n  ")).join("\n") + "\n";
      case "ol": return "\n" + [...n.children].filter((c) => c.tagName === "LI").map((li, i) => `${i + 1}. ` + conv(li).trim().replace(/\n/g, "\n   ")).join("\n") + "\n";
      case "li": return kids();
      case "a": return n.href && !n.href.startsWith("javascript:") ? `[${kids()}](${n.href})` : kids();
      case "blockquote": return "\n" + kids().trim().split("\n").map((l) => `> ${l}`).join("\n") + "\n";
      case "hr": return "\n---\n";
      case "table": {
        const rows = [...n.querySelectorAll("tr")].map((tr) => "| " + [...tr.children].map((td) => conv(td).trim().replace(/\|/g, "\\|").replace(/\n/g, " ")).join(" | ") + " |");
        if (rows.length > 1) rows.splice(1, 0, "|" + " --- |".repeat(rows[0].split("|").length - 2));
        return "\n" + rows.join("\n") + "\n";
      }
      case "button": case "script": case "style": case "svg": case "noscript": return "";
      default: return kids();
    }
  }

  // Recover the RAW mermaid source for rendered diagram widgets and tag each container with
  // data-mk-mermaid so conv() emits a fenced block instead of dropping the <svg>/<canvas> (which
  // used to leak the bare word "Mermaid" into the ingest). Synchronous because extractConversation
  // is sync: DOM code elements -> data-attributes -> the message's raw markdown pulled straight out
  // of React state (fence-by-index). Best-effort, never throws.
  const MERMAID_HEAD = /^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b/;
  function recoverMermaid(rootsArr) {
    const grab = (c) => (c.tagName === "TEXTAREA" ? c.value : c.textContent) || "";
    const FENCE = String.fromCharCode(96, 96, 96); // the three-backtick code fence

    // Pull the reply's RAW markdown out of React's internal state — the source of truth the
    // diagram was rendered from, immune to widget DOM churn. Walk up to the nearest fiber and
    // climb until we find a string prop holding a mermaid fence. "" when not React.
    const reactMarkdown = (startEl) => {
      try {
        let node = startEl, fiber = null;
        while (node && !fiber) {
          const key = Object.keys(node).find((k) => k.startsWith("__reactFiber$") || k.startsWith("__reactInternalInstance$"));
          fiber = key ? node[key] : null;
          if (!fiber) node = node.parentElement;
        }
        if (!fiber) return "";
        const dig = (obj, depth) => {
          if (!obj || depth > 5) return "";
          if (typeof obj === "string") return obj.includes(FENCE + "mermaid") ? obj : "";
          if (typeof obj !== "object") return "";
          let keys;
          try { keys = Object.keys(obj); } catch { return ""; }
          if (keys.length > 60) keys = keys.slice(0, 60);
          for (const k of keys) {
            let v; try { v = obj[k]; } catch { continue; }
            if (typeof v === "string" && v.includes(FENCE + "mermaid")) return v;
          }
          for (const k of keys) {
            let v; try { v = obj[k]; } catch { continue; }
            if (v && typeof v === "object") {
              const r = dig(v, depth + 1);
              if (r) return r;
            }
          }
          return "";
        };
        let cur = fiber;
        for (let i = 0; cur && i < 50; i++, cur = cur.return) {
          const r = dig(cur.memoizedProps, 0);
          if (r) return r;
        }
        return "";
      } catch { return ""; }
    };
    const fencesFrom = (md) => {
      const out = [];
      if (!md) return out;
      const re = /`{3}mermaid[^\n]*\r?\n([\s\S]*?)`{3}/gi;
      let m;
      while ((m = re.exec(md)) !== null) out.push(m[1].trim());
      return out;
    };

    for (const root of rootsArr) {
      const fences = fencesFrom(reactMarkdown(root));
      const found = [];
      const seen = new Set();
      const push = (c) => { if (c && !seen.has(c) && c.querySelector("svg, canvas")) { seen.add(c); found.push(c); } };
      for (const c of root.querySelectorAll('.mermaid, [class*="mermaid" i], [data-testid*="mermaid" i], [class*="diagram" i], [data-testid*="diagram" i]')) push(c);
      for (const s of root.querySelectorAll('svg[id*="mermaid" i], canvas[id*="mermaid" i]')) {
        push(s.closest('[class*="mermaid" i], [class*="diagram" i], [data-testid*="mermaid" i]') || s.parentElement || s);
      }
      for (const h of root.querySelectorAll("div, span, header, h1, h2, h3, h4, p")) {
        if (/^mermaid$/i.test((h.textContent || "").trim()) && !h.querySelector("svg, canvas")) {
          let anc = h.parentElement;
          for (let d = 0; anc && d < 8; d++, anc = anc.parentElement) {
            if (anc.querySelector("svg, canvas")) { push(anc); break; }
          }
        }
      }
      const containers = found.filter((a) => !found.some((b) => b !== a && a.contains(b)));

      let fenceIdx = 0;
      for (const el of containers) {
        try {
          if (el.dataset.mkMermaid) { fenceIdx++; continue; }
          let source = "";
          const scope = el.closest('[data-message-id], [data-message-author-role]') || el.parentElement || el;
          const codeEl = scope.querySelector('code[class*="language-mermaid"]');
          if (codeEl && codeEl.textContent.trim()) source = codeEl.textContent;
          if (!source) {
            for (const cand of scope.querySelectorAll("code, pre, textarea")) {
              if (MERMAID_HEAD.test(grab(cand).trim())) { source = grab(cand); break; }
            }
          }
          if (!source) {
            let node = el;
            for (let d = 0; node && d < 4 && !source; d++, node = node.parentElement) {
              for (const attr of node.attributes || []) {
                if (MERMAID_HEAD.test(attr.value || "")) { source = attr.value; break; }
              }
            }
          }
          if (!source) {
            for (const holder of el.querySelectorAll("[data-code], [data-content], [data-source]")) {
              const v = holder.dataset.code || holder.dataset.content || holder.dataset.source || "";
              if (MERMAID_HEAD.test(v.trim())) { source = v; break; }
            }
          }
          if (!source && fences.length > fenceIdx) source = fences[fenceIdx];
          if (source.trim()) el.dataset.mkMermaid = source.trim();
          fenceIdx++;
        } catch { fenceIdx++; }
      }
    }
  }
})();
