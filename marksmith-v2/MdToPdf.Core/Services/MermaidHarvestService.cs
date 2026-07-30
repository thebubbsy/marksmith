using MdToPdf.Models;
using Markdig.Syntax;

namespace MdToPdf.Services;

// The portable half of mermaid rasterization/geometry-harvesting: builds the self-contained
// render page, drives it via IWebRenderHost, and parses the result. The UI-shell-specific parts
// (owning the preview WebView, pausing its debounce timer while the render page is loaded,
// refreshing the live preview afterwards) stay on each platform's main window, which calls these
// methods sandwiched in that wrapper — see MainWindow.xaml.cs's RenderMermaidPngsAsync etc.
public sealed class MermaidHarvestService
{
    // Shared deserialization options for the harvest payloads — allocated once for the process
    // instead of being rebuilt inside each 150ms polling iteration.
    private static readonly System.Text.Json.JsonSerializerOptions HarvestJson = new() { PropertyNameCaseInsensitive = true };

    private static List<string> ExtractFences(string markdown)
    {
        var doc = Markdig.Markdown.Parse(TextNormalizer.Newlines(markdown));
        var fences = new List<string>();
        foreach (var block in doc.Descendants<Markdig.Syntax.FencedCodeBlock>())
        {
            if (block.Info?.Trim().StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true)
            {
                fences.Add(block.Lines.ToString());
            }
        }
        return fences;
    }

    // A single DOCX export runs three harvest passes back-to-back on the SAME markdown (exact
    // geometry, generic geometry, PNG rasterization). Each used to re-parse the whole document
    // with Markdig to re-extract the identical fence list — memoize the last extraction instead.
    // A different document (or a re-export after edits) simply re-extracts.
    private string? _fenceSource;
    private List<string> _fenceCache = new();
    private List<string> FencesFor(string markdown)
    {
        if (!string.Equals(markdown, _fenceSource, StringComparison.Ordinal))
        {
            _fenceSource = markdown;
            _fenceCache = ExtractFences(markdown);
        }
        return _fenceCache;
    }

