using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Models;
using Markdig;
using SkiaSharp;

namespace MarkSmith.Services;

// Port of create_html_content() from md_to_pdf_tui.py: Markdown -> themed HTML string that
// WebView2 can navigate to for live preview and PDF export via CoreWebView2.PrintToPdfAsync.
public sealed partial class MarkdownHtmlService
{
    // Render() runs on every preview keystroke. The patterns below were previously invoked via
    // the static Regex.* helpers, which re-parse+hash the pattern into .NET's bounded (15-entry)
    // process-wide cache on each call. Hoisting them to source-generated [GeneratedRegex] matchers
    // makes each one a pre-compiled, allocation-free singleton with no per-call cache lookup —
    // the patterns are copied verbatim, so behavior is identical.
    [GeneratedRegex(@"\$\$\s*(\\begin\{[A-Za-z*]+\}.*?\\end\{[A-Za-z*]+\})\s*\$\$", RegexOptions.Singleline)]
    private static partial Regex MathEnvBlockRe();

    [GeneratedRegex("<pre><code class=\"language-mermaid\">(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex MermaidFenceHtmlRe();

    [GeneratedRegex("<pre><code(?: class=\"language-[\\w-]+\")?>(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex AnyCodeBlockRe();

    [GeneratedRegex("`([^`]+)`|'([^']+)'|\"([^\"]+)\"", RegexOptions.Singleline)]
    private static partial Regex QuotedLiteralRe();

    [GeneratedRegex("<pre><code class=\"language-([\\w-]+)\">(.*?)</code></pre>", RegexOptions.Singleline)]
    private static partial Regex PluginLangCodeRe();

    [GeneratedRegex("<div class=\"mermaid\">.*?</div>", RegexOptions.Singleline)]
    private static partial Regex MermaidDivRe();

    // :::smartart type="…" blocks: the marker line, then nested "- " bullets (indentation =
    // hierarchy), closed by ":::". Same syntax the DOCX export's SmartArtDetector accepts. A blank
    // line (or document start, allowing a leading BOM) must precede the block — matching Markdig's
    // own block semantics; fenced-code spans are excluded separately via FencedSpans().
    [GeneratedRegex(@"(?:\A\uFEFF?|\n\n)\s*:::smartart\s+type=""([^""]+)""\s*\r?\n(.*?)\r?\n:::\s*", RegexOptions.Singleline)]
    private static partial Regex SmartArtBlockRe();

    // Fenced code spans (```…``` or ~~~…~~~, same opener/closer) — smartart markers inside these
    // must never be lifted out, even when a blank line sits inside the fence.
    [GeneratedRegex(@"^[ \t]*(```+|~~~+)[^\n]*\n[\s\S]*?\n[ \t]*\1\s*$", RegexOptions.Multiline)]
    private static partial Regex FenceSpanRe();

    [GeneratedRegex(@"^\d+\. ")]
    private static partial Regex OrderedListLineRe();

    [GeneratedRegex(@"[*_`~]")]
    private static partial Regex InlineMarkRe();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex LangTagRe();

    [GeneratedRegex("<h([123]) id=\"([^\"]+)\"[^>]*>(.*?)</h\\1>", RegexOptions.Singleline)]
    private static partial Regex TocHeadingRe();

    [GeneratedRegex(@"</?[a-z][a-z0-9]*(?:[\s/>]|$)[^>]*>")]
    private static partial Regex HtmlTagStripRe();

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

    private static List<(int Start, int End)> FencedSpans(string markdown)
    {
        var spans = new List<(int, int)>();
        foreach (Match m in FenceSpanRe().Matches(markdown ?? ""))
        {
            spans.Add((m.Index, m.Index + m.Length));
        }
        return spans;
    }

    private static string NormalizeForRender(string markdown, AppSettings settings)
    {
        markdown = TextNormalizer.Newlines(markdown);
        markdown = MathEnvBlockRe().Replace(markdown, "\n$$$$\n${1}\n$$$$\n");
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = KanbanNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown, settings.DashMode);
        markdown = DiagramFenceSniffer.Apply(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);
        return markdown;
    }

    // interactive == the LIVE PREVIEW (not PDF export). Only then may we swap in the focused
    // diagram viewer; the exported document is never affected.
    public string Render(string markdown, AppSettings settings, ThemeDefinition theme,
        LlmClassification? classification = null, bool interactive = false)
    {
        if (settings.ThemeLightInfluence)        {
            theme = theme.ApplyLightInfluence();
        }

        markdown = NormalizeForRender(markdown, settings);
        markdown = MarkSmith.Core.Composer.ShapeMarkdownHtml.PreTransform(markdown);

        // :::smartart blocks (the Markdig pipeline has no container extension) are lifted OUT of the
        // markdown into HTML-comment placeholders BEFORE parsing, then each placeholder is swapped
        // for a generated SVG diagram after the sanitize step — the SVG is our own trusted markup,
        // never user HTML, mirroring the mermaid escape-safety model. Fenced code spans are excluded
        // so a code sample merely SHOWING the syntax is never lifted.
        var smartArtBlocks = new List<(string Alias, string Inner)>();
        var smartArtFences = FencedSpans(markdown);
        markdown = SmartArtBlockRe().Replace(markdown, m =>
        {
            foreach (var f in smartArtFences)
            {
                if (m.Index >= f.Start && m.Index < f.End) return m.Value; // inside a code fence
            }
            string alias = m.Groups[1].Value.Trim().ToLowerInvariant();
            smartArtBlocks.Add((alias, m.Groups[2].Value.Trim()));
            return $"\n\n<!--SMARTART:{smartArtBlocks.Count - 1}-->\n\n";
        });

        var body = Markdown.ToHtml(markdown, settings.NoEmoji ? PipelineNoEmoji : Pipeline);
        
        if (settings.NoEmoji) body = EmojiStripper.Strip(body);
        
        // Sanitize the markdig output FIRST — everything appended after this point (mermaid init,
        // KaTeX, the lens, plugin SVGs) is our own, trusted markup and must not be filtered.
        body = HtmlSanitizer.Apply(body);
        body = EmbedLocalImages(body);

        // Markdig renders ```mermaid fences as <pre><code class="language-mermaid">…</code></pre> with
        // the content HTML-escaped. Rewrite the fence to a <div class="mermaid">, but KEEP the content
        // escaped: mermaid reads the element's textContent (which the browser decodes automatically),
        // so diagrams render correctly, while the browser never parses malicious markup like
        // <img onerror=…> as live HTML inside the div. HtmlDecode-ing here would reintroduce that XSS.
        body = MermaidFenceHtmlRe().Replace(body,
            m => $"<div class=\"mermaid\">{m.Groups[1].Value}</div>");

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
            body = AnyCodeBlockRe().Replace(body,
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
                        foreach (Match lit in QuotedLiteralRe().Matches(codeContent))
                        {
                            var group = lit.Groups[1].Success ? lit.Groups[1] : lit.Groups[2].Success ? lit.Groups[2] : lit.Groups[3];
                            var candidate = group.Value.Trim();
                            if (MermaidDiagramStart.IsMatch(candidate))
                                extras.Append($"<div class=\"mermaid mermaid-embedded\">{System.Net.WebUtility.HtmlEncode(candidate)}</div>");
                        }
                    }
                    return extras.Length > 0 ? m.Value + extras : m.Value;
                });
        }

        // Plugin-provided diagram languages (e.g. ```plantuml via the optional PlantUML plugin) —
        // a generalization of the ```mermaid handling above for fence languages the core pipeline
        // doesn't know how to render itself. Unlike Mermaid (rendered client-side by mermaid.min.js
        // inside the WebView), a diagram plugin renders out-of-process, synchronously, right here —
        // by the time this HTML reaches the browser the SVG already exists. See
        // MarkSmith.Plugins.IDiagramPlugin / PluginManager.
        var pluginTheme = Plugins.PluginTheme.From(theme);
        body = PluginLangCodeRe().Replace(body,
            m =>
            {
                var language = m.Groups[1].Value;
                var installed = AppServices.Plugins.FindDiagramRenderer(language);
                if (installed != null)
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                    var svg = AppServices.Plugins.RenderToSvgCached(installed, decoded, pluginTheme);
                    if (svg != null)
                    {
                        svg = Plugins.SvgSanitizer.Sanitize(svg);
                        // Theme-aware engines (PlantUML skinparams, Graphviz flags) already rendered
                        // on-theme, so their SVG must NOT be filtered. Non-theme-aware engines emit
                        // artwork assuming a light page (black strokes, transparent bg) which is
                        // invisible on a dark theme (the PlantUML-arrows-on-black bug) — those get
                        // the auto-invert treatment on dark themes so they stay legible.
                        var cls = installed.IsThemeAware ? "plugin-diagram" : "plugin-diagram plugin-diagram-autoinvert";
                        return $"<div class=\"{cls}\">{svg}</div>";
                    }
                    // installed.Name is author-controlled (a dropped-in plugin.json) and lands in the
                    // WebView DOM — escape it (same for the missing-plugin hint below).
                    return m.Value + "<div class=\"plugin-diagram-error\">⚠ " + System.Net.WebUtility.HtmlEncode(installed.Name) + " couldn't render this diagram — check the syntax.</div>";
                }

                var known = AppServices.Plugins.FindAnyDiagramPlugin(language);
                if (known != null)
                    return m.Value + $"<div class=\"plugin-diagram-missing\">🧩 Install the <b>{System.Net.WebUtility.HtmlEncode(known.Name)}</b> plugin (Settings → Plugins) to render this diagram.</div>";

                return m.Value;
            });

        // When the whole document is essentially one diagram + a title (and maybe a few words),
        // the live preview becomes a dedicated diagram viewer: title top-left, +/−/Reset, pan/zoom.
        if (interactive && settings.MermaidEnabled)
        {
            var focus = AnalyzeDiagramFocus(markdown);
            if (focus.Focused)
            {
                var mm = MermaidDivRe().Match(body);
                if (mm.Success) return BuildFocusedDiagramHtml(theme, mm.Value, focus.Title, focus.Subtitle);
            }
        }

        var isDark = !settings.ThemeLightInfluence && 
                     (theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro");
        var alertStyles = isDark ? AlertStylesDark : AlertStyles;

        // Plugin diagrams (PlantUML, Graphviz, D2, …) emit their own SVG with fixed dark-on-white
        // line art, so on a dark theme that white slab jars against the page. Flip the art to light
        // with the canonical invert + hue-rotate trick (keeps real colours roughly hue-correct);
        // light themes leave it untouched. The frame around it is themed in CSS below, like .mermaid.
        var pluginDiagramSvgFilter = isDark ? "filter: invert(1) hue-rotate(180deg);" : "";

        // SmartArt diagrams: swap each lifted placeholder for its generated SVG, framed like
        // .mermaid and auto-inverted on dark themes (the renderer's art assumes a light page).
        for (int i = 0; i < smartArtBlocks.Count; i++)
        {
            var (alias, inner) = smartArtBlocks[i];
            string svg;
            try
            {
                var ast = MarkdownAstParser.Parse(inner);
                var pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias);
                svg = HtmlPreviewRenderer.RenderHtml(ast, pkg != null ? alias : "hierarchy", pkg?.Title ?? alias);
            }
            catch (Exception ex)
            {
                svg = "<div class=\"smartart-error\">⚠ SmartArt couldn't render: " +
                      System.Net.WebUtility.HtmlEncode(ex.Message) + "</div>";
            }
            string cls = isDark ? "smartart smartart-autoinvert" : "smartart";
            body = body.Replace($"<!--SMARTART:{i}-->", $"<div class=\"{cls}\">{svg}</div>");
        }

