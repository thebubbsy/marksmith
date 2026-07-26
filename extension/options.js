const $ = (id) => document.getElementById(id);
let toastTimer = null;

const THEMES_FALLBACK = ["GitHub Light", "GitHub Dark", "Solarized Light", "Solarized Dark",
    "Dracula", "Monokai Pro", "Cyberpunk", "Nordic", "Forest", "Obsidian"];

// Output profile fields -> the keys the app's OutputOverride expects (camelCase).
const OUT_DEFAULTS = {
    theme: "GitHub Light", themeLightInfluence: false, contentWidth: 800, a4FixedWidth: true, unlimitedHeight: true,
    includeToc: false, showAttribution: true, noEmoji: false, dashMode: 0, dashCustom: "",
    headingShift: 0, boldMode: 0, italicMode: 0, normalizeLlm: true,
    format: "docx", outputFolder: "", mermaidDocxMode: 1, oversizedDiagramMode: 1,
    diagramGridSize: 2, smartConnectors: true, connectorRouting: "default", connectorArrowhead: "default",
    brandCoverPage: false
};
const NUMS = new Set(["contentWidth", "dashMode", "headingShift", "boldMode", "italicMode", "mermaidDocxMode", "oversizedDiagramMode", "diagramGridSize"]);
const BOOLS = new Set(["a4FixedWidth", "unlimitedHeight", "includeToc", "showAttribution", "noEmoji", "normalizeLlm", "themeLightInfluence", "smartConnectors", "brandCoverPage"]);

function fillThemes(list, selected) {
    const sel = $("o_theme");
    if (!sel) return;
    sel.innerHTML = "";
    for (const name of list) {
        const o = document.createElement("option");
        o.value = name; o.textContent = name;
        if (name === selected) o.selected = true;
        sel.appendChild(o);
    }
}

function readOutput() {
    const out = {};
    for (const k of Object.keys(OUT_DEFAULTS)) {
        const el = $("o_" + k);
        if (!el) {
            out[k] = OUT_DEFAULTS[k];
            continue;
        }
        if (BOOLS.has(k)) out[k] = el.checked;
        else if (NUMS.has(k)) out[k] = Number(el.value);
        else out[k] = el.value;
    }
    return out;
}

function writeOutput(out) {
    for (const k of Object.keys(OUT_DEFAULTS)) {
        const el = $("o_" + k);
        if (!el) continue;
        if (BOOLS.has(k)) el.checked = !!out[k];
        else el.value = out[k] !== undefined && out[k] !== null ? out[k] : OUT_DEFAULTS[k];
    }
}

function showToast(msg) {
    const toast = $("toast");
    if (!toast) return;
    toast.textContent = msg || "✓ Options saved successfully";
    toast.classList.add("show");
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
        toast.classList.remove("show");
    }, 2500);
}

async function init() {
    // Load from chrome.storage.sync with fallback to chrome.storage.local
    let s = {};
    try {
        s = await chrome.storage.sync.get({ port: 47821, autoSendIdle: false, idleSeconds: 20, output: OUT_DEFAULTS });
    } catch {
        s = await chrome.storage.local.get({ port: 47821, autoSendIdle: false, idleSeconds: 20, output: OUT_DEFAULTS });
    }

    if ($("port")) $("port").value = s.port || 47821;
    if ($("autoSendIdle")) $("autoSendIdle").checked = !!s.autoSendIdle;
    if ($("idleSeconds")) $("idleSeconds").value = s.idleSeconds || 20;

    const out = { ...OUT_DEFAULTS, ...s.output };

    // Populate themes from the running app if reachable, else a static list.
    let themes = THEMES_FALLBACK;
    try {
        const r = await fetch(`http://127.0.0.1:${s.port || 47821}/api/themes`);
        if (r.ok) themes = await r.json();
    } catch { /* app not running — fallback list */ }
    fillThemes(themes, out.theme);
    writeOutput(out);
    if ($("o_theme")) $("o_theme").value = out.theme;
}

if ($("save")) {
    $("save").addEventListener("click", async () => {
        const port = Math.min(65535, Math.max(1024, Number($("port") ? $("port").value : 47821) || 47821));
        const idleSeconds = Math.min(300, Math.max(5, Number($("idleSeconds") ? $("idleSeconds").value : 20) || 20));
        const payload = {
            port,
            idleSeconds,
            autoSendIdle: $("autoSendIdle") ? $("autoSendIdle").checked : false,
            output: readOutput(),
        };

        // Persist to both sync and local storage for maximum reliability across browsers/offline
        try { await chrome.storage.sync.set(payload); } catch {}
        try { await chrome.storage.local.set(payload); } catch {}

        showToast("✓ Options saved successfully");
    });
}

init();
