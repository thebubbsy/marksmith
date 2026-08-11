// Marksmith WASM — client-side file download bridge.
// Blazor passes the .docx bytes (byte[] → Uint8Array) and we hand them to the browser.
window.marksmithDownload = function (filename, bytes) {
    const blob = new Blob([bytes], { type: "application/vnd.openxmlformats-officedocument.wordprocessingml.document" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
};

// Settings persistence bridge (WasmSettingsStore).
window.marksmithStorageGet = function (key) {
    try { return localStorage.getItem(key) || ""; } catch { return ""; }
};
window.marksmithStorageSet = function (key, value) {
    try { localStorage.setItem(key, value); } catch { /* private mode */ }
};

// Post-render enhancement: KaTeX math, highlighted code, mermaid diagrams.
// Runs after Blazor injects the preview HTML; re-run on every preview update.
window.marksmithEnhancePreview = function () {
    try {
        if (window.katex) {
            document.querySelectorAll(".katex-mathml").forEach(() => {});
            document.querySelectorAll("math").forEach((el) => {
                const tex = el.getAttribute("alt");
                const inline = !el.closest(".math-display");
                if (tex && window.katex) {
                    try { katex.render(tex, el.parentElement, { displayMode: !inline, throwOnError: false }); } catch {}
                }
            });
        }
        if (window.hljs) {
            document.querySelectorAll("pre code:not(.hljs)").forEach((el) => {
                try { hljs.highlightElement(el); } catch {}
            });
        }
        if (window.mermaid) {
            const els = document.querySelectorAll("pre > code.language-mermaid");
            els.forEach((el) => {
                const src = el.textContent || "";
                if (!src.trim()) return;
                const pre = el.closest("pre");
                const holder = document.createElement("div");
                holder.className = "mermaid";
                holder.textContent = src;
                if (pre) { pre.replaceWith(holder); }
                else { el.replaceWith(holder); }
            });
            if (document.querySelectorAll(".mermaid").length) {
                try { mermaid.initialize({ startOnLoad: false, theme: "default" }); mermaid.run({ nodes: document.querySelectorAll(".mermaid") }); } catch {}
            }
        }
    } catch { /* enhancement is best-effort */ }
};