        var extraHead = BuildExtraHead(body, theme);
        var attribution = BuildAttribution(settings, classification, theme);
        var toc = settings.IncludeToc ? BuildToc(body, theme) : "";
        var stats = DocumentStatsService.Analyze(markdown);
        var statsPill = settings.ShowWordCount && stats.Words > 0 ? $"<div class=\"stats-pill\"><svg style=\"width:12px;height:12px;vertical-align:-1px;margin-right:4px\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"12\" cy=\"12\" r=\"10\"/><polyline points=\"12 6 12 12 16 14\"/></svg>{stats.SummaryText}</div>" : "";
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
        // the same font it had on the source AI-chat page, not just the DOCX export honoring it.
        // Typography presets (Task 16) layer underneath the brand font: BrandFontFamily wins, then
        // the selected preset, then the format-specific default. A custom TTF/OTF (CustomFontPath)
        // is embedded via @font-face so Chromium's PDF print uses the exact font.
        string bodyFontFamily;
        if (!string.IsNullOrWhiteSpace(settings.BrandFontFamily))
        {
            bodyFontFamily = $"\"{settings.BrandFontFamily.Trim().Replace("\"", "")}\", -apple-system, \"Segoe UI\", sans-serif";
        }
        else
        {
            var preset = FontManagerService.FindPreset(settings.FontPreset);
            bodyFontFamily = preset != null && preset.Id != FontManagerService.SystemPresetId
                ? preset.CssStack
                : (settings.TargetFormat == "docx" ? "\"Calibri\", \"Cambria\", sans-serif" : FontManagerService.DefaultStack);
        }

        // Custom font embedding (Task 16): inline the configured TTF/OTF as a base64 @font-face rule
        // and prefer it for the body so the rendered document/PDF uses the exact font.
        var fontFaceCss = "";
        if (FontManagerService.IsEmbeddableFontFile(settings.CustomFontPath))
        {
            var face = FontManagerService.BuildFontFaceCss(settings.CustomFontPath);
            if (face != null)
            {
                fontFaceCss = face;
                bodyFontFamily = $"\"{FontManagerService.GetFontFamilyName(settings.CustomFontPath)}\", {bodyFontFamily}";
            }
        }

        // lang/dir come from the source page's metadata on ingest (see AppSettings.ContentLanguage/
        // ContentDirection). Emitting dir="rtl" on <html> makes the whole document lay out
        // right-to-left in the preview AND the PDF, instead of forcing Arabic/Hebrew/etc. content
        // left-to-right. Values are sanitized to a strict allow-list so nothing from the page can
        // inject attributes into the tag.
        var htmlAttrs = BuildHtmlRootAttrs(settings.ContentLanguage, settings.ContentDirection);

        var mermaidEnabled = settings.MermaidEnabled && body.Contains("mermaid", StringComparison.OrdinalIgnoreCase);
        var mermaidScript = mermaidEnabled ? $$"""
            <link rel="stylesheet" href="{{Services.WebAssets.LiquidFillCss}}">
            <script src="{{Services.WebAssets.Mermaid}}"></script>
            <script src="{{Services.WebAssets.MermaidInteropJs}}"></script>
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
            </script>
            """ : "";

        // Click-to-expand zoom lens — works for BOTH Mermaid and plugin (Graphviz/D2/Typst/…)
        // diagrams, so it lives in its own script gated on either being present, NOT bundled with
        // mermaid.js's loader (a Graphviz-only document must still get the lens without pulling in
        // 2.5 MB of mermaid.js it never uses).
        var hasZoomable = mermaidEnabled || body.Contains("plugin-diagram", StringComparison.Ordinal);
        var lensScript = hasZoomable ? """
            <script>
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
                    // Use the diagram's NATURAL width (viewBox/width attr), not getBoundingClientRect():
                    // on-screen the SVG may be shrunk to fit its column (max-width:100%), but the lens
                    // clone renders at natural size and we want the opening scale to fit that.
                    const w = (svg.viewBox && svg.viewBox.baseVal && svg.viewBox.baseVal.width) ||
                              parseFloat(svg.getAttribute("width")) ||
                              svg.getBoundingClientRect().width || 800;
                    sc = Math.min(1, (innerWidth - 60) / w);
                    tx = Math.max(30, (innerWidth - w * sc) / 2); ty = 40;
                    lens.classList.add("open"); bar.style.display = "flex"; apply();
                };
                const closeLens = () => { lens.classList.remove("open"); bar.style.display = "none"; };
                document.querySelectorAll(".mermaid, .plugin-diagram").forEach(m =>
                    m.addEventListener("click", (e) => {
                        if (e.target.closest(".mermaid-edit-btn")) return;
                        const s = m.querySelector("svg"); if (s) openLens(s);
                    }));
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

        string effectiveBodyBg = settings.ThemeLightInfluence ? $"radial-gradient(circle at center, #ffffff 40%, {theme.Background} 120%)" : theme.Background;
        string effectiveText = theme.Text;

        bool isLight = ThemeDefinition.IsLight(theme.Background);
        string workspaceBg = interactive ? (isLight ? "#eaeaea" : "#141416") : effectiveBodyBg;
        string pageBg = effectiveBodyBg;
        string bodyClass = isLight ? "ms-light" : "ms-dark";

        var overflowScript = interactive ? $$"""
            <script>
            // Robust Mermaid Parse Error Interception
            window.mermaidError = null;
            if (window.mermaid) {
                const origParseError = mermaid.parseError;
                mermaid.parseError = function(err, hash) {
                    window.mermaidError = err;
                    if (origParseError) origParseError(err, hash);
                    console.error("Mermaid Parse Error:", err);
                    try { window.chrome.webview.postMessage(JSON.stringify({ type: "mermaid-error", error: err.toString() })); } catch(e) {}
                };
            }

            function escapeHtml(str) {
                return str.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#039;");
            }

            function checkPageOverflow() {
                const canvas = document.getElementById("canvas");
                if (!canvas) return;
                
                // Render Mermaid custom error cards if parseError occurred or standard error display was shown
                document.querySelectorAll(".mermaid").forEach(m => {
                    if (m.innerText.includes("Syntax error") || m.querySelector(".error-icon") || (window.mermaidError && m.querySelector("svg") === null)) {
                        m.classList.add("mermaid-render-failed");
                        const errText = window.mermaidError ? window.mermaidError.toString() : m.innerText;
                        m.innerHTML = `<div class="mermaid-error-card"><strong>⚠️ Mermaid Render Error</strong><pre>${escapeHtml(errText)}</pre></div>`;
                    }
                });

                const canvasRect = canvas.getBoundingClientRect();
                const overflows = [];
                
                // Check elements inside canvas
                canvas.querySelectorAll("table, pre, img, .mermaid, .plugin-diagram, .math").forEach(el => {
                    const rect = el.getBoundingClientRect();
                    // Allow 3px buffer for margins/paddings
                    if (rect.right > canvasRect.right + 3) {
                        const amount = Math.round(rect.right - canvasRect.right);
                        let name = el.tagName.toLowerCase();
                        if (el.classList.contains("mermaid")) name = "Mermaid Diagram";
                        else if (el.classList.contains("plugin-diagram")) name = "Plugin Diagram";
                        else if (name === "pre") name = "Code Block";
                        
                        overflows.push({ element: name, amount: amount });
                        el.style.outline = "2px dashed #e53e3e";
                        el.title = `Overflowing page boundary by ${amount}px`;
                    } else {
                        // Reset outline if it was resolved
                        if (el.style.outline === "2px dashed rgb(229, 62, 62)") {
                            el.style.outline = "";
                        }
                    }
                });

                // Display warning banner at the top/bottom if there's any overflow
                let banner = document.getElementById("overflow-banner");
                if (overflows.length > 0) {
                    if (!banner) {
                        banner = document.createElement("div");
                        banner.id = "overflow-banner";
                        document.body.appendChild(banner);
                    }
                    const list = overflows.map(o => `${o.element} (+${o.amount}px)`).join(", ");
                    banner.innerHTML = `⚠️ <strong>Page Width Overflowed:</strong> ${list} exceed page margins. Clicking diagram opens zoom view.`;
                    banner.style.display = "block";

                    // Post warning to C# host
                    try { window.chrome.webview.postMessage(JSON.stringify({ type: "page-overflow", elements: overflows })); } catch(e) {}
                } else if (banner) {
                    banner.style.display = "none";
                }
            }
            function updatePageBreaks() {
                if ({{settings.UnlimitedHeight.ToString().ToLower()}}) return;
                const canvas = document.getElementById("canvas");
                if (!canvas) return;

                document.querySelectorAll(".page-break-gap").forEach(el => el.remove());

                // A4 page height at 96 DPI: the preview page is always at least one full A4 page
                // tall (min-height on #canvas), so the dashed break markers sit on true page
                // boundaries — every 1123px — and a document that fits on one page shows none.
                const pageHeight = 1123;
                const totalHeight = canvas.scrollHeight;
                if (totalHeight <= pageHeight + 50) return;

                const pageCount = Math.ceil(totalHeight / pageHeight);
                for (let i = 1; i < pageCount; i++) {
                    const gap = document.createElement("div");
                    gap.className = "page-break-gap";
                    gap.style.top = (i * pageHeight) + "px";
                    gap.setAttribute("data-page", "Page " + i + " Break · Page " + (i + 1) + " Starts Below");
                    canvas.appendChild(gap);
                }
            }

            function makeMermaidInteractive() {
                // No upper-right button overlay — long-press water-fill gesture handles Studio launch
            }

            window.addEventListener("load", () => {
                setTimeout(checkPageOverflow, 800);
                setTimeout(updatePageBreaks, 600);
                setTimeout(makeMermaidInteractive, 1000);
            });
            window.addEventListener("resize", () => {
                checkPageOverflow();
                updatePageBreaks();
                makeMermaidInteractive();
            });
            
            // Also monitor DOM mutations in case of dynamically injected content
            const observer = new MutationObserver((mutations) => {
                setTimeout(checkPageOverflow, 200);
                setTimeout(updatePageBreaks, 300);
                setTimeout(makeMermaidInteractive, 400);
            });
            observer.observe(canvas, { childList: true, subtree: true });
            </script>
            """ : "";

        // Mini-TOC scroll-spy (live preview only, and only when a TOC was emitted): as the document
        // scrolls, the TOC entry for the heading nearest the top of the viewport gets .active. The
        // heading ids come from Markdig's AutoIdentifiers — the same ids BuildToc links to.
        var scrollSpyScript = interactive && toc.Length > 0 ? """
            <script>
            window.addEventListener("DOMContentLoaded", () => {
                // Re-query #toc and the headings on every pass: live in-place canvas swaps
                // (split typing / portal edits) replace them wholesale, so nodes cached at
                // load would go stale after the first swap and the spy would freeze.
                let active = null, ticking = false;
                const spy = () => {
                    ticking = false;
                    const toc = document.getElementById("toc");
                    if (!toc) return;
                    let link = null;
                    for (const a of toc.querySelectorAll('a[href^="#"]')) {
                        const el = document.getElementById(decodeURIComponent(a.getAttribute("href").slice(1)));
                        if (!el) continue;
                        if (!link) link = a; // first resolvable heading is the default
                        if (el.getBoundingClientRect().top <= 120) link = a; else break;
                    }
                    if (link !== active) {
                        if (active) active.classList.remove("active");
                        if (link) link.classList.add("active");
                        active = link;
                    }
                };
                const onScroll = () => { if (!ticking) { ticking = true; requestAnimationFrame(spy); } };
                window.addEventListener("scroll", onScroll, { passive: true });
                window.addEventListener("resize", onScroll, { passive: true });
                spy();
            });
            </script>
            """ : "";

        // Issue-locator homing beacon (ISS-012, live preview only): the app calls
        // triggerRedRadarBeacon(line) when the user clicks a lint issue in the sidebar; we scroll
        // the matching element into view, flash a red highlight and drop a pulsing radar ring on it.
        var radarScript = interactive ? """
            <script>
            function triggerRedRadarBeacon(lineNumber) {
                const el = document.querySelector(`[data-line='${lineNumber}']`) || document.querySelector("h1, h2, p, pre");
                if (el) {
                    el.scrollIntoView({ behavior: "smooth", block: "center" });
                    el.classList.add("issue-target-highlight");

                    const rect = el.getBoundingClientRect();
                    const beacon = document.createElement("div");
                    beacon.className = "radar-beacon-container";
                    beacon.style.left = (rect.left + 30) + "px";
                    beacon.style.top = (rect.top + window.scrollY + rect.height / 2) + "px";
                    beacon.innerHTML = '<div class="radar-beacon-ring"></div><div class="radar-beacon-ring"></div>';

                    document.body.appendChild(beacon);

                    setTimeout(() => {
                        beacon.remove();
                        el.classList.remove("issue-target-highlight");
                    }, 2500);
                }

                // Flash & scroll issue line inside open Looking Glass portal
                const portalTa = document.getElementById("portal-textarea");
                if (portalTa) {
                    const text = portalTa.value || "";
                    const lines = text.split("\n");
                    let lineIdx = Math.max(0, lineNumber - 1);
                    if (lineIdx < lines.length) {
                        let offset = 0;
                        for (let i = 0; i < lineIdx; i++) offset += lines[i].length + 1;
                        portalTa.focus();
                        try { portalTa.setSelectionRange(offset, offset + lines[lineIdx].length); } catch (x) {}
                    }
                    const aperture = document.getElementById("portal-aperture");
                    if (aperture) {
                        aperture.style.outline = "3px solid #ef4444";
                        aperture.style.boxShadow = "0 0 25px rgba(239, 68, 68, 0.7)";
                        setTimeout(() => {
                            aperture.style.outline = "";
                            aperture.style.boxShadow = "";
                        }, 2500);
                    }
                }
            }
            </script>
            """ : "";

        // Interactive tabbed content (ISS-015, live preview only): switches the visible pane within
        // a .md-tab-group when its tab button is clicked. Print ignores this (CSS shows all panes).
        var tabScript = interactive ? """
            <script>
            function selectMdTab(btn, targetId) {
                const group = btn.closest(".md-tab-group");
                if (!group) return;

                group.querySelectorAll(".md-tab-link").forEach(b => {
                    b.classList.remove("active");
                    b.setAttribute("aria-selected", "false");
                });
                group.querySelectorAll(".md-tab-content").forEach(c => {
                    c.classList.remove("active");
                });

                btn.classList.add("active");
                btn.setAttribute("aria-selected", "true");
                const target = group.querySelector("#" + targetId);
                if (target) {
                    target.classList.add("active");
                }
            }

            document.addEventListener("keydown", (e) => {
                if (e.target && e.target.classList && e.target.classList.contains("md-tab-link")) {
                    const nav = e.target.closest(".md-tab-nav");
                    if (!nav) return;
                    const tabs = Array.from(nav.querySelectorAll(".md-tab-link"));
                    const idx = tabs.indexOf(e.target);
                    if (e.key === "ArrowRight" && idx >= 0 && idx < tabs.length - 1) {
                        e.preventDefault();
                        tabs[idx + 1].focus();
                        tabs[idx + 1].click();
                    } else if (e.key === "ArrowLeft" && idx > 0) {
                        e.preventDefault();
                        tabs[idx - 1].focus();
                        tabs[idx - 1].click();
                    }
                }
            });
            </script>
            """ : "";

        // Looking Glass portal mode (ISS-004, live preview only, opt-in): the rendered preview is
        // the default surface; clicking it opens a circular "aperture" that reveals the editable
        // Markdown source sitting BEHIND the preview, through a fog-of-war blur (clear at the
        // caret, blurring back to the full preview). A glowing cursor ring tracks the pointer and
        // a short Web Audio whir + iris animation play as each portal opens. Edits are synced back
        // to the app over the postMessage bridge (portal-open / portal-edit / portal-closed).
        var revealScope = Math.Clamp(settings.PortalRevealScope, 0, 100);
        // Portal shape: a circle spotlight, full-width "focus" reading bands in three heights, a
        // square, or the logo cutout. Sanitized to the known set so the value can be inlined into
        // the script safely.
        var portalShape = settings.PortalShape is "focus1" or "focus2" or "focus3" or "square" or "logo" ? settings.PortalShape : "circle";
        // Focus-blur initial state for this render: whether the rendered preview behind an open
        // portal blurs. Ctrl+Alt+X in the app toggles it live via __portalSetBlur; a full
        // re-render re-inlines the persisted choice. Inlined as a bare true/false literal.
        var portalFocusBlur = settings.PortalFocusBlur ? "true" : "false";
        var portalScript = interactive && settings.LookingGlassMode ? $$"""
            <script>
            function playPortalWhirSound() {
                try {
                    const AudioCtx = window.AudioContext || window.webkitAudioContext;
                    if (!AudioCtx) return;
                    const ctx = new AudioCtx();
                    const osc = ctx.createOscillator();
                    const gain = ctx.createGain();

                    osc.type = "sine";
                    osc.frequency.setValueAtTime(130, ctx.currentTime);
                    osc.frequency.exponentialRampToValueAtTime(450, ctx.currentTime + 0.22);

                    gain.gain.setValueAtTime(0.15, ctx.currentTime);
                    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.22);

                    osc.connect(gain);
                    gain.connect(ctx.destination);

                    osc.start();
                    osc.stop(ctx.currentTime + 0.22);
                } catch(e) {}
            }

            (function () {
                const REVEAL = {{revealScope}}; // size dial 0..100 — scales whichever shape is active
                // Shape state is mutable: the app's shape picker pushes straight in via
                // __portalSetShape, so the shape + its derived flags are lets, not consts.
                let SHAPE = "{{portalShape}}"; // "circle" | "focus1" | "square" | "logo"
                let isBand = SHAPE.indexOf("focus") === 0; // full-width reading band vs circle spotlight
                let isSquare = SHAPE === "square";
                let isLogo = SHAPE === "logo";
                const ring = document.createElement("div");
                ring.id = "portal-cursor-ring";
                document.body.appendChild(ring);

                let portal = null, portalTa = null, editTimer = null, pendingText = "";
                let blurFocus = {{portalFocusBlur}}; // focus WITH blur: preview behind the portal blurs
                let dragging = false, dragStartX = 0, dragStartY = 0, dragScrollLeft = 0, dragScrollTop = 0; // middle-button source panning
                let clickFrac = 0; // vertical document fraction of the opening click (0..1) — positional caret fallback
                let clickFracX = 0; // horizontal document fraction of the opening click (0..1) — left/right source positioning
                let curApH = 200;  // height of the currently-open aperture (circle diameter or band height)
                let pendingClickInfo = null;

                const vh = window.innerHeight || 800;
                // Focus bands: skinny full-width strips for line-oriented reading. The slider fattens
                // the base band (~3 lines at 0) and the ×2/×3 presets multiply it — more line widths,
                // more content — clamped so a band never swallows the whole viewport.
                let bandMult = SHAPE === "focus2" ? 2 : SHAPE === "focus3" ? 3.2 : 1;
                // Size dial -> aperture dimensions, split out into applyReveal so the live slider
                // (__portalSetReveal) can recompute on the fly and grow/shrink an open portal in
                // real time instead of waiting for a full re-render.
                let apertureSize = 0, bandH = 0, clearStop = 0, fadeStop = 0;
                let curReveal = REVEAL; // tracks the dial so a live shape swap can rebuild sizes
                function applyReveal(reveal) {
                    curReveal = reveal;
                    apertureSize = Math.round(200 + (reveal / 100) * (vh * 0.9 - 200)); // circle diameter / shape size
                    bandH = Math.round(Math.min(vh * 0.85, (60 + (reveal / 100) * 160) * bandMult));
                    clearStop = Math.round(35 + reveal * 0.4);       // % radius kept fully clear
                    fadeStop = Math.min(clearStop + 28, 96);         // % radius hit fully transparent
                }
                applyReveal(REVEAL);

                // Mouse events report unzoomed viewport pixels, but the ring/aperture are absolutely
                // positioned inside the zoomed document (the app re-applies a CSS zoom on the root
                // after every navigation) — so raw clientX/Y drift off the cursor at any zoom ≠ 100%.
                // Map through the root's bounding rect + effective zoom to get true document coords.
                function docPoint(e) {
                    const r = document.documentElement.getBoundingClientRect();
                    const z = document.documentElement.currentCSSZoom
                        || parseFloat(document.documentElement.style.zoom || "1") || 1;
                    return { x: (e.clientX - r.left) / z, y: (e.clientY - r.top) / z };
                }

                document.addEventListener("mousemove", (e) => {
                    const p = docPoint(e);
                    ring.style.left = p.x + "px";
                    ring.style.top = p.y + "px";
                });

                function post(msg) { try { chrome.webview.postMessage(JSON.stringify(msg)); } catch (e) {} }

                // Light Markdown strip so rendered text can be matched back to a source line.
                function stripMd(s) {
                    return String(s).replace(/[#>*_`~\[\]()!|\\-]/g, " ").replace(/\s+/g, " ").trim().toLowerCase();
                }
                // Match the clicked block's text back to a source line. Returns -1 when there's
                // nothing to match — callers fall back to the click's document position instead of
                // silently landing at the top. When the same text appears more than once (repeated
                // headings, list items), the occurrence closest to the positional estimate wins.
                function caretLineFromText(source, estimate) {
                    const target = stripMd(pendingText).slice(0, 40);
                    if (target.length < 4) return -1;
                    const lines = source.split("\n");
                    let best = -1;
                    for (let i = 0; i < lines.length; i++) {
                        const sl = stripMd(lines[i]);
                        if (sl.length >= 4 && (sl.indexOf(target) >= 0 || target.indexOf(sl.slice(0, 40)) >= 0)) {
                            if (best < 0 || Math.abs(i - estimate) < Math.abs(best - estimate)) best = i;
                        }
                    }
                    return best;
                }

                function caretOffsetFromClickInfo(source, estimateLine) {
                    const lines = source.split("\n");
                    if (lines.length === 0) return { offset: 0, length: 0 };

                    const exactWord = (pendingClickInfo?.exactWord || "").trim().toLowerCase();
                    if (exactWord.length >= 2) {
                        let bestOffset = -1;
                        let bestDist = Infinity;
                        let curOff = 0;
                        for (let i = 0; i < lines.length; i++) {
                            const lineLower = lines[i].toLowerCase();
                            let matchIdx = lineLower.indexOf(exactWord);
                            while (matchIdx >= 0) {
                                const dist = Math.abs(i - estimateLine);
                                if (dist < bestDist) {
                                    bestDist = dist;
                                    bestOffset = curOff + matchIdx;
                                }
                                matchIdx = lineLower.indexOf(exactWord, matchIdx + 1);
                            }
                            curOff += lines[i].length + 1;
                        }
                        if (bestOffset >= 0) return { offset: bestOffset, length: exactWord.length };
                    }

                    let bestLine = caretLineFromText(source, estimateLine);
                    if (bestLine < 0) bestLine = estimateLine;
                    bestLine = Math.max(0, Math.min(lines.length - 1, bestLine));

                    let off = 0;
                    for (let i = 0; i < bestLine; i++) off += lines[i].length + 1;

                    const lineLen = lines[bestLine].length;
                    const colEst = Math.round(clickFracX * lineLen);
                    return { offset: off + Math.min(colEst, lineLen), length: 0 };
                }

                // Focus blur (Ctrl+Alt+X in the app): while a portal is open, the rendered preview
                // — the #canvas column, a SIBLING of the portal on <body> — blurs so the eye stays
                // on the revealed source; the aperture and cursor ring stay sharp. The inline style
                // survives in-place canvas swaps (only #canvas CONTENTS are replaced), and full
                // re-renders re-inline the persisted choice.
                function setPreviewBlur(on) {
                    const canvas = document.getElementById("canvas");
                    if (!canvas) return;
                    canvas.style.transition = "filter 0.22s ease";
                    canvas.style.filter = on ? "blur(6px)" : "";
                }
                window.__portalSetBlur = function (on) {
                    blurFocus = !!on;
                    if (portal) setPreviewBlur(blurFocus);
                };

                function closePortal(silent) {
                    if (!portal) return;
                    const p = portal;
                    portal = null; portalTa = null;
                    dragging = false;
                    p.classList.add("portal-closing");
                    setTimeout(() => p.remove(), 200);
                    // Silent closes are portal MOVES (openPortal re-opens immediately) — keep the
                    // blur up; a real close restores the sharp preview.
                    if (!silent) setPreviewBlur(false);
                    if (!silent) post({ type: "portal-closed" });
                }

                // The "page" is the #canvas column (fixed content width, centered in the preview) —
                // NOT the whole scrollable document. Focus bands must span exactly that width and
                // align to its left edge. getBoundingClientRect is zoomed, so divide the effective
                // CSS zoom back out to get document coordinates (the portal lives in the same zoomed
                // subtree, so these map 1:1 onto its style.left/width).
                function pageRect() {
                    const z = document.documentElement.currentCSSZoom
                        || parseFloat(document.documentElement.style.zoom || "1") || 1;
                    const canvas = document.getElementById("canvas");
                    if (!canvas) return { left: 8, width: Math.max(240, (window.innerWidth || 800) / z - 16) };
                    const r = canvas.getBoundingClientRect();
                    const root = document.documentElement.getBoundingClientRect();
                    return { left: (r.left - root.left) / z, width: r.width / z };
                }

                function openPortal(x, y, el, ev) {
                    closePortal(true);
                    let clickInfo = { exactWord: "", fullText: el ? (el.textContent || "") : "" };
                    if (ev && document.caretRangeFromPoint) {
                        try {
                            const range = document.caretRangeFromPoint(ev.clientX, ev.clientY);
                            if (range && range.startContainer) {
                                const txt = range.startContainer.textContent || "";
                                const off = range.startOffset || 0;
                                const wordBefore = (txt.slice(0, off).match(/[\w\-]+$/) || [""])[0];
                                const wordAfter = (txt.slice(off).match(/^[\w\-]+/) || [""])[0];
                                clickInfo.exactWord = wordBefore + wordAfter;
                            }
                        } catch (e) {}
                    }
                    pendingClickInfo = clickInfo;
                    pendingText = clickInfo.fullText;

                    portal = document.createElement("div");
                    portal.className = "portal-aperture" + (isBand ? " portal-band" : isSquare ? " portal-square" : isLogo ? " portal-logo" : "");
                    const docW = document.documentElement.scrollWidth;
                    const docH = document.documentElement.scrollHeight;
                    const pg = pageRect(); // the #canvas page column — bands match it, not the whole preview
                    // Bands span the page (#canvas) width and align to its left edge; circles stay
                    // square on the click point.
                    const apW = isBand ? Math.max(240, pg.width) : apertureSize;
                    const apH = isBand ? bandH : apertureSize;
                    curApH = apH;
                    portal.style.width = apW + "px";
                    portal.style.height = apH + "px";
                    // Where in the document (0=top, 1=bottom) the portal was opened: the caret
                    // fallback in __portalSetSource lands at the same fraction of the source.
                    clickFrac = docH > 0 ? Math.min(1, Math.max(0, y / docH)) : 0;
                    // Horizontal twin (0=left, 1=right): __portalSetSource pans the source to the
                    // same left/right fraction, so clicking the right of the preview reveals the
                    // right of the raw Markdown (and the left reveals the left).
                    clickFracX = docW > 0 ? Math.min(1, Math.max(0, x / docW)) : 0;
                    portal.style.left = isBand ? pg.left + "px"
                        : Math.min(Math.max(x - apW / 2, 8), Math.max(8, docW - apW - 8)) + "px";
                    portal.style.top = Math.min(Math.max(y - apH / 2, 8), Math.max(8, docH - apH - 8)) + "px";

                    const closeBtn = document.createElement("div");
                    closeBtn.className = "portal-close";
                    closeBtn.textContent = "\u00D7";
                    closeBtn.addEventListener("click", (ev) => { ev.stopPropagation(); closePortal(); });
                    portal.appendChild(closeBtn);

                    portalTa = document.createElement("textarea");
                    portalTa.className = "portal-source";
                    portalTa.spellcheck = false;
                    portalTa.wrap = "off";
                    // Circle keeps the radial fog-of-war; bands fade top/bottom only (the strip is
                    // meant to read clean edge-to-edge across the line).
                    const mask = isBand
                        ? "linear-gradient(to bottom, transparent 0%, rgba(0,0,0,1) 22%, rgba(0,0,0,1) 78%, transparent 100%)"
                        : (isSquare || isLogo) ? "none" : "radial-gradient(circle, rgba(0,0,0,1) " + clearStop + "%, rgba(0,0,0,0.55) " + ((clearStop + fadeStop) / 2) + "%, transparent " + fadeStop + "%)";
                    portalTa.style.maskImage = mask;
                    portalTa.style.webkitMaskImage = mask;
                    portal.appendChild(portalTa);

                    document.body.appendChild(portal);
                    if (blurFocus) setPreviewBlur(true);
                    playPortalWhirSound();
                    post({ type: "portal-open" });

                    portalTa.addEventListener("input", () => {
                        followCaret();
                        clearTimeout(editTimer);
                        editTimer = setTimeout(() => post({ type: "portal-edit", text: portalTa ? portalTa.value : "" }), 400);
                    });
                    // Arrow keys / Home / End / clicks move the caret without firing "input".
                    portalTa.addEventListener("keyup", followCaret);
                    portalTa.addEventListener("click", followCaret);
                    portalTa.addEventListener("keydown", (ev) => {
                        if (ev.key === "Escape") { ev.stopPropagation(); closePortal(); }
                    });
                }

                // The portal follows the | typing cursor: as the caret walks along (or down) a line,
                // the source pans under the fixed aperture exactly as if the user were middle-mouse
                // grab-dragging it, so what they type is always inside the clear centre of the shape.
                let charW = 0;
                function measureCharW() {
                    if (charW || !portalTa) return charW || 8;
                    try {
                        const c2d = document.createElement("canvas").getContext("2d");
                        const cs = getComputedStyle(portalTa);
                        c2d.font = cs.fontSize + " " + cs.fontFamily;
                        charW = c2d.measureText("M").width || 8; // monospace: every column is one M wide
                    } catch (e) { charW = 8; }
                    return charW;
                }
                function followCaret() {
                    if (!portalTa) return;
                    const pos = portalTa.selectionStart || 0;
                    const before = portalTa.value.slice(0, pos);
                    const nl = before.lastIndexOf("\n");
                    const line = nl < 0 ? 0 : (before.match(/\n/g) || []).length;
                    const col = pos - nl - 1;
                    const pad = 80, lineH = 20; // mirror .portal-source padding / line-height
                    const x = pad + col * measureCharW();
                    const y = pad + line * lineH;
                    // Comfort box: only pan when the caret drifts out of the middle of the aperture,
                    // so the text doesn't slide on every single keystroke.
                    const w = portalTa.clientWidth, h = portalTa.clientHeight;
                    const cx = x - portalTa.scrollLeft, cy = y - portalTa.scrollTop;
                    if (cx < w * 0.30) portalTa.scrollLeft = Math.max(0, x - w * 0.30);
                    else if (cx > w * 0.70) portalTa.scrollLeft = x - w * 0.70;
                    if (cy < h * 0.35) portalTa.scrollTop = Math.max(0, y - h * 0.35);
                    else if (cy > h * 0.65) portalTa.scrollTop = y - h * 0.65;
                }

                // The app calls this with the editor's current Markdown to fill the open portal and
                // land the caret on the exact line and word matching the clicked spot.
                window.__portalSetSource = function (source) {
                    if (!portalTa) return;
                    source = source || "";
                    portalTa.value = source;
                    const lines = source.split("\n");
                    const estimateLine = Math.round(clickFrac * Math.max(0, lines.length - 1));

                    const match = caretOffsetFromClickInfo(source, estimateLine);
                    const off = match.offset;
                    const len = match.length || 0;

                    portalTa.focus();
                    try { portalTa.setSelectionRange(off, off + len); } catch (e) {}

                    const before = source.slice(0, off);
                    const lastNl = before.lastIndexOf("\n");
                    const line = (before.match(/\n/g) || []).length;
                    const col = off - lastNl - 1;

                    const lineH = 20;
                    const cWidth = measureCharW();
                    const pad = 80;
                    const targetY = pad + line * lineH;
                    const targetX = pad + col * cWidth;
                    const curApW = portalTa.clientWidth || apertureSize;

                    portalTa.scrollTop = Math.max(0, targetY - curApH / 2 + lineH / 2);
                    portalTa.scrollLeft = Math.max(0, targetX - curApW / 2);
                };

                // Live size dial: the app calls this on every slider tick so the open aperture
                // grows/shrinks in real time — a cheap in-place resize, no re-navigation, so the
                // portal and its caret survive the drag. The circle stays centered on itself and
                // bands keep their vertical center; both are clamped to the document bounds. The
                // fog-of-war mask is rebuilt too, since its clear/fade stops track the dial.
                function resizeOpenPortal() {
                    if (!portal || !portalTa) return;
                    const docW = document.documentElement.scrollWidth;
                    const docH = document.documentElement.scrollHeight;
                    const pg = pageRect(); // bands stay locked to the #canvas page column
                    const apW = isBand ? Math.max(240, pg.width) : apertureSize;
                    const apH = isBand ? bandH : apertureSize;
                    const cx = (parseFloat(portal.style.left) || 0) + (parseFloat(portal.style.width) || apW) / 2;
                    const cy = (parseFloat(portal.style.top) || 0) + (parseFloat(portal.style.height) || apH) / 2;
                    portal.style.width = apW + "px";
                    portal.style.height = apH + "px";
                    portal.style.left = (isBand ? pg.left : Math.min(Math.max(cx - apW / 2, 8), Math.max(8, docW - apW - 8))) + "px";
                    portal.style.top = Math.min(Math.max(cy - apH / 2, 8), Math.max(8, docH - apH - 8)) + "px";
                    curApH = apH;
                    const mask = isBand
                        ? "linear-gradient(to bottom, transparent 0%, rgba(0,0,0,1) 22%, rgba(0,0,0,1) 78%, transparent 100%)"
                        : (isSquare || isLogo) ? "none" : "radial-gradient(circle, rgba(0,0,0,1) " + clearStop + "%, rgba(0,0,0,0.55) " + ((clearStop + fadeStop) / 2) + "%, transparent " + fadeStop + "%)";
                    portalTa.style.maskImage = mask;
                    portalTa.style.webkitMaskImage = mask;
                }
                window.__portalSetReveal = function (reveal) {
                    reveal = Math.max(0, Math.min(100, Number(reveal) || 0));
                    applyReveal(reveal);
                    resizeOpenPortal();
                };
                // Live shape switch: the app's shape picker pushes straight in so an open aperture
                // morphs in real time — same in-place path as the size dial (no re-navigation, the
                // portal and its caret survive). The derived flags + band multiplier are recomputed,
                // the aperture re-classed (band / square / logo each carry their own CSS), and the
                // size + fog-of-war mask rebuilt for the new shape at the current dial value.
                window.__portalSetShape = function (shape) {
                    if (["circle", "focus1", "focus2", "focus3", "square", "logo"].indexOf(shape) < 0) return;
                    SHAPE = shape;
                    isBand = SHAPE.indexOf("focus") === 0;
                    isSquare = SHAPE === "square";
                    isLogo = SHAPE === "logo";
                    bandMult = SHAPE === "focus2" ? 2 : SHAPE === "focus3" ? 3.2 : 1;
                    applyReveal(curReveal);
                    if (portal) {
                        portal.className = "portal-aperture" + (isBand ? " portal-band" : isSquare ? " portal-square" : isLogo ? " portal-logo" : "");
                    }
                    resizeOpenPortal();
                };

                // Split + portal: typing in the MAIN editor streams in here so the raw MD inside
                // the shape stays live. The portal's own caret owns the text while it is genuinely
                // being edited (its edits are already local), so only portals that don't hold real
                // focus accept a push — which also kills the echo when a portal edit round-trips
                // the app's debounce. document.activeElement alone is NOT enough: when OS focus
                // moves to the WinUI editor, WebView2 leaves activeElement stale on the textarea,
                // which used to block legitimate editor→portal sync (one-way-only bug). Requiring
                // document.hasFocus() means "the preview actually owns focus right now", so typing
                // in the editor (WebView unfocused) always lands, while typing through the shape
                // (WebView focused, textarea active) is still protected from stale pushes.
                window.__portalUpdateSource = function (source) {
                    if (!portalTa || (document.hasFocus() && document.activeElement === portalTa)) return;
                    source = source || "";
                    const old = portalTa.value;
                    if (old === source) return;
                    // Land the caret at the first divergence so followCaret pans the view to
                    // wherever the user is typing in the editor — text appears inside the shape.
                    let i = 0;
                    const n = Math.min(old.length, source.length);
                    while (i < n && old.charCodeAt(i) === source.charCodeAt(i)) i++;
                    const st = portalTa.scrollTop, sl = portalTa.scrollLeft;
                    portalTa.value = source;
                    portalTa.scrollTop = st;
                    portalTa.scrollLeft = sl;
                    try { portalTa.setSelectionRange(i, i); } catch (e) {}
                    followCaret();
                };

                // Formatting-toolbar routing: while a portal is open the app forwards the editor
                // toolbar's wrap/insert here so it lands at the portal caret. Mirrors the TextBox
                // behavior — wrap the selection, or insert and park the caret between the markers.
                // The synthetic input event reuses the typing path (followCaret + debounced
                // portal-edit post), so the app and preview sync exactly as if it was typed.
                window.__portalApplyEdit = function (prefix, suffix) {
                    if (!portalTa) return;
                    prefix = prefix || ""; suffix = suffix || "";
                    const s = portalTa.selectionStart || 0, e = portalTa.selectionEnd || 0;
                    const v = portalTa.value;
                    const sel = v.slice(s, e);
                    if (!sel) {
                        portalTa.value = v.slice(0, s) + prefix + suffix + v.slice(e);
                        const at = s + prefix.length;
                        try { portalTa.setSelectionRange(at, at); } catch (x) {}
                    } else {
                        let leadLen = 0;
                        while (leadLen < sel.length && (sel[leadLen] === '\r' || sel[leadLen] === '\n')) leadLen++;
                        let trailLen = 0;
                        while (trailLen < (sel.length - leadLen) && (sel[sel.length - 1 - trailLen] === '\r' || sel[sel.length - 1 - trailLen] === '\n')) trailLen++;
                        const lead = sel.slice(0, leadLen);
                        const core = sel.slice(leadLen, sel.length - trailLen);
                        const trail = sel.slice(sel.length - trailLen);

                        const isInline = !!suffix;
                        const coreHasFormat = isInline && core.length >= (prefix.length + suffix.length) && core.startsWith(prefix) && core.endsWith(suffix);
                        const preIdx = s + leadLen - prefix.length;
                        const folIdx = s + leadLen + core.length;
                        const surroundingHasFormat = isInline && !coreHasFormat && preIdx >= 0 && (folIdx + suffix.length) <= v.length && v.slice(preIdx, preIdx + prefix.length) === prefix && v.slice(folIdx, folIdx + suffix.length) === suffix;

                        if (coreHasFormat) {
                            const unformatted = core.slice(prefix.length, core.length - suffix.length);
                            portalTa.value = v.slice(0, s) + lead + unformatted + trail + v.slice(e);
                            const coreStart = s + leadLen;
                            try { portalTa.setSelectionRange(coreStart, coreStart + unformatted.length); } catch (x) {}
                        } else if (surroundingHasFormat) {
                            portalTa.value = v.slice(0, preIdx) + core + v.slice(folIdx + suffix.length);
                            try { portalTa.setSelectionRange(preIdx, preIdx + core.length); } catch (x) {}
                        } else {
                            let rep = "";
                            const pTrim = prefix.trim();
                            if (!suffix && (pTrim === '#' || pTrim === '##' || pTrim === '###' || pTrim === '####' || pTrim === '-' || pTrim === '1.' || pTrim === '- []' || pTrim === '>')) {
                                const lines = core.split('\n');
                                const formatted = lines.map(line => line.length > 0 ? (line.endsWith('\r') ? prefix + line.slice(0, -1) + '\r' : prefix + line) : line).join('\n');
                                rep = lead + formatted + trail;
                            } else {
                                rep = lead + prefix + core + suffix + trail;
                            }

                            portalTa.value = v.slice(0, s) + rep + v.slice(e);
                            const coreStart = s + leadLen + prefix.length;
                            try { portalTa.setSelectionRange(coreStart, coreStart + core.length); } catch (x) {}
                        }
                    }
                    portalTa.focus();
                    portalTa.dispatchEvent(new Event("input", { bubbles: true }));
                };

                // Middle-button (button 1) press-and-drag pans the Markdown source inside the open
                // aperture — the circle stays put while the text behind it scrolls, so you can read
                // up/down/left/right through the fixed window. Grab-and-pan like touch scrolling:
                // drag down to see earlier content, drag up to see later. preventDefault + capture
                // phase free the middle button from its default autoscroll and keep the caret still.
                document.addEventListener("mousedown", (e) => {
                    if (e.button !== 1 || !portalTa) return;
                    dragging = true;
                    dragStartX = e.clientX;
                    dragStartY = e.clientY;
                    dragScrollLeft = portalTa.scrollLeft;
                    dragScrollTop = portalTa.scrollTop;
                    e.preventDefault();
                    e.stopPropagation();
                }, true);
                document.addEventListener("mousemove", (e) => {
                    if (!dragging || !portalTa) return;
                    if (!(e.buttons & 4)) { dragging = false; return; } // released (even outside the window)
                    portalTa.scrollLeft = dragScrollLeft - (e.clientX - dragStartX);
                    portalTa.scrollTop = dragScrollTop - (e.clientY - dragStartY);
                    e.preventDefault();
                }, true);
                document.addEventListener("mouseup", (e) => { if (e.button === 1) dragging = false; }, true);

                // In portal mode the whole preview is a "click to reveal the source behind it"
                // surface: a click opens (or moves) a portal at that spot. Capture phase + default
                // suppression so we win over links/tabs/selection while the mode is on.
                document.addEventListener("click", (e) => {
                    if (portal && portal.contains(e.target)) return; // clicks inside the portal pass through
                    // Only take text from a real content block. Falling back to e.target here used to
                    // hand caretLineFromText the whole page's text (body/#canvas), which "matched" the
                    // first lines of the source and threw the caret to the top of the document.
                    const el = (e.target && e.target.closest)
                        ? e.target.closest("h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,table,code")
                        : null;
                    const p = docPoint(e);
                    openPortal(p.x, p.y, el, e);
                    e.preventDefault();
                    e.stopPropagation();
                }, true);
            })();
            </script>
            """ : "";

        // Fit-to-width (LIVE preview only): the page is laid out at a fixed content width (A4
        // fidelity), but the live preview should USE the whole pane — when the left drawer
        // auto-closes (or the window widens) the page zooms to fill the space, exactly like
        // Word's zoom-to-fit. A transform keeps the page layout intact; the print/export CSS
        // overrides the scale, so DOCX/PDF output is unaffected.
        var fitWidthScript = interactive ? """
<script>
(function () {
    // marksmith-fit-width
    var canvas = document.getElementById('canvas');
    if (!canvas) return;
    var PAD = 24; // breathing room on each side
    var scale = 0;
    var fit = function () {
        var natural = canvas.offsetWidth; // fixed content width (px, box-sizing: border-box)
        if (!natural) return;
        var avail = window.innerWidth;
        var next = Math.min(Math.max((avail - PAD) / natural, 0.5), 2.0);
        if (Math.abs(next - scale) >= 0.01) { // no-op guard (also breaks observer loops)
            scale = next;
            canvas.style.transformOrigin = 'top center';
            canvas.style.transform = 'scale(' + scale + ')';
        }
        // The height always tracks the (possibly grown) content so the page stays fully
        // scrollable even when the scale itself did not change (late mermaid/images).
        var rect = canvas.getBoundingClientRect();
        document.body.style.height = (rect.top + rect.height + PAD) + 'px';
    };
    var timer = 0;
    var schedule = function () { clearTimeout(timer); timer = setTimeout(fit, 60); };
    window.addEventListener('resize', schedule);
    window.addEventListener('load', schedule);
    if (document.readyState !== 'loading') schedule();
    else document.addEventListener('DOMContentLoaded', schedule);
    // Late-rendered content (mermaid SVGs, images) changes the canvas size — re-fit then too.
    new MutationObserver(schedule).observe(canvas, { childList: true, subtree: true });
})();
</script>
""" : "";

        return $$"""
            <!DOCTYPE html><html{{htmlAttrs}}><head><meta charset="UTF-8">
            <script>
            // Deterministic export-readiness contract (see IWebRenderHost.WaitForExportReadyAsync):
            // Mermaid completion via MutationObserver on data-processed, async image decodes via
            // img.decode()/load/error, and layout settle via double requestAnimationFrame. No sleeps.
            window.marksmithWaitForExportReady = function(checkMermaid) {
                return new Promise((resolve) => {
                    const done = () => resolve(true);
                    let mDone = !checkMermaid, iDone = false;
                    const tryDone = () => { if (mDone && iDone) requestAnimationFrame(() => requestAnimationFrame(done)); };

                    if (checkMermaid) {
                        const check = () => {
                            const nodes = document.querySelectorAll('.mermaid');
                            if (!nodes.length || Array.from(nodes).every(n => n.hasAttribute('data-processed'))) {
                                mDone = true;
                                tryDone();
                                return true;
                            }
                            return false;
                        };
                        });
                        setTimeout(() => { observer.disconnect(); callback(); }, 5000);
                    };

                    const waitForImages = (callback) => {
                        const imgs = Array.from(document.images);
                        if (imgs.length == 0) { callback(); return; }
                        let remaining = imgs.length;
                        const onImgDone = () => { remaining--; if (remaining <= 0) callback(); };
                        imgs.forEach(img => {
                            if (img.complete && img.naturalHeight > 0) { onImgDone(); }
                            else if (typeof img.decode == 'function') { img.decode().then(onImgDone).catch(onImgDone); }
                            else { img.addEventListener('load', onImgDone); img.addEventListener('error', onImgDone); }
                        });
                    };

                    const settleLayout = () => {
                        requestAnimationFrame(() => { requestAnimationFrame(() => { resolve("true"); }); });
                    };

                    waitForMermaidIfNeeded(() => { waitForImages(() => { settleLayout(); }); });
                });
            };
            </script>
            {{mermaidScript}}
            {{lensScript}}
            {{extraHead}}
            <style>
            {{fontFaceCss}}
            body { margin: 0; padding: 0; background: {{workspaceBg}}; color: {{effectiveText}}; 
                   font-family: {{bodyFontFamily}}; font-size: 16px; line-height: 1.6; word-wrap: break-word; overflow-x: auto;
                   -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            #canvas { padding: 60px 40px; width: {{(settings.TargetFormat == "docx" ? 794 : settings.ContentWidth)}}px; min-width: {{(settings.TargetFormat == "docx" ? 794 : settings.ContentWidth)}}px; max-width: none; margin: {{(interactive ? "40px auto" : "0 auto")}}; box-sizing: border-box; transition: filter .3s ease, opacity .3s ease; {{(interactive ? $"min-height: 1123px; background: {pageBg}; box-shadow: 0 4px 16px rgba(0,0,0,0.25); border: 1px solid {theme.Border}; border-radius: 4px;" : "")}} }
            @media print {
              @page { margin: 0 !important; }
              html, body { margin: 0 !important; padding: 0 !important; background: {{effectiveBodyBg}} !important; height: auto !important; -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; }
              #canvas { background: {{effectiveBodyBg}} !important; box-shadow: none !important; border: none !important; width: 100% !important; max-width: 100% !important; min-width: 0 !important; margin: 0 !important; padding: 48px 54px !important; transform: none !important; }
            }
            body.ms-loading #canvas { filter: blur(14px); opacity: .6; }
            h1, h2 { color: {{theme.Heading}}; border-bottom: 2px solid {{theme.Border}}; padding-bottom: 8px; }
            /* Hard rule: explicit colored font (inline HTML / syntax highlighting) cannot be overridden by theming */
            font[color] { color: revert; }
            pre { background: {{theme.Code}}; padding: 16px; border-radius: 6px; overflow-x: auto; border: 1px solid {{theme.Border}}; white-space: pre; word-wrap: normal; font-family: "Cascadia Code", "Cascadia Mono", "Fira Code", Consolas, "Courier New", monospace; font-variant-ligatures: none; }
            code { font-family: "Cascadia Code", "Cascadia Mono", "Fira Code", Consolas, "Courier New", monospace; font-variant-ligatures: none; }
            /* ASCII / box-drawing diagrams (ISS-006): ligatures and loose leading break column
               alignment, so code blocks flagged as ascii/text get tight, uniform metrics. */
            pre code.language-ascii, pre code.language-text, pre code.language-txt, pre.ascii-diagram { line-height: 1.15 !important; letter-spacing: 0px !important; font-size: 14px; display: block; overflow-x: auto; }
            table { border-collapse: collapse; width: 100%; margin: 16px 0; border: 2px solid {{theme.Border}}; word-break: break-word; overflow-wrap: anywhere; }
            th, td { border: 1px solid {{theme.Border}}; padding: 8px 12px; text-align: left; overflow-wrap: anywhere; word-break: break-word; }
            th { background: {{theme.Code}}; font-weight: bold; }
            .markdown-alert { border-radius: 6px; padding: 10px 16px; margin-bottom: 16px; }
            .markdown-alert-title { font-weight: bold; margin: 0 0 4px 0; }
            {{alertCss}}
            {{calloutCss}}
            /* Screen (the live preview): diagrams render at natural size when they fit, and shrink
               to fit the column when they don't (max-width:100% + height:auto keeps the aspect ratio),
               so a wide diagram is never clipped at the frame's edge nor hidden behind a subtle
               horizontal scrollbar. Clicking still opens the full-screen pan/zoom viewer, which shows
               the diagram at its natural size. Print (the PDF export): fit-to-page-width. */
            .mermaid { width: 100%; max-width: 100%; margin: 32px 0; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; cursor: zoom-in; }
            .mermaid svg { max-width: 100%; height: auto; }
            /* Plugin diagrams get the same themed frame as .mermaid so they belong to the page.
               Theme-aware engines (see IsThemeAware) render on-theme and are shown as-is; engines
               that don't theme their own output get .plugin-diagram-autoinvert, whose line art is
               inverted on dark themes for legibility (pluginDiagramSvgFilter). */
            .plugin-diagram { width: 100%; max-width: 100%; margin: 32px 0; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; text-align: center; cursor: zoom-in; }
            .plugin-diagram svg { max-width: 100%; height: auto; }
            .plugin-diagram-autoinvert svg { {{pluginDiagramSvgFilter}} }
            /* SmartArt diagrams (:::smartart blocks) get the same themed frame as mermaid; on dark
               themes the renderer's light-page artwork is auto-inverted for legibility. */
            .smartart { width: 100%; max-width: 100%; margin: 32px 0; background: {{theme.Code}}; border-radius: 8px; padding: 20px; border: 2px solid {{theme.Border}}; box-sizing: border-box; overflow-x: auto; text-align: center; }
            .smartart .smartart-container { margin: 0 auto; }
            .smartart-autoinvert .smartart-container { {{pluginDiagramSvgFilter}} }
            .smartart-error { color: #cf222e; font-size: 13px; text-align: center; padding: 12px; }
            .plugin-diagram-error, .plugin-diagram-missing { margin: 10px 0 24px; padding: 10px 14px; border-radius: 6px; background: {{theme.Secondary}}; border: 1px solid {{theme.Border}}; color: {{theme.Text}}; font-size: 13px; }
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
            .kanban-board { margin: 24px 0; padding: 16px; border: 1px solid {{theme.Border}}; border-radius: 8px; background: {{theme.Secondary}}; }
            .kanban-board-title { font-weight: 700; font-size: 1.1em; color: {{theme.Heading}}; margin-bottom: 12px; }
            .kanban-columns { display: flex; gap: 16px; overflow-x: auto; padding-bottom: 8px; }
            .kanban-column { flex: 1; min-width: 180px; background: {{theme.Background}}; border: 1px solid {{theme.Border}}; border-radius: 6px; padding: 12px; display: flex; flex-direction: column; gap: 8px; }
            .kanban-column-title { font-weight: 700; font-size: 0.95em; color: {{theme.Heading}}; border-bottom: 2px solid {{theme.Primary}}; padding-bottom: 6px; margin-bottom: 4px; }
            .kanban-cards { display: flex; flex-direction: column; gap: 8px; }
            .kanban-card { background: {{theme.Code}}; border: 1px solid {{theme.Border}}; border-radius: 4px; padding: 8px 12px; font-size: 0.9em; }
            .kanban-card.completed { text-decoration: line-through; opacity: 0.75; }
            .kanban-tag { display: inline-block; padding: 1px 6px; border-radius: 4px; background: {{theme.Secondary}}; border: 1px solid {{theme.Border}}; font-size: 0.8em; margin-left: 4px; color: {{theme.Primary}}; }
            .page-break { border: none; border-top: 2px dashed {{theme.Border}}; margin: 26px 0; position: relative; }
            .page-break::after { content: "page break"; position: absolute; top: -9px; left: 50%; transform: translateX(-50%); padding: 0 10px; background: {{theme.Background}}; font-size: 10px; letter-spacing: 0.08em; text-transform: uppercase; color: {{theme.Text}}; opacity: 0.55; }
            @media print {
              .page-break { {{(settings.UnlimitedHeight ? "page-break-after: avoid !important; break-after: avoid !important; " : "page-break-after: always; ")}}border: none; }
              .page-break::after { content: ""; }
              {{(settings.UnlimitedHeight ? "hr { page-break-after: avoid !important; break-after: avoid !important; }" : "")}}
            }
            img { max-width: 100%; }
            .footnotes { margin-top: 30px; padding-top: 12px; border-top: 1px solid {{theme.Border}}; font-size: 0.9em; }
            /* --- Capability-Aware Preview CSS --- */
            /* If the output format is DOCX and Mermaid ShapeForge mode is off, non-supported items could be dimmed here, but for now we rely on the backend. */
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
            /* Scroll-spy (live preview only): the entry for the heading currently in view is bolded
               and accented so a long document's place is always visible in the mini-TOC. */
            #toc a.active { color: {{theme.Heading}}; font-weight: 700; }
            sup.cite { font-size: 0.72em; color: {{theme.Heading}}; }
            
            /* Custom styles for diagram errors and page overflow warnings */
            .mermaid-error-card { background: #fff5f5; color: #c53030; border: 1px solid #feb2b2; padding: 16px; border-radius: 6px; font-family: sans-serif; margin: 10px 0; text-align: left; }
            .mermaid-error-card pre { background: #fff0f0; border: none; padding: 8px; margin: 8px 0 0 0; color: #9b2c2c; font-family: monospace; font-size: 13px; overflow-x: auto; }
            body.ms-dark .mermaid-error-card { background: #2d1a1a; color: #f5c2c2; border-color: #e53e3e; }
            body.ms-dark .mermaid-error-card pre { background: #3d1f1f; color: #feb2b2; }
            
            /* Multi-Page Preview Dividers showing backdrop between pages */
            #canvas { position: relative; }
            .page-break-gap {
                position: absolute;
                left: -40px;
                right: -40px;
                height: 24px;
                margin-top: -12px;
                background: {{workspaceBg}};
                border-top: 1px dashed {{theme.Border}};
                border-bottom: 1px dashed {{theme.Border}};
                display: flex;
                align-items: center;
                justify-content: center;
                user-select: none;
                pointer-events: none;
                z-index: 100;
            }
            .page-break-gap::after {
                content: attr(data-page);
                background: {{theme.Secondary}};
                color: {{theme.Text}};
                border: 1px solid {{theme.Border}};
                border-radius: 999px;
                padding: 2px 14px;
                font-size: 11px;
                font-weight: 700;
                letter-spacing: 0.05em;
                text-transform: uppercase;
                opacity: 0.9;
                box-shadow: 0 2px 6px rgba(0,0,0,0.2);
            }
            
            #overflow-banner { position: fixed; bottom: 20px; right: 20px; z-index: 1000; background: rgba(254, 243, 199, 0.95); border: 1px solid #f59e0b; color: #78350f; padding: 12px 18px; border-radius: 8px; font-size: 13px; box-shadow: 0 4px 12px rgba(0,0,0,0.15); backdrop-filter: blur(4px); font-family: sans-serif; animation: slideIn 0.3s ease-out; }
            @keyframes slideIn { from { transform: translateY(20px); opacity: 0; } to { transform: translateY(0); opacity: 1; } }
            body.ms-dark #overflow-banner { background: rgba(45, 34, 18, 0.95); border-color: #d97706; color: #fef3c7; }
            /* Issue-locator radar beacon (ISS-012): a pulsing red homing ring dropped onto the
               element for the lint issue the user clicked in the sidebar. */
            .radar-beacon-container { position: absolute; width: 60px; height: 60px; pointer-events: none; transform: translate(-50%, -50%); z-index: 999; }
            .radar-beacon-ring { position: absolute; inset: 0; border-radius: 50%; border: 2px solid #ef4444; background: rgba(239, 68, 68, 0.15); animation: radarPulse 1.8s cubic-bezier(0.215, 0.61, 0.355, 1) infinite; }
            .radar-beacon-ring:nth-child(2) { animation-delay: 0.4s; }
            @keyframes radarPulse { 0% { transform: scale(0.1); opacity: 1; } 80% { opacity: 0.8; } 100% { transform: scale(2.5); opacity: 0; } }
            .issue-target-highlight { background: rgba(239, 68, 68, 0.25) !important; outline: 2px dashed #ef4444 !important; transition: background 0.5s ease; }
            /* Interactive tabbed content (ISS-015): MS Word Ribbon-style tab strip for live preview & print */
            .md-tab-group { margin: 20px 0; border: 1px solid {{theme.Border}}; border-radius: 8px; background: {{theme.Background}}; overflow: hidden; box-shadow: 0 2px 8px rgba(0, 0, 0, 0.12); }
            .md-tab-nav { display: flex; background: {{theme.Secondary}}; border-bottom: 1px solid {{theme.Border}}; padding: 6px 8px 0 8px; gap: 4px; overflow-x: auto; scrollbar-width: none; }
            .md-tab-nav::-webkit-scrollbar { display: none; }
            .md-tab-link { padding: 9px 18px; border: 1px solid transparent; border-bottom: none; border-radius: 6px 6px 0 0; background: transparent; color: {{theme.Text}}; opacity: 0.75; font-family: "Segoe UI", Calibri, sans-serif; font-weight: 600; font-size: 13.5px; cursor: pointer; transition: all 0.15s ease-in-out; outline: none; user-select: none; }
            .md-tab-link:hover { opacity: 1.0; background: {{theme.Background}}; color: {{theme.Heading}}; }
            .md-tab-link.active { opacity: 1.0; background: {{theme.Background}}; color: {{theme.Heading}}; border-color: {{theme.Border}}; border-top: 3px solid {{theme.Heading}}; border-bottom: 1px solid {{theme.Background}}; font-weight: 700; margin-bottom: -1px; }
            .md-tab-content { display: none; padding: 18px; background: {{theme.Background}}; color: {{theme.Text}}; animation: mdTabFade 0.15s ease-in-out; }
            .md-tab-content.active { display: block; }
            @keyframes mdTabFade { from { opacity: 0; transform: translateY(2px); } to { opacity: 1; transform: translateY(0); } }
            @media print { .md-tab-content { display: block !important; border-top: 1px dashed {{theme.Border}}; page-break-inside: avoid; } }
            /* Looking Glass portal mode (ISS-004): the preview is the default surface; clicking
               opens a circular aperture revealing the editable Markdown source behind it through a
               fog-of-war blur (clear at the caret, blurring back to the preview). The ring/aperture
               elements are only created by the portal script when the mode is on, so this CSS is
               inert otherwise. */
            #portal-cursor-ring { position: absolute; width: 44px; height: 44px; border-radius: 50%; border: 2px solid rgba(88, 166, 255, 0.65); box-shadow: 0 0 16px rgba(88, 166, 255, 0.35), inset 0 0 10px rgba(88, 166, 255, 0.15); pointer-events: none; transform: translate(-50%, -50%); animation: portalPulse 2s infinite ease-in-out; z-index: 60; }
            @keyframes portalPulse { 0%, 100% { transform: translate(-50%, -50%) scale(1); opacity: 0.75; } 50% { transform: translate(-50%, -50%) scale(1.12); opacity: 1; } }
            .portal-aperture { position: absolute; border-radius: 50%; overflow: hidden; z-index: 55; background: rgba(13, 17, 23, 0.42); backdrop-filter: blur(5px); -webkit-backdrop-filter: blur(5px); border: 2px solid rgba(88, 166, 255, 0.55); box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.4), 0 8px 32px rgba(0, 0, 0, 0.5), inset 0 0 24px rgba(88, 166, 255, 0.12); animation: portalIris 0.22s ease-out; }
            @keyframes portalIris { from { transform: scale(0.25); opacity: 0; } to { transform: scale(1); opacity: 1; } }
            /* Full-width "focus" reading bands: rounded strip instead of a circle, iris opens
               vertically like a letterbox, and the close button moves to the right edge where a
               skinny band actually has room for it. */
            .portal-aperture.portal-band { border-radius: 14px; animation: portalIrisBand 0.22s ease-out; }
            @keyframes portalIrisBand { from { transform: scaleY(0.15); opacity: 0; } to { transform: scaleY(1); opacity: 1; } }
            .portal-band .portal-close { left: auto; right: 12px; top: 50%; transform: translateY(-50%); }
            .portal-aperture.portal-closing { animation: portalClose 0.2s ease-in forwards; }
            @keyframes portalClose { from { transform: scale(1); opacity: 1; } to { transform: scale(0.25); opacity: 0; } }
            /* 80px padding doubles as pan overscroll: native scrolling clamps at the content box, so
               the generous inset lets middle-drag carry corner text ~80px past its natural bound into
               the clear centre of the shape (the masked rim was otherwise unreadable). */
            .portal-source { position: absolute; inset: 0; width: 100%; height: 100%; box-sizing: border-box; border: none; outline: none; resize: none; background: transparent; color: #dbe6f2; font-family: "Cascadia Mono", Consolas, monospace; font-size: 13px; line-height: 20px; padding: 80px; white-space: pre; overflow: auto; caret-color: #58a6ff; }
            .portal-close { position: absolute; top: 8px; left: 50%; transform: translateX(-50%); width: 26px; height: 26px; line-height: 22px; text-align: center; border-radius: 50%; background: rgba(88, 166, 255, 0.18); border: 1px solid rgba(88, 166, 255, 0.5); color: #9ecbff; font-size: 16px; cursor: pointer; z-index: 2; user-select: none; }
            </style></head><body class="{{bodyClass}}"><div id="canvas"><!--ms-canvas-start-->{{attribution}}{{toc}}{{body}}{{footer}}<!--ms-canvas-end--></div>{{overflowScript}}{{scrollSpyScript}}{{radarScript}}{{tabScript}}{{portalScript}}{{fitWidthScript}}</body></html>
            """;
    }

    /// <summary>
    /// Renders only the inner canvas content (attribution + TOC + body + footer) without the
    /// surrounding HTML shell, CSS, or page scripts. Used by the live preview path which swaps
    /// #canvas innerHTML in place — avoids materializing the ~50 KB static JS/CSS shell just to
    /// discard it. Returns null when the document triggers the focused-diagram early return
    /// (caller should fall back to a full navigation).
    /// </summary>
    public string? RenderCanvasOnly(string markdown, AppSettings settings, ThemeDefinition theme,
        LlmClassification? classification = null)
    {
        if (settings.ThemeLightInfluence)
            theme = theme.ApplyLightInfluence();

        markdown = NormalizeForRender(markdown, settings);
        var body = Markdown.ToHtml(markdown, settings.NoEmoji ? PipelineNoEmoji : Pipeline);

        if (settings.NoEmoji) body = EmojiStripper.Strip(body);
        body = HtmlSanitizer.Apply(body);
        body = EmbedLocalImages(body);

        body = MermaidFenceHtmlRe().Replace(body,
            m => $"<div class=\"mermaid\">{m.Groups[1].Value}</div>");

        if (settings.MermaidEnabled)
        {
            body = AnyCodeBlockRe().Replace(body,
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
                        foreach (Match lit in QuotedLiteralRe().Matches(codeContent))
                        {
                            var group = lit.Groups[1].Success ? lit.Groups[1] : lit.Groups[2].Success ? lit.Groups[2] : lit.Groups[3];
                            var candidate = group.Value.Trim();
                            if (MermaidDiagramStart.IsMatch(candidate))
                                extras.Append($"<div class=\"mermaid mermaid-embedded\">{System.Net.WebUtility.HtmlEncode(candidate)}</div>");
                        }
                    }
                    return extras.Length > 0 ? m.Value + extras : m.Value;
                });
        }

        var pluginTheme = Plugins.PluginTheme.From(theme);
        body = PluginLangCodeRe().Replace(body,
            m =>
            {
                var language = m.Groups[1].Value;
                var installed = AppServices.Plugins.FindDiagramRenderer(language);
                if (installed != null)
                {
                    var decoded = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value);
                    var svg = AppServices.Plugins.RenderToSvgCached(installed, decoded, pluginTheme);
                    if (svg != null)
                    {
                        svg = Plugins.SvgSanitizer.Sanitize(svg, theme.Background);
                        var cls = installed.IsThemeAware ? "plugin-diagram" : "plugin-diagram plugin-diagram-autoinvert";
                        return $"<div class=\"{cls}\">{svg}</div>";
                    }
                    return m.Value + "<div class=\"plugin-diagram-error\">⚠ " + System.Net.WebUtility.HtmlEncode(installed.Name) + " couldn't render this diagram — check the syntax.</div>";
                }

                var known = AppServices.Plugins.FindAnyDiagramPlugin(language);
                if (known != null)
                    return m.Value + $"<div class=\"plugin-diagram-missing\">🧩 Install the <b>{System.Net.WebUtility.HtmlEncode(known.Name)}</b> plugin (Settings → Plugins) to render this diagram.</div>";

                return m.Value;
            });

        // Focused-diagram documents get a dedicated full-page viewer — signal the caller to
        // fall back to a full navigation (which builds the complete page with zoom/pan controls).
        if (settings.MermaidEnabled)
        {
            var focus = AnalyzeDiagramFocus(markdown);
            if (focus.Focused)
            {
                var mm = MermaidDivRe().Match(body);
                if (mm.Success) return null;
            }
        }

        var attribution = BuildAttribution(settings, classification, theme);
        var toc = settings.IncludeToc ? BuildToc(body, theme) : "";
        var footer = AppServices.License.ShowFooter
            ? "<div class=\"mark-footer\">Made with <a href=\"https://github.com/thebubbsy/marksmith\">Marksmith</a> — turn AI chats into polished documents</div>"
            : "";

        return attribution + toc + body + footer;
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
                line.StartsWith("* ") || line.StartsWith("> ") || OrderedListLineRe().IsMatch(line))
                return (false, "", "");
            if (subtitle.Length == 0) subtitle = line;
            proseLen += line.Length;
        }
        if (title.Length == 0 || proseLen > 320) return (false, "", "");
        return (true, StripInline(title), StripInline(subtitle));
    }

    private static string StripInline(string s) =>
        System.Net.WebUtility.HtmlEncode(InlineMarkRe().Replace(s, "").Trim());

    // Builds the ` lang="…" dir="…"` suffix for the <html> tag from source-page metadata. Both
    // values are page-derived, so they're strictly validated rather than interpolated raw: lang
    // must look like a BCP-47 tag (letters/digits/hyphen), dir must be exactly ltr/rtl/auto —
    // anything else is dropped, so the page can't smuggle extra attributes into the tag.
    private static string BuildHtmlRootAttrs(string? language, string? direction)
    {
        var attrs = "";
        var lang = (language ?? "").Trim();
        if (lang.Length is > 0 and <= 35 && LangTagRe().IsMatch(lang))
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
            /* Long-press (800ms hold) on the diagram opens it in the Diagram Studio — the same
               gesture as the regular preview. A radial progress ring tracks the hold so the user
               gets immediate feedback that the gesture is registering (and can tell it apart from
               the pan/drag interaction, which cancels the hold as soon as the pointer moves). */
            #dv-holdring { position: fixed; width: 44px; height: 44px; margin: -22px 0 0 -22px;
                           pointer-events: none; z-index: 20; display: none;
                           filter: drop-shadow(0 2px 6px rgba(0,0,0,0.45)); }
            #dv-holdring .track { fill: none; stroke: rgba(255,255,255,0.18); stroke-width: 3.5; }
            #dv-holdring .bar { fill: none; stroke: #4cc9f0; stroke-width: 3.5; stroke-linecap: round;
                                stroke-dasharray: 113; stroke-dashoffset: 113;
                                transform: rotate(-90deg); transform-origin: 50% 50%; }
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
              <svg id="dv-holdring" viewBox="0 0 40 40"><circle class="track" cx="20" cy="20" r="18"/><circle class="bar" cx="20" cy="20" r="18"/></svg>
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

              // Capture the diagram source NOW — this inline script runs during parsing, before
              // mermaid's startOnLoad render replaces the .mermaid div's text with the SVG. The
              // long-press gesture below sends this source back to the host to open the Studio.
              const diagramSource = (document.querySelector(".mermaid") || { textContent: "" }).textContent || "";

              // Export / interop helper — the host (C#) listens for these messages.
              const post = (m) => { try { window.chrome.webview.postMessage(JSON.stringify(m)); } catch (e) {} };

              // ---- Long-press (800ms stationary hold) -> open in Diagram Studio --------------
              // Integrated with the pan handler below: a drag past 8px cancels the hold (that's
              // a pan), a stationary hold past 800ms opens the Studio. The progress ring gives
              // live feedback so the gesture is discoverable and distinguishable from panning.
              const ring = document.getElementById("dv-holdring");
              const ringBar = ring.querySelector(".bar");
              const HOLD_MS = 800, HOLD_MOVE = 8, RING_CIRC = 113;
              let holdActive = false, holdRaf = 0, holdStart = 0, holdX = 0, holdY = 0;
              function startHold(x, y) {
                holdActive = true; holdStart = performance.now(); holdX = x; holdY = y;
                ring.style.left = x + "px"; ring.style.top = y + "px"; ring.style.display = "block";
                ringBar.style.strokeDashoffset = RING_CIRC;
                (function frame() {
                  if (!holdActive) return;
                  const p = Math.min(1, (performance.now() - holdStart) / HOLD_MS);
                  ringBar.style.strokeDashoffset = RING_CIRC * (1 - p);
                  if (p >= 1) { completeHold(); return; }
                  holdRaf = requestAnimationFrame(frame);
                })();
              }
              function cancelHold() {
                if (!holdActive) return;
                holdActive = false; cancelAnimationFrame(holdRaf);
                ring.style.display = "none";
              }
              function completeHold() {
                holdActive = false; cancelAnimationFrame(holdRaf);
                ring.style.display = "none";
                // Clear the pan state so the view doesn't stick in drag mode once the Studio opens.
                drag = null; stage.classList.remove("dragging");
                post({ type: "launch-mermaid-studio", index: 0, code: diagramSource, gesture: "long-press-800ms" });
              }

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
                stage.classList.add("dragging"); stage.setPointerCapture(e.pointerId);
                if (e.button === 0) startHold(e.clientX, e.clientY); });
              stage.addEventListener("pointermove", (e) => { if (drag) { tx = e.clientX - drag.x; ty = e.clientY - drag.y; apply(); }
                if (holdActive && Math.hypot(e.clientX - holdX, e.clientY - holdY) > HOLD_MOVE) cancelHold(); });
              stage.addEventListener("pointerup", () => { drag = null; stage.classList.remove("dragging"); cancelHold(); });
              stage.addEventListener("pointercancel", () => { drag = null; stage.classList.remove("dragging"); cancelHold(); });
              document.getElementById("dv-in").addEventListener("click", () => { sc = Math.min(12, sc * 1.25); apply(); });
              document.getElementById("dv-out").addEventListener("click", () => { sc = Math.max(0.1, sc / 1.25); apply(); });
              document.getElementById("dv-reset").addEventListener("click", () => { fit(true); });
              window.addEventListener("resize", () => { if (fitted) fit(true); });

              // Export the diagram as a file — the host (C#) shows a save dialog and writes it.
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

    // Cache of already-inlined local images. Without it, every preview render re-read each image
    // from disk, re-decoded it (Skia) and re-ran Convert.ToBase64String — a 1 MB screenshot cost a
    // full disk read + decode + ~1.3 MB of base64 string on every debounced keystroke pause. The
    // key folds in last-write-time + length, so an edited file misses naturally and is re-inlined.
    // Bounded so a long session can't accumulate unbounded base64 strings.
    private sealed record ImageCacheEntry(long ByteLength, string DataUri);
    private static readonly Dictionary<string, ImageCacheEntry> ImageCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ImageCacheLock = new();
    private const int MaxImageCacheEntries = 64;

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
                        // The resulting data URI is cached so a re-render reuses it instead of
                        // re-reading/re-encoding the file (see ImageCache above).
                        var cacheKey = $"{localPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
                        ImageCacheEntry? entry;
                        lock (ImageCacheLock) ImageCache.TryGetValue(cacheKey, out entry);
                        if (entry is null)
                        {
                            var (data, mime) = PrepareImageForInline(localPath, info);
                            if (data is not null && mime is not null)
                            {
                                entry = new ImageCacheEntry(data.Length, $"data:{mime};base64,{Convert.ToBase64String(data)}");
                                lock (ImageCacheLock)
                                {
                                    if (ImageCache.Count >= MaxImageCacheEntries) ImageCache.Clear();
                                    ImageCache[cacheKey] = entry;
                                }
                            }
                        }
                        if (entry is not null &&
                            budgetUsed + entry.ByteLength * 4 / 3 <= MaxInlineBudgetBytes)
                        {
                            src = entry.DataUri;
                            budgetUsed += entry.ByteLength * 4 / 3;
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
        SKBitmap? scaled = scale < 1.0
            ? bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SKFilterQuality.High)
            : bitmap;
        if (scaled is null) return (null, null);
        try
        {
            using var image = SKImage.FromBitmap(scaled);
            if (image is null) return (null, null);
            // PNG keeps sharp edges + transparency for graphics/logos; the size win comes from the
            // resolution drop, not lossy compression, so text/line art stays crisp.
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            if (encoded is null) return (null, null);
            return (encoded.ToArray(), "image/png");
        }
        finally
        {
            if (!ReferenceEquals(scaled, bitmap)) scaled.Dispose();
        }
    }

    // KaTeX for math and highlight.js for code fences, pulled from the bundled offline assets only
    // when the rendered body actually needs them (plain documents stay dependency-free).
    private static string BuildExtraHead(string body, ThemeDefinition theme)
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
                        onload="window.__msRenderMath = function (root) { renderMathInElement(root, {
                            delimiters: [{left:'$$',right:'$$',display:true},{left:'$',right:'$',display:false},{left:'\\(',right:'\\)',display:false},{left:'\\[',right:'\\]',display:true}],
                            // \tag is display-mode-only in KaTeX and hard-errors in inline math,
                            // which used to leave the WHOLE span as raw text. Override it to render
                            // as an inline '(label)' — matching how the DOCX exporter emits it.
                            macros: {'\\tag': '\\;\\;(\\text{#1})'},
                            // Any other unsupported command degrades to red text instead of
                            // aborting the span and dumping raw source at the reader.
                            throwOnError: false
                        }); };
                        // Named + window-scoped so in-place canvas swaps (live typing / portal
                        // edits) can re-render fresh math with the exact same options.
                        window.__msRenderMath(document.body);"></script>
                """;
        }
        if (body.Contains("language-"))
        {
            var hlTheme = !ThemeDefinition.IsLight(theme.Code) ? "github-dark" : "github";
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
        var matches = TocHeadingRe().Matches(body);
        if (matches.Count < 2) return "";

        var sb = new StringBuilder("<nav id=\"toc\">\n <div class=\"toc-title\">Contents</div>\n <ul>");
        foreach (Match m in matches)
        {
            var level = m.Groups[1].Value[0] - '0';
            var text = HtmlTagStripRe().Replace(m.Groups[3].Value, "").Trim();
            var indent = (level - 1) * 16;
            sb.Append($"<li style=\"margin-left:{indent}px\"><a href=\"#{m.Groups[2].Value}\">{text}</a></li>");
        }
        sb.Append("</ul>\n </nav>");
        return sb.ToString();
    }
}
