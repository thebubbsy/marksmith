// Capture hygiene helpers (pip stripping, DLP scan, history, filename) — pure functions,
// also unit-tested directly by node.
importScripts("hygiene.js");

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
        title: "Send selection to MarkSmith",
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
chrome.alarms.create("command-poll", { periodInMinutes: 0.5 }); // reverse command channel
chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === "health-poll") refreshBadge();
    if (alarm.name === "command-poll") pollCommands();
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

// ── Reverse Command Channel (App → Extension) ──────────────────────────────
// The desktop app enqueues jobs (e.g. 'theme-prompt') via its loopback API. We poll
// every 30 s, attempt auto-injection into the active web-AI composer, and fall back
// to a 1-click 'Copy Prompt' stored in chrome.storage.local for the popup UI.
async function pollCommands() {
    try {
        const port = await getPort();
        const resp = await fetch(`http://127.0.0.1:${port}/api/commands`);
        if (!resp.ok) return;
        const jobs = await resp.json();
        if (!Array.isArray(jobs) || !jobs.length) return;
        for (const job of jobs) {
            if (job.type === "theme-prompt") await handleThemePrompt(port, job);
        }
    } catch {
        // App offline or transient network error — silently skip.
    }
}

async function handleThemePrompt(port, job) {
    // Attempt auto-inject into the active web-AI chat composer & submit.
    const tab = await activeTab();
    if (tab?.id && tab.url) {
        try {
            const [res] = await chrome.scripting.executeScript({
                target: { tabId: tab.id },
                func: injectPromptIntoComposer,
                args: [job.prompt],
            });
            if (res?.result?.ok) {
                // Success — post the AI's reply back once it arrives (best-effort poll).
                scheduleResultCollection(port, job.id, tab.id);
                return;
            }
        } catch {
            // DOM injection failed — fall through to manual fallback.
        }
    }
    // Fallback: store prompt for the popup's 'Copy Prompt' UI.
    await chrome.storage.local.set({
        pendingPrompt: { id: job.id, prompt: job.prompt, ts: Date.now() },
    });
    notify("Marksmith: Prompt Ready", "Auto-inject unavailable — open the popup to copy the theme prompt.");
}

// Injected into the page: locate the composer via selectors.js, paste prompt, click send.
function injectPromptIntoComposer(prompt) {
    // selectors.js is loaded as a content script before this executes.
    const sites = globalThis.MARKSMITH_SITES || [];
    const site = sites.find((s) => s.host.test(location.hostname));
    if (!site) return { ok: false, reason: "unsupported-site" };

    const composer = document.querySelector(site.composer);
    if (!composer) return { ok: false, reason: "composer-not-found" };

    // Set value depending on element type.
    if (composer.tagName === "TEXTAREA" || composer.tagName === "INPUT") {
        const nativeSetter = Object.getOwnPropertyDescriptor(
            HTMLTextAreaElement.prototype, "value"
        )?.set || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
        if (nativeSetter) nativeSetter.call(composer, prompt);
        else composer.value = prompt;
        composer.dispatchEvent(new Event("input", { bubbles: true }));
    } else {
        // contenteditable div
        composer.focus();
        composer.textContent = prompt;
        composer.dispatchEvent(new InputEvent("input", { bubbles: true, inputType: "insertText" }));
    }

    // Click send after a short delay so the framework registers the input.
    const sendBtn = document.querySelector(site.sendBtn);
    if (!sendBtn) return { ok: false, reason: "send-button-not-found" };
    setTimeout(() => sendBtn.click(), 300);
    return { ok: true };
}

// After auto-inject, wait for the AI reply then post it back to the app.
function scheduleResultCollection(port, jobId, tabId) {
    // Give the AI ~20 s to respond, then grab the latest reply.
    setTimeout(async () => {
        try {
            const [res] = await chrome.scripting.executeScript({
                target: { tabId },
                func: extractMarkdown,
                args: ["latest"],
            });
            const reply = res?.result?.ok ? res.result.markdown : "";
            await fetch(`http://127.0.0.1:${port}/api/commands/result`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ id: jobId, replyMarkdown: reply }),
            });
        } catch {
            // Best-effort — the user can still manually paste in the app.
        }
    }, 20000);
}

