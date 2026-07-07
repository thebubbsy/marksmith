using System.Text.RegularExpressions;
using MdToPdf.Models;
using Markdig;

namespace MdToPdf.Services;

// Port of create_html_content() from md_to_pdf_tui.py: Markdown -> themed HTML string that
// WebView2 can navigate to for live preview and PDF export via CoreWebView2.PrintToPdfAsync.
public sealed class MarkdownHtmlService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tables, footnotes, task lists, etc. — mirrors mdit-py-plugins' table/footnote support
        .UseYamlFrontMatter()    // mirrors front_matter_plugin
        .UseAlertBlocks()        // GitHub-style > [!NOTE] blocks, mirrors the hand-rolled ALERT_PATTERN parsing
        .UseMathematics()        // $..$ / $$..$$ -> span.math/div.math, rendered by KaTeX (ChatGPT exports carry LaTeX)
        .Build();

    // Same alert accent colors used by the Python app's DOCX alert-box rendering, for visual parity.
    private static readonly Dictionary<string, (string Color, string Icon)> AlertStyles = new()
    {
        ["note"] = ("#0969da", "ℹ️"),
        ["tip"] = ("#1f883d", "💡"),
        ["important"] = ("#8250df", "📢"),
        ["warning"] = ("#bf8700", "⚠️"),
        ["caution"] = ("#cf222e", "🛑"),
    };

    private static readonly Dictionary<string, (string Color, string Icon)> AlertStylesDark = new()
    {
        ["note"] = ("#58a6ff", "ℹ️"),
        ["tip"] = ("#3fb950", "💡"),
        ["important"] = ("#a371f7", "📢"),
        ["warning"] = ("#d29922", "⚠️"),
        ["caution"] = ("#f85149", "🛑"),
    };

    public string Render(string markdown, AppSettings settings, ThemeDefinition theme, LlmClassification? classification = null)
    {
        markdown = TextNormalizer.Newlines(markdown);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);
        var body = Markdown.ToHtml(markdown, Pipeline);

        // Markdig renders ```mermaid fences as <pre><code class="language-mermaid">…</code></pre> with
        // the content HTML-escaped, but mermaid.js only auto-renders class="mermaid" elements holding
        // raw diagram text. Rewrite the fence to a <div class="mermaid"> and unescape its content.
        body = Regex.Replace(body,
            "<pre><code class=\"language-mermaid\">(.*?)</code></pre>",
            m => $"<div class=\"mermaid\">{System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)}</div>",
            RegexOptions.Singleline);
        var isDark = theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro";
        var alertStyles = isDark ? AlertStylesDark : AlertStyles;

        var extraHead = BuildExtraHead(body, isDark);
        var attribution = BuildAttribution(settings, classification, theme);
        var toc = settings.IncludeToc ? BuildToc(body, theme) : "";
        // Free tier stamps a subtle footer on every export/preview; Pro removes it.
        var footer = MdToPdf.App.License.ShowFooter
            ? "<div class=\"mark-footer\">Made with <a href=\"https://github.com/thebubbsy/marksmith\">Marksmith</a> — turn AI chats into polished documents</div>"
            : "";

        var alertCss = string.Join("\n", alertStyles.Select(kv => $$"""
            .markdown-alert-{{kv.Key}} { border-left: 5px solid {{kv.Value.Color}}; background: {{theme.Secondary}}; }
            .markdown-alert-{{kv.Key}} .markdown-alert-title { color: {{kv.Value.Color}}; }
            """));

        var mermaidEnabled = settings.MermaidEnabled && body.Contains("mermaid", StringComparison.OrdinalIgnoreCase);
        var mermaidScript = mermaidEnabled ? $$"""
            <script src="https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js"></script>
            <script>
            mermaid.initialize({
                startOnLoad: true,
                theme: "base",
                themeVariables: {
                    primaryColor: "{{theme.Background}}",
                    primaryTextColor: "{{theme.Primary}}",
                    primaryBorderColor: "{{theme.Line}}",
                    lineColor: "{{theme.Line}}",
                    secondaryColor: "{{theme.Secondary}}",
                    tertiaryColor: "{{theme.Background}}"
                },
                maxTextSize: 10000000,
                maxNodes: 10000,
                // useMaxWidth:true (the default) makes diagrams ELASTIC to the viewport — zooming in
                // shrinks them back to fit, which reads as "zoom is broken". Pin every family to its
                // natural size; the canvas scrolls instead.
                flowchart: { useMaxWidth: false, htmlLabels: true, curve: "linear" },
                sequence: { useMaxWidth: false },
                gantt: { useMaxWidth: false },
                journey: { useMaxWidth: false },
                timeline: { useMaxWidth: false },
                class: { useMaxWidth: false },
                state: { useMaxWidth: false },
                er: { useMaxWidth: false },
                pie: { useMaxWidth: false },
                quadrantChart: { useMaxWidth: false },
                mindmap: { useMaxWidth: false },
                gitGraph: { useMaxWidth: false },
                securityLevel: "loose"
            });
            // Click-to-expand lens: wheel zooms about the cursor, drag pans, Esc / ✕ closes.
            window.addEventListener("DOMContentLoaded", () => {
                const lens = document.createElement("div"); lens.id = "mk-lens";
                lens.innerHTML = '<div id="mk-lens-stage"></div>';
                const bar = document.createElement("div"); bar.id = "mk-lens-bar";
                bar.innerHTML = '<span id="mk-lens-pct">100%</span><button id="mk-lens-out">−</button><button id="mk-lens-in">+</button><button id="mk-lens-reset">Reset</button><button id="mk-lens-close">✕ Close</button>';
                bar.style.display = "none";
                document.body.append(lens, bar);
                const stage = lens.firstChild;
                let sc = 1, tx = 0, ty = 0, drag = null;
                const apply = () => { stage.style.transform = `translate(${tx}px, ${ty}px) scale(${sc})`;
                                      document.getElementById("mk-lens-pct").textContent = Math.round(sc * 100) + "%"; };
                const openLens = (svg) => {
                    stage.innerHTML = ""; stage.appendChild(svg.cloneNode(true));
                    const w = svg.getBoundingClientRect().width || 800;
                    sc = Math.min(1, (innerWidth - 60) / w);
                    tx = Math.max(30, (innerWidth - w * sc) / 2); ty = 40;
                    lens.classList.add("open"); bar.style.display = "flex"; apply();
                };
                const closeLens = () => { lens.classList.remove("open"); bar.style.display = "none"; };
                document.querySelectorAll(".mermaid").forEach(m =>
                    m.addEventListener("click", () => { const s = m.querySelector("svg"); if (s) openLens(s); }));
                lens.addEventListener("wheel", (e) => {
                    e.preventDefault();
                    const f = e.deltaY < 0 ? 1.15 : 1 / 1.15;
                    tx = e.clientX - (e.clientX - tx) * f; ty = e.clientY - (e.clientY - ty) * f;
                    sc = Math.min(12, Math.max(0.1, sc * f)); apply();
                }, { passive: false });
                lens.addEventListener("pointerdown", (e) => { drag = { x: e.clientX - tx, y: e.clientY - ty }; lens.classList.add("dragging"); lens.setPointerCapture(e.pointerId); });
                lens.addEventListener("pointermove", (e) => { if (drag) { tx = e.clientX - drag.x; ty = e.clientY - drag.y; apply(); } });
                lens.addEventListener("pointerup", () => { drag = null; lens.classList.remove("dragging"); });
                document.getElementById("mk-lens-in").addEventListener("click", () => { sc = Math.min(12, sc * 1.25); apply(); });
                document.getElementById("mk-lens-out").addEventListener("click", () => { sc = Math.max(0.1, sc / 1.25); apply(); });
                document.getElementById("mk-lens-reset").addEventListener("click", () => { sc = 1; tx = 30; ty = 40; apply(); });
                document.getElementById("mk-lens-close").addEventListener("click", closeLens);
                window.addEventListener("keydown", (e) => { if (e.key === "Escape") closeLens(); });
            });
            </script>
            """ : "";

        return $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            {{mermaidScript}}
            {{extraHead}}
            <style>
            body { background: {{theme.Background}}; color: {{theme.Text}}; font-family: -apple-system, "Segoe UI", sans-serif; line-height: 1.6; margin: 0; padding: 0; display: flex; flex-direction: column; align-items: center; width: 100%; }
            #canvas { padding: 60px 40px; width: 100%; max-width: {{settings.ContentWidth}}px; box-sizing: border-box; transition: filter .3s ease, opacity .3s ease; }
            body.ms-loading #canvas { filter: blur(14px); opacity: .6; }
            h1, h2 { color: {{theme.Heading}}; border-bottom: 2px solid {{theme.Border}}; padding-bottom: 8px; }
            pre { background: {{theme.Code}}; padding: 16px; border-radius: 6px; overflow-x: auto; border: 1px solid {{theme.Border}}; }
            table { border-collapse: collapse; width: 100%; margin: 16px 0; border: 2px solid {{theme.Border}}; }
            th, td { border: 1px solid {{theme.Border}}; padding: 8px 12px; text-align: left; }
            th { background: {{theme.Code}}; font-weight: bold; }
            .markdown-alert { border-radius: 6px; padding: 10px 16px; margin-bottom: 16px; }
            .markdown-alert-title { font-weight: bold; margin: 0 0 4px 0; }
            {{alertCss}}
            /* Screen (the live preview): diagrams at NATURAL size. Wide ones break out of the text
               column to the full preview width; anything larger scrolls, and clicking opens the
               full-screen pan/zoom viewer. Print (the PDF export): fit-to-page-width. */
            .mermaid { width: fit-content; min-width: 100%; max-width: calc(100vw - 48px); position: relative; left: 50%; transform: translateX(-50%); margin: 32px 0; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; cursor: zoom-in; }
            .mermaid svg { max-width: none !important; }
            #mk-lens { position: fixed; inset: 0; z-index: 99; display: none; background: {{theme.Background}}f2; cursor: grab; overflow: hidden; }
            #mk-lens.open { display: block; }
            #mk-lens.dragging { cursor: grabbing; }
            #mk-lens-stage { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
            #mk-lens-bar { position: fixed; top: 14px; right: 18px; z-index: 100; display: flex; gap: 8px; align-items: center; font-size: 13px; color: {{theme.Text}}; }
            #mk-lens-bar button { background: {{theme.Secondary}}; color: {{theme.Text}}; border: 1px solid {{theme.Border}}; border-radius: 6px; padding: 4px 12px; font-size: 14px; cursor: pointer; }
            #mk-lens-bar button:hover { border-color: {{theme.Heading}}; }
            @media print {
              .mermaid { overflow-x: visible; width: 100%; max-width: 100%; left: 0; transform: none; cursor: auto; }
              .mermaid svg { width: 100% !important; height: auto !important; max-width: 100% !important; }
              #mk-lens, #mk-lens-bar { display: none !important; }
            }
            .mermaid .node rect, .mermaid .node circle, .mermaid .node polygon, .mermaid .node path, .mermaid .cluster rect { stroke: {{theme.Line}} !important; stroke-width: 2px !important; fill: {{theme.Background}} !important; }
            .mermaid .edgePath path { stroke: {{theme.Line}} !important; stroke-width: 2px !important; }
            .mermaid .label { color: {{theme.Primary}} !important; }
            .attribution { display: flex; align-items: center; gap: 10px; font-size: 12.5px; border: 1px solid {{theme.Border}}; border-left: 4px solid {{theme.Heading}}; background: {{theme.Secondary}}; border-radius: 8px; padding: 10px 14px; margin-bottom: 28px; opacity: 0.92; }
            .attribution .badge { font-weight: 700; color: {{theme.Heading}}; }
            .mark-footer { margin-top: 34px; padding-top: 14px; border-top: 1px solid {{theme.Border}}; text-align: center; font-size: 12px; color: {{theme.Text}}; opacity: 0.62; }
            .mark-footer a { color: {{theme.Heading}}; text-decoration: none; font-weight: 600; }
            #toc { border: 1px solid {{theme.Border}}; background: {{theme.Secondary}}; border-radius: 8px; padding: 14px 20px; margin-bottom: 28px; }
            #toc .toc-title { font-weight: 700; color: {{theme.Heading}}; margin-bottom: 6px; }
            #toc ul { margin: 0; padding-left: 18px; }
            #toc li { margin: 3px 0; }
            #toc a { color: {{theme.Text}}; text-decoration: none; }
            #toc a:hover { color: {{theme.Heading}}; }
            sup.cite { font-size: 0.72em; color: {{theme.Heading}}; }
            </style></head><body><div id="canvas">{{attribution}}{{toc}}{{body}}{{footer}}</div></body></html>
            """;
    }

    // KaTeX for math and highlight.js for code fences, loaded from CDN only when the rendered
    // body actually needs them (offline exports of plain documents stay dependency-free).
    private static string BuildExtraHead(string body, bool isDark)
    {
        var head = "";
        if (body.Contains("class=\"math\""))
        {
            head += """
                <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.css">
                <script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/katex.min.js"></script>
                <script defer src="https://cdn.jsdelivr.net/npm/katex@0.16.11/dist/contrib/auto-render.min.js"
                        onload="renderMathInElement(document.body, {delimiters: [{left:'$$',right:'$$',display:true},{left:'$',right:'$',display:false},{left:'\\(',right:'\\)',display:false},{left:'\\[',right:'\\]',display:true}]});"></script>
                """;
        }
        if (body.Contains("language-"))
        {
            var hlTheme = isDark ? "github-dark" : "github";
            head += $"""
                <link rel="stylesheet" href="https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11.10.0/build/styles/{hlTheme}.min.css">
                <script src="https://cdn.jsdelivr.net/gh/highlightjs/cdn-release@11.10.0/build/highlight.min.js"></script>
                <script>document.addEventListener('DOMContentLoaded', () => hljs.highlightAll());</script>
                """;
        }
        return head;
    }

    private static string BuildAttribution(AppSettings settings, LlmClassification? c, ThemeDefinition theme)
    {
        if (!settings.ShowAttribution || c is null || c.Source == LlmSource.Generic) return "";
        var fixes = c.AppliedFixes.Count > 0 ? $" · {c.AppliedFixes.Count} formatting fixes applied" : "";
        var badge = settings.NoEmoji ? c.SourceName : $"⚡ {c.SourceName}";
        return $"""
            <div class="attribution">
                <span class="badge">{badge}</span>
                <span>Imported {DateTime.Now:d MMM yyyy, HH:mm}{fixes} · Marksmith</span>
            </div>
            """;
    }

    // Markdig's AutoIdentifiers (in UseAdvancedExtensions) gives every heading an id we can link to.
    private static string BuildToc(string body, ThemeDefinition theme)
    {
        var matches = Regex.Matches(body, "<h([123]) id=\"([^\"]+)\"[^>]*>(.*?)</h\\1>", RegexOptions.Singleline);
        if (matches.Count < 2) return "";

        var items = matches.Select(m =>
        {
            var level = int.Parse(m.Groups[1].Value);
            var text = Regex.Replace(m.Groups[3].Value, "<[^>]+>", "").Trim();
            var indent = (level - 1) * 16;
            return $"<li style=\"margin-left:{indent}px\"><a href=\"#{m.Groups[2].Value}\">{text}</a></li>";
        });

        return $"""
            <nav id="toc">
                <div class="toc-title">Contents</div>
                <ul>{string.Join("", items)}</ul>
            </nav>
            """;
    }
}
