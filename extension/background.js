// Marksmith Connector — grabs assistant replies from AI chat sites, converts them to
// Markdown, and drives the Marksmith desktop app's local API. Two paths:
//   • /api/ingest  — push the reply into the running app's preview ("Send to Marksmith")
//   • /api/convert — get finished PDF/DOCX/PPTX/EPUB bytes back and download them in-browser
// Also powers the popup control center (health / inspect / send / download messages) and a
// toolbar badge that flags when the app can't be reached.

const DEFAULT_PORT = 47821;

// Content types the app's /api/convert can return, keyed by the `format` we request.
const MIME = {
    pdf: "application/pdf",
    docx: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    pptx: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    epub: "application/epub+zip",
};

// The AI-chat sites we can pull a reply from (used to scope the page context-menus).
const CHAT_URLS = [
    "https://chatgpt.com/*",
    "https://chat.openai.com/*",
    "https://gemini.google.com/*",
    "https://claude.ai/*",
    "https://copilot.microsoft.com/*",
];

chrome.runtime.onInstalled.addListener(() => {
    chrome.contextMenus.create({
        id: "mdpdfm-selection",
        title: "Send selection to Marksmith",
        contexts: ["selection"],
    });
    chrome.contextMenus.create({
        id: "mdpdfm-conversation",
        title: "Send full conversation to Marksmith",
        contexts: ["page"],
        documentUrlPatterns: CHAT_URLS,
    });
    chrome.contextMenus.create({
        id: "mdpdfm-dl-pdf",
        title: "Download latest reply as PDF",
        contexts: ["page"],
        documentUrlPatterns: CHAT_URLS,
    });
    chrome.contextMenus.create({
        id: "mdpdfm-dl-docx",
        title: "Download latest reply as DOCX",
        contexts: ["page"],
        documentUrlPatterns: CHAT_URLS,
    });
});

// ── toolbar badge: a quiet "!" when the app is unreachable ──────────────────
// Polls /api/health once a minute so the icon tells you at a glance whether sends
// will work, without you having to open the popup.
chrome.alarms.create("health-poll", { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === "health-poll") refreshBadge();
});
chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "sync" && changes.port) refreshBadge();
});
refreshBadge();

async function refreshBadge() {
    let ok = false;
    try { ok = (await health()).ok; } catch { ok = false; }
    if (ok) {
        chrome.action.setBadgeText({ text: "" });
    } else {
        chrome.action.setBadgeBackgroundColor({ color: "#f85149" });
        chrome.action.setBadgeText({ text: "!" });
    }
}

// ── context-menu clicks ─────────────────────────────────────────────────────
chrome.contextMenus.onClicked.addListener((info, tab) => {
    if (info.menuItemId === "mdpdfm-selection") grabAndSend(tab, "selection");
    else if (info.menuItemId === "mdpdfm-conversation") grabAndSend(tab, "all");
    else if (info.menuItemId === "mdpdfm-dl-pdf") downloadFromTab(tab, "latest", "pdf");
    else if (info.menuItemId === "mdpdfm-dl-docx") downloadFromTab(tab, "latest", "docx");
});

// ── message router ──────────────────────────────────────────────────────────
// Serves the popup control center and the copybutton's attention poll. Every branch
// responds asynchronously, so each returns `true` to keep the channel open.
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    // Attention channel: copybutton.js polls through us (MV3 content scripts can't fetch
    // cross-origin themselves). The app bumps /api/attention when it spots a plain-text paste.
    if (msg?.type === "attention-poll") {
        (async () => {
            try {
                const port = await getPort();
                const resp = await fetch(`http://127.0.0.1:${port}/api/attention`);
                sendResponse(resp.ok ? await resp.json() : { flashTs: 0 });
            } catch {
                sendResponse({ flashTs: 0 }); // app not running — nothing to flash
            }
        })();
        return true;
    }

    if (msg?.type === "health") {
        (async () => sendResponse(await health()))();
        return true;
    }

    if (msg?.type === "inspect") {
        (async () => sendResponse(await inspectActiveTab(msg.mode || "latest")))();
        return true;
    }

    if (msg?.type === "send") {
        (async () => {
            const tab = await activeTab();
            if (!tab?.id) return sendResponse({ ok: false, error: "No active tab." });
            // Popup shows its own toast, so suppress the Windows notification here.
            sendResponse(await grabAndSend(tab, msg.mode || "latest", { notify: false }));
        })();
        return true;
    }

    if (msg?.type === "download") {
        (async () => {
            const tab = await activeTab();
            if (!tab?.id) return sendResponse({ ok: false, error: "No active tab." });
            sendResponse(await downloadFromTab(tab, msg.mode || "latest", msg.format || "pdf", { notify: false }));
        })();
        return true;
    }
});

