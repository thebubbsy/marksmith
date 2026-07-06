const $ = (id) => document.getElementById(id);
const status = $("status");

const THEMES_FALLBACK = ["GitHub Light", "GitHub Dark", "Solarized Light", "Solarized Dark",
    "Dracula", "Monokai Pro", "Cyberpunk", "Nordic", "Forest", "Obsidian"];

// Output profile fields -> the keys the app's OutputOverride expects (camelCase).
const OUT_DEFAULTS = {
    theme: "GitHub Light", contentWidth: 800, a4FixedWidth: true, unlimitedHeight: true,
    includeToc: false, showAttribution: true, noEmoji: false, dashMode: 0, dashCustom: "",
    headingShift: 0, boldMode: 0, italicMode: 0, normalizeLlm: true,
    format: "pdf", outputFolder: "", mermaidDocxMode: 1,
};
const NUMS = new Set(["contentWidth", "dashMode", "headingShift", "boldMode", "italicMode", "mermaidDocxMode"]);
const BOOLS = new Set(["a4FixedWidth", "unlimitedHeight", "includeToc", "showAttribution", "noEmoji", "normalizeLlm"]);

function fillThemes(list, selected) {
    const sel = $("o_theme");
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
        if (BOOLS.has(k)) out[k] = el.checked;
        else if (NUMS.has(k)) out[k] = Number(el.value);
        else out[k] = el.value;
    }
    return out;
}

function writeOutput(out) {
    for (const k of Object.keys(OUT_DEFAULTS)) {
        const el = $("o_" + k);
        if (BOOLS.has(k)) el.checked = !!out[k];
        else el.value = out[k];
    }
}

async function init() {
    const s = await chrome.storage.sync.get({ port: 47821, autoSendIdle: false, idleSeconds: 20, output: OUT_DEFAULTS });
    $("port").value = s.port;
    $("autoSendIdle").checked = s.autoSendIdle;
    $("idleSeconds").value = s.idleSeconds;
    const out = { ...OUT_DEFAULTS, ...s.output };

    // Populate themes from the running app if reachable, else a static list.
    let themes = THEMES_FALLBACK;
    try {
        const r = await fetch(`http://127.0.0.1:${s.port}/api/themes`);
        if (r.ok) themes = await r.json();
    } catch { /* app not running — fallback list */ }
    fillThemes(themes, out.theme);
    writeOutput(out);
    $("o_theme").value = out.theme;
}

$("save").addEventListener("click", async () => {
    const port = Math.min(65535, Math.max(1024, Number($("port").value) || 47821));
    const idleSeconds = Math.min(300, Math.max(5, Number($("idleSeconds").value) || 20));
    await chrome.storage.sync.set({
        port, idleSeconds,
        autoSendIdle: $("autoSendIdle").checked,
        output: readOutput(),
    });
    status.textContent = "Saved ✓";
    setTimeout(() => (status.textContent = ""), 1500);
});

init();
