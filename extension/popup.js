// Marksmith Connector — popup control center.
// Talks to the background service worker (which owns extraction + the local API), and shows:
//   • live connection status (GET /api/health)
//   • what's detected on the active tab (source, model, char count, AI-classification, math)
//   • one-click Send-to-app, direct in-browser downloads (PDF/DOCX/PPTX/EPUB), and Copy-as-Markdown.

const $ = (id) => document.getElementById(id);

let mode = "latest";          // "latest" | "all"
let currentMarkdown = "";     // last successful extraction, reused for copy
let currentMeta = null;
let connected = false;
let hasContent = false;       // did we find an assistant reply on this tab?

const dlButtons = Array.from(document.querySelectorAll(".dl"));
const modeButtons = $("modeSeg") ? Array.from($("modeSeg").querySelectorAll("button")) : [];

// ── messaging helper ────────────────────────────────────────────────────────
// The background service worker is the single owner of extraction + fetch logic,
// so the popup stays a thin UI. Every round-trip is a chrome.runtime message.
function ask(msg) {
    return new Promise((resolve) => {
        try {
            chrome.runtime.sendMessage(msg, (resp) => {
                if (chrome.runtime.lastError) resolve({ ok: false, error: chrome.runtime.lastError.message });
                else resolve(resp || { ok: false, error: "No response from background." });
            });
        } catch (e) {
            resolve({ ok: false, error: e.message });
        }
    });
}

// ── toast ───────────────────────────────────────────────────────────────────
function toast(text, kind = "dim") {
    const el = $("toast");
    el.className = kind;
    el.textContent = text;
}

// ── connection status ───────────────────────────────────────────────────────
function setConn(state, label) { // state: "ok" | "err" | "busy"
    const el = $("conn");
    el.className = "conn " + state;
    $("connTxt").textContent = label;
}

async function checkHealth() {
    setConn("busy", "Checking…");
    const r = await ask({ type: "health" });
    connected = !!r.ok;
    if (connected) {
        setConn("ok", "Connected");
        $("helpCard").classList.add("hidden");
    } else {
        setConn("err", "Offline");
        $("helpCard").classList.remove("hidden");
        $("extId").textContent = chrome.runtime.id;
    }
    refreshButtons();
}

// ── page inspection (extract + classify) ────────────────────────────────────
async function inspect() {
    $("srcBody").classList.remove("hidden");
    $("srcEmpty").classList.add("hidden");
    $("srcName").textContent = "Scanning…";
    $("srcModel").textContent = "";
    $("srcChars").innerHTML = '<span class="spinner"></span>';
    $("chipConf").classList.add("hidden");
    $("chipMath").classList.add("hidden");

    const r = await ask({ type: "inspect", mode });
    if (!r.ok) {
        hasContent = false;
        currentMarkdown = "";
        $("srcBody").classList.add("hidden");
        const empty = $("srcEmpty");
        empty.classList.remove("hidden");
        empty.textContent = r.error || "No assistant reply found on this page.";
        refreshButtons();
        return;
    }

    hasContent = true;
    currentMarkdown = r.markdown || "";
    currentMeta = r.meta || null;

    const meta = currentMeta;
    $("srcName").textContent = prettySource(meta?.source);
    $("srcModel").textContent = meta?.model || "";
    $("srcChars").textContent = `${(currentMarkdown.length).toLocaleString()} chars`;

    const cls = r.cls;
    if (cls) {
        if (cls.confidence != null) {
            const c = $("chipConf");
            c.classList.remove("hidden");
            c.textContent = `${Math.round(cls.confidence * 100)}% ${cls.source || ""}`.trim();
        }
        if (cls.hasMath) $("chipMath").classList.remove("hidden");
    }

    // Capture preview: raw markdown, truncated for the popup (the full text is what ships).
    $("previewBody").textContent = currentMarkdown.length > 4000
        ? currentMarkdown.slice(0, 4000) + "\n… (preview truncated — the full capture is sent)"
        : currentMarkdown;
    $("previewCard").classList.toggle("hidden", currentMarkdown.length === 0);

    // DLP: sensitive content flagged before anything leaves the browser.
    if (r.dlp && r.dlp.length) {
        $("dlpBody").textContent =
            `This capture looks like it contains: ${r.dlp.join(", ")}. Sending shares it with the conversion pipeline — confirm before sending sensitive data.`;
        $("dlpCard").classList.remove("hidden");
    } else {
        $("dlpCard").classList.add("hidden");
    }

    renderHistory();
    refreshButtons();
}

function prettySource(id) {
    const map = { chatgpt: "ChatGPT", gemini: "Gemini", claude: "Claude", copilot: "Copilot", selection: "Selection" };
    return map[id] || (id ? id[0].toUpperCase() + id.slice(1) : "Selection");
}