// ── small shared helpers ────────────────────────────────────────────────────
async function getPort() {
    const { port } = await chrome.storage.sync.get({ port: DEFAULT_PORT });
    return port;
}

async function activeTab() {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    return tab;
}

async function health() {
    try {
        const port = await getPort();
        const resp = await fetch(`http://127.0.0.1:${port}/api/health`);
        if (!resp.ok) return { ok: false };
        const j = await resp.json();
        return { ok: true, app: j.app || "Marksmith" };
    } catch {
        return { ok: false };
    }
}

async function extractFromTab(tabId, mode) {
    const [res] = await chrome.scripting.executeScript({
        target: { tabId },
        func: extractMarkdown,
        args: [mode],
    });
    return res?.result;
}

// Extract from the active tab AND classify the result (best-effort) so the popup can
// preview source/model/char-count/math before the user commits to a send or download.
async function inspectActiveTab(mode) {
    const tab = await activeTab();
    if (!tab?.id) return { ok: false, error: "No active tab." };

    let extracted;
    try {
        extracted = await extractFromTab(tab.id, mode);
    } catch (e) {
        return { ok: false, error: "Cannot read this page — " + e.message };
    }
    if (!extracted?.ok) {
        return { ok: false, error: extracted?.error || "Could not find assistant content on this page." };
    }

    let cls = null;
    try {
        const port = await getPort();
        const resp = await fetch(`http://127.0.0.1:${port}/api/classify`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ markdown: extracted.markdown }),
        });
        if (resp.ok) cls = await resp.json();
    } catch {
        // App offline — classification is a nicety, not a blocker. The popup still
        // shows the extraction; it just won't display a confidence chip.
    }
    return { ok: true, markdown: extracted.markdown, meta: extracted.meta, cls };
}

// ── send to the app (/api/ingest) ───────────────────────────────────────────
async function grabAndSend(tab, mode, opts = {}) {
    const showNotify = opts.notify !== false;

    let extracted;
    try {
        extracted = await extractFromTab(tab.id, mode);
    } catch (e) {
        if (showNotify) notify("Cannot read this page", e.message);
        return { ok: false, error: "Cannot read this page — " + e.message };
    }

    if (!extracted?.ok) {
        const err = extracted?.error || "Could not find assistant content on this page.";
        if (showNotify) notify("Nothing to send", err);
        return { ok: false, error: err };
    }

    try {
        const { port, output } = await chrome.storage.sync.get({ port: DEFAULT_PORT, output: null });
        // Fold the captured source metadata into the output profile so the app applies it on
        // ingest (definitive source, model, title, font, language/direction, brand accent). The
        // user's saved output profile keeps priority for any field it explicitly sets.
        const m = extracted.meta || {};
        const merged = {
            ...(output || {}),
            sourceFontFamily: m.font || undefined,
            sourceId: m.source || undefined,
            sourceModel: m.model || undefined,
            sourceTitle: m.title || undefined,
            sourceLanguage: m.lang || undefined,
            sourceDirection: m.dir || undefined,
            sourceAccentColor: m.accent || undefined,
        };
        const hasAny = Object.values(merged).some((v) => v !== undefined && v !== null);
        const resp = await fetch(`http://127.0.0.1:${port}/api/ingest`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ markdown: extracted.markdown, output: hasAny ? merged : undefined }),
        });
        if (!resp.ok) throw new Error(`API returned HTTP ${resp.status}`);
        if (showNotify) notify("Sent to Marksmith ✓", `${extracted.markdown.length.toLocaleString()} chars ingested — check the preview.`);
        return { ok: true, chars: extracted.markdown.length };
    } catch (e) {
        const err = "Is the app running with Automation → Local REST API enabled? " + e.message;
        if (showNotify) notify("Marksmith unreachable", err);
        return { ok: false, error: err };
    }
}

