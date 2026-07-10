using System.Text;
using System.Text.RegularExpressions;
using MdToPdf.Models;
using Markdig;
using SkiaSharp;

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
        .UseEmojiAndSmiley(enableSmileys: false) // :rocket: -> 🚀 (GitHub/Discord shortcodes, everywhere in AI output); smileys OFF so ":)" in prose isn't rewritten
        .Build();

    // No-emoji mode: identical pipeline minus the emoji extension — shortcode conversion happens
    // during the Markdig parse, AFTER EmojiStripper has already run on the raw markdown, so with
    // the emoji extension active a :rocket: would sneak a 🚀 into a document the user explicitly
    // asked to keep emoji-free.
    private static readonly MarkdownPipeline PipelineNoEmoji = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UseAlertBlocks()
        .UseMathematics()
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

    // First token of a backtick-delimited string literal that identifies it as real Mermaid
    // diagram source (rather than an arbitrary JS/TS template literal), used to find diagrams
    // embedded inside code EXAMPLES — e.g. a library README's ```typescript block calling
    // renderMermaid(`graph TD ...`) — that never went through a real ```mermaid fence.
    private static readonly Regex MermaidDiagramStart = new(
        @"^(graph\s|flowchart\s|sequenceDiagram\b|classDiagram\b|stateDiagram(-v2)?\b|erDiagram\b|journey\b|gantt\b|pie\b|quadrantChart\b|requirementDiagram\b|gitGraph\b|mindmap\b|timeline\b|C4(Context|Container|Component|Dynamic|Deployment)\b|sankey(-beta)?\b|block-beta\b|packet-beta\b|kanban\b|radar(-beta)?\b|xychart-beta\b|zenuml\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // interactive == the LIVE PREVIEW (not PDF export). Only then may we swap in the focused
    // diagram viewer; the exported document is never affected.
    public string Render(string markdown, AppSettings settings, ThemeDefinition theme,
        LlmClassification? classification = null, bool interactive = false)
    {
        markdown = TextNormalizer.Newlines(markdown);
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);
        var body = Markdown.ToHtml(markdown, settings.NoEmoji ? PipelineNoEmoji : Pipeline);
        body = EmbedLocalImages(body);

        // Markdig renders ```mermaid fences as <pre><code class="language-mermaid">…</code></pre> with
        // the content HTML-escaped. Rewrite the fence to a <div class="mermaid">, but KEEP the content
        // escaped: mermaid reads the element's textContent (which the browser decodes automatically),
        // so diagrams render correctly, while the browser never parses malicious markup like
        // <img onerror=…> as live HTML inside the div. HtmlDecode-ing here would reintroduce that XSS.
        body = Regex.Replace(body,
            "<pre><code class=\"language-mermaid\">(.*?)</code></pre>",
            m => $"<div class=\"mermaid\">{m.Groups[1].Value}</div>",
            RegexOptions.Singleline);

        // Some Markdown (typically a library's own README showing "here's how to render a
        // diagram") carries real Mermaid diagram source without a genuine ```mermaid fence:
        // either the WHOLE block is diagram source under a bare/mislabeled fence (a "Supported
        // Diagrams" showcase using plain ``` instead of ```mermaid), or the diagram source is a
        // quoted string-literal argument inside other code — e.g. a ```typescript block calling
        // renderMermaid(`graph TD ...`), or a plain renderMermaid('graph TD; A-->B') call. The
        // code block itself is still legitimate, useful example code, so it's left in place; a
        // live diagram preview is appended right after it whenever the block's full content (or
        // a backtick/single/double-quoted literal inside it) starts with a known Mermaid
        // diagram-type keyword — a narrow enough signature (checked against a fixed keyword
        // list, not just "looks like it might be a diagram") that it doesn't false-positive on
        // ordinary code/strings elsewhere in the example (same encoding-safety note as above:
        // the literal is kept HTML-escaped, since mermaid reads .textContent, which the browser
        // decodes for us).
        if (settings.MermaidEnabled)
        {
            body = Regex.Replace(body,
                "<pre><code(?: class=\"language-[\\w-]+\")?>(.*?)</code></pre>",
                m =>
                {
                    var codeContent = m.Groups[1].Value;
                    var extras = new StringBuilder();
                    var whole = codeContent.Trim();
                    if (MermaidDiagramStart.IsMatch(whole))
                    {
                        extras.Append($"<div class=\"mermaid mermaid-embedded\">{whole}</div>");
                    }
                    else
                    {
                        foreach (Match lit in Regex.Matches(codeContent, "`([^`]+)`|'([^']+)'|\"([^\"]+)\"", RegexOptions.Singleline))
                        {
                            var group = lit.Groups[1].Success ? lit.Groups[1] : lit.Groups[2].Success ? lit.Groups[2] : lit.Groups[3];
                            var candidate = group.Value.Trim();
                            if (MermaidDiagramStart.IsMatch(candidate))
                                extras.Append($"<div class=\"mermaid mermaid-embedded\">{candidate}</div>");
                        }
                    }
                    return extras.Length > 0 ? m.Value + extras : m.Value;
                },
                RegexOptions.Singleline);
        }

        // Plugin-provided diagram languages (e.g. ```plantuml via the optional PlantUML plugin) —
        // a generalization of the ```mermaid handling above for fence languages the core pipeline
        // doesn't know how to render itself. Unlike Mermaid (rendered client-side by mermaid.min.js
        // inside the WebView), a diagram plugin renders out-of-process, synchronously, right here —
        // by the time this HTML reaches the browser the SVG already exists. See
        // MdToPdf.Plugins.IDiagramPlugin / PluginManager.
        body = Regex.Replace(body,
            "<pre><code class=\"language-([\\w-]+)\">(.*?)</code></pre>",
            m =>
            {
                var language = m.Groups[1].Value;
                var installed = AppServices.Plugins.FindDiagramRenderer(language);
                if (installed != null)
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                    var svg = AppServices.Plugins.RenderToSvgCached(installed, decoded);
                    if (svg != null) return $"<div class=\"plugin-diagram\">{svg}</div>";
                    return m.Value + "<div class=\"plugin-diagram-error\">⚠ " + installed.Name + " couldn't render this diagram — check the syntax.</div>";
                }

                var known = AppServices.Plugins.FindAnyDiagramPlugin(language);
                if (known != null)
                    return m.Value + $"<div class=\"plugin-diagram-missing\">🧩 Install the <b>{known.Name}</b> plugin (Settings → Plugins) to render this diagram.</div>";

                return m.Value;
            },
            RegexOptions.Singleline);

        // When the whole document is essentially one diagram + a title (and maybe a few words),
        // the live preview becomes a dedicated diagram viewer: title top-left, +/−/Reset, pan/zoom.
        if (interactive && settings.MermaidEnabled)
        {
            var focus = AnalyzeDiagramFocus(markdown);
            if (focus.Focused)
            {
                var mm = Regex.Match(body, "<div class=\"mermaid\">.*?</div>", RegexOptions.Singleline);
                if (mm.Success) return BuildFocusedDiagramHtml(theme, mm.Value, focus.Title, focus.Subtitle);
            }
        }

        var isDark = theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro";
        var alertStyles = isDark ? AlertStylesDark : AlertStyles;

        var extraHead = BuildExtraHead(body, isDark);
        var attribution = BuildAttribution(settings, classification, theme);
        var toc = settings.IncludeToc ? BuildToc(body, theme) : "";
        // Free tier stamps a subtle footer on every export/preview; Pro removes it.
        var footer = AppServices.License.ShowFooter
            ? "<div class=\"mark-footer\">Made with <a href=\"https://github.com/thebubbsy/marksmith\">Marksmith</a> — turn AI chats into polished documents</div>"
            : "";

        // Callout titles get a leading icon via CSS ::before — the emoji from the style table
        // normally, or a plain geometric glyph (text-presentation Unicode, colored by the title's
        // accent) when the user has emoji stripped. Mirrors DocxExportService.RenderAlert.
        string AlertGlyph(string kind) => kind switch
        {
            "note" => "●", "tip" => "◆", "important" => "■", "warning" => "▲", "caution" => "✕", _ => "●",
        };
        var alertCss = string.Join("\n", alertStyles.Select(kv => $$"""
            .markdown-alert-{{kv.Key}} { border-left: 5px solid {{kv.Value.Color}}; background: {{theme.Secondary}}; }
            .markdown-alert-{{kv.Key}} .markdown-alert-title { color: {{kv.Value.Color}}; }
            .markdown-alert-{{kv.Key}} .markdown-alert-title::before { content: "{{(settings.NoEmoji ? AlertGlyph(kv.Key) : kv.Value.Icon)}} "; }
            .md-callout-{{kv.Key}} { border-left: 5px solid {{kv.Value.Color}}; }
            .md-callout-{{kv.Key}} > summary { color: {{kv.Value.Color}}; }
            .md-callout-{{kv.Key}} > summary::after { content: " {{(settings.NoEmoji ? AlertGlyph(kv.Key) : kv.Value.Icon)}}"; }
            """));

        // Foldable callouts (Obsidian `> [!tip]-`) render as a real <details> so the preview gets
        // native collapse-by-default + click-to-toggle. Shared chrome here; per-kind accent above.
        var calloutCss = $$"""
            .md-callout { background: {{theme.Secondary}}; border-radius: 6px; margin: 16px 0; overflow: hidden; }
            .md-callout > summary { cursor: pointer; padding: 10px 16px; font-weight: 700; list-style: none; user-select: none; -webkit-user-select: none; display: flex; align-items: center; }
            .md-callout > summary::-webkit-details-marker { display: none; }
            .md-callout > summary::before { content: "\25B8"; margin-right: 8px; font-size: 0.8em; transition: transform 0.15s; }
            .md-callout[open] > summary::before { transform: rotate(90deg); }
            .md-callout > summary::after { margin-left: auto; opacity: 0.85; }
            .md-callout > *:not(summary) { margin-left: 16px; margin-right: 16px; }
            .md-callout > *:last-child { margin-bottom: 12px; }
            """;

        // BrandFontFamily also arrives here from a "Copy as Markdown" clipboard capture (see
        // OutputOverride.SourceFontFamily / AppSettings.CloneWith) so the preview shows the reply in
        // the same font it had on the source AI-chat page, not just the DOCX export honoring it.
        var bodyFontFamily = string.IsNullOrWhiteSpace(settings.BrandFontFamily)
            ? "-apple-system, \"Segoe UI\", sans-serif"
            : $"\"{settings.BrandFontFamily.Trim().Replace("\"", "")}\", -apple-system, \"Segoe UI\", sans-serif";

        // lang/dir come from the source page's metadata on ingest (see AppSettings.ContentLanguage/
        // ContentDirection). Emitting dir="rtl" on <html> makes the whole document lay out
        // right-to-left in the preview AND the PDF, instead of forcing Arabic/Hebrew/etc. content
        // left-to-right. Values are sanitized to a strict allow-list so nothing from the page can
        // inject attributes into the tag.
        var htmlAttrs = BuildHtmlRootAttrs(settings.ContentLanguage, settings.ContentDirection);

        var mermaidEnabled = settings.MermaidEnabled && body.Contains("mermaid", StringComparison.OrdinalIgnoreCase);
        var mermaidScript = mermaidEnabled ? $$"""
            <script src="{{Services.WebAssets.Mermaid}}"></script>
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
                securityLevel: "strict"
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
            <!DOCTYPE html><html{{htmlAttrs}}><head><meta charset="UTF-8">
            {{mermaidScript}}
            {{extraHead}}
            <style>
            body { background: {{theme.Background}}; color: {{theme.Text}}; font-family: {{bodyFontFamily}}; line-height: 1.6; margin: 0; padding: 0; display: flex; flex-direction: column; align-items: center; width: 100%; }
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
            {{calloutCss}}
            /* Screen (the live preview): diagrams at NATURAL size. Wide ones break out of the text
               column to the full preview width; anything larger scrolls, and clicking opens the
               full-screen pan/zoom viewer. Print (the PDF export): fit-to-page-width. */
            .mermaid { width: fit-content; min-width: 100%; max-width: calc(100vw - 48px); position: relative; left: 50%; transform: translateX(-50%); margin: 32px 0; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; cursor: zoom-in; }
            .mermaid svg { max-width: none !important; }
            /* Diagrams recovered from a string literal inside a code EXAMPLE (not a real ```mermaid
               fence) sit directly under that code block instead of floating at the normal 32px
               diagram margin, and get a small caption so it reads as "this is what the code above
               renders", not as a second independent diagram. */
            .mermaid-embedded { margin-top: 8px; }
            .mermaid-embedded::before { content: "Rendered preview"; display: block; text-align: center; font-size: 11px; font-weight: 700; letter-spacing: 0.06em; text-transform: uppercase; color: {{theme.Text}}; opacity: 0.5; margin-bottom: 10px; }
            /* 2026 dialect elements emitted by DialectNormalizer */
            .wikilink { color: {{theme.Primary}}; border-bottom: 1px dashed {{theme.Primary}}; cursor: default; }
            .md-tag { display: inline-block; padding: 1px 9px; border-radius: 999px; background: {{theme.Secondary}}; border: 1px solid {{theme.Border}}; color: {{theme.Primary}}; font-size: 0.85em; }
            .code-title { display: inline-block; margin-bottom: -14px; padding: 4px 12px; border: 1px solid {{theme.Border}}; border-bottom: none; border-radius: 6px 6px 0 0; background: {{theme.Code}}; font-family: Consolas, monospace; font-size: 12px; color: {{theme.Text}}; opacity: 0.85; }
            .tab-label { display: inline-block; margin: 14px 0 6px 0; padding: 4px 14px; border: 1px solid {{theme.Border}}; border-radius: 6px 6px 0 0; border-bottom: 2px solid {{theme.Primary}}; background: {{theme.Secondary}}; font-weight: 700; font-size: 13px; color: {{theme.Primary}}; }
            .page-break { border: none; border-top: 2px dashed {{theme.Border}}; margin: 26px 0; position: relative; }
            .page-break::after { content: "page break"; position: absolute; top: -9px; left: 50%; transform: translateX(-50%); padding: 0 10px; background: {{theme.Background}}; font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: {{theme.Text}}; opacity: 0.55; }
            @media print { .page-break { page-break-after: always; border: none; } .page-break::after { content: ""; } }
            img { max-width: 100%; }
            .footnotes { margin-top: 30px; padding-top: 12px; border-top: 1px solid {{theme.Border}}; font-size: 0.9em; }
            #mk-lens { position: fixed; inset: 0; z-index: 99; display: none; background: {{theme.Background}}f2; cursor: grab; overflow: hidden; user-select: none; -webkit-user-select: none; }
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
            /* Plugin-rendered diagrams (e.g. PlantUML): same card treatment as .mermaid, but the SVG
               already carries its own colors from the plugin's own renderer, so no node/edge color
               overrides here. */
            .plugin-diagram { width: fit-content; max-width: calc(100vw - 48px); margin: 32px auto; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; text-align: center; }
            .plugin-diagram-missing, .plugin-diagram-error { margin: 8px 0 24px; padding: 10px 14px; border-radius: 6px; font-size: 13px; background: {{theme.Secondary}}; border: 1px solid {{theme.Border}}; color: {{theme.Text}}; }
            @media print {
              .plugin-diagram { max-width: 100%; }
              .plugin-diagram-missing, .plugin-diagram-error { display: none; }
            }
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

    private static readonly Regex MermaidFenceRe =
        new("```mermaid[ \\t]*\\n.*?```", RegexOptions.Singleline | RegexOptions.Compiled);

    // Is this document "just a diagram"? Exactly one mermaid block, a title, and at most a few
    // words of intro — no other diagrams, tables, images, lists or code. Returns the title/subtitle.
    private static (bool Focused, string Title, string Subtitle) AnalyzeDiagramFocus(string markdown)
    {
        if (MermaidFenceRe.Matches(markdown).Count != 1) return (false, "", "");
        var rest = MermaidFenceRe.Replace(markdown, "");
        if (rest.Contains("```")) return (false, "", ""); // another code block

        string title = "", subtitle = "";
        int proseLen = 0;
        foreach (var raw in rest.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) { if (title.Length == 0) title = line.TrimStart('#', ' ').Trim(); continue; }
            // Any richer block means it's a real document, not a diagram card.
            if (line.Contains('|') || line.StartsWith("![") || line.StartsWith("- ") ||
                line.StartsWith("* ") || line.StartsWith("> ") || Regex.IsMatch(line, @"^\d+\. "))
                return (false, "", "");
            if (subtitle.Length == 0) subtitle = line;
            proseLen += line.Length;
        }
        if (title.Length == 0 || proseLen > 320) return (false, "", "");
        return (true, StripInline(title), StripInline(subtitle));
    }

    private static string StripInline(string s) =>
        System.Net.WebUtility.HtmlEncode(Regex.Replace(s, @"[*_`~]", "").Trim());

    // Builds the ` lang="…" dir="…"` suffix for the <html> tag from source-page metadata. Both
    // values are page-derived, so they're strictly validated rather than interpolated raw: lang
    // must look like a BCP-47 tag (letters/digits/hyphen), dir must be exactly ltr/rtl/auto —
    // anything else is dropped, so the page can't smuggle extra attributes into the tag.
    private static string BuildHtmlRootAttrs(string? language, string? direction)
    {
        var attrs = "";
        var lang = (language ?? "").Trim();
        if (lang.Length is > 0 and <= 35 && Regex.IsMatch(lang, "^[A-Za-z0-9-]+$"))
            attrs += $" lang=\"{lang}\"";
        var dir = (direction ?? "").Trim().ToLowerInvariant();
        if (dir is "ltr" or "rtl" or "auto")
            attrs += $" dir=\"{dir}\"";
        return attrs;
    }

    // The focused diagram viewer (live preview only): a full-viewport pan/zoom stage holding a
    // single Mermaid diagram, with the title top-left and +/−/Reset controls. No Close — this IS
    // the view, not an overlay. Never used for export.
    private static string BuildFocusedDiagramHtml(ThemeDefinition theme, string mermaidDiv, string title, string subtitle)
    {
        var sub = string.IsNullOrEmpty(subtitle) ? "" : $"<div id=\"dv-sub\">{subtitle}</div>";
        return $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="{{Services.WebAssets.Mermaid}}"></script>
            <style>
            html, body { margin: 0; height: 100%; overflow: hidden; background: {{theme.Background}}; color: {{theme.Text}};
                         font-family: -apple-system, "Segoe UI", sans-serif; }
            /* Panning the diagram must never highlight its labels. */
            #dv, #dv-stage, #dv-inner { user-select: none; -webkit-user-select: none; }
            #dv { position: fixed; inset: 0; }
            #dv-head { position: absolute; top: 16px; left: 22px; z-index: 5; max-width: 62%; pointer-events: none; }
            #dv-title { font-size: 19px; font-weight: 700; color: {{theme.Heading}}; line-height: 1.25; }
            #dv-sub { font-size: 12.5px; opacity: 0.72; margin-top: 3px; }
            #dv-controls { position: absolute; top: 14px; right: 18px; z-index: 5; display: flex; gap: 8px; align-items: center; font-size: 13px; }
            #dv-controls #dv-pct { opacity: 0.7; min-width: 40px; text-align: right; }
            #dv-controls button { background: {{theme.Secondary}}; color: {{theme.Text}}; border: 1px solid {{theme.Border}};
                                  border-radius: 6px; padding: 4px 12px; font-size: 14px; cursor: pointer; }
            #dv-controls button:hover { border-color: {{theme.Heading}}; }
            #dv-stage { position: absolute; inset: 0; overflow: hidden; cursor: grab; }
            #dv-stage.dragging { cursor: grabbing; }
            #dv-inner { position: absolute; left: 0; top: 0; transform-origin: 0 0; }
            #dv-inner .mermaid { margin: 0; padding: 0; border: none; background: transparent; width: auto;
                                 min-width: 0; max-width: none; left: auto; transform: none; overflow: visible; }
            #dv-inner .mermaid svg { max-width: none !important; }
            .mermaid .node rect, .mermaid .node circle, .mermaid .node polygon, .mermaid .node path, .mermaid .cluster rect {
                stroke: {{theme.Line}} !important; stroke-width: 2px !important; fill: {{theme.Background}} !important; }
            .mermaid .edgePath path { stroke: {{theme.Line}} !important; stroke-width: 2px !important; }
            .mermaid .label { color: {{theme.Primary}} !important; }
            </style></head>
            <body>
            <div id="dv">
              <header id="dv-head"><div id="dv-title">{{title}}</div>{{sub}}</header>
              <div id="dv-controls"><span id="dv-pct">100%</span>
                <button id="dv-out" title="Zoom out">−</button>
                <button id="dv-in" title="Zoom in">+</button>
                <button id="dv-reset">Reset</button>
                <button id="dv-png" title="Save diagram as PNG">PNG</button>
                <button id="dv-svg" title="Save diagram as SVG">SVG</button></div>
              <div id="dv-stage"><div id="dv-inner">{{mermaidDiv}}</div></div>
            </div>
            <script>
            mermaid.initialize({ startOnLoad: true, theme: "base",
              themeVariables: { primaryColor: "{{theme.Background}}", primaryTextColor: "{{theme.Primary}}",
                primaryBorderColor: "{{theme.Line}}", lineColor: "{{theme.Line}}",
                secondaryColor: "{{theme.Secondary}}", tertiaryColor: "{{theme.Background}}" },
              maxTextSize: 10000000, maxNodes: 10000,
              flowchart: { useMaxWidth: false, htmlLabels: true, curve: "linear" },
              sequence: { useMaxWidth: false }, gantt: { useMaxWidth: false }, class: { useMaxWidth: false },
              er: { useMaxWidth: false }, pie: { useMaxWidth: false }, mindmap: { useMaxWidth: false },
              securityLevel: "strict" });
            (function () {
              const stage = document.getElementById("dv-stage"), inner = document.getElementById("dv-inner");
              const pct = document.getElementById("dv-pct");
              let sc = 1, tx = 0, ty = 0, drag = null, fitted = false, lastW = 0, lastH = 0;
              const apply = () => { inner.style.transform = `translate(${tx}px,${ty}px) scale(${sc})`;
                                    pct.textContent = Math.round(sc * 100) + "%"; };
              function fit(force) {
                const svg = inner.querySelector("svg"); if (!svg) return false;
                inner.style.transform = "none";
                const w = svg.getBoundingClientRect().width, h = svg.getBoundingClientRect().height;
                if (!w || !h) return false;
                // Mermaid can report an intermediate size mid-layout (multi-pass text measurement,
                // font loading) before settling on its final one — fitting to that reads as "zoomed
                // in wrong" and, since fitted then blocks the poll loop from ever trying again, it
                // never self-corrects. Require two consecutive identical readings (240ms apart)
                // before treating a measurement as final, unless force is set (the explicit Reset
                // button, where "measure once and go" is the whole point).
                if (!force) {
                  const stable = w === lastW && h === lastH;
                  lastW = w; lastH = h;
                  if (!stable) return false;
                }
                sc = Math.min(1.5, Math.min((stage.clientWidth - 48) / w, (stage.clientHeight - 96) / h));
                tx = (stage.clientWidth - w * sc) / 2; ty = Math.max(70, (stage.clientHeight - h * sc) / 2);
                apply(); return true;
              }
              // mermaid renders asynchronously — poll for the SVG, then fit once it's stable.
              const t = setInterval(() => { if (!fitted && fit(false)) { fitted = true; clearInterval(t); } }, 120);
              setTimeout(() => clearInterval(t), 8000);
              stage.addEventListener("wheel", (e) => { e.preventDefault();
                const r = stage.getBoundingClientRect(), cx = e.clientX - r.left, cy = e.clientY - r.top;
                const f = e.deltaY < 0 ? 1.15 : 1 / 1.15;
                tx = cx - (cx - tx) * f; ty = cy - (cy - ty) * f;
                sc = Math.min(12, Math.max(0.1, sc * f)); apply(); }, { passive: false });
              stage.addEventListener("pointerdown", (e) => { drag = { x: e.clientX - tx, y: e.clientY - ty };
                stage.classList.add("dragging"); stage.setPointerCapture(e.pointerId); });
              stage.addEventListener("pointermove", (e) => { if (drag) { tx = e.clientX - drag.x; ty = e.clientY - drag.y; apply(); } });
              stage.addEventListener("pointerup", () => { drag = null; stage.classList.remove("dragging"); });
              document.getElementById("dv-in").addEventListener("click", () => { sc = Math.min(12, sc * 1.25); apply(); });
              document.getElementById("dv-out").addEventListener("click", () => { sc = Math.max(0.1, sc / 1.25); apply(); });
              document.getElementById("dv-reset").addEventListener("click", () => { fit(true); });
              window.addEventListener("resize", () => { if (fitted) fit(true); });

              // Export the diagram as a file — the host (C#) shows a save dialog and writes it.
              const post = (m) => { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) {} };
              function svgText() {
                const svg = inner.querySelector("svg"); if (!svg) return null;
                const clone = svg.cloneNode(true);
                clone.setAttribute("xmlns", "http://www.w3.org/2000/svg");
                clone.style.background = "{{theme.Background}}";
                return '<?xml version="1.0" encoding="UTF-8"?>\n' + new XMLSerializer().serializeToString(clone);
              }
              document.getElementById("dv-svg").addEventListener("click", () => {
                const s = svgText(); if (s) post({ type: "save-diagram", format: "svg", data: s });
              });
              document.getElementById("dv-png").addEventListener("click", () => {
                const svg = inner.querySelector("svg"); if (!svg) return;
                const w = svg.getBoundingClientRect().width, h = svg.getBoundingClientRect().height;
                const url = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svgText());
                const img = new Image();
                img.onload = () => {
                  const c = document.createElement("canvas"); c.width = Math.ceil(w * 2); c.height = Math.ceil(h * 2);
                  const g = c.getContext("2d"); g.fillStyle = "{{theme.Background}}"; g.fillRect(0, 0, c.width, c.height);
                  g.drawImage(img, 0, 0, c.width, c.height);
                  post({ type: "save-diagram", format: "png", data: c.toDataURL("image/png") });
                };
                img.src = url;
              });
            })();
            </script>
            </body></html>
            """;
    }

    // Local-disk images can't load in the preview at all: the document is served from a secure
    // https origin (WebView2 virtual host / loopback server), from which file:-scheme and bare
    // C:\path subresources are blocked as mixed content. Inline them as data: URIs instead —
    // works in the preview, prints into the PDF, and keeps the app offline-first. Remote http(s)
    // images pass through untouched. Also honors Obsidian's `![alt|300](...)` width-hint syntax
    // for BOTH local and remote images (the |300 arrives glued to the alt text).
    private static readonly Regex ImgTag = new("<img src=\"([^\"]+)\" alt=\"([^\"]*)\"([^>]*)>", RegexOptions.Compiled);
    private static readonly Regex AltSizeHint = new(@"^(.*)\|(\d{2,4})$", RegexOptions.Compiled);
    // CRITICAL: WebView2's NavigateToString (and the Avalonia NativeWebView equivalent) silently
    // FAILS past roughly 2 MB — the preview then just spins forever. Base64 inflates a file ~1.34x,
    // so a couple of multi-MB photos inlined here blow that ceiling and take the whole preview down.
    // Cap it hard: skip any single image over ~900 KB, and stop inlining once the running total of
    // encoded image data would approach the budget — the doc's own markup plus this must stay well
    // under 2 MB. An image that's skipped keeps its original src (it just won't show in the preview),
    // which is strictly better than a preview that never loads. (Serving arbitrary local images
    // through the host's asset server, with no size limit, is the real fix — a follow-up.)
    private const long MaxInlineBudgetBytes = 1_200_000;

    private static string EmbedLocalImages(string body)
    {
        if (!body.Contains("<img", StringComparison.Ordinal)) return body;
        long budgetUsed = 0;
        return ImgTag.Replace(body, m =>
        {
            var src = m.Groups[1].Value;
            var alt = m.Groups[2].Value;
            var rest = m.Groups[3].Value;
            // Markdig percent-encodes link destinations (C:\Users\... arrives as C:%5CUsers%5C...),
            // so DETECT local paths against a decoded copy — but leave a remote URL's src exactly
            // as written, since decoding could corrupt legitimately-encoded query strings.
            var decoded = Uri.UnescapeDataString(System.Net.WebUtility.HtmlDecode(src));

            var width = "";
            var sizeHint = AltSizeHint.Match(alt);
            if (sizeHint.Success)
            {
                alt = sizeHint.Groups[1].Value.Trim();
                width = $" width=\"{sizeHint.Groups[2].Value}\"";
            }

            var localPath = decoded.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? decoded[8..].Replace('/', '\\')
                : (decoded.Length > 2 && decoded[1] == ':' ? decoded : null);
            if (localPath is not null)
            {
                try
                {
                    var info = new FileInfo(localPath);
                    if (info.Exists)
                    {
                        // Large/high-res local images (phone photos, screenshots, 1273px logos) are
                        // downscaled to a document-sensible size FIRST — a page is only ~800px wide,
                        // so a bigger source is wasted bytes that (base64-inflated) blow the
                        // NavigateToString ceiling and take the whole preview down. Downscaling turns
                        // a 1.7 MB PNG into ~100 KB that looks identical at display size. Small images
                        // pass through untouched; SVG is never rasterized.
                        var (data, mime) = PrepareImageForInline(localPath, info);
                        if (data is not null && mime is not null &&
                            budgetUsed + data.Length * 4 / 3 <= MaxInlineBudgetBytes)
                        {
                            src = $"data:{mime};base64,{Convert.ToBase64String(data)}";
                            budgetUsed += data.Length * 4 / 3;
                        }
                    }
                }
                catch { /* unreadable/undecodable file: leave the original src; alt text still shows */ }
            }

            return $"<img src=\"{src}\" alt=\"{System.Net.WebUtility.HtmlEncode(alt)}\"{width}{rest}>";
        });
    }

    private const int MaxImageDimension = 1400; // downscale target: covers 2x the ~800px page width

    // Returns the bytes+mime to inline for a local image: the file as-is when it's already small
    // and modest-resolution, or a downscaled re-encode when it's oversized. Returns (null, null)
    // for formats we don't rasterize (SVG) or anything Skia can't decode.
    private static (byte[]? Data, string? Mime) PrepareImageForInline(string path, FileInfo info)
    {
        var ext = info.Extension.ToLowerInvariant();
        if (ext == ".svg") return (null, null); // vector: inlining raw would need XML, not a raster path

        var raw = File.ReadAllBytes(path);

        // Fast path: already small AND not huge-resolution → inline the original bytes verbatim,
        // preserving the exact format (and any transparency/animation) with zero re-encoding.
        if (info.Length <= 350_000)
        {
            using var codec = SKCodec.Create(new MemoryStream(raw));
            if (codec is null) return (null, null); // undecodable — caller leaves original src
            if (Math.Max(codec.Info.Width, codec.Info.Height) <= MaxImageDimension)
            {
                var mime = ext switch
                {
                    ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
                    ".webp" => "image/webp", ".bmp" => "image/bmp", _ => "image/png",
                };
                return (raw, mime);
            }
        }

        using var bitmap = SKBitmap.Decode(raw);
        if (bitmap is null) return (null, null);

        var scale = (double)MaxImageDimension / Math.Max(bitmap.Width, bitmap.Height);
        SKBitmap scaled = scale < 1.0
            ? bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SKFilterQuality.High)
            : bitmap;
        try
        {
            using var image = SKImage.FromBitmap(scaled);
            // PNG keeps sharp edges + transparency for graphics/logos; the size win comes from the
            // resolution drop, not lossy compression, so text/line art stays crisp.
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            return (encoded.ToArray(), "image/png");
        }
        finally
        {
            if (!ReferenceEquals(scaled, bitmap)) scaled.Dispose();
        }
    }

    // KaTeX for math and highlight.js for code fences, pulled from the bundled offline assets only
    // when the rendered body actually needs them (plain documents stay dependency-free).
    private static string BuildExtraHead(string body, bool isDark)
    {
        var head = "";
        if (body.Contains("class=\"math\""))
        {
            // mhchem (KaTeX's official chemistry extension, \ce{}/\pu{}) loads only when the
            // document actually uses it. Defer-scripts execute in order, so it registers itself
            // against the katex global after katex.min.js and before auto-render's onload fires.
            var mhchem = body.Contains("\\ce{") || body.Contains("\\pu{")
                ? $"""<script defer src="{Services.WebAssets.Base}/mhchem.min.js"></script>{"\n"}"""
                : "";
            head += $$"""
                <link rel="stylesheet" href="{{Services.WebAssets.KatexCss}}">
                <script defer src="{{Services.WebAssets.KatexJs}}"></script>
                {{mhchem}}<script defer src="{{Services.WebAssets.KatexAutoRender}}"
                        onload="renderMathInElement(document.body, {
                            delimiters: [{left:'$$',right:'$$',display:true},{left:'$',right:'$',display:false},{left:'\\(',right:'\\)',display:false},{left:'\\[',right:'\\]',display:true}],
                            // \tag is display-mode-only in KaTeX and hard-errors in inline math,
                            // which used to leave the WHOLE span as raw text. Override it to render
                            // as an inline '(label)' — matching how the DOCX exporter emits it.
                            macros: {'\\tag': '\\;\\;(\\text{#1})'},
                            // Any other unsupported command degrades to red text instead of
                            // aborting the span and dumping raw source at the reader.
                            throwOnError: false
                        });"></script>
                """;
        }
        if (body.Contains("language-"))
        {
            var hlTheme = isDark ? "github-dark" : "github";
            head += $"""
                <link rel="stylesheet" href="{Services.WebAssets.Base}/{hlTheme}.min.css">
                <script src="{Services.WebAssets.HighlightJs}"></script>
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
