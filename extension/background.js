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
        ],
    });
});

chrome.action.onClicked.addListener((tab) => grabAndSend(tab, "latest"));

chrome.contextMenus.onClicked.addListener((info, tab) => {
    if (info.menuItemId === "mdpdfm-selection") grabAndSend(tab, "selection");
    else if (info.menuItemId === "mdpdfm-conversation") grabAndSend(tab, "all");
});

// Governance relay: the content script (governance.js) sends metadata-only usage reports; we
// forward them to the configured collector. Content scripts can hit 127.0.0.1 directly, but
// routing through the service worker keeps the collector URL/config in one place.
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg?.type !== "governance-report") return;
    (async () => {
        try {
            const managed = await chrome.storage.managed.get(null).catch(() => ({}));
            const sync = await chrome.storage.sync.get(null).catch(() => ({}));
            const cfg = { ...sync, ...managed };
            const base = cfg.collectorUrl || `http://127.0.0.1:${cfg.port || DEFAULT_PORT}`;
            const resp = await fetch(base.replace(/\/$/, "") + "/api/governance/report", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify(msg.payload),
            });
            sendResponse({ ok: resp.ok });
        } catch (e) {
            sendResponse({ ok: false, error: e.message });
        }
    })();
    return true; // async response
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
        const resp = await fetch(`http://127.0.0.1:${port}/api/ingest`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ markdown: extracted.markdown, output: output || undefined }),
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

    if (mode === "selection") {
        const sel = getSelection()?.toString();
        if (sel?.trim()) return { ok: true, markdown: sel.trim() };
        return { ok: false, error: "Nothing selected." };
    }

    const host = location.hostname;
    let roots = [];
    if (host.includes("chatgpt.com") || host.includes("chat.openai.com")) {
        roots = [...document.querySelectorAll('[data-message-author-role="assistant"] .markdown, [data-message-author-role="assistant"] .prose')];
    } else if (host.includes("gemini.google.com")) {
        roots = [...document.querySelectorAll("message-content .markdown")];
        if (!roots.length) roots = [...document.querySelectorAll("message-content, model-response")];
    } else if (host.includes("claude.ai")) {
        roots = [...document.querySelectorAll('[data-testid="assistant-message"], .font-claude-message')];
    }

    if (!roots.length) {
        const sel = getSelection()?.toString();
        if (sel?.trim()) return { ok: true, markdown: sel.trim() };
        return { ok: false, error: "No assistant message found — select text and use the right-click menu instead." };
    }

    if (mode === "latest") roots = roots.slice(-1);
    return { ok: true, markdown: clean(roots.map(conv).join("\n\n---\n\n")) };
}