// ── direct in-browser download (/api/convert) ───────────────────────────────
// The headline upgrade: get finished bytes back from the app and hand them to the
// browser's download manager — no need to open Marksmith at all.
async function downloadFromTab(tab, mode, format, opts = {}) {
    const showNotify = opts.notify !== false;

    let extracted;
    try {
        extracted = await extractFromTab(tab.id, mode);
    } catch (e) {
        if (showNotify) notify("Cannot read this page", e.message);
        return { ok: false, error: "Cannot read this page — " + e.message };
    }
    if (!extracted?.ok) {
        const err = extracted?.error || "Could not find assistant content on this page.";
        if (showNotify) notify("Nothing to convert", err);
        return { ok: false, error: err };
    }

    try {
        const filename = await convertAndDownload(extracted.markdown, format, extracted.meta);
        if (showNotify) notify(`Downloading ${filename} ✓`, "Marksmith converted the reply — check your browser downloads.");
        return { ok: true, filename };
    } catch (e) {
        if (showNotify) notify("Download failed", e.message);
        return { ok: false, error: e.message };
    }
}

async function convertAndDownload(markdown, format, meta) {
    const { port, output } = await chrome.storage.sync.get({ port: DEFAULT_PORT, output: null });
    // Honor the saved output profile (theme, width, diagram mode, …) but force the format
    // the user just asked for — a profile set to "pdf" must not block a DOCX download.
    const ovr = { ...(output || {}), format };

    const resp = await fetch(`http://127.0.0.1:${port}/api/convert`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ markdown, output: ovr }),
    });
    if (!resp.ok) throw new Error(`Marksmith returned HTTP ${resp.status}. Is the Local REST API on?`);

    const buf = await resp.arrayBuffer();
    if (!buf.byteLength) throw new Error("Marksmith returned an empty file.");

    const dataUrl = bufferToDataUrl(buf, MIME[format] || "application/octet-stream");
    const filename = buildFilename(meta, format);
    await chrome.downloads.download({
        url: dataUrl,
        filename,
        saveAs: false,
        conflictAction: "uniquify",
    });
    return filename;
}

// MV3 service workers have no URL.createObjectURL, so we base64-encode the bytes into a
// data: URL for chrome.downloads. Chunked to avoid blowing the call stack on big documents.
function bufferToDataUrl(buffer, mime) {
    const bytes = new Uint8Array(buffer);
    let binary = "";
    const CHUNK = 0x8000;
    for (let i = 0; i < bytes.length; i += CHUNK) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK));
    }
    return `data:${mime};base64,` + btoa(binary);
}

