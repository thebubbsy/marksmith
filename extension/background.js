// Marksmith Connector — grabs assistant replies from AI chat sites, converts them to
// Markdown, and POSTs them to the Marksmith desktop app's local API (/api/ingest).

const DEFAULT_PORT = 47821;

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
        documentUrlPatterns: [
            "https://chatgpt.com/*",
            "https://chat.openai.com/*",
            "https://gemini.google.com/*",
            "https://claude.ai/*",
            "https://copilot.microsoft.com/*",
        ],
    });
});

chrome.action.onClicked.addListener((tab) => grabAndSend(tab, "latest"));

chrome.contextMenus.onClicked.addListener((info, tab) => {
    if (info.menuItemId === "mdpdfm-selection") grabAndSend(tab, "selection");
    else if (info.menuItemId === "mdpdfm-conversation") grabAndSend(tab, "all");
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    // Attention channel: copybutton.js polls through us (MV3 content scripts can't fetch
    // cross-origin themselves). The app bumps /api/attention when it spots a plain-text paste.
    if (msg?.type === "attention-poll") {
        (async () => {
            try {
                const { port } = await chrome.storage.sync.get({ port: DEFAULT_PORT });
                const resp = await fetch(`http://127.0.0.1:${port}/api/attention`);
                sendResponse(resp.ok ? await resp.json() : { flashTs: 0 });
            } catch {
                sendResponse({ flashTs: 0 }); // app not running — nothing to flash
            }
        })();
        return true; // async response
    }

});

async function grabAndSend(tab, mode) {
    let extracted;
    try {
        const [res] = await chrome.scripting.executeScript({
            target: { tabId: tab.id },
            func: extractMarkdown,
            args: [mode],
        });
        extracted = res?.result;
    } catch (e) {
        notify("Cannot read this page", e.message);
        return;
    }

    if (!extracted?.ok) {
        notify("Nothing to send", extracted?.error || "Could not find assistant content on this page.");
        return;
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
        notify("Sent to Marksmith ✓", `${extracted.markdown.length.toLocaleString()} chars ingested — check the preview.`);
    } catch (e) {
        notify("Marksmith unreachable", "Is the app running with Automation → Local REST API enabled? " + e.message);
    }
}

function notify(title, message) {
    chrome.notifications.create({ type: "basic", iconUrl: "icons/icon128.png", title, message });
}

// Injected into the page. Finds assistant messages via per-site selectors and converts their
// HTML to Markdown with a small recursive walker (headings, lists, tables, code fences, links).
// mode: "latest" = newest assistant reply, "all" = whole conversation, "selection" = user selection.
function extractMarkdown(mode) {
    function conv(n) {
        if (n.nodeType === Node.TEXT_NODE) return n.textContent;
        if (n.nodeType !== Node.ELEMENT_NODE) return "";
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
            case "ul":
                return "\n" + [...n.children].filter((c) => c.tagName === "LI")
                    .map((li) => "- " + conv(li).trim().replace(/\n/g, "\n  ")).join("\n") + "\n";
            case "ol":
                return "\n" + [...n.children].filter((c) => c.tagName === "LI")
                    .map((li, i) => `${i + 1}. ` + conv(li).trim().replace(/\n/g, "\n   ")).join("\n") + "\n";
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
    return { ok: true, markdown: clean(roots.map(conv).join("\n\n---\n\n")), meta: buildMeta(roots[0]) };
}
