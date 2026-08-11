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

// ── mermaid geometry harvest ────────────────────────────────────────────────
// Mirrors the desktop's WebView harvester (HarvestedDiagram contract in Core):
// walk each rendered .mermaid svg and return JSON of {W,H,Nodes,Edges} so the
// export can rebuild the EXACT preview layout as native Word DrawingML shapes.
window.marksmithHarvestMermaid = function () {
    function parsePath(d) {
        // Minimal path sampler: M/L/C/Q/Z tokens; curves sampled at 20 segments.
        const pts = [];
        const re = /([MLCQZ])([^MLCQZ]*)/g;
        let m, cx = 0, cy = 0, sx = 0, sy = 0;
        while ((m = re.exec(d)) !== null) {
            const cmd = m[1], args = (m[2] || "").trim().split(/[\s,]+/).map(Number).filter((n) => !isNaN(n));
            if (cmd === "M") { cx = args[0]; cy = args[1]; sx = cx; sy = cy; pts.push({ x: cx, y: cy }); }
            else if (cmd === "L") {
                for (let i = 0; i + 1 < args.length; i += 2) { cx = args[i]; cy = args[i + 1]; pts.push({ x: cx, y: cy }); }
            }
            else if (cmd === "C" && args.length >= 6) {
                const x0 = cx, y0 = cy;
                for (let i = 0; i + 5 < args.length; i += 6) {
                    const c1x = args[i], c1y = args[i + 1], c2x = args[i + 2], c2y = args[i + 3], x1 = args[i + 4], y1 = args[i + 5];
                    for (let t = 1; t <= 20; t++) {
                        const u = t / 20, v = 1 - u;
                        cx = v * v * v * x0 + 3 * v * v * u * c1x + 3 * v * u * u * c2x + u * u * u * x1;
                        cy = v * v * v * y0 + 3 * v * v * u * c1y + 3 * v * u * u * c2y + u * u * u * y1;
                        pts.push({ x: cx, y: cy });
                    }
                }
            }
            else if (cmd === "Q" && args.length >= 4) {
                const x0 = cx, y0 = cy, qx = args[0], qy = args[1], x1 = args[2], y1 = args[3];
                for (let t = 1; t <= 20; t++) {
                    const u = t / 20, v = 1 - u;
                    cx = v * v * x0 + 2 * v * u * qx + u * u * x1;
                    cy = v * v * y0 + 2 * v * u * qy + u * u * y1;
                    pts.push({ x: cx, y: cy });
                }
            }
            else if (cmd === "Z") { pts.push({ x: sx, y: sy }); }
        }
        return pts;
    }

    const out = [];
    document.querySelectorAll(".mermaid svg").forEach((svg) => {
        const vb = (svg.getAttribute("viewBox") || "0 0 0 0").split(/\s+/).map(Number);
        const W = vb[2] > 0 ? vb[2] : svg.getBoundingClientRect().width;
        const H = vb[3] > 0 ? vb[3] : svg.getBoundingClientRect().height;
        const nodes = [];
        svg.querySelectorAll("g.node, g.cluster").forEach((g) => {
            let bbox;
            try { bbox = g.getBBox(); } catch { return; }
            if (!bbox || bbox.width < 1 || bbox.height < 1) return;
            const cls = g.getAttribute("class") || "";
            let kind = "Rect";
            if (cls.includes("diamond")) kind = "Diamond";
            else if (cls.includes("circle") || cls.includes("ellipse")) kind = "Ellipse";
            else if (cls.includes("hexagon")) kind = "Hexagon";
            else if (cls.includes("cylinder")) kind = "Cylinder";
            else if (cls.includes("cluster")) kind = "Subgraph";
            const fo = g.querySelector("foreignObject");
            const label = (fo ? fo.textContent : "") || g.querySelector("text")?.textContent || "";
            const shape = g.querySelector("rect, polygon, path");
            let fill = "";
            if (shape) { try { fill = getComputedStyle(shape).fill || ""; } catch { } }
            nodes.push({
                Id: g.id || "",
                Cx: bbox.x + bbox.width / 2, Cy: bbox.y + bbox.height / 2,
                W: bbox.width, H: bbox.height,
                Kind: kind, Label: label.trim(), Fill: fill,
            });
        });
        const edges = [];
        svg.querySelectorAll("g.edgePaths path").forEach((p) => {
            const pts = parsePath(p.getAttribute("d") || "");
            if (pts.length < 2) return;
            const first = pts[0], last = pts[pts.length - 1];
            const labelEl = p.parentElement?.parentElement?.querySelector(".edgeLabel, g.edgeLabel");
            const label = labelEl ? (labelEl.textContent || "").trim() : "";
            const cls = p.getAttribute("class") || "";
            const dash = (p.getAttribute("stroke-dasharray") || "").length > 0 || cls.includes("dashed") || cls.includes("dotted");
            edges.push({
                X1: first.x, Y1: first.y, X2: last.x, Y2: last.y,
                Dashed: dash,
                Label: label || null,
                Lx: 0, Ly: 0,
                Points: pts.map((pt) => [Math.round(pt.x * 10) / 10, Math.round(pt.y * 10) / 10]),
            });
        });
        out.push({ W: Math.max(1, W), H: Math.max(1, H), Nodes: nodes, Edges: edges });
    });
    return JSON.stringify(out);
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