// ── context-menu clicks ─────────────────────────────────────────────────────
chrome.contextMenus.onClicked.addListener((info, tab) => {
    if (info.menuItemId === "mdpdfm-selection") {
        grabAndSend(tab, "selection", { selectionText: info.selectionText, notify: true });
    } else if (info.menuItemId === "mdpdfm-conversation") {
        grabAndSend(tab, "all", { notify: true });
    } else if (info.menuItemId === "mdpdfm-dl-pdf") {
        downloadFromTab(tab, "latest", "pdf", { notify: true });
    } else if (info.menuItemId === "mdpdfm-dl-docx") {
        downloadFromTab(tab, "latest", "docx", { notify: true });
    }
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

    if (msg?.type === "send-text") {
        (async () => {
            sendResponse(await sendDirectMarkdown(msg.text, msg.meta || {}, { notify: msg.notify !== false }));
        })();
        return true;
    }

    if (msg?.type === "download-history") {
        (async () => {
            try {
                const list = await getHistory();
                const entry = list[msg.index || 0];
                if (!entry) return sendResponse({ ok: false, error: "No capture at that index." });
                sendResponse(await convertAndDownload(entry.text, msg.format || "pdf", entry.meta, { notify: false }));
            } catch (e) {
                sendResponse({ ok: false, error: e.message });
            }
        })();
        return true;
    }

    if (msg?.type === "history") {
        (async () => sendResponse({ ok: true, list: await getHistory() }))();
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

async function extractFromTab(tabId, mode, imgMode) {
    const [res] = await chrome.scripting.executeScript({
        target: { tabId },
        func: extractMarkdown,
        args: [mode, imgMode || "url"],
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

    // DLP: flag PII before the user commits to a send/download (opt-out in Options).
    let dlp = [];
    try {
        const { dlpScan } = await chrome.storage.sync.get({ dlpScan: true });
        if (dlpScan) dlp = scanDlp(extracted.markdown);
    } catch { /* best effort */ }
    return { ok: true, markdown: extracted.markdown, meta: extracted.meta, cls, dlp };
}

// ── direct send to the app (/api/ingest) ────────────────────────────────────
async function sendDirectMarkdown(markdown, meta = {}, opts = {}) {
    const showNotify = opts.notify !== false;
    if (!markdown || !markdown.trim()) {
        if (showNotify) notify("Nothing to send", "The selection is empty.");
        return { ok: false, error: "Empty content." };
    }

    const cfg = await chrome.storage.sync.get({ stripPips: true });
    if (cfg.stripPips) markdown = stripCitationPips(markdown);
    await pushHistory({ markdown, meta });

    try {
        const { port, output } = await chrome.storage.sync.get({ port: DEFAULT_PORT, output: {} });
        const m = meta || {};
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
            body: JSON.stringify({ markdown, output: hasAny ? merged : undefined }),
        });
        if (!resp.ok) throw new Error(`API returned HTTP ${resp.status}`);
        if (showNotify) notify("Sent to Marksmith ✓", `${markdown.length.toLocaleString()} chars ingested — check the preview.`);
        return { ok: true, chars: markdown.length };
    } catch (e) {
        const err = "Is the app running with Automation → Local REST API enabled? " + e.message;
        if (showNotify) notify("Marksmith unreachable", err);
        return { ok: false, error: err };
    }
}

// ── send to the app (/api/ingest) ───────────────────────────────────────────
async function grabAndSend(tab, mode, opts = {}) {
    const showNotify = opts.notify !== false;

    // Image-embedding preference (selection sends only). Default is "ask": when the selection
    // actually contains images, pop a one-tap chooser on the page (link vs embed, with an optional
    // "remember my choice"). Linking (URL) stays the default — embedding is opt-in and best-effort.
    let imgMode = "url";
    if (mode === "selection") {
        try {
            const { imgEmbedPref } = await chrome.storage.sync.get({ imgEmbedPref: "ask" });
            if (imgEmbedPref === "ask") {
                const qPromise = chrome.scripting.executeScript({ target: { tabId: tab.id }, func: detectAndQueryImagePref });
                const timeoutPromise = new Promise((resolve) => setTimeout(() => resolve([{ result: { needed: false, choice: "url" } }]), 3500));
                const [q] = await Promise.race([qPromise, timeoutPromise]);
                const ans = q?.result || {};
                if (ans.needed) {
                    imgMode = ans.choice === "base64" ? "base64" : "url";
                    if (ans.remember) await chrome.storage.sync.set({ imgEmbedPref: imgMode });
                }
            } else if (imgEmbedPref === "base64") {
                imgMode = "base64";
            }
        } catch { imgMode = "url"; /* never let the prompt block a send */ }
    }

    let extracted;
    try {
        extracted = await extractFromTab(tab.id, mode, imgMode);
    } catch (e) {
        if (opts.selectionText) {
            extracted = { ok: true, markdown: opts.selectionText, meta: { title: tab?.title || "Selection", source: "selection" } };
        } else {
            if (showNotify) notify("Cannot read this page", e.message);
            return { ok: false, error: "Cannot read this page — " + e.message };
        }
    }

    if (!extracted?.ok) {
        if (opts.selectionText) {
            extracted = { ok: true, markdown: opts.selectionText, meta: { title: tab?.title || "Selection", source: "selection" } };
        } else {
            const err = extracted?.error || "Could not find assistant content on this page.";
            if (showNotify) notify("Nothing to send", err);
            return { ok: false, error: err };
        }
    }

    // Opt-in embedding: the in-page pass already inlined any CORS-friendly images; now use the
    // optional host permission to fetch the rest (e.g. GitHub's camo proxy) from the background.
    if (imgMode === "base64") {
        extracted.markdown = await embedRemainingRemoteImages(extracted.markdown);
    }

    return await sendDirectMarkdown(extracted.markdown, extracted.meta, { notify: showNotify });
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
    // Capture hygiene: strip citation pips (opt-out in Options) and keep a local history entry.
    const cfg = await chrome.storage.sync.get({ stripPips: true });
    if (cfg.stripPips) markdown = stripCitationPips(markdown);
    await pushHistory({ markdown, meta });

    const { port, output } = await chrome.storage.sync.get({ port: DEFAULT_PORT, output: {} });
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

// The optional host permission that lets the background fetch image bytes regardless of CORS.
// Requested lazily, and only, when the user opts into embedding. Best-effort: the request needs a
// user gesture, so if it's triggered outside one it simply returns false and we fall back to
// linking — the Options page is the guaranteed place to grant it.
async function ensureImagePermission() {
    const origins = ["<all_urls>"];
    try {
        if (await chrome.permissions.contains({ origins })) return true;
        return await chrome.permissions.request({ origins });
    } catch { return false; }
}

// Background embedding pass (only when the user opts in AND grants the optional host permission):
// fetch any images the page-context pass couldn't — CORS-blocked hosts like GitHub's camo proxy —
// and swap their links for base64 data-URIs. The service worker isn't bound by CORS for hosts it
// has permission for, which is what makes embedding work everywhere. Best-effort: no permission or
// any fetch failure just leaves the link in place, so the send never breaks.
async function embedRemainingRemoteImages(markdown) {
    if (!(await ensureImagePermission())) return markdown;
    const re = /(!\[[^\]]*\])\((https?:\/\/[^)\s]+)\)/g;
    const urls = [...new Set([...markdown.matchAll(re)].map((m) => m[2]))];
    if (!urls.length) return markdown;
    const dataByUrl = new Map();
    await Promise.all(urls.map(async (url) => {
        try {
            const resp = await fetch(url, { credentials: "omit" });
            if (!resp.ok) return;
            const buf = await resp.arrayBuffer();
            if (!buf.byteLength || buf.byteLength > 20 * 1024 * 1024) return; // skip empty / >20 MB
            const mime = (resp.headers.get("content-type") || "image/png").split(";")[0].trim() || "image/png";
            dataByUrl.set(url, bufferToDataUrl(buf, mime));
        } catch { /* leave as a link */ }
    }));
    return markdown.replace(re, (full, alt, url) => (dataByUrl.has(url) ? `${alt}(${dataByUrl.get(url)})` : full));
}


function notify(title, message) {
    chrome.notifications.create({ type: "basic", iconUrl: "icons/icon128.png", title, message });
}

// Injected into the page when a selection-send runs with the image preference set to "ask".
// Counts the images the selection actually captured and, if any, pops a small chooser asking
// whether to send them as links (URL — small, the default) or embedded pixels (base64 —
// permanent, larger), with an optional "remember my choice". Resolves { needed, count, choice,
// remember }. Best-effort: any failure just means we proceed with the default (link).
async function detectAndQueryImagePref() {
    let count = 0;
    try {
        const sel = getSelection();
        if (sel && sel.rangeCount > 0) {
            const holder = document.createElement("div");
            for (let i = 0; i < sel.rangeCount; i++) holder.appendChild(sel.getRangeAt(i).cloneContents());
            count = holder.querySelectorAll("img").length;
        }
    } catch { count = 0; }
    if (!count) return { needed: false, count: 0, choice: "url", remember: false };

    return await new Promise((resolve) => {
        const STYLE = `
            #mk-imgpref-root{position:fixed;inset:0;z-index:2147483647;display:flex;align-items:center;justify-content:center;background:rgba(8,9,14,.55);font-family:'Segoe UI',system-ui,-apple-system,sans-serif;}
            #mk-imgpref-root .mk-card{width:470px;max-width:calc(100vw - 40px);background:#1c1c28;color:#e8e8f0;border:1px solid #2a2a3a;border-radius:16px;padding:22px 24px;box-shadow:0 24px 70px rgba(0,0,0,.55);}
            #mk-imgpref-root h3{margin:0 0 4px;font-size:17px;font-weight:700;}
            #mk-imgpref-root .mk-sub{margin:0 0 16px;font-size:13px;color:#9a9ab0;line-height:1.5;}
            #mk-imgpref-root .mk-opt{display:block;border:1px solid #2a2a3a;border-radius:12px;padding:12px 14px;margin-bottom:10px;cursor:pointer;transition:border-color .15s,background .15s;}
            #mk-imgpref-root .mk-opt:hover{border-color:#7c4dff;background:rgba(124,77,255,.09);}
            #mk-imgpref-root .mk-opt b{display:block;font-size:14px;margin-bottom:4px;}
            #mk-imgpref-root .mk-opt .mk-rec{font-style:normal;font-size:10.5px;font-weight:700;letter-spacing:.4px;text-transform:uppercase;color:#3fb950;background:rgba(63,185,80,.14);border:1px solid rgba(63,185,80,.35);border-radius:20px;padding:2px 8px;margin-left:6px;vertical-align:1px;}
            #mk-imgpref-root .mk-opt .mk-desc{display:block;font-size:12.5px;color:#9a9ab0;line-height:1.5;}
            #mk-imgpref-root .mk-remember{display:flex;align-items:center;gap:8px;font-size:13px;color:#c8c8d8;margin:14px 0 2px;cursor:pointer;user-select:none;}
            #mk-imgpref-root .mk-remember input{accent-color:#7c4dff;width:15px;height:15px;cursor:pointer;}
            #mk-imgpref-root .mk-actions{display:flex;justify-content:flex-end;margin-top:12px;}
            #mk-imgpref-root .mk-skip{background:none;border:none;color:#9a9ab0;font-size:13px;cursor:pointer;padding:4px 6px;margin-right:auto;}
            #mk-imgpref-root .mk-skip:hover{color:#e8e8f0;}
        `;
        const styleEl = document.createElement("style");
        styleEl.textContent = STYLE;
        const root = document.createElement("div");
        root.id = "mk-imgpref-root";
        root.innerHTML = `
            <div class="mk-card" role="dialog" aria-modal="true" aria-label="Image embedding preference">
                <h3>\u{1F4F7} <span id="mk-imgpref-count"></span> in your selection</h3>
                <p class="mk-sub">How should Marksmith receive the images? You can change this later in the extension's Options.</p>
                <div class="mk-opt" data-choice="url">
                    <b>Link to the image<span class="mk-rec">recommended</span></b>
                    <span class="mk-desc">Sends the image's web address. Small &amp; fast, keeps the document lean. Downside: the picture breaks if the page is private, deleted, or the link expires.</span>
                </div>
                <div class="mk-opt" data-choice="base64">
                    <b>Embed the image itself</b>
                    <span class="mk-desc">Downloads the pixels and bakes them into the document — permanent, works offline forever. Downside: a much larger, slower send; protected images may still fall back to a link.</span>
                </div>
                <label class="mk-remember"><input type="checkbox" id="mk-imgpref-remember"> Remember my choice — don't ask again</label>
                <div class="mk-actions"><button class="mk-skip" type="button">Skip — just link them</button></div>
            </div>`;
        document.documentElement.appendChild(styleEl);
        document.documentElement.appendChild(root);
        root.querySelector("#mk-imgpref-count").textContent = count + (count === 1 ? " image" : " images");
        const isRemember = () => root.querySelector("#mk-imgpref-remember").checked;
        const finish = (choice, rem) => {
            try { root.remove(); styleEl.remove(); } catch {}
            document.removeEventListener("keydown", onKey, true);
            resolve({ needed: true, count, choice, remember: !!rem });
        };
        const onKey = (e) => { if (e.key === "Escape") finish("url", false); };
        document.addEventListener("keydown", onKey, true);
        root.addEventListener("click", (e) => { if (e.target === root) finish("url", false); });
        root.querySelector(".mk-skip").addEventListener("click", () => finish("url", false));
        for (const opt of root.querySelectorAll(".mk-opt")) {
            opt.addEventListener("click", () => finish(opt.dataset.choice, isRemember()));
        }
    });
}

// Injected into the page. Finds assistant messages via per-site selectors and converts their
// HTML to Markdown with a small recursive walker (headings, lists, tables, code fences, links).
// mode: "latest" = newest assistant reply, "all" = whole conversation, "selection" = user selection.
// Async because the mermaid pre-pass may need to click a "code" toggle and wait for the raw
// fenced source to appear before the walker runs.
async function extractMarkdown(mode, imgMode) {
    // First token of real Mermaid diagram source — shared by the fence labeller in `conv`
    // (GitHub keeps the source in <pre lang="mermaid"> with no language-* class) and by the
    // rendered-diagram source recovery below.
    const MERMAID_HEAD = /^\s*(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|journey|gitGraph|mindmap|timeline|quadrantChart|requirementDiagram|C4Context|xychart-beta|sankey-beta|block-beta|packet-beta|kanban|architecture-beta)\b/;

    // Resolve the REAL, absolute URL of an <img> so a highlighted image ships as a working
    // ![alt](url) embed. Lazy loaders park the true URL in a data-* attribute while `src` holds
    // a placeholder; responsive images expose the size actually rendered via `currentSrc`
    // (srcset/<picture>). Relative URLs are resolved against the page so the embed survives
    // leaving the site. Tracking pixels / tiny inline placeholders yield "" so we never ship a
    // 1×1 grey dot as if it were content.
    function bestFromSrcset(set) {
        let best = "", bestW = -1;
        for (const cand of set.split(",")) {
            const [url, desc = ""] = cand.trim().split(/\s+/);
            if (!url) continue;
            const w = parseFloat(desc) || 0; // "2x" → 2, "300w" → 300 — good enough to pick the biggest
            if (w >= bestW) { bestW = w; best = url; }
        }
        return best;
    }
    function bestImgSrc(n) {
        const abs = (u) => { try { return new URL(u, document.baseURI).href; } catch { return ""; } };
        // 1) Lazy-load data attributes (real URL parked until the image scrolls into view).
        for (const attr of ["data-src", "data-lazy-src", "data-original", "data-lazy", "data-srcset", "data-lazyload"]) {
            const v = (n.getAttribute(attr) || "").trim();
            if (!v) continue;
            const pick = v.includes(",") ? bestFromSrcset(v) : v.split(/\s+/)[0];
            if (pick && !pick.startsWith("data:")) return abs(pick);
        }
        // 2) What the browser actually rendered (respects srcset/<picture>), else the authored src.
        const rendered = n.currentSrc || n.src || "";
        if (!rendered) return "";
        if (rendered.startsWith("data:")) return rendered.length >= 200 ? rendered : ""; // tiny inline = placeholder
        if (n.getAttribute("width") === "1" && n.getAttribute("height") === "1") return ""; // tracking pixel
        return rendered; // currentSrc / src are already absolute
    }

    // Fetch each captured image's bytes and stash a base64 data-URI on the clone so `conv` embeds
    // the pixels instead of a link. Used only when the user opts into embedding. Best-effort per
    // image: anything that can't be fetched (CORS, auth, timeout, oversize) silently falls back to
    // its URL so the send never fails.
    async function embedImagesAsBase64(root) {
        const toDataUrl = async (url) => {
            const ctrl = new AbortController();
            const timer = setTimeout(() => ctrl.abort(), 12000);
            try {
                const resp = await fetch(url, { mode: "cors", credentials: "omit", signal: ctrl.signal });
                if (!resp.ok) return "";
                const blob = await resp.blob();
                if (!blob.size || blob.size > 20 * 1024 * 1024) return ""; // skip empty / >20 MB
                return await new Promise((res) => {
                    const fr = new FileReader();
                    fr.onload = () => res(typeof fr.result === "string" ? fr.result : "");
                    fr.onerror = () => res("");
                    fr.readAsDataURL(blob);
                });
            } catch { return ""; } finally { clearTimeout(timer); }
        };
        await Promise.all([...root.querySelectorAll("img")].map(async (img) => {
            try {
                const url = bestImgSrc(img);
                if (!url || url.startsWith("data:")) return; // nothing to fetch / already inline
                const data = await toDataUrl(url);
                if (data) img.dataset.mkImg = data;
            } catch { /* fall back to the URL in conv */ }
        }));
    }

    function conv(n) {
        if (n.nodeType === Node.TEXT_NODE) return n.textContent;
        if (n.nodeType !== Node.ELEMENT_NODE) return "";

        const cls = typeof n.className === "string" ? n.className : (n.className?.baseVal || "");
        // Screen-reader-only chrome (GitHub's "Loading" spinners, "Copy code" labels, …) is a
        // hidden duplicate of visible content by definition — drop it so it never leaks into
        // the Markdown as stray text.
        if (/(^|\s)(sr-only|visually-hidden)(-\S+)?(\s|$)/.test(cls)) return "";
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
                if (n.dataset?.mkMermaidSource || n.querySelector("[data-mk-mermaid-source]")) return "";
                const code = n.querySelector("code");
                let lang = [...(code?.classList || [])].find((c) => c.startsWith("language-"))?.slice(9) || "";
                const txt = (code || n).textContent.replace(/\n$/, "");
                if (!lang) {
                    const attrLang = (n.getAttribute("lang") || code?.getAttribute("lang") || "").toLowerCase();
                    if (attrLang === "mermaid" || MERMAID_HEAD.test(txt.trim())) lang = "mermaid";
                }
                if (/^(code\s*snippet|code|text)$/i.test(lang.trim()) && MERMAID_HEAD.test(txt.trim())) lang = "mermaid";
                if (lang === "mermaid") {
                    const scope = n.closest('[data-message-id], [data-message-author-role], message-content, model-response') || n.parentElement;
                    if (scope && scope.querySelector('[data-mk-mermaid]')) return "";
                }
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
            case "img": {
                const src = (n.dataset && n.dataset.mkImg) || bestImgSrc(n);
                return src ? `![${n.alt || ""}](${src})` : "";
            }
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
        const grab = (c) => (c.tagName === "TEXTAREA" ? c.value : c.textContent) || "";

        // Pull the message's RAW markdown straight out of React's internal state — this is the
        // source of truth ChatGPT rendered the diagram from, and it survives every DOM redesign
        // (this is the case that used to leak the bare word "Mermaid" into exports). Walk up from
        // the reply element to the nearest fiber, then climb the fiber tree until we find a string
        // prop holding a ```mermaid fence. Best-effort — returns "" when the site isn't React.
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
                    if (typeof obj === "string") return obj.includes("```mermaid") ? obj : "";
                    if (typeof obj !== "object") return "";
                    let keys;
                    try { keys = Object.keys(obj); } catch { return ""; }
                    if (keys.length > 60) keys = keys.slice(0, 60);
                    for (const k of keys) {
                        let v; try { v = obj[k]; } catch { continue; }
                        if (typeof v === "string" && v.includes("```mermaid")) return v;
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
            const re = /```mermaid[^\n]*\r?\n([\s\S]*?)```/gi;
            let m;
            while ((m = re.exec(md)) !== null) out.push(m[1].trim());
            return out;
        };

        for (const root of rootsArr) {
            // The message's raw markdown (via React) is the most reliable source of the diagram
            // source — grab it once per reply and split out its ```mermaid fences so we can hand
            // the Nth fence to the Nth detected diagram even when the widget DOM hides the source.
            const fences = fencesFrom(reactMarkdown(root));

            // Collect candidate containers that hold a rendered diagram (an <svg> or <canvas> —
            // ChatGPT's newest interactive widgets render to canvas). Wrapper classes/testids have
            // churned repeatedly, so we combine several signals: mermaid/diagram class & testid
            // selectors, mermaid.js's own svg id ("mermaid-…"), and any wrapper whose header label
            // just reads "Mermaid". A false positive is harmless: containers only get tagged when
            // real mermaid source is found.
            const found = [];
            const seen = new Set();
            const push = (c) => {
                if (c && !seen.has(c) && c.querySelector("svg, canvas")) { seen.add(c); found.push(c); }
            };
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
            // Keep only the innermost matches so we never tag a large wrapper and swallow its siblings.
            const containers = found.filter((a) => !found.some((b) => b !== a && a.contains(b)));

            let fenceIdx = 0;
            for (const el of containers) {
                try {
                    if (el.dataset.mkMermaid) { fenceIdx++; continue; }
                    let source = "";

                    // 1) Raw source already present in the DOM (possibly hidden). Besides the classic
                    //    language-mermaid class, accept any <code>/<pre>/<textarea> whose text starts
                    //    with a mermaid header keyword. Search the whole message scope, not just the
                    //    diagram wrapper — the source often sits in a sibling panel.
                    const scope = el.closest('[data-message-id], [data-message-author-role]') || el.parentElement || el;
                    const codeEl = scope.querySelector('code[class*="language-mermaid"]');
                    if (codeEl && codeEl.textContent.trim()) { source = codeEl.textContent; codeEl.dataset.mkMermaidSource = "true"; }
                    if (!source) {
                        for (const cand of scope.querySelectorAll("code, pre, textarea")) {
                            if (MERMAID_HEAD.test(grab(cand).trim())) { source = grab(cand); cand.dataset.mkMermaidSource = "true"; break; }
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
                    //    aria-labels, so fall back to matching visible button/tab text too (leniently,
                    //    and poll for up to ~1.5s rather than a single fixed wait).
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

                    // 4) Fall back to the message's raw markdown pulled from React state — the Nth
                    //    detected diagram gets the Nth ```mermaid fence. This is the fix for widgets
                    //    that never expose their source in the DOM at all.
                    if (!source && fences.length > fenceIdx) source = fences[fenceIdx];

                    if (source.trim()) el.dataset.mkMermaid = source.trim();
                    fenceIdx++;
                } catch { fenceIdx++; /* non-fatal: a failed recovery just means that diagram is skipped */ }
            }
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
        let sel = getSelection();
        let selText = sel?.toString() || "";
        let anchor = sel?.anchorNode?.nodeType === 1 ? sel.anchorNode : sel?.anchorNode?.parentElement;

        if (!selText.trim()) {
            const activeEl = document.activeElement;
            if (activeEl && (activeEl.tagName === "TEXTAREA" || activeEl.tagName === "INPUT")) {
                const start = activeEl.selectionStart;
                const end = activeEl.selectionEnd;
                if (typeof start === "number" && typeof end === "number" && end > start) {
                    selText = activeEl.value.substring(start, end);
                    anchor = activeEl;
                }
            } else if (activeEl?.shadowRoot) {
                const shadowSel = activeEl.shadowRoot.getSelection ? activeEl.shadowRoot.getSelection() : null;
                if (shadowSel?.toString()?.trim()) {
                    sel = shadowSel;
                    selText = shadowSel.toString();
                    anchor = shadowSel.anchorNode?.nodeType === 1 ? shadowSel.anchorNode : shadowSel.anchorNode?.parentElement;
                }
            }
        }

        if (selText.trim()) {
            let markdown = "";
            try {
                if (sel && sel.rangeCount > 0) {
                    const holder = document.createElement("div");
                    for (let i = 0; i < sel.rangeCount; i++) {
                        holder.appendChild(sel.getRangeAt(i).cloneContents());
                    }
                    for (const pre of document.querySelectorAll('pre[lang="mermaid"]')) {
                        const zone = pre.closest(".js-render-enrichment-fallback")
                            || pre.parentElement?.parentElement || pre.parentElement || pre;
                        let hit = false, inClone = false;
                        for (let i = 0; i < sel.rangeCount; i++) {
                            const r = sel.getRangeAt(i);
                            if (r.intersectsNode(pre)) inClone = true;
                            else if (r.intersectsNode(zone)) hit = true;
                        }
                        if (hit && !inClone) holder.appendChild(pre.cloneNode(true));
                    }
                    if (imgMode === "base64") await embedImagesAsBase64(holder);
                    markdown = clean(conv(holder)).trim();
                }
            } catch { markdown = ""; }
            if (!markdown) markdown = selText.trim();
            return { ok: true, markdown, meta: buildMeta(anchor || document.body) };
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