// ── enable/disable actions based on state ───────────────────────────────────
function refreshButtons() {
    const ready = connected && hasContent;
    $("sendBtn").disabled = !ready;
    $("copyBtn").disabled = !hasContent;
    for (const b of dlButtons) b.disabled = !ready;
}

// ── actions ─────────────────────────────────────────────────────────────────
async function doSend() {
    $("sendBtn").disabled = true;
    $("sendTxt").textContent = "Sending…";
    const r = await ask({ type: "send", mode });
    if (r.ok) {
        toast(`Sent ${(r.chars || 0).toLocaleString()} chars to Marksmith ✓`, "ok");
        renderHistory();
    } else {
        toast(r.error || "Send failed.", "err");
    }
    $("sendTxt").textContent = "Send to Marksmith";
    refreshButtons();
}

async function doDownload(format, btn) {
    const label = btn.querySelector(".fmt").textContent;
    btn.disabled = true;
    btn.querySelector(".fmt").textContent = "…";
    const r = await ask({ type: "download", mode, format });
    if (r.ok) {
        toast(`Downloading ${r.filename} ✓`, "ok");
        renderHistory();
    } else {
        toast(r.error || `${label} download failed.`, "err");
    }
    btn.querySelector(".fmt").textContent = label;
    refreshButtons();
}

async function doCopy() {
    if (!currentMarkdown) return;
    try {
        await navigator.clipboard.writeText(currentMarkdown);
        toast("Markdown copied to clipboard ✓", "ok");
    } catch {
        // Fallback for older clipboard permissions in popups.
        const ta = document.createElement("textarea");
        ta.value = currentMarkdown;
        document.body.appendChild(ta);
        ta.select();
        document.execCommand("copy");
        ta.remove();
        toast("Markdown copied to clipboard ✓", "ok");
    }
}

// ── capture history ─────────────────────────────────────────────────────────
const escHtml = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

async function renderHistory() {
    const r = await ask({ type: "history" });
    const list = (r.ok && r.list) || [];
    const el = $("histList");
    if (!list.length) {
        el.innerHTML = '<div class="help">No captures yet — grab something from an AI chat.</div>';
        return;
    }
    el.innerHTML = "";
    list.forEach((e, i) => {
        const title = (e.meta && e.meta.title) || "capture";
        const when = new Date(e.ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
        const row = document.createElement("div");
        row.className = "hist-row";
        const t = document.createElement("span");
        t.className = "hist-t";
        t.title = title;
        t.textContent = title;
        const m = document.createElement("span");
        m.className = "hist-meta";
        m.textContent = `${(e.text || "").length.toLocaleString()}c · ${when}`;
        const btns = document.createElement("span");
        btns.className = "hist-btns";
        for (const f of ["pdf", "docx"]) {
            const b = document.createElement("button");
            b.className = "mini";
            b.textContent = f.toUpperCase();
            b.addEventListener("click", async () => {
                b.disabled = true;
                const rr = await ask({ type: "download-history", index: i, format: f });
                toast(rr.ok ? `Downloading ${rr.filename} ✓` : (rr.error || "Download failed."), rr.ok ? "ok" : "err");
                b.disabled = false;
            });
            btns.appendChild(b);
        }
        row.append(t, m, btns);
        el.appendChild(row);
    });
}

// ── wiring ──────────────────────────────────────────────────────────────────
$("sendBtn").addEventListener("click", doSend);
$("copyBtn").addEventListener("click", doCopy);
$("histClear").addEventListener("click", async () => {
    try { await chrome.storage.local.remove("captureHistory"); } catch { /* best effort */ }
    renderHistory();
    toast("Capture history cleared", "ok");
});
for (const b of dlButtons) {
    b.addEventListener("click", () => doDownload(b.dataset.format, b));
}

// Mode segmented control — re-inspect when the scope changes.
for (const b of modeButtons) {
    b.addEventListener("click", () => {
        if (b.dataset.mode === mode) return;
        mode = b.dataset.mode;
        for (const x of modeButtons) x.classList.toggle("on", x === b);
        inspect();
    });
}

$("openOptions").addEventListener("click", (e) => {
    e.preventDefault();
    chrome.runtime.openOptionsPage();
});

$("extId").addEventListener("click", () => {
    navigator.clipboard.writeText(chrome.runtime.id).then(
        () => toast("Extension ID copied ✓", "ok"),
        () => toast("Select and copy the ID manually.", "err"),
    );
});

// Version in the footer.
const ver = chrome.runtime.getManifest().version;
$("verTxt").textContent = `v${ver}`;

// Kick off: connection first (fast), check if selection is present, then inspect.
checkHealth();
(async () => {
    try {
        const selTest = await ask({ type: "inspect", mode: "selection" });
        if (selTest?.ok && selTest?.markdown?.trim()) {
            mode = "selection";
            for (const x of modeButtons) x.classList.toggle("on", x.dataset.mode === "selection");
        }
    } catch {}
    inspect();
})();
