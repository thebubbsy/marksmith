// Marksmith Connector — "Copy as Markdown" button, injected into every assistant reply on the
// supported AI chat sites (ChatGPT, Gemini, Claude, Copilot). The button adopts each site's own
// text color, font, and corner radius so it reads as part of the host UI. Clicking converts that
// reply's HTML to Markdown and puts it on the clipboard — so even users who skip the automation
// never paste plain text again.
//
// The Marksmith desktop app can also ask for attention: when it detects a plain-text paste it bumps
// /api/attention, and the button pulses to teach the user it exists (polled via the service worker,
// which has host permission for 127.0.0.1 — content scripts can't fetch cross-origin in MV3).

(() => {
    "use strict";

    const BTN_CLASS = "mk-copy-md-btn";
    const WRAP_CLASS = "mk-copy-md-wrap";

    // Per-site: how to find assistant replies, what corner radius feels native, the canonical
    // source id + brand accent Marksmith maps to a theme, and a best-effort selector for the
    // current model label (fragile — sites rename these constantly — so a miss just omits it).
    const SITES = [
        {
            host: /(^|\.)chatgpt\.com$|(^|\.)chat\.openai\.com$/,
            messages: '[data-message-author-role="assistant"]',
            content: ".markdown, .prose",
            radius: "8px",
            id: "chatgpt",
            accent: "#10a37f",
            model: '[data-testid="model-switcher-dropdown-button"], [data-testid^="model-switcher"]',
        },
        {
            host: /(^|\.)gemini\.google\.com$/,
            messages: "model-response, message-content",
            content: ".markdown",
            radius: "16px",
            id: "gemini",
            accent: "#1a73e8",
            model: '[data-test-id="bard-mode-menu-button"], .logo-pill-label-container, .current-mode-title',
        },
        {
            host: /(^|\.)claude\.ai$/,
            messages: '[data-testid="assistant-message"], .font-claude-message',
            content: null,
            radius: "8px",
            id: "claude",
            accent: "#d97757",
            model: '[data-testid="model-selector-dropdown"], button[aria-haspopup="menu"] [data-testid="model-name"]',
        },
        {
            host: /(^|\.)copilot\.microsoft\.com$/,
            messages: '[data-content="ai-message"], [data-testid="ai-message"], [class*="ai-message"]',
            content: null,
            radius: "4px",
            id: "copilot",
            accent: "#0f6cbd",
            model: null,
        },
    ];

    const site = SITES.find((s) => s.host.test(location.hostname));
    if (!site) return;

    // ---------- styling (inherits the host page's font; colors derive from the message text) ----------
    const style = document.createElement("style");
    style.textContent = `
        .${WRAP_CLASS} { display: flex; justify-content: flex-end; margin-top: 2px; }
        .${BTN_CLASS} {
            display: inline-flex; align-items: center; gap: 5px;
            font: inherit; font-size: 12px; line-height: 1;
            padding: 5px 10px; border-radius: ${site.radius};
            border: 1px solid color-mix(in srgb, currentColor 22%, transparent);
            background: transparent; color: inherit; opacity: 0.55;
            cursor: pointer; transition: opacity .15s ease, background .15s ease;
            user-select: none;
        }
        .${BTN_CLASS}:hover { opacity: 1; background: color-mix(in srgb, currentColor 8%, transparent); }
        .${BTN_CLASS}.mk-copied { opacity: 1; }
        .${BTN_CLASS}.mk-flash { animation: mk-pulse 1s ease-in-out 4; opacity: 1; }
        @keyframes mk-pulse {
            0%, 100% { box-shadow: 0 0 0 0 rgba(139, 109, 255, 0); }
            50% { box-shadow: 0 0 0 5px rgba(139, 109, 255, 0.45); }
        }
        @media (prefers-reduced-motion: reduce) { .${BTN_CLASS}.mk-flash { animation: none; outline: 2px solid rgba(139,109,255,.7); } }

        #mk-sel-floating-bar {
            position: absolute; z-index: 2147483640; display: none; align-items: center; gap: 6px;
            background: #181924; border: 1px solid #2e2e42; border-radius: 8px;
            box-shadow: 0 8px 24px rgba(0,0,0,0.45); padding: 4px 6px;
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
            font-size: 12px; color: #e2e2ec; animation: mk-fadein .15s ease-out;
        }
        @keyframes mk-fadein { from { opacity: 0; transform: translateY(4px); } to { opacity: 1; transform: translateY(0); } }
        .mk-sel-btn {
            display: inline-flex; align-items: center; gap: 5px; padding: 4px 8px; border-radius: 6px;
            border: 1px solid transparent; background: #262738; color: #f0f0fa; cursor: pointer;
            font-weight: 500; font-size: 11.5px; transition: background .12s, border-color .12s;
        }
        .mk-sel-btn:hover { background: #35374e; border-color: #7c4dff; }
        .mk-sel-btn.mk-primary { background: #7c4dff; color: #fff; }
        .mk-sel-btn.mk-primary:hover { background: #8f66ff; }
    `;
    document.documentElement.appendChild(style);

    // ---------- HTML -> Markdown (same walker the toolbar/context-menu send uses) ----------
    function conv(n) {
        if (n.nodeType === Node.TEXT_NODE) return n.textContent;
        if (n.nodeType !== Node.ELEMENT_NODE) return "";
        if (n.classList?.contains(WRAP_CLASS)) return ""; // never copy our own button

        const cls = typeof n.className === "string" ? n.className : (n.className?.baseVal || "");
        if (cls.includes("katex") || cls.includes("math-inline") || cls.includes("math-display")) {
            const tex = n.querySelector('annotation[encoding="application/x-tex"], script[type="math/tex"]');
            if (tex) {
                const t = tex.textContent.trim();
                return cls.includes("-display") ? `\n$$${t}$$\n` : `$${t}$`;
            }
        }

        // A rendered diagram whose raw ```mermaid source was recovered by the pre-pass below —
        // emit the fenced source instead of hitting the <svg>/<canvas> and dropping the diagram.
        if (n.dataset && n.dataset.mkMermaid) {
            return `\n\`\`\`mermaid\n${n.dataset.mkMermaid}\n\`\`\`\n`;
        }
        const tag = n.tagName.toLowerCase();
        const kids = () => [...n.childNodes].map(conv).join("");
        switch (tag) {
            case "h1": return `\n# ${kids().trim()}\n`;
            case "h2": return `\n## ${kids().trim()}\n`;
            case "h3": return `\n### ${kids().trim()}\n`;
            case "h4": return `\n#### ${kids().trim()}\n`;
            case "h5": return `\n##### ${kids().trim()}\n`;
            case "h6": return `\n###### ${kids().trim()}\n`;
            case "p": return `\n${kids()}\n`;
            case "br": return "\n";
            case "strong": case "b": return `**${kids()}**`;
            case "em": case "i": return `*${kids()}*`;
            case "del": case "s": return `~~${kids()}~~`;
            case "code": return n.closest("pre") ? kids() : "`" + kids() + "`";
            case "pre": {
                if (n.dataset?.mkMermaidSource || n.querySelector("[data-mk-mermaid-source]")) return "";
                const code = n.querySelector("code");
                let lang = [...(code?.classList || [])].find((c) => c.startsWith("language-"))?.slice(9) || "";
                const txt = (code || n).textContent.replace(/\n$/, "");
                if (!lang && MERMAID_HEAD.test(txt.trim())) lang = "mermaid";
                if (/^(code\s*snippet|code|text)$/i.test(lang.trim()) && MERMAID_HEAD.test(txt.trim())) lang = "mermaid";
                if (lang === "mermaid") {
                    const scope = n.closest('[data-message-id], [data-message-author-role], message-content, model-response') || n.parentElement;
                    if (scope && scope.querySelector('[data-mk-mermaid]')) return "";
                }
                return `\n\`\`\`${lang}\n${txt}\n\`\`\`\n`;
            }
            case "ul":
                return "\n" + [...n.children].filter((c) => c.tagName === "LI")
                    .map((li) => "- " + conv(li).trim().split(String.fromCharCode(10)).join(String.fromCharCode(10) + "  ")).join(String.fromCharCode(10)) + String.fromCharCode(10);
            case "ol":
                return "\n" + [...n.children].filter((c) => c.tagName === "LI")
                    .map((li, i) => `${i + 1}. ` + conv(li).trim().split(String.fromCharCode(10)).join(String.fromCharCode(10) + "   ")).join(String.fromCharCode(10)) + String.fromCharCode(10);
            case "li": return kids();
            case "a": return n.href && !n.href.startsWith("javascript:") ? `[${kids()}](${n.href})` : kids();
            case "img": return n.src ? `![${n.alt || ""}](${n.src})` : "";
            case "blockquote":
                return "\n" + kids().trim().split("\n").map((l) => `> ${l}`).join("\n") + "\n";
            case "hr": return "\n---\n";
            case "table": {
                const rows = [...n.querySelectorAll("tr")].map(
                    (tr) => "| " + [...tr.children].map((td) => conv(td).trim().replace(/\|/g, "\\|").replace(/\n/g, " ")).join(" | ") + " |");
                if (rows.length > 1) {
                    const cols = rows[0].split("|").length - 2;
                    rows.splice(1, 0, "|" + " --- |".repeat(cols));
                }
                return "\n" + rows.join("\n") + "\n";
            }
            case "button": case "script": case "style": case "svg": case "noscript":
                return "";
            default: return kids();
        }
    }

    function toMarkdown(root) {
        const target = site.content ? root.querySelector(site.content) || root : root;
        return conv(target).replace(/\n{3,}/g, "\n\n").trim();
    }

    // Recover the RAW mermaid source for rendered diagram widgets inside this reply and tag the
    // container with data-mk-mermaid so conv() emits a fenced block instead of dropping the
    // <svg>/<canvas> (which used to leak the bare word "Mermaid" into the copy). Strategies, in
    // order: code element in the DOM -> data-attributes -> click the widget's "Code" toggle ->
    // pull the message's raw markdown straight out of React state and use its mermaid fences.
    // Best-effort, never throws.
    const MERMAID_HEAD = /^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b/;
    async function recoverMermaid(root) {
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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
        const fences = (() => {
            const out = [];
            const md = reactMarkdown(root);
            if (!md) return out;
            const re = /`{3}mermaid[^\n]*\r?\n([\s\S]*?)`{3}/gi;
            let m;
            while ((m = re.exec(md)) !== null) out.push(m[1].trim());
            return out;
        })();

        const found = [];
        const seen = new Set();
        const push = (c) => { if (c && !seen.has(c) && c.querySelector("svg, canvas")) { seen.add(c); found.push(c); } };
        for (const c of root.querySelectorAll('.mermaid, [class*="mermaid" i], [data-testid*="mermaid" i], [class*="diagram" i], [data-testid*="diagram" i]')) push(c);
        for (const s of root.querySelectorAll('svg[id*="mermaid" i], canvas[id*="mermaid" i]')) {
            push(s.closest('[class*="mermaid" i], [class*="diagram" i], [data-testid*="mermaid" i]') || s.parentElement || s);
        }
        for (const h of root.querySelectorAll("h1, h2, h3, h4, h5, h6, header, title, label, figcaption, [class*='title' i], [class*='header' i], [class*='label' i]")) {
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
                if (codeEl && codeEl.textContent.trim()) { source = codeEl.textContent; codeEl.dataset.mkMermaidSource = "true"; }
                if (!source) {
                    for (const cand of scope.querySelectorAll("code, pre, textarea")) {
                        if (MERMAID_HEAD.test(grab(cand).trim())) { source = grab(cand); cand.dataset.mkMermaidSource = "true"; break; }
                    }
                }
                if (!source) {
                    for (const holder of el.querySelectorAll("[data-code], [data-content], [data-source]")) {
                        const v = holder.dataset.code || holder.dataset.content || holder.dataset.source || "";
                        if (MERMAID_HEAD.test(v.trim())) { source = v; break; }
                    }
                }
                if (!source) {
                    let toggle = scope.querySelector(
                        'button[aria-label*="code" i]:not([aria-label*="copy" i]), button[data-testid*="code" i]:not([data-testid*="copy" i]), button[title*="code" i]:not([title*="copy" i])'
                    );
                    if (!toggle) {
                        toggle = [...scope.querySelectorAll('button, [role="tab"], [role="button"]')].find((b) => {
                            const t = (b.textContent || "").trim();
                            return /\b(code|source)\b/i.test(t) && !/\bcop(y|ied)\b/i.test(t) && t.length <= 20;
                        });
                    }
                    if (toggle) {
                        toggle.click();
                        let revealed = null;
                        for (let w = 0; w < 6 && !revealed; w++) {
                            await sleep(250);
                            revealed = scope.querySelector('code[class*="language-mermaid"]') ||
                                [...scope.querySelectorAll("code, pre, textarea")].find((c) => MERMAID_HEAD.test(grab(c).trim()));
                        }
                        if (revealed) source = grab(revealed);
                        toggle.click(); // leave the page as we found it
                        await sleep(120);
                    }
                }
                // Fall back to the reply's raw markdown from React state — Nth diagram gets Nth fence.
                if (!source && fences.length > fenceIdx) source = fences[fenceIdx];
                if (source.trim()) el.dataset.mkMermaid = source.trim();
                fenceIdx++;
            } catch { fenceIdx++; /* non-fatal: a failed recovery just means that diagram is skipped */ }
        }
    }

    // Everything Marksmith can learn about where this reply came from, read off the live page at
    // copy time — the context that's lost the instant the reply becomes plain Markdown. Shape
    // matches ClipboardSourceMeta.Wire on the app side (short keys keep the clipboard marker small).
    function collectMeta(root) {
        const target = site.content ? root.querySelector(site.content) || root : root;
        const cs = getComputedStyle(target);
        return {
            font: cs.fontFamily || "",              // the exact font the user is looking at
            source: site.id,                        // canonical, from the hostname — ground truth
            model: readModel(),                     // best-effort; "" when the page hides it
            title: readTitle(),                     // conversation title -> export filename
            lang: (document.documentElement.lang || "").trim(),
            dir: cs.direction || "",                // "ltr" | "rtl" — real RTL layout on export
            accent: site.accent,                    // brand color -> nearest Marksmith theme
        };
    }

    function readModel() {
        if (!site.model) return "";
        const el = document.querySelector(site.model);
        const t = (el?.textContent || "").replace(/\s+/g, " ").trim();
        // Guard against a selector accidentally grabbing a whole paragraph.
        return t.length > 0 && t.length <= 40 ? t : "";
    }

    function readTitle() {
        // The tab title is the conversation title on every supported site, minus the site suffix
        // (" - ChatGPT", " | Gemini", etc.) and any leading unread-count "(3) ".
        let t = (document.title || "").replace(/^\(\d+\)\s*/, "");
        t = t.replace(/\s*[-–|]\s*(ChatGPT|OpenAI|Gemini|Google Gemini|Claude|Copilot|Microsoft Copilot)\s*$/i, "");
        t = t.trim();
        // Generic landing titles carry no real conversation name — skip them.
        if (/^(chatgpt|gemini|claude|copilot|new chat|new conversation)$/i.test(t)) return "";
        return t.length <= 200 ? t : t.slice(0, 200);
    }

    function escapeHtml(s) {
        return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    }

    // Writes plain Markdown as text/plain (unchanged behavior for every paste target), plus a
    // text/html alternative carrying the source metadata in a leading HTML comment
    // (<!--marksmith-meta:{json}-->) that only Marksmith looks for — see
    // MarkSmith.Core.Services.ClipboardSourceMeta. Falls back to a plain-text-only copy if the rich write
    // isn't available or is rejected, so "Copy as Markdown" never breaks over this.
    async function copyWithMeta(md, meta) {
        const hasMeta = meta && Object.values(meta).some((v) => v);
        if (hasMeta && window.ClipboardItem) {
            const marker = `<!--marksmith-meta:${encodeURIComponent(JSON.stringify(meta))}-->`;
            const fontStyle = meta.font ? `font-family:${escapeHtml(meta.font)};` : "";
            const html = `${marker}<pre style="${fontStyle}white-space:pre-wrap;">${escapeHtml(md)}</pre>`;
            try {
                await navigator.clipboard.write([
                    new ClipboardItem({
                        "text/plain": new Blob([md], { type: "text/plain" }),
                        "text/html": new Blob([html], { type: "text/html" }),
                    }),
                ]);
                return;
            } catch {
                // Some browsers/contexts reject multi-format writes — fall through to plain text.
            }
        }
        await navigator.clipboard.writeText(md);
    }

    // ---------- injection ----------
    function makeButton(root) {
        const wrap = document.createElement("div");
        wrap.className = WRAP_CLASS;
        const btn = document.createElement("button");
        btn.className = BTN_CLASS;
        btn.type = "button";
        btn.title = "Copy this reply as Markdown — headings, tables and code intact (Marksmith)";
        btn.innerHTML = svgIcon() + "<span>Copy as Markdown</span>";
        btn.addEventListener("click", async (e) => {
            e.preventDefault();
            e.stopPropagation();
            await recoverMermaid(root); // tag rendered diagrams with their raw source first
            const md = toMarkdown(root);
            if (!md) return flashText(btn, "Nothing to copy");
            try {
                await copyWithMeta(md, collectMeta(root));
                flashText(btn, "✓ Copied as Markdown");
            } catch {
                flashText(btn, "Copy failed");
            }
        });
        wrap.appendChild(btn);
        return wrap;
    }

    function flashText(btn, text) {
        const span = btn.querySelector("span");
        const old = span.textContent;
        span.textContent = text;
        btn.classList.add("mk-copied");
        setTimeout(() => { span.textContent = old; btn.classList.remove("mk-copied"); }, 1600);
    }

    function svgIcon() {
        return '<svg width="12" height="12" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" aria-hidden="true">' +
            '<rect x="5" y="5" width="9" height="9" rx="1.5"/><path d="M11 5V3.5A1.5 1.5 0 0 0 9.5 2h-6A1.5 1.5 0 0 0 2 3.5v6A1.5 1.5 0 0 0 3.5 11H5"/></svg>';
    }

    function scan() {
        requestAnimationFrame(() => {
            const elements = document.querySelectorAll(site.messages);
            for (let i = 0; i < elements.length; i++) {
                const root = elements[i];
                if (root.dataset.mkCopyBtn) continue;
                // Only decorate messages that have real content (streaming placeholders come back later).
                if ((root.textContent || "").trim().length < 8) continue;
                root.dataset.mkCopyBtn = "1";
                root.appendChild(makeButton(root));
            }
        });
    }

    let scanTimer = null;
    new MutationObserver(() => {
        if (scanTimer) clearTimeout(scanTimer);
        scanTimer = setTimeout(scan, 350);
    }).observe(document.body, { childList: true, subtree: true });
    scan();

    // ---------- Floating selection action bar ----------
    const floatBar = document.createElement("div");
    floatBar.id = "mk-sel-floating-bar";
        <button type="button" class="mk-sel-btn mk-primary" id="mk-sel-send-btn">
            <span>⚡ Send to Marksmith</span>
        </button>
        <button type="button" class="mk-sel-btn" id="mk-sel-copy-btn">
            <span>📋 Copy MD</span>
        </button>
        <button type="button" class="mk-sel-btn" id="mk-sel-lens-btn" title="Inspect Markdown Source">
            <span>🔍 Lens</span>
        </button>
    `;
    document.documentElement.appendChild(floatBar);

    function getSelectionMarkdown() {
        const sel = window.getSelection();
        if (!sel || sel.rangeCount === 0 || !sel.toString().trim()) return "";
        try {
            const holder = document.createElement("div");
            for (let i = 0; i < sel.rangeCount; i++) holder.appendChild(sel.getRangeAt(i).cloneContents());
            const md = conv(holder).trim();
            return md || sel.toString().trim();
        } catch {
            return sel.toString().trim();
        }
    }

    const sendBtn = floatBar.querySelector("#mk-sel-send-btn");
    const copyMdBtn = floatBar.querySelector("#mk-sel-copy-btn");

    sendBtn.addEventListener("mousedown", async (e) => {
        e.preventDefault();
        e.stopPropagation();
        const md = getSelectionMarkdown();
        if (!md) return;
        const origText = sendBtn.firstElementChild.textContent;
        sendBtn.firstElementChild.textContent = "Sending…";
        try {
            chrome.runtime.sendMessage({
                type: "send-text",
                text: md,
                meta: { title: document.title, source: site.id }
            }, (resp) => {
                if (resp?.ok) {
                    sendBtn.firstElementChild.textContent = "✓ Ingested";
                    setTimeout(() => {
                        floatBar.style.display = "none";
                        sendBtn.firstElementChild.textContent = origText;
                    }, 1200);
                } else {
                    sendBtn.firstElementChild.textContent = "Error";
                    setTimeout(() => { sendBtn.firstElementChild.textContent = origText; }, 1500);
                }
            });
        } catch {
            sendBtn.firstElementChild.textContent = origText;
        }
    });

    copyMdBtn.addEventListener("mousedown", async (e) => {
        e.preventDefault();
        e.stopPropagation();
        const md = getSelectionMarkdown();
        if (!md) return;
        const origText = copyMdBtn.firstElementChild.textContent;
        try {
            await navigator.clipboard.writeText(md);
            copyMdBtn.firstElementChild.textContent = "✓ Copied";
            setTimeout(() => {
                floatBar.style.display = "none";
                copyMdBtn.firstElementChild.textContent = origText;
            }, 1200);
    const lensBtn = floatBar.querySelector("#mk-sel-lens-btn");

    lensBtn?.addEventListener("mousedown", (e) => {
        e.preventDefault();
        e.stopPropagation();
        const md = getSelectionMarkdown();
        if (!md) return;

        let lensModal = document.getElementById("mk-lens-preview-modal");
        if (!lensModal) {
            lensModal = document.createElement("div");
            lensModal.id = "mk-lens-preview-modal";
            lensModal.style.cssText = "position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);width:min(90vw,640px);max-height:80vh;background:#181824;color:#f0f0ff;border:1px solid #7c4dff;border-radius:10px;box-shadow:0 12px 40px rgba(0,0,0,0.5);z-index:999999;display:flex;flex-direction:column;font-family:monospace;padding:16px;";
            document.documentElement.appendChild(lensModal);
        }

        lensModal.innerHTML = `
            <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;border-bottom:1px solid #333;padding-bottom:6px;">
                <span style="font-weight:bold;color:#a855f7;">🔍 Markdown Lens Inspector (${md.length} chars)</span>
                <button type="button" id="mk-lens-close" style="background:#28283c;color:#fff;border:none;border-radius:4px;padding:4px 10px;cursor:pointer;">✕</button>
            </div>
            <pre style="overflow:auto;flex:1;background:#0d0d15;padding:12px;border-radius:6px;margin:0;font-size:12px;line-height:1.4;white-space:pre-wrap;word-break:break-word;">${md.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</pre>
        `;
        lensModal.style.display = "flex";
        lensModal.querySelector("#mk-lens-close")?.addEventListener("click", () => {
            lensModal.style.display = "none";
        });
    });

    document.addEventListener("mouseup", (e) => {
        if (floatBar.contains(e.target)) return;
        setTimeout(() => {
            const sel = window.getSelection();
            if (!sel || sel.isCollapsed || !sel.toString().trim() || sel.toString().trim().length < 3) {
                floatBar.style.display = "none";
                return;
            }
            try {
                const range = sel.getRangeAt(0);
                const rect = range.getBoundingClientRect();
                if (rect.width === 0 && rect.height === 0) {
                    floatBar.style.display = "none";
                    return;
                }
                const top = Math.max(10, window.scrollY + rect.top - 42);
                const left = Math.max(10, Math.min(window.scrollX + rect.left, window.innerWidth - 240));
                floatBar.style.top = `${top}px`;
                floatBar.style.left = `${left}px`;
                floatBar.style.display = "flex";
            } catch {
                floatBar.style.display = "none";
            }
        }, 30);
    });

    document.addEventListener("selectionchange", () => {
        const sel = window.getSelection();
        if (!sel || sel.isCollapsed || !sel.toString().trim()) {
            floatBar.style.display = "none";
        }
    });

    // ---------- attention channel (Marksmith app -> these buttons) ----------
    let lastFlash = Date.now(); // ignore anything the app raised before this page existed
    setInterval(() => {
        if (document.visibilityState !== "visible") return;
        try {
            chrome.runtime.sendMessage({ type: "attention-poll" }, (resp) => {
                void chrome.runtime.lastError; // app offline / SW asleep — fine
                const ts = resp?.flashTs || 0;
                if (ts <= lastFlash) return;
                lastFlash = ts;
                for (const b of document.querySelectorAll("." + BTN_CLASS)) {
                    b.classList.add("mk-flash");
                    setTimeout(() => b.classList.remove("mk-flash"), 4500);
                }
                document.querySelector("." + BTN_CLASS)?.scrollIntoView({ block: "nearest", behavior: "smooth" });
            });
        } catch { /* extension context invalidated (update/reload) */ }
    }, 5000);
})();
