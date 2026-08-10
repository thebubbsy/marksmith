const $ = (id) => document.getElementById(id);
let toastTimer = null;

const THEMES_FALLBACK = ["GitHub Light", "GitHub Dark", "Solarized Light", "Solarized Dark",
    "Dracula", "Monokai Pro", "Cyberpunk", "Nordic", "Forest", "Obsidian"];

// Output profile — OVERRIDE model. Every field is optional: when a control is left on
// "App default" (empty value) the key is OMITTED from the stored profile and from the payload
// sent to the app, so the app's own saved setting wins (OutputOverride contract: a missing
// field falls back to whatever the app currently has). Only explicit choices are sent — the
// extension tracks the app's settings live instead of snapshotting them.
// Types: "str" = pass through, "int" = Number(), "bool" = tri-state select ("1"/"0"/"").
const FIELD_TYPES = {
    theme: "str", contentWidth: "int", dashMode: "int", dashCustom: "str",
    headingShift: "int", format: "str", mermaidDocxMode: "int", oversizedDiagramMode: "int",
    mermaidEnabled: "bool", connectorRouting: "str", connectorArrowhead: "str",
    fontPreset: "str", pdfPageNumberPosition: "str", fileNameTemplate: "str",
    boldMode: "int", italicMode: "int", authorName: "str", outputFolder: "str",
    pdfHeaderTemplate: "str", pdfFooterTemplate: "str", pdfEncrypt: "bool",
    pdfUserPassword: "str", pdfOwnerPassword: "str",
    pdfAllowPrinting: "bool", pdfAllowCopying: "bool", pdfAllowModifying: "bool",
    themeLightInfluence: "bool", brandCoverPage: "bool", smartConnectors: "bool",
    a4FixedWidth: "bool", unlimitedHeight: "bool", includeToc: "bool", showWordCount: "bool",
    showAttribution: "bool", normalizeLlm: "bool", noEmoji: "bool", pageBorder: "bool",
    trackChanges: "bool"
};

function fillThemes(list, selected) {
    const sel = $("o_theme");
    if (!sel) return;
    sel.innerHTML = "";
    const def = document.createElement("option");
    def.value = ""; def.textContent = "App default";
    sel.appendChild(def);
    for (const name of list) {
        const o = document.createElement("option");
        o.value = name; o.textContent = name;
        if (name === selected) o.selected = true;
        sel.appendChild(o);
    }
}

// Build the sparse override object to persist + send. Empty control = App default = omitted.
function readOutput() {
    const out = {};
    for (const [k, type] of Object.entries(FIELD_TYPES)) {
        const el = $("o_" + k);
        if (!el) continue;
        const raw = el.value;
        if (raw === "" || raw === null || raw === undefined) continue; // App default
        if (type === "bool") out[k] = raw === "1";
        else if (type === "int") {
            const n = Number(raw);
            if (Number.isFinite(n)) out[k] = n;
        }
        else out[k] = raw;
    }
    return out;
}

// Reflect a stored (sparse) profile onto the controls. Missing keys land on "App default".
function writeOutput(out) {
    for (const [k, type] of Object.entries(FIELD_TYPES)) {
        const el = $("o_" + k);
        if (!el) continue;
        const v = out ? out[k] : undefined;
        if (v === undefined || v === null || v === "") { el.value = ""; continue; }
        el.value = type === "bool" ? (v ? "1" : "0") : String(v);
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
        s = await chrome.storage.sync.get({ port: 47821, autoSendIdle: false, idleSeconds: 20, imgEmbedPref: "ask", output: {} });
    } catch {
        s = await chrome.storage.local.get({ port: 47821, autoSendIdle: false, idleSeconds: 20, imgEmbedPref: "ask", output: {} });
    }

    if ($("port")) $("port").value = s.port || 47821;
    if ($("autoSendIdle")) $("autoSendIdle").checked = !!s.autoSendIdle;
    if ($("idleSeconds")) $("idleSeconds").value = s.idleSeconds || 20;
    if ($("imgEmbedPref")) $("imgEmbedPref").value = ["ask", "url", "base64"].includes(s.imgEmbedPref) ? s.imgEmbedPref : "ask";

    // Sparse overrides only — never merge in defaults. A field that isn't stored tracks the
    // app's live setting. (Profiles saved by older extension versions stored every key; those
    // simply load as explicit overrides and can be cleared with "Reset to app defaults".)
    const out = s.output || {};

    // Populate themes from the running app if reachable, else a static list.
    let themes = THEMES_FALLBACK;
    try {
        const r = await fetch(`http://127.0.0.1:${s.port || 47821}/api/themes`);
        if (r.ok) themes = await r.json();
    } catch { /* app not running — fallback list */ }
    fillThemes(themes, out.theme);
    writeOutput(out);
    if ($("o_theme")) $("o_theme").value = out.theme || "";

    // Show how many fields are currently overridden, so the model is visible at a glance.
    updateOverrideCount(out);
}

function updateOverrideCount(out) {
    const el = $("overrideCount");
    if (!el) return;
    const n = out ? Object.keys(out).filter((k) => k in FIELD_TYPES).length : 0;
    el.textContent = n === 0
        ? "Everything tracks the app's settings."
        : `${n} field${n === 1 ? "" : "s"} overridden — the rest track the app's settings.`;
}

if ($("resetOutput")) {
    $("resetOutput").addEventListener("click", () => {
        for (const k of Object.keys(FIELD_TYPES)) {
            const el = $("o_" + k);
            if (el) el.value = "";
        }
        updateOverrideCount({});
        showToast("All fields reset to App default — click Save to apply");
    });
}

if ($("save")) {
    $("save").addEventListener("click", async () => {
        const port = Math.min(65535, Math.max(1024, Number($("port") ? $("port").value : 47821) || 47821));
        const idleSeconds = Math.min(300, Math.max(5, Number($("idleSeconds") ? $("idleSeconds").value : 20) || 20));
        const output = readOutput();
        const payload = {
            port,
            idleSeconds,
            autoSendIdle: $("autoSendIdle") ? $("autoSendIdle").checked : false,
            imgEmbedPref: $("imgEmbedPref") && ["ask", "url", "base64"].includes($("imgEmbedPref").value) ? $("imgEmbedPref").value : "ask",
            output,
        };

        // Embedding defeats CORS via the optional host permission — request it here (a guaranteed
        // user gesture in the Options UI) the moment the user commits to "always embed".
        if (payload.imgEmbedPref === "base64") {
            try { await chrome.permissions.request({ origins: ["<all_urls>"] }); } catch {}
        }

        // Persist to both sync and local storage for maximum reliability across browsers/offline
        try { await chrome.storage.sync.set(payload); } catch {}
        try { await chrome.storage.local.set(payload); } catch {}

        updateOverrideCount(output);
        showToast("✓ Options saved successfully");
    });
}

init();
