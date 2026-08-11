// Marksmith Connector — capture hygiene helpers (pure functions + storage helpers).
//
// Loaded by background.js via importScripts("hygiene.js") AND directly by the unit tests
// (node), so these must have no top-level chrome/dependencies. The chrome.* storage calls live
// inside functions and are already best-effort (try/catch) so a failure never breaks a send.

// 1) Citation pips: 【7†source】-style artifacts AI chats append to claims. The desktop app also
//    cleans them, but stripping at the source means preview/copy/history are already clean.
const PIP_RE = /【[^】]*(?:†|‡|\u2020)[^】]*】/g; // 【7†source】 / unicode dagger variants
const PIP_ALT_RE = /【\s*\d+\s*source】/gi;      // 【7source】 variants

function stripCitationPips(md) {
    return (md || "")
        .replace(PIP_RE, "")
        .replace(PIP_ALT_RE, "")
        .replace(/\n{3,}/g, "\n\n")
        .trim();
}

// 2) DLP scan: flag PII (cards, emails, API keys, phones) before content leaves the browser.
//    Warnings only — the user decides whether to send.
const DLP_PATTERNS = [
    { id: "cc", label: "credit-card number", re: /\b(?:\d[ -]?){13,16}\b/ },
    { id: "email", label: "email address", re: /\b[A-Za-z0-9.+-]+@[A-Za-z0-9-]+\.[A-Za-z0-9.-]{2,}\b/ },
    { id: "apikey", label: "API key", re: /\b(?:sk-[A-Za-z0-9]{16,}|AIza[0-9A-Za-z_-]{20,}|ghp_[A-Za-z0-9]{20,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})\b/ },
    { id: "phone", label: "phone number", re: /\b\+?\d{1,3}[-. (]*\d{3}[-. )]*\d{4}\b/ },
];

function scanDlp(markdown) {
    const hits = [];
    for (const p of DLP_PATTERNS) {
        if (p.re.test(markdown || "")) hits.push(p.label);
    }
    return hits;
}

// 3) Capture history: last N extractions stored locally for re-download / re-copy.
const HISTORY_KEY = "captureHistory";
const HISTORY_MAX = 25;

async function pushHistory({ markdown, meta, ts }) {
    try {
        const { [HISTORY_KEY]: list = [] } = await chrome.storage.local.get(HISTORY_KEY);
        list.unshift({ text: (markdown || "").slice(0, 200_000), meta: meta || null, ts: ts || Date.now() });
        if (list.length > HISTORY_MAX) list.length = HISTORY_MAX;
        await chrome.storage.local.set({ [HISTORY_KEY]: list });
    } catch { /* best effort */ }
}

async function getHistory() {
    try {
        const { [HISTORY_KEY]: list = [] } = await chrome.storage.local.get(HISTORY_KEY);
        return list;
    } catch { return []; }
}

// 4) Smart output naming: conversation title + date, sanitized; dedupe is handled by the
//    download manager's uniquify conflict action.
function buildFilename(meta, format) {
    const raw = (meta?.title || "").trim() || "marksmith-export";
    const base = raw
        .replace(/[\\/:*?"<>|\r\n]/g, " ")
        .replace(/\s+/g, " ")
        .trim()
        .slice(0, 80) || "marksmith-export";
    const stamp = new Date();
    const date = `${stamp.getFullYear()}-${String(stamp.getMonth() + 1).padStart(2, "0")}-${String(stamp.getDate()).padStart(2, "0")}`;
    return `${base} ${date}.${format}`;
}

// Expose for node unit tests (the browser context gets these as globals via importScripts).
if (typeof module !== "undefined" && module.exports) {
    module.exports = { stripCitationPips, scanDlp, pushHistory, getHistory, buildFilename, DLP_PATTERNS, PIP_RE, PIP_ALT_RE };
}
