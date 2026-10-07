// Marksmith Connector — "MarkSmith app · live" tab of the Options page.
//
// Draws MarkSmith's own settings from the app's schema (GET /api/extension/settings) and applies
// each change the moment it's made (POST /api/extension/settings), so the desktop app's panels
// update while you watch. The app owns the list — labels, help text, choices, ranges and which
// rows are Pro — so a new desktop option appears here without touching the extension.
//
// The pure helpers at the bottom are exported for node tests; everything that touches the DOM or
// chrome.* lives inside initAppSettings().

(function () {
    "use strict";

    // ── pure helpers (unit-tested) ──────────────────────────────────────────
    // A control's raw value -> the JSON value the app expects, or { error } when it can't be sent.
    function coerceValue(field, raw) {
        switch (field.kind) {
            case "toggle":
                return { value: !!raw };
            case "number": {
                const text = String(raw ?? "").trim();
                if (!/^-?\d+$/.test(text)) return { error: `${field.label} must be a whole number.` };
                const n = Number(text);
                if ((field.min != null && n < field.min) || (field.max != null && n > field.max))
                    return { error: `${field.label} must be between ${field.min} and ${field.max}.` };
                return { value: n };
            }
            case "choice":
                return (field.choices || []).some((c) => c.value === raw)
                    ? { value: raw }
                    : { error: `Pick one of the choices for ${field.label}.` };
            case "text": {
                const text = String(raw ?? "");
                if (field.maxLength && text.length > field.maxLength)
                    return { error: `${field.label} is limited to ${field.maxLength} characters.` };
                return { value: text };
            }
            default:
                return { error: "Unknown setting type." };
        }
    }

    // How the row's status line reads after a save attempt.
    function describeResult(field, result) {
        if (!result) return { kind: "err", text: "MarkSmith didn't answer. Is it still running?" };
        const refusal = (result.rejected || []).find((r) => r.key === field.key);
        if (refusal) return { kind: "err", text: refusal.reason };
        if ((result.applied || []).includes(field.key)) return { kind: "ok", text: "Saved to MarkSmith" };
        return { kind: "err", text: "MarkSmith didn't apply this change." };
    }

    if (typeof module !== "undefined" && module.exports) {
        module.exports = { coerceValue, describeResult };
        return;
    }

    // ── DOM / chrome ────────────────────────────────────────────────────────
    const $ = (id) => document.getElementById(id);
    let port = 47821;
    let schema = null;           // last GET/POST response
    const rows = new Map();      // key -> { field, input, status, timer }
    let loading = false;

    async function api(method, body) {
        const resp = await fetch(`http://127.0.0.1:${port}/api/extension/settings`, {
            method,
            headers: body ? { "Content-Type": "application/json" } : undefined,
            body: body ? JSON.stringify(body) : undefined,
        });
        let json = null;
        try { json = await resp.json(); } catch { /* empty body */ }
        if (!resp.ok) {
            const err = new Error(resp.status === 404
                ? "This version of MarkSmith can't be configured from the browser yet. Update the app."
                : (json && json.error) || `MarkSmith returned HTTP ${resp.status}.`);
            err.status = resp.status;
            throw err;
        }
        return json;
    }

    function setState(state, text) {
        const pill = $("appStatus");
        if (!pill) return;
        pill.className = "header-status-pill live-" + state;
        pill.textContent = text;
    }

    async function load({ quiet = false } = {}) {
        if (loading) return;
        loading = true;
        if (!quiet) setState("busy", "Connecting to MarkSmith…");
        try {
            schema = await api("GET");
            render();
            setState("ok", "Connected · changes apply to MarkSmith instantly");
        } catch (e) {
            schema = null;
            renderOffline(e);
            setState("err", e.status ? "MarkSmith refused the request" : "MarkSmith isn't running");
        } finally {
            loading = false;
        }
    }

    function renderOffline(e) {
        const grid = $("appGrid");
        grid.innerHTML = "";
        rows.clear();
        const card = document.createElement("div");
        card.className = "card offline-card";
        card.innerHTML = `
            <div class="card-header"><h2>Can't reach MarkSmith</h2></div>
            <div class="card-desc"></div>
            <div class="hint">Make sure the app is running with <b>Source &rarr; Automation &rarr; Local REST API</b> on,
            and that the port under <b>Connection</b> on the Extension tab matches (default 47821).</div>
            <div class="offline-actions"><button type="button" id="appRetry">Try again</button></div>`;
        card.querySelector(".card-desc").textContent = e && e.status ? e.message : "The extension couldn't connect to the MarkSmith desktop app.";
        grid.appendChild(card);
        card.querySelector("#appRetry").addEventListener("click", () => load());
    }

    function render() {
        const grid = $("appGrid");
        const focusedKey = document.activeElement?.dataset?.key;
        grid.innerHTML = "";
        rows.clear();

        const lic = schema.license || {};
        const banner = $("appLicense");
        if (banner) {
            banner.textContent = lic.canAutomate
                ? `MarkSmith ${lic.edition}: every option below is available.`
                : `MarkSmith ${lic.edition || "Free"}: rows marked PRO need MarkSmith Pro or the trial. Email is free on every plan.`;
        }

        for (const group of schema.groups || []) {
            const card = document.createElement("section");
            card.className = "card app-card";
            card.setAttribute("aria-labelledby", "grp-" + group.id);
            const head = document.createElement("div");
            head.className = "card-header";
            head.innerHTML = `<h2 id="grp-${group.id}"></h2>`;
            head.querySelector("h2").textContent = group.title;
            const desc = document.createElement("div");
            desc.className = "card-desc";
            desc.textContent = group.description || "";
            card.append(head, desc);
            for (const field of group.fields || []) card.appendChild(renderField(field));
            grid.appendChild(card);
        }
        if (focusedKey) rows.get(focusedKey)?.input.focus();
    }

    function proBadge(field) {
        if (!field.pro) return null;
        const b = document.createElement("span");
        b.className = "pro-badge";
        b.textContent = "PRO";
        b.title = field.pro === "automation" ? "Needs MarkSmith Pro or the trial" : "Part of MarkSmith Pro";
        return b;
    }

    function renderField(field) {
        const wrap = document.createElement("div");
        wrap.className = "field app-field" + (field.locked ? " locked" : "");
        const id = "app_" + field.key;

        const label = document.createElement("label");
        label.htmlFor = id;
        label.textContent = field.label;
        const badge = proBadge(field);
        if (badge) label.appendChild(badge);

        const hint = document.createElement("div");
        hint.className = "hint";
        hint.textContent = field.locked ? `${field.description} Needs MarkSmith Pro: start the trial in the app.` : field.description;

        const status = document.createElement("div");
        status.className = "field-status";
        status.setAttribute("role", "status");
        status.setAttribute("aria-live", "polite");

        let input;
        if (field.kind === "toggle") {
            wrap.classList.add("toggle-row");
            const txt = document.createElement("div");
            txt.className = "txt";
            txt.append(label, hint, status);
            const sw = document.createElement("label");
            sw.className = "switch";
            input = document.createElement("input");
            input.type = "checkbox";
            input.checked = !!field.value;
            const slider = document.createElement("span");
            slider.className = "slider";
            sw.append(input, slider);
            wrap.append(txt, sw);
        } else {
            if (field.kind === "choice") {
                input = document.createElement("select");
                for (const c of field.choices || []) {
                    const o = document.createElement("option");
                    o.value = c.value;
                    o.textContent = c.label;
                    input.appendChild(o);
                }
                input.value = field.value ?? "";
            } else {
                input = document.createElement("input");
                input.type = field.kind === "number" ? "number" : "text";
                if (field.kind === "number") {
                    if (field.min != null) input.min = field.min;
                    if (field.max != null) input.max = field.max;
                    input.step = 1;
                }
                if (field.placeholder) input.placeholder = field.placeholder;
                if (field.maxLength) input.maxLength = field.maxLength;
                input.spellcheck = false;
                input.value = field.value ?? "";
            }
            wrap.append(label, input, hint, status);
        }
        input.id = id;
        input.dataset.key = field.key;
        hint.id = id + "_hint";
        input.setAttribute("aria-describedby", hint.id);
        input.disabled = !!field.locked;

        const row = { field, input, status, timer: null, sent: field.value };
        rows.set(field.key, row);

        if (field.kind === "toggle" || field.kind === "choice") {
            input.addEventListener("change", () => save(row, field.kind === "toggle" ? input.checked : input.value));
        } else if (field.kind === "number") {
            input.addEventListener("change", () => save(row, input.value));
        } else {
            // Text saves as you pause typing, and again (if needed) when you leave the box.
            input.addEventListener("input", () => {
                clearTimeout(row.timer);
                showStatus(row, "busy", "Saving when you pause…");
                row.timer = setTimeout(() => save(row, input.value), 700);
            });
            input.addEventListener("change", () => { clearTimeout(row.timer); save(row, input.value); });
        }
        return wrap;
    }

    function showStatus(row, kind, text) {
        row.status.className = "field-status " + kind;
        row.status.textContent = text;
        clearTimeout(row.statusTimer);
        if (kind === "ok") row.statusTimer = setTimeout(() => { row.status.textContent = ""; row.status.className = "field-status"; }, 1800);
    }

    function restore(row) {
        const v = row.sent;
        if (row.field.kind === "toggle") row.input.checked = !!v;
        else row.input.value = v ?? "";
    }

    async function save(row, raw) {
        const { value, error } = coerceValue(row.field, raw);
        if (error) { showStatus(row, "err", error); return; }
        if (value === row.sent) { showStatus(row, "", ""); return; }
        showStatus(row, "busy", "Saving…");
        let result = null;
        try {
            result = await api("POST", { changes: { [row.field.key]: value } });
        } catch (e) {
            showStatus(row, "err", e.message && e.status ? e.message : "MarkSmith isn't running, so nothing changed.");
            restore(row);
            return;
        }
        const outcome = describeResult(row.field, result);
        if (result && result.settings) {
            schema = result.settings;
            // Keep every other row in step with what the app now holds (a theme change can move
            // other values), without re-rendering the row being edited.
            for (const g of schema.groups || []) for (const f of g.fields || []) {
                const other = rows.get(f.key);
                if (!other) continue;
                other.sent = f.value;
                other.field.locked = f.locked;
                other.input.disabled = !!f.locked;
                if (other !== row && document.activeElement !== other.input) restore(other);
            }
        }
        if (outcome.kind === "ok") row.sent = value;
        else restore(row);
        showStatus(row, outcome.kind, outcome.text);
    }

    async function initAppSettings(currentPort) {
        port = currentPort || 47821;
        await load();
        // The desktop app may have changed since this tab was last looked at: refresh quietly on
        // return, unless the user is mid-edit.
        document.addEventListener("visibilitychange", () => {
            if (document.visibilityState === "visible" && $("appPanel") && !$("appPanel").hidden
                && !document.activeElement?.dataset?.key) load({ quiet: true });
        });
    }

    window.MarksmithAppSettings = { init: initAppSettings, reload: () => load() };
})();