// Turn the captured conversation title into a safe, friendly filename.
function buildFilename(meta, format) {
    const raw = (meta?.title || "").trim() || "marksmith-export";
    const base = raw
        .replace(/[\\/:*?"<>|\r\n]/g, " ")
        .replace(/\s+/g, " ")
        .trim()
        .slice(0, 80) || "marksmith-export";
    return `${base}.${format}`;
}

function notify(title, message) {
    chrome.notifications.create({ type: "basic", iconUrl: "icons/icon128.png", title, message });
}

// Injected into the page. Finds assistant messages via per-site selectors and converts their
// HTML to Markdown with a small recursive walker (headings, lists, tables, code fences, links).
// mode: "latest" = newest assistant reply, "all" = whole conversation, "selection" = user selection.
// Async because the mermaid pre-pass may need to click a "code" toggle and wait for the raw
// fenced source to appear before the walker runs.
async function extractMarkdown(mode) {
    function conv(n) {
        if (n.nodeType === Node.TEXT_NODE) return n.textContent;
        if (n.nodeType !== Node.ELEMENT_NODE) return "";

        const cls = typeof n.className === "string" ? n.className : (n.className?.baseVal || "");
        if (cls.includes("katex") || cls.includes("math-inline") || cls.includes("math-display")) {
            const tex = n.querySelector('annotation[encoding="application/x-tex"], script[type="math/tex"]');
            if (tex) {
                const t = tex.textContent.trim();
                return cls.includes("-display") ? `\n$$${t}$$\n` : `$${t}$`;
            }
        }

        // A rendered diagram whose raw ```mermaid source was recovered by the pre-pass below —
        // emit the fenced source. Without this the walker hits the rendered <svg> and drops the
        // diagram entirely, so exports lose every chart.
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
            case "code":
                return n.closest("pre") ? kids() : "`" + kids() + "`";
            case "pre": {
                const code = n.querySelector("code");
                const lang = [...(code?.classList || [])].find((c) => c.startsWith("language-"))?.slice(9) || "";
                const txt = (code || n).textContent.replace(/\n$/, "");
                return `\n\`\`\`${lang}\n${txt}\n\`\`\`\n`;
            }
            case "ul": {
                const items = [...n.children].filter((c) => c.tagName === "LI")
                    .map((li) => "- " + conv(li).trim().replace(/\n/g, "\n  "));
                return "\n" + items.join("\n") + "\n";
            }
            case "ol": {
                const items = [...n.children].filter((c) => c.tagName === "LI")
                    .map((li, i) => `${i + 1}. ` + conv(li).trim().replace(/\n/g, "\n   "));
                return "\n" + items.join("\n") + "\n";
            }
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
                return ""; // strips "Copy code" buttons and other UI chrome
            default: return kids();
        }
    }

    const clean = (md) => md.replace(/\n{3,}/g, "\n\n").trim();

    // Recover the RAW ```mermaid source for every rendered diagram and tag its container with
    // data-mk-mermaid so `conv` emits a fenced block instead of dropping the <svg>. ChatGPT renders
    // diagrams to SVG and hides the original fenced source, so we try (in order): a code element
    // already in the DOM, a data-attribute holding the source, and finally clicking the "code"
    // toggle and reading what it reveals. Best-effort — never lets extraction fail.
    async function recoverMermaidSources(rootsArr) {
        const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
        const MERMAID_HEAD = /^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b/;

        // Collect candidate containers that hold a rendered diagram (an <svg> or <canvas> —
        // ChatGPT's newest interactive widgets render to canvas). Wrapper classes/testids have
        // churned repeatedly, so besides the mermaid/diagram selectors we also accept any wrapper
        // whose small header label just reads "Mermaid" (the current widget chrome). A false
        // positive is harmless: containers only get tagged when real mermaid source is found.
        const found = [];
        const seen = new Set();
        const push = (c) => {
            if (c && !seen.has(c) && c.querySelector("svg, canvas")) { seen.add(c); found.push(c); }
        };
        for (const root of rootsArr) {
            for (const c of root.querySelectorAll('.mermaid, [class*="mermaid" i], [data-testid*="mermaid" i], [class*="diagram" i], [data-testid*="diagram" i]')) push(c);
            for (const h of root.querySelectorAll("div, span, header, h1, h2, h3, h4")) {
                if (h.childElementCount === 0 && /^mermaid$/i.test((h.textContent || "").trim())) {
                    let anc = h.parentElement;
                    for (let d = 0; anc && d < 6; d++, anc = anc.parentElement) {
                        if (anc.querySelector("svg, canvas")) { push(anc); break; }
                    }
                }
            }
        }
        // Keep only the innermost matches so we never tag a large wrapper and swallow its siblings.
        const containers = found.filter((a) => !found.some((b) => b !== a && a.contains(b)));

        for (const el of containers) {
            try {
                if (el.dataset.mkMermaid) continue;
                let source = "";

                // 1) Raw source already present in the DOM (possibly hidden). Besides the classic
                //    language-mermaid class, accept any <code>/<pre>/<textarea> inside the widget
                //    whose text starts with a mermaid header keyword.
                const scope = el.closest("[data-message-id]") || el.parentElement || el;
                const codeEl = el.querySelector('code[class*="language-mermaid"]') ||
                    scope.querySelector('code[class*="language-mermaid"]');
                if (codeEl && codeEl.textContent.trim()) source = codeEl.textContent;
                if (!source) {
                    for (const cand of scope.querySelectorAll("code, pre, textarea")) {
                        const t = cand.tagName === "TEXTAREA" ? cand.value : cand.textContent;
                        if (MERMAID_HEAD.test((t || "").trim())) { source = t; break; }
                    }
                }

                // 2) Source stashed in a data-attribute (data-code, data-content, …) on the
                //    container or a close ancestor/descendant.
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

                // 3) Click the "Code" toggle to reveal the source, read it, then toggle back.
                //    ChatGPT's widget header now uses plain text "Code"/"Source" tabs without
                //    aria-labels, so fall back to matching visible button/tab text too.
                if (!source) {
                    let toggle = scope.querySelector(
                        'button[aria-label*="code" i]:not([aria-label*="copy" i]), button[data-testid*="code" i]:not([data-testid*="copy" i]), button[title*="code" i]:not([title*="copy" i])'
                    );
                    if (!toggle) {
                        toggle = [...scope.querySelectorAll('button, [role="tab"], [role="button"]')].find((b) => {
                            const t = (b.textContent || "").trim();
                            return /^(code|source|view code|show code)$/i.test(t);
                        });
                    }
                    if (toggle) {
                        toggle.click();
                        await sleep(350);
                        let revealed = scope.querySelector('code[class*="language-mermaid"]');
                        if (!revealed) {
                            revealed = [...scope.querySelectorAll("code, pre, textarea")].find((c) =>
                                MERMAID_HEAD.test(((c.tagName === "TEXTAREA" ? c.value : c.textContent) || "").trim()));
                        }
                        if (revealed) {
                            const t = revealed.tagName === "TEXTAREA" ? revealed.value : revealed.textContent;
                            if (t && t.trim()) source = t;
                        }
                        toggle.click(); // leave the page as we found it
                        await sleep(150);
                    }
                }

                if (source.trim()) el.dataset.mkMermaid = source.trim();
            } catch { /* non-fatal: a failed recovery just means that diagram is skipped */ }
        }
    }

    const host = location.hostname;

    // Per-site canonical id + brand accent + best-effort model selector — the same data
    // copybutton.js's SITES carries, inlined here because this function is injected standalone.
    const SITE_META = [
        { test: (h) => h.includes("chatgpt.com") || h.includes("chat.openai.com"), id: "chatgpt", accent: "#10a37f", model: '[data-testid="model-switcher-dropdown-button"], [data-testid^="model-switcher"]' },
        { test: (h) => h.includes("gemini.google.com"), id: "gemini", accent: "#1a73e8", model: '[data-test-id="bard-mode-menu-button"], .logo-pill-label-container, .current-mode-title' },
        { test: (h) => h.includes("claude.ai"), id: "claude", accent: "#d97757", model: '[data-testid="model-selector-dropdown"]' },
        { test: (h) => h.includes("copilot.microsoft.com"), id: "copilot", accent: "#0f6cbd", model: null },
    ];

    // Source context Marksmith applies on ingest — captured off the live page here, since it's all
    // gone once the reply is plain Markdown. `root` (the reply element) supplies font + direction;
    // the rest comes from the page/tab. Shape matches ClipboardSourceMeta.Wire on the app side.
    function buildMeta(root) {
        const sm = SITE_META.find((s) => s.test(host)) || {};
        const cs = root ? getComputedStyle(root) : null;
        let title = (document.title || "").replace(/^\(\d+\)\s*/, "")
            .replace(/\s*[-–|]\s*(ChatGPT|OpenAI|Gemini|Google Gemini|Claude|Copilot|Microsoft Copilot)\s*$/i, "").trim();
        if (/^(chatgpt|gemini|claude|copilot|new chat|new conversation)$/i.test(title)) title = "";
        let model = "";
        if (sm.model) {
            const el = document.querySelector(sm.model);
            const t = (el?.textContent || "").replace(/\s+/g, " ").trim();
            if (t && t.length <= 40) model = t;
        }
        return {
            font: cs?.fontFamily || "",
            source: sm.id || "",
            model,
            title: title.slice(0, 200),
            lang: (document.documentElement.lang || "").trim(),
            dir: cs?.direction || "",
            accent: sm.accent || "",
        };
    }

    if (mode === "selection") {
        const sel = getSelection();
        const selText = sel?.toString();
        if (selText?.trim()) {
            const anchor = sel.anchorNode?.nodeType === 1 ? sel.anchorNode : sel.anchorNode?.parentElement;
            return { ok: true, markdown: selText.trim(), meta: buildMeta(anchor || document.body) };
        }
        return { ok: false, error: "Nothing selected." };
    }

    let roots = [];
    if (host.includes("chatgpt.com") || host.includes("chat.openai.com")) {
        roots = [...document.querySelectorAll('[data-message-author-role="assistant"] .markdown, [data-message-author-role="assistant"] .prose')];
    } else if (host.includes("gemini.google.com")) {
        roots = [...document.querySelectorAll("message-content .markdown")];
        if (!roots.length) roots = [...document.querySelectorAll("message-content, model-response")];
    } else if (host.includes("claude.ai")) {
        roots = [...document.querySelectorAll('[data-testid="assistant-message"], .font-claude-message')];
    } else if (host.includes("copilot.microsoft.com")) {
        // Copilot's DOM churns; try the stable-ish data attributes first, then class heuristics.
        roots = [...document.querySelectorAll('[data-content="ai-message"], [data-testid="ai-message"]')];
        if (!roots.length) roots = [...document.querySelectorAll('[class*="ai-message"]')];
    }

    if (!roots.length) {
        const sel = getSelection()?.toString();
        if (sel?.trim()) return { ok: true, markdown: sel.trim(), meta: buildMeta(null) };
        return { ok: false, error: "No assistant message found — select text and use the right-click menu instead." };
    }

    if (mode === "latest") roots = roots.slice(-1);
    await recoverMermaidSources(roots);
    return { ok: true, markdown: clean(roots.map(conv).join("\n\n---\n\n")), meta: buildMeta(roots[0]) };
}