    // Rasterizes every ```mermaid fence to a PNG (2x scale). Returns one entry per fence, null
    // where a diagram failed to render — DocxExportService falls back per-diagram.
    public async Task<List<byte[]?>> RenderMermaidPngsAsync(IWebRenderHost host, string markdown, AppSettings settings, ThemeDefinition theme)
    {
        var fences = FencesFor(markdown);
        if (fences.Count == 0) return new();
        if (!await host.EnsureReadyAsync()) { System.IO.File.WriteAllText("geo_dump.txt", "TIMEOUT"); return new(); }

        var sourcesJson = System.Text.Json.JsonSerializer.Serialize(fences);
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="{{Services.WebAssets.Mermaid}}"></script></head>
            <body><script>
            window.__pngs = null; window.__err = null; window.onerror = function(m) { window.__err = m; }; window.onunhandledrejection = function(e) { window.__err = e.reason; };
            const sources = {{sourcesJson}};
            mermaid.initialize({ startOnLoad: false, theme: "base",
              themeVariables: { primaryColor: "{{theme.Background}}", primaryTextColor: "{{theme.Primary}}",
                primaryBorderColor: "{{theme.Line}}", lineColor: "{{theme.Line}}",
                secondaryColor: "{{theme.Secondary}}", tertiaryColor: "{{theme.Background}}" },
              flowchart: { useMaxWidth: false, htmlLabels: false, curve: "linear" },
              securityLevel: "strict" });
            (async () => {
              const out = [];
              for (let i = 0; i < sources.length; i++) {
                try { out.push(await toPng((await mermaid.render("m" + i, sources[i])).svg)); }
                catch (e) { out.push(null); }
              }
              window.__pngs = out;
            })();
            async function toPng(svgText) {
              const vb = /viewBox="[-\d.]+ [-\d.]+ ([\d.]+) ([\d.]+)"/.exec(svgText);
              const w = vb ? Math.ceil(parseFloat(vb[1])) : 600, h = vb ? Math.ceil(parseFloat(vb[2])) : 400;
              const parsed = new DOMParser().parseFromString(svgText, "image/svg+xml");
              if (parsed.querySelector("parsererror")) throw new Error("svg parse failed");
              const el = parsed.documentElement;
              el.setAttribute("width", String(w)); el.setAttribute("height", String(h));
              el.removeAttribute("style");
              svgText = new XMLSerializer().serializeToString(el);
              const url = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svgText);
              const img = new Image();
              await new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = url; });
              const c = document.createElement("canvas"); c.width = w * 2; c.height = h * 2;
              const ctx = c.getContext("2d");
              ctx.fillStyle = "{{theme.Background}}"; ctx.fillRect(0, 0, c.width, c.height);
              ctx.drawImage(img, 0, 0, c.width, c.height);
              return c.toDataURL("image/png");
            }
            </script></body></html>
            """;

        var result = new List<byte[]?>();
        try
        {
            await host.BeginHarvestAsync();
            await host.NavigateToStringAsync(html);
            for (int i = 0; i < 60; i++) // up to ~9s (CDN + render)
            {
                await Task.Delay(150);
                var raw = await host.ExecuteScriptAsync("JSON.stringify(window.__pngs)");
                if (raw is null or "null" or "\"null\"") continue;
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null") continue;
                var urls = System.Text.Json.JsonSerializer.Deserialize<List<string?>>(json) ?? new();
                foreach (var u in urls)
                    result.Add(u is not null && u.StartsWith("data:image/png;base64,")
                        ? Convert.FromBase64String(u["data:image/png;base64,".Length..])
                        : null);
                break;
            }
        }
        catch { /* rendering is best-effort; exporter falls back per-diagram */ }
        finally { await host.EndHarvestAsync(); }
        while (result.Count < fences.Count) result.Add(null);
        return result;
    }

    // "Exact layout" harvest: render each flowchart fence with mermaid and read back the geometry
    // mermaid itself computed (node centres/sizes, edge endpoints, labels), so ShapeForge can
    // rebuild the diagram in Word node-for-node instead of re-laying-it-out.
    public async Task<List<Mermaid.HarvestedDiagram?>> HarvestMermaidGeometryAsync(IWebRenderHost host, string markdown, AppSettings settings, ThemeDefinition theme)
    {
        var fences = FencesFor(markdown);
        if (fences.Count == 0) return new();
        if (!await host.EnsureReadyAsync()) { System.IO.File.WriteAllText("geo_dump.txt", "TIMEOUT"); return new(); }

        var sourcesJson = System.Text.Json.JsonSerializer.Serialize(fences);
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="{{Services.WebAssets.Mermaid}}"></script></head>
            <body><script>
            window.__geo = null;
            const sources = {{sourcesJson}};
            mermaid.initialize({ startOnLoad: false, theme: "base",
              flowchart: { useMaxWidth: false, htmlLabels: false, curve: "linear" }, securityLevel: "strict" });
            function T(node, root) { // node centre in root coords (getCTM is relative to the svg)
              const m = node.getCTM ? node.getCTM() : null; return m ? [m.e, m.f] : [0, 0]; }
            // Root-space bounding box: transform the local getBBox() corners through getCTM().
            // A g.node is positioned by a translate transform, so its CTM carries the centre — but
            // a g.cluster (subgraph) keeps an IDENTITY group transform and positions its rect with
            // ABSOLUTE x/y, so getCTM().e/.f read ~0 and every subgraph collapses to the origin.
            // Mapping the bbox corners through the CTM yields correct root coords for BOTH.
            function rootBox(n) {
              let bb = { x: 0, y: 0, width: 0, height: 0 }; try { bb = n.getBBox(); } catch (e) {}
              const m = n.getCTM ? n.getCTM() : null;
              if (!m) return { x: bb.x, y: bb.y, w: bb.width, h: bb.height };
              const pts = [[bb.x, bb.y], [bb.x + bb.width, bb.y], [bb.x, bb.y + bb.height], [bb.x + bb.width, bb.y + bb.height]]
                .map(([x, y]) => [x * m.a + y * m.c + m.e, x * m.b + y * m.d + m.f]);
              const xs = pts.map(p => p[0]), ys = pts.map(p => p[1]);
              const minX = Math.min(...xs), minY = Math.min(...ys);
              return { x: minX, y: minY, w: Math.max(...xs) - minX, h: Math.max(...ys) - minY };
            }
            function kindOf(n) {
              if (n.classList && n.classList.contains("cluster")) return "Subgraph";
              if (n.querySelector("circle")) return "Circle";
              if (n.querySelector("ellipse")) return "Ellipse";
              const p = n.querySelector("polygon");
              if (p) { const pts = (p.getAttribute("points")||"").trim().split(/\s+/).length; return pts >= 6 ? "Hexagon" : "Diamond"; }
              if (n.querySelector("path") && !n.querySelector("rect")) return "Cylinder";
              const r = n.querySelector("rect"); if (r && parseFloat(r.getAttribute("rx")) > 0) return "RoundRect";
              return "Rect";
            }
            function lines(n) { // reconstruct wrapped label lines from tspans
              const ts = [...n.querySelectorAll("tspan")].map(t => t.textContent).filter(s => s && s.trim());
              return (ts.length ? ts.join("\n") : (n.textContent||"")).trim();
            }
            function harvest(svgEl) {
              const nodes = [...svgEl.querySelectorAll("g.node, g.cluster")].map(n => {
                const rb = rootBox(n);
                return { Id: n.id.replace(/^flowchart-/,"").replace(/-\d+$/,""), Cx: +(rb.x + rb.w / 2).toFixed(1), Cy: +(rb.y + rb.h / 2).toFixed(1), W: +rb.w.toFixed(1), H: +rb.h.toFixed(1), Kind: kindOf(n), Label: lines(n) };
              });
              const edges = [...svgEl.querySelectorAll("path.flowchart-link, .edgePath path")].map(p => {
                const dashed = (p.getAttribute("class")||"").includes("dashed") || getComputedStyle(p).strokeDasharray !== "none";
                const m = p.getCTM ? p.getCTM() : null;
                const map = pt => m ? [pt.x*m.a + pt.y*m.c + m.e, pt.x*m.b + pt.y*m.d + m.f] : [pt.x, pt.y];
                let pts = [];
                try {
                  const L = p.getTotalLength();
                  const N = Math.max(6, Math.min(30, Math.round(L / 16)));
                  for (let k = 0; k <= N; k++) { const [x, y] = map(p.getPointAtLength(L * k / N)); pts.push([+x.toFixed(1), +y.toFixed(1)]); }
                } catch (e) {
                  const nums = [...(p.getAttribute("d")||"").matchAll(/[-\d.]+/g)].map(v => +v[0]);
                  pts = [[nums[0]||0, nums[1]||0], [nums[nums.length-2]||0, nums[nums.length-1]||0]];
                }
                return { X1: pts[0][0], Y1: pts[0][1], X2: pts[pts.length-1][0], Y2: pts[pts.length-1][1], Dashed: dashed, Label: null, Lx: 0, Ly: 0, Points: pts };
              });
              const labels = [...svgEl.querySelectorAll(".edgeLabels .edgeLabel, .edgeLabel")].map(l => {
                const g = l.closest("g") || l; const [x, y] = T(g); return { t: (l.textContent||"").trim(), x, y };
              }).filter(l => l.t);
              labels.forEach(lab => {
                let best = null, bd = 1e9;
                edges.forEach(e => { const mx=(e.X1+e.X2)/2, my=(e.Y1+e.Y2)/2, dd=(mx-lab.x)**2+(my-lab.y)**2; if (dd<bd && !e.Label) { bd=dd; best=e; } });
                if (best) { best.Label = lab.t; best.Lx = lab.x; best.Ly = lab.y; }
              });
              const vb = (svgEl.getAttribute("viewBox")||"0 0 0 0").split(/\s+/).map(Number);
              return { W: vb[2], H: vb[3], Nodes: nodes, Edges: edges };
            }
            (async () => {
              const out = [];
              for (let i = 0; i < sources.length; i++) {
                try {
                  const first = (sources[i].trim().split(/\s+/)[0]||"").toLowerCase();
                  if (first !== "graph" && first !== "flowchart") { out.push(null); continue; }
                  const holder = document.createElement("div");
                  holder.style.cssText = "position:fixed;left:0;top:0;opacity:0;pointer-events:none;z-index:-1";
                  document.body.appendChild(holder);
                  const { svg } = await mermaid.render("mg" + i, sources[i]);
                  holder.innerHTML = svg; const el = holder.querySelector("svg");
                  void el.getBoundingClientRect(); // force synchronous layout before measuring
                  out.push(harvest(el)); holder.remove();
                } catch (e) { window.__geo = "ERROR: " + (e && e.message); return; }
              }
              window.__geo = JSON.stringify(out);
            })();
            </script></body></html>
            """;

        var result = new List<Mermaid.HarvestedDiagram?>();
        try
        {
            await host.BeginHarvestAsync();
            await host.NavigateToStringAsync(html);
            for (int i = 0; i < 130; i++) // up to ~20s (CDN + a large graph's layout + geometry sampling)
            {
                await Task.Delay(150);
                var raw = await host.ExecuteScriptAsync("window.__geo");
                var err = await host.ExecuteScriptAsync("window.__err");
                if (err != null && err != "null") {
                    try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "merr.txt"), $"\n[JS_ERR] {err}\n"); } catch {}
                }
                
                if (raw is null or "null" or "\"null\"") continue;
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null") continue;
                result = System.Text.Json.JsonSerializer.Deserialize<List<Mermaid.HarvestedDiagram?>>(json, HarvestJson) ?? new();
                break;
            }
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "merr.txt"), $"[MERMAID-HARVEST-CS] {ex}\n"); } catch {}
            System.Diagnostics.Debug.WriteLine($"[MERMAID-HARVEST] Exception: {ex}");
        }
        finally { await host.EndHarvestAsync(); }
        while (result.Count < fences.Count) result.Add(null);
        return result;
    }

    // The "no fallback" harvester: for ANY mermaid diagram type, read every visual primitive —
    // shapes with mermaid's real colours, curved edge paths, text — from the rendered SVG so
    // ShapeForge can rebuild it as native Word shapes instead of falling back to a picture.
    public async Task<List<Mermaid.GenericDiagram?>> HarvestGenericGeometryAsync(IWebRenderHost host, string markdown, AppSettings settings, ThemeDefinition theme)
    {
        var fences = FencesFor(markdown);
        if (fences.Count == 0) return new();
        if (!await host.EnsureReadyAsync()) { System.IO.File.WriteAllText("geo_dump.txt", "TIMEOUT"); return new(); }

        var sourcesJson = System.Text.Json.JsonSerializer.Serialize(fences);
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="{{Services.WebAssets.Mermaid}}"></script></head>
            <body><script>
            window.__gen = null;
            const sources = {{sourcesJson}};
            mermaid.initialize({ startOnLoad: false, theme: "base",
              themeVariables: { primaryColor: "{{theme.Background}}", primaryTextColor: "{{theme.Primary}}",
                primaryBorderColor: "{{theme.Line}}", lineColor: "{{theme.Line}}",
                secondaryColor: "{{theme.Secondary}}", tertiaryColor: "{{theme.Background}}" },
              flowchart: { useMaxWidth: false, htmlLabels: true },
              state: { useMaxWidth: false }, securityLevel: "strict" });
            function harvest(svgEl) {
              const nodes = [], edges = [], texts = [];
              const M = el => el.getCTM ? el.getCTM() : null, box = el => { try { return el.getBBox(); } catch(e) { return null; } }, cs = el => getComputedStyle(el);
              const abs = el => { const b = box(el), m = M(el); if (!b) return null; return { x: m ? m.a*b.x + m.c*b.y + m.e : b.x, y: m ? m.b*b.x + m.d*b.y + m.f : b.y, w: m ? b.width*m.a : b.width, h: m ? b.height*m.d : b.height }; };
              const closed = new Set(["rect","circle","ellipse","polygon"]);
              svgEl.querySelectorAll("rect,circle,ellipse,polygon,polyline,path,line").forEach(el => {
                const st = cs(el), fill = st.fill, stroke = st.stroke, tag = el.tagName.toLowerCase();
                const isFilled = fill && fill !== "none" && !fill.startsWith("rgba(0, 0, 0, 0)");
                if (closed.has(tag) || (tag === "path" && isFilled)) {
                  const a = abs(el); if (!a || a.w < 1 || a.h < 1) return;
                  let kind = tag === "circle" ? "Circle" : tag === "ellipse" ? "Ellipse" : tag === "polygon" ? "Diamond" : (tag === "rect" && +el.getAttribute("rx") > 0) ? "RoundRect" : "Rect";
                  nodes.push({ X: +a.x.toFixed(1), Y: +a.y.toFixed(1), W: +a.w.toFixed(1), H: +a.h.toFixed(1), Kind: kind, Fill: fill, Stroke: stroke });
                } else if (tag === "path" || tag === "line" || tag === "polyline") {
                  const m = M(el), map = pt => m ? [pt.x*m.a+pt.y*m.c+m.e, pt.x*m.b+pt.y*m.d+m.f] : [pt.x, pt.y]; let pts = [];
                  try { const L = el.getTotalLength(); const N = Math.max(2, Math.min(30, Math.round(L/16))); for (let k=0;k<=N;k++){ const [x,y] = map(el.getPointAtLength(L*k/N)); pts.push([+x.toFixed(1),+y.toFixed(1)]); } } catch(e){}
                  const dashed = (cs(el).strokeDasharray || "none") !== "none";
                  if (pts.length >= 2) edges.push({ Points: pts, Stroke: stroke, Dashed: dashed });
                }
              });
              svgEl.querySelectorAll("foreignObject").forEach(fo => { const a = abs(fo); const t = (fo.textContent||"").trim(); const c = cs(fo.querySelector("*") || fo).color; if (a && t) texts.push({ X:+a.x.toFixed(1), Y:+a.y.toFixed(1), W:+a.w.toFixed(1), H:+a.h.toFixed(1), Text: t, Color: c }); });
              svgEl.querySelectorAll("text").forEach(tx => { if (tx.closest("foreignObject")) return; const a = abs(tx); const t = (tx.textContent||"").trim(); if (a && t) texts.push({ X:+a.x.toFixed(1), Y:+a.y.toFixed(1), W:+a.w.toFixed(1), H:+a.h.toFixed(1), Text: t, Color: cs(tx).fill }); });
              // Canvas size: prefer the viewBox, fall back to the width/height attributes, and always
              // grow to enclose the harvested content. Without this, an SVG that omits its viewBox
              // (some state-diagram renders) collapses to a 1pt canvas — a tiny backdrop card while the
              // shapes, harvested at their real coordinates, spill off the right edge of the page.
              const vb = (svgEl.getAttribute("viewBox")||"").split(/\s+/).map(Number);
              let W = vb.length === 4 ? vb[2] : 0, H = vb.length === 4 ? vb[3] : 0;
              if (!(W > 0 && H > 0)) {
                const num = v => { const m = /[\d.]+/.exec(v||""); return m ? parseFloat(m[0]) : 0; };
                W = num(svgEl.getAttribute("width")); H = num(svgEl.getAttribute("height"));
              }
              let maxX = 0, maxY = 0;
              nodes.forEach(n => { maxX = Math.max(maxX, n.X + n.W); maxY = Math.max(maxY, n.Y + n.H); });
              texts.forEach(t => { maxX = Math.max(maxX, t.X + t.W); maxY = Math.max(maxY, t.Y + t.H); });
              edges.forEach(e => e.Points.forEach(p => { maxX = Math.max(maxX, p[0]); maxY = Math.max(maxY, p[1]); }));
              W = Math.max(W, maxX); H = Math.max(H, maxY);
              if (!(W > 0)) W = 600; if (!(H > 0)) H = 400;
              return { W: W, H: H, Nodes: nodes, Edges: edges, Texts: texts };
            }
            (async () => {
              const out = [];
              for (let i = 0; i < sources.length; i++) {
                try {
                  const holder = document.createElement("div");
                  holder.style.cssText = "position:fixed;left:0;top:0;opacity:0;pointer-events:none;z-index:-1";
                  document.body.appendChild(holder);
                  const { svg } = await mermaid.render("gg" + i, sources[i]);
                  holder.innerHTML = svg; const el = holder.querySelector("svg");
                  void el.getBoundingClientRect();
                  out.push(harvest(el)); holder.remove();
                } catch (e) { out.push(null); }
              }
              window.__gen = JSON.stringify(out);
            })();
            </script></body></html>
            """;

        var result = new List<Mermaid.GenericDiagram?>();
        try
        {
            await host.BeginHarvestAsync();
            await host.NavigateToStringAsync(html);
            for (int i = 0; i < 130; i++)
            {
                await Task.Delay(150);
                var raw = await host.ExecuteScriptAsync("window.__gen");
                if (raw is null or "null" or "\"null\"") continue;
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null") continue;
                result = System.Text.Json.JsonSerializer.Deserialize<List<Mermaid.GenericDiagram?>>(json, HarvestJson) ?? new();
                break;
            }
        }
        catch { /* best-effort; the exporter falls back to snapshot/code for any fence that fails */ }
        finally { await host.EndHarvestAsync(); }
        while (result.Count < fences.Count) result.Add(null);
        return result;
    }
}




