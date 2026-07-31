using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdToPdf.Core.AdvancedFeatures;
using MdToPdf.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W14 = DocumentFormat.OpenXml.Office2010.Word;
using W15 = DocumentFormat.OpenXml.Office2013.Word;
using System.Text.Json;
using System.Globalization;
using System.Xml.Linq;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Wps = DocumentFormat.OpenXml.Office2010.Word.DrawingShape;
namespace MdToPdf.Services;

// Native DOCX export via DocumentFormat.OpenXml (option 2 of the design decision that used to live
// here): the Python app shells out to pandoc (generate_docx_core in md_to_pdf_tui.py), which this
// port deliberately avoids so the app has zero external dependencies. Markdig's AST is walked
// directly into OOXML, and the document leans hard on Word machinery most generators skip:
//   - w:background + w:displayBackgroundShape paint the page in the selected theme, so a Dracula
//     export opens as a dark document, not black-on-white (doc defaults carry the theme text color)
//   - OpenType typography defaults: kerning, standard+contextual ligatures, old-style proportional
//     numerals, contextual alternates (w14:* run extensions)
//   - a dropped capital (w:framePr dropCap) on the first body paragraph
//   - real Word fields: PAGE/NUMPAGES in the footer, and a self-updating TOC field (w:updateFields)
//     when "Include TOC" is on — plus bookmarks on every heading so #anchor links navigate in Word
//   - w:pgBorders page frame, A4 vs Letter geometry from the A4FixedWidth setting, auto-hyphenation
//   - tables get repeating header rows (w:tblHeader), unsplittable rows, zebra banding, and
//     accessibility alt text (w:tblCaption/w:tblDescription)
//   - LaTeX/KaTeX math becomes real, *editable* Word equations (OMML) via LatexToOmml — fractions,
//     roots, n-ary sum/integral with proper limits, delimiters, Greek and upright function names —
//     not flat text (inline $..$ flows in the run; display $$..$$ is a centered equation paragraph)
// GitHub alerts render as single-cell tables with the same theme accent palette the Python DOCX
// path used. Mermaid flowcharts render as NATIVE Word shape groups (boxes/diamonds/connectors) via
// MermaidDocxRenderer — editable in Word, no browser needed; unsupported diagram types keep the
// code-block fallback.
public sealed partial class DocxExportService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UseAlertBlocks()
        .UseMathematics()
        .UseEmojiAndSmiley(enableSmileys: false) // :rocket: -> emoji chars in Word too (same as the HTML pipeline)
        .Build();

    // See MarkdownHtmlService.PipelineNoEmoji — same rationale: no-emoji mode must not let
    // shortcode conversion reintroduce emoji after EmojiStripper already ran.
    private static readonly MarkdownPipeline PipelineNoEmoji = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UseAlertBlocks()
        .UseMathematics()
        .UseEmojiAndSmiley(enableSmileys: false)
        .Build();

    private static readonly ThemeCatalog Themes = new();

    // Same alert accent colors as MarkdownHtmlService / the Python app's DOCX alert rendering.
    private static readonly Dictionary<string, (string Color, string Icon)> AlertStyles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = ("#0969da", "ℹ️"),
            ["tip"] = ("#1f883d", "💡"),
            ["important"] = ("#8250df", "📌"),
            ["warning"] = ("#bf8700", "⚠️"),
            ["caution"] = ("#cf222e", "🛑"),
        };

    private static readonly Dictionary<string, (string Color, string Icon)> AlertStylesDark =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = ("#58a6ff", "ℹ️"),
            ["tip"] = ("#3fb950", "💡"),
            ["important"] = ("#a371f7", "📌"),
            ["warning"] = ("#d29922", "⚠️"),
            ["caution"] = ("#f85149", "🛑"),
        };

    // mermaidImages: optional pre-rasterized PNGs of the document's mermaid fences (in order),
    // supplied by MainWindow's snapshot renderer. Used directly in Snapshot mode, and as the
    // fallback in ShapeForge mode for diagram types the shape engine can't fully parse.
    // cleanupNotes: the normalizer's applied-fix list; when present, a real Word comment anchored
    // at the top of the document discloses exactly what Marksmith cleaned — transparent, on-brand.
    public Task ExportAsync(string markdown, string docxPath, AppSettings settings,
        IReadOnlyList<byte[]?>? mermaidImages = null, IReadOnlyList<string>? cleanupNotes = null,
        IReadOnlyList<Mermaid.HarvestedDiagram?>? mermaidGeometry = null,
        IReadOnlyList<Mermaid.GenericDiagram?>? mermaidGenericGeometry = null,
        int? oversizedDiagramModeOverride = null) =>
        Task.Run(() =>
        {
            markdown = TextNormalizer.Newlines(markdown);
            markdown = AdmonitionNormalizer.Apply(markdown);
            markdown = DialectNormalizer.Apply(markdown);
            markdown = DiagramFenceSniffer.Apply(markdown);
            
            var pipelineFeatures = new AdvancedFeaturePipeline();
            var docId = AdvancedFeaturePipeline.ContentBasedDocumentId(markdown);
            var featureNodes = pipelineFeatures.Process(markdown, docId);
            
            foreach (var node in featureNodes.OrderByDescending(n => n.Block.Start))
            {
                var before = markdown.Substring(0, node.Block.Start);
                var after = markdown.Substring(node.Block.End);
                markdown = before + $"<!-- MARKSMITH_FEATURE:{node.StableId} -->" + after;
            }
            var featureDict = featureNodes.GroupBy(n => n.StableId).ToDictionary(g => g.Key, g => g.First());

            if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
            markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
            markdown = FormattingService.Apply(markdown, settings);
            var doc = Markdown.Parse(markdown, settings.NoEmoji ? PipelineNoEmoji : Pipeline);
            var theme = Themes.GetOrDefault(settings.Theme);
            var isDark = theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro";
            var title = FirstHeadingText(doc) ?? "Markdown Export";

            var dir = Path.GetDirectoryName(docxPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var package = WordprocessingDocument.Create(docxPath, WordprocessingDocumentType.Document);
            package.PackageProperties.Title = title;
            package.PackageProperties.Creator = "Marksmith";
            package.PackageProperties.Subject = "Generated from Markdown";
            package.PackageProperties.Created = DateTime.UtcNow;
            package.PackageProperties.Modified = DateTime.UtcNow;

            var main = package.AddMainDocumentPart();
            var body = new W.Body();
            // w:background must precede w:body; displayBackgroundShape in settings makes Word honor it.
            main.Document = new W.Document(
                new W.DocumentBackground { Color = Hex(theme.Background) }, body);
            
            main.Document.AddNamespaceDeclaration("w14", "http://schemas.microsoft.com/office/word/2010/wordml");
            main.Document.AddNamespaceDeclaration("w15", "http://schemas.microsoft.com/office/word/2012/wordml");
            main.Document.MCAttributes = new DocumentFormat.OpenXml.MarkupCompatibilityAttributes { Ignorable = "w14 w15" };

            var ctx = new Ctx
            {
                Settings = settings,
                MainPart = main,
                Numbering = AddNumbering(main),
                Theme = theme,
                Alerts = isDark ? AlertStylesDark : AlertStyles,
                LinkColor = isDark ? "6CB6FF" : "0563C1",
                NoEmoji = settings.NoEmoji,
                MermaidMode = settings.MermaidDocxMode,
                MermaidImages = mermaidImages,
                MermaidGeometry = mermaidGeometry,
                MermaidExactLayout = mermaidGeometry is not null, // geometry is only harvested when exact chosen
                MermaidGenericGeometry = mermaidGenericGeometry,
                BrandFont = string.IsNullOrWhiteSpace(settings.BrandFontFamily) ? null : settings.BrandFontFamily.Trim(),
                OversizedDiagramMode = oversizedDiagramModeOverride ?? settings.OversizedDiagramMode,
                DiagramGridSize = Math.Clamp(settings.DiagramGridSize, 2, 3),
                SmartConnectors = settings.SmartConnectors,
                AdvancedFeatures = featureDict,
            };

            AddStyles(main, ctx);
            CollectAnchors(doc, ctx);

            if (settings.BrandCoverPage) AppendCoverPage(body, ctx, settings, title);
            if (settings.IncludeToc) AppendTocField(body, ctx);

            foreach (var block in doc)
                RenderBlock(block, body, ctx, listLevel: -1);

            if (cleanupNotes is { Count: > 0 }) AddCleanupComment(main, body, cleanupNotes);

            // Written after rendering: an oversized ShapeForge diagram flips the doc to Web Layout,
            // where a wider-than-page drawing scrolls instead of clipping (the user's own idea).
            AddSettings(main, updateFieldsOnOpen: settings.IncludeToc, trackChanges: settings.TrackChanges,
                webLayout: settings.UnlimitedHeight || ctx.ForceWebLayout || !ThemeDefinition.IsLight(ctx.Theme.Background));

            body.Append(BuildSectionProperties(main, ctx, settings, title));
            main.Document.Save();
        });

    // A genuine Word comment (review pane, margin bubble) anchored on the first paragraph,
    // disclosing every normalization the AI-cleanup engine applied. Transparency, not magic.
    private static void AddCleanupComment(MainDocumentPart main, W.Body body, IReadOnlyList<string> notes)
    {
        var part = main.WordprocessingCommentsPart ?? main.AddNewPart<WordprocessingCommentsPart>();
        part.Comments ??= new W.Comments();

        var comment = new W.Comment { Id = "1", Author = "Marksmith", Initials = "MS", Date = DateTime.UtcNow };
        comment.Append(new W.Paragraph(new W.Run(new W.Text(
            $"Marksmith normalized {notes.Count} AI formatting quirk{(notes.Count == 1 ? "" : "s")} in this document:"))));
        foreach (var n in notes)
            comment.Append(new W.Paragraph(new W.Run(new W.Text("• " + n) { Space = SpaceProcessingModeValues.Preserve })));
        part.Comments.Append(comment);
        part.Comments.Save();

        var first = body.Elements<W.Paragraph>().FirstOrDefault();
        if (first is null) { first = new W.Paragraph(); body.PrependChild(first); }
        int at = first.Elements<W.ParagraphProperties>().Any() ? 1 : 0;
        first.InsertAt(new W.CommentRangeStart { Id = "1" }, at);
        first.Append(new W.CommentRangeEnd { Id = "1" });
        first.Append(new W.Run(new W.CommentReference { Id = "1" }));
    }

    // Append mode: add the content as a dated section to an existing running document instead of
    // creating a new file — a growing compendium of your AI work. Creates the file fresh if missing.
    public Task ExportAppendAsync(string markdown, string docxPath, AppSettings settings,
        IReadOnlyList<byte[]?>? mermaidImages = null,
        IReadOnlyList<Mermaid.HarvestedDiagram?>? mermaidGeometry = null,
        IReadOnlyList<Mermaid.GenericDiagram?>? mermaidGenericGeometry = null,
        int? oversizedDiagramModeOverride = null) => Task.Run(() =>
    {
        if (!File.Exists(docxPath)) { ExportAsync(markdown, docxPath, settings, mermaidImages, null, mermaidGeometry, mermaidGenericGeometry, oversizedDiagramModeOverride).GetAwaiter().GetResult(); return; }

        markdown = TextNormalizer.Newlines(markdown);
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown);
        markdown = DiagramFenceSniffer.Apply(markdown);

        var pipelineFeatures = new AdvancedFeaturePipeline();
        var docId = AdvancedFeaturePipeline.ContentBasedDocumentId(markdown);
        var featureNodes = pipelineFeatures.Process(markdown, docId);
        
        foreach (var node in featureNodes.OrderByDescending(n => n.Block.Start))
        {
            var before = markdown.Substring(0, node.Block.Start);
            var after = markdown.Substring(node.Block.End);
            markdown = before + $"<!-- MARKSMITH_FEATURE:{node.StableId} -->" + after;
        }
        var featureDict = featureNodes.GroupBy(n => n.StableId).ToDictionary(g => g.Key, g => g.First());

        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);
        var doc = Markdown.Parse(markdown, settings.NoEmoji ? PipelineNoEmoji : Pipeline);
        var theme = Themes.GetOrDefault(settings.Theme);
        var isDark = theme.Name.Contains("Dark") || theme.Name is "Dracula" or "Cyberpunk" or "Obsidian" or "Monokai Pro";

        using var package = WordprocessingDocument.Open(docxPath, true);
        var main = package.MainDocumentPart!;
        var body = main.Document.Body!;

        // Reuse the existing document's numbering and bookmarks; offset new ids past what's there so an
        // appended section never collides with earlier ones.
        var numbering = main.NumberingDefinitionsPart?.Numbering ?? AddNumbering(main);
        int maxNum = numbering.Elements<W.NumberingInstance>()
            .Select(n => (int)(n.NumberID?.Value ?? 0)).DefaultIfEmpty(1).Max();
        int maxBm = body.Descendants<W.BookmarkStart>()
            .Select(b => int.TryParse(b.Id?.Value, out var v) ? v : 0).DefaultIfEmpty(0).Max();

        var ctx = new Ctx
        {
            Settings = settings,
            MainPart = main,
            Numbering = numbering,
            Theme = theme,
            Alerts = isDark ? AlertStylesDark : AlertStyles,
            LinkColor = isDark ? "6CB6FF" : "0563C1",
            NoEmoji = settings.NoEmoji,
            NextNumId = maxNum + 1,
            NextBookmarkId = maxBm + 1,
            DropCapPending = false, // no drop cap on appended sections
            MermaidMode = settings.MermaidDocxMode,
            MermaidImages = mermaidImages,
            MermaidGeometry = mermaidGeometry,
            MermaidExactLayout = mermaidGeometry is not null,
            MermaidGenericGeometry = mermaidGenericGeometry,
            BrandFont = string.IsNullOrWhiteSpace(settings.BrandFontFamily) ? null : settings.BrandFontFamily.Trim(),
            OversizedDiagramMode = oversizedDiagramModeOverride ?? settings.OversizedDiagramMode,
            DiagramGridSize = Math.Clamp(settings.DiagramGridSize, 2, 3),
            SmartConnectors = settings.SmartConnectors,
            AdvancedFeatures = featureDict,
        };

        CollectAnchors(doc, ctx);
        var tag = "s" + ctx.NextBookmarkId; // unique prefix so appended anchors stay unique
        foreach (var key in ctx.Anchors.Keys.ToList())
        {
            var name = tag + "_" + ctx.Anchors[key];
            ctx.Anchors[key] = name.Length > 40 ? name[..40] : name;
        }

        // Build the new section in a temp container, then splice it in before the trailing sectPr.
        var tmp = new W.Body();
        tmp.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
        var divider = new W.Paragraph(new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading2" }));
        AddText(divider, $"Added {DateTime.Now:d MMM yyyy, HH:mm}", new Fmt { Color = ctx.HeadingHex });
        tmp.Append(divider);
        foreach (var block in doc) RenderBlock(block, tmp, ctx, listLevel: -1);

        var sectPr = body.Elements<W.SectionProperties>().LastOrDefault();
        foreach (var el in tmp.ChildElements.ToList())
        {
            el.Remove();
            if (sectPr is not null) body.InsertBefore(el, sectPr); else body.Append(el);
        }

        ctx.MainPart.NumberingDefinitionsPart?.Numbering?.Save();
        main.Document.Save();
    });

    private sealed class Ctx
    {
        public required MainDocumentPart MainPart { get; init; }
        public required W.Numbering Numbering { get; init; }
        public required AppSettings Settings { get; init; }
        public required ThemeDefinition Theme { get; init; }
        public required Dictionary<string, (string Color, string Icon)> Alerts { get; init; }
        public required string LinkColor { get; init; }
        public required bool NoEmoji { get; init; }
        public int NextNumId = 2; // numId 1 is the shared bullet instance
        public int NextBookmarkId = 1;
        public uint NextDrawingId = 1000; // docPr ids for drawings (mermaid shapes / snapshots)
        public int NextRevisionId = 1;    // sequential revision id for OpenXML track changes
        public string DefaultRevisionAuthor = "Marksmith AI";
        public DateTime DefaultRevisionDate = DateTime.UtcNow;
        public string? BrandFont;         // branding kit: document-wide font override
        public bool ForceWebLayout;       // an oversized ShapeForge diagram wants Web Layout view
        public int MermaidMode = 1;       // 0 = Snapshot picture, 1 = ShapeForge native shapes
        public IReadOnlyList<byte[]?>? MermaidImages; // pre-rasterized PNGs, one per fence in order
        public IReadOnlyList<Mermaid.HarvestedDiagram?>? MermaidGeometry; // exact mermaid geometry per fence
        public bool MermaidExactLayout; // OversizedDiagramMode==Exact: prefer harvested geometry
        public IReadOnlyList<Mermaid.GenericDiagram?>? MermaidGenericGeometry; // generic per-fence geometry (any type)
        public int MermaidSeen;           // index of the next mermaid fence encountered
        public bool DropCapPending = true;
        public readonly Dictionary<string, string> Anchors = new(); // markdig heading id -> bookmark name
        public int OversizedDiagramMode;   // 0=Ask,1=Exact,2=Reflow,3=MultiPageVertical,4=Grid,5=ShrinkToFit
        public int DiagramGridSize = 2;    // grid multiplier for mode 4 (2=2×2, 3=3×3)
        public bool SmartConnectors = true;
        
        public required Dictionary<string, FeatureNode> AdvancedFeatures { get; init; }

        public string TextHex => Hex(Theme.Text);
        public string HeadingHex => Hex(Theme.Heading);
        public string BorderHex => Hex(Theme.Border);
        public string CodeHex
        {
            get
            {
                var hex = Hex(Theme.Code);
                bool isDarkDoc = !ThemeDefinition.IsLight(Theme.Background);
                if (isDarkDoc && ThemeDefinition.IsLight("#" + hex)) return "1E222A";
                return hex;
            }
        }
        public string SecondaryHex
        {
            get
            {
                var hex = Hex(Theme.Secondary);
                bool isDarkDoc = !ThemeDefinition.IsLight(Theme.Background);
                if (isDarkDoc && ThemeDefinition.IsLight("#" + hex)) return "2B303B";
                return hex;
            }
        }
        public string PrimaryHex => Hex(Theme.Primary);
    }

    // Inline formatting state threaded through the inline walker.
    private readonly record struct Fmt(
        bool Bold, bool Italic, bool Strike, bool Code, bool Superscript, bool Subscript,
        bool Highlight, bool Underline, string? Color, bool NoProof = false, bool WikiLink = false, bool UnderlineDash = false,
        RevisionKind Revision = RevisionKind.None, string? RevisionAuthor = null, DateTime? RevisionDate = null, int RevisionId = 0,
        W.UnderlineValues? UnderlineStyle = null, string? UnderlineColor = null,
        W.HighlightColorValues? HighlightColor = null, string? ShadingColor = null)
    {
        public bool EffectiveUnderline => Underline || (UnderlineStyle.HasValue && UnderlineStyle.Value != W.UnderlineValues.None);

        public W.UnderlineValues EffectiveUnderlineStyle => UnderlineStyle ?? (WikiLink ? W.UnderlineValues.Dash : (Underline ? W.UnderlineValues.Single : W.UnderlineValues.None));

        public W.HighlightColorValues? EffectiveHighlightColor => HighlightColor ?? (Highlight ? W.HighlightColorValues.Yellow : null);
    }

    private static string Hex(string cssColor) => cssColor.TrimStart('#').ToUpperInvariant();

    // Parse "#RRGGBB"/"RRGGBB" into RGB; returns false on any malformed input so callers can fall back.
    private static bool TryParseHexRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var s = (hex ?? "").Trim().TrimStart('#');
        if (s.Length != 6) return false;
        try
        {
            r = Convert.ToInt32(s.Substring(0, 2), 16);
            g = Convert.ToInt32(s.Substring(2, 2), 16);
            b = Convert.ToInt32(s.Substring(4, 2), 16);
            return true;
        }
        catch { return false; }
    }

    // Linear blend of two hex colors; fraction 0 = all `baseHex`, 1 = all `overlayHex`. Used to tint
    // callout panels with their accent and to step heading colors down the accent->body ramp.
    private static string BlendHex(string baseHex, string overlayHex, double fraction)
    {
        if (!TryParseHexRgb(baseHex, out var br, out var bg, out var bb)) return Hex(overlayHex);
        if (!TryParseHexRgb(overlayHex, out var or, out var og, out var ob)) return Hex(baseHex);
        int Mix(int a, int c) => (int)Math.Round(a + (c - a) * Math.Clamp(fraction, 0, 1));
        return $"{Mix(br, or):X2}{Mix(bg, og):X2}{Mix(bb, ob):X2}";
    }

    private static string? FirstHeadingText(MarkdownDocument doc)
    {
        foreach (var h in doc.Descendants<HeadingBlock>())
            if (h.Inline is not null)
            {
                var text = GetPlainText(h.Inline).Trim();
                if (text.Length > 0) return text;
            }
        return null;
    }

    // Word bookmark names: start with a letter, [A-Za-z0-9_] only, 40-char limit.
    private static void CollectAnchors(MarkdownDocument doc, Ctx ctx)
    {
        var used = new HashSet<string>();
        foreach (var h in doc.Descendants<HeadingBlock>())
        {
            var id = h.TryGetAttributes()?.Id;
            if (id is null || ctx.Anchors.ContainsKey(id)) continue;
            var sb = new StringBuilder("H_");
            foreach (var c in id)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            var name = sb.ToString(0, Math.Min(sb.Length, 35));
            var candidate = name;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}_{n}";
            ctx.Anchors[id] = candidate;
        }
    }

    // ---------------------------------------------------------------- blocks

    private static void RenderBlock(Block block, OpenXmlCompositeElement target, Ctx ctx, int listLevel)
    {
        // The very first body paragraph gets a dropped capital — once any other body-level content
        // has rendered, the window closes.
        if (target is W.Body && block is not HeadingBlock && block is not Markdig.Extensions.Yaml.YamlFrontMatterBlock)
        {
            var wasPending = ctx.DropCapPending;
            ctx.DropCapPending = false;
            if (wasPending && block is ParagraphBlock dropPara && TryRenderDropCap(dropPara, target, ctx))
                return;
        }

        switch (block)
        {
            case HeadingBlock h:
            {
                var para = new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphStyleId { Val = $"Heading{Math.Clamp(h.Level, 1, 6)}" }));
                var id = h.TryGetAttributes()?.Id;
                if (id is not null && ctx.Anchors.TryGetValue(id, out var bookmark))
                {
                    var bmId = (ctx.NextBookmarkId++).ToString();
                    para.Append(new W.BookmarkStart { Name = bookmark, Id = bmId });
                    RenderInlines(para, h.Inline, ctx, default);
                    para.Append(new W.BookmarkEnd { Id = bmId });
                }
                else
                {
                    RenderInlines(para, h.Inline, ctx, default);
                }
                target.Append(para);
                break;
            }

            case Markdig.Extensions.Yaml.YamlFrontMatterBlock:
                break; // front matter is metadata, never rendered (matches HTML path)

            case MathBlock math:
            {
                // Display math → a centered paragraph holding a real, editable Word equation (OMML).
                var mp = new W.Paragraph(new W.ParagraphProperties(
                    new W.Justification { Val = W.JustificationValues.Center },
                    new W.SpacingBetweenLines { Before = "120", After = "120" }));
                mp.Append(LatexToOmml.Build(math.Lines.ToString()));
                target.Append(mp);
                break;
            }

            case FencedCodeBlock fence when fence.Info?.Trim().StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true:
            {
                // Two user-selectable methods (settings.MermaidDocxMode):
                //   ShapeForge (1) — rebuild the diagram as native, editable Word shapes; if the shape
                //                    engine can't fully parse it, fall back to the snapshot picture.
                //   Snapshot  (0) — embed a picture of the rendered diagram.
                // Last resort either way: the plain code block (never a half-understood diagram).
                var idx = ctx.MermaidSeen++;
                var png = ctx.MermaidImages is not null && idx < ctx.MermaidImages.Count ? ctx.MermaidImages[idx] : null;
                var geo = ctx.MermaidGeometry is not null && idx < ctx.MermaidGeometry.Count ? ctx.MermaidGeometry[idx] : null;
                var gen = ctx.MermaidGenericGeometry is not null && idx < ctx.MermaidGenericGeometry.Count ? ctx.MermaidGenericGeometry[idx] : null;

                System.Diagnostics.Debug.WriteLine($"[MERMAID-DIAG] fence#{idx}: MermaidMode={ctx.MermaidMode}, ExactLayout={ctx.MermaidExactLayout}, geo={(geo is null ? "null" : $"Nodes={geo.Nodes.Count},Edges={geo.Edges.Count},IsEmpty={geo.IsEmpty}")}, gen={(gen is null ? "null" : $"IsEmpty={gen.IsEmpty}")}, png={(png is null ? "null" : $"{png.Length}B")}");
                Console.WriteLine($"[MERMAID-DIAG] fence#{idx}: MermaidMode={ctx.MermaidMode}, ExactLayout={ctx.MermaidExactLayout}, geo={(geo is null ? "null" : $"Nodes={geo.Nodes.Count},Edges={geo.Edges.Count},IsEmpty={geo.IsEmpty}")}, gen={(gen is null ? "null" : $"IsEmpty={gen.IsEmpty}")}, png={(png is null ? "null" : $"{png.Length}B")}");

                // Exact-layout mode: rebuild mermaid's OWN geometry as native shapes (node-for-node,
                // no reordering) and open the document in Web Layout so a wide diagram scrolls.
                if (ctx.MermaidMode == 1 && ctx.MermaidExactLayout && geo is { IsEmpty: false })
                {
                    var md = geo.ToMDiagram(ctx.Theme);
                    if (ctx.OversizedDiagramMode == 3) // multi-page vertical
                    {
                        var drawId = ctx.NextDrawingId;
                        var bands = Mermaid.DocxShapeEmitter.ToMultiPageParagraphXml(md, ctx.Theme, ref drawId, ctx.SmartConnectors);
                        ctx.NextDrawingId = drawId;
                        foreach (var bandXml in bands)
                        {
                            var bp = new W.Paragraph { InnerXml = bandXml };
                            bp.PrependChild(new W.ParagraphProperties(
                                new W.SpacingBetweenLines { Before = "60", After = "60" },
                                new W.Justification { Val = W.JustificationValues.Center }));
                            target.Append(bp);
                        }
                    }
                    else
                    {
                        var xml = Mermaid.DocxShapeEmitter.ToParagraphXml(md, ctx.Theme, ctx.NextDrawingId++, out _,
                            oversizedMode: ctx.OversizedDiagramMode, gridSize: ctx.DiagramGridSize, smartConnectors: ctx.SmartConnectors);
                        var p = new W.Paragraph { InnerXml = xml };
                        p.PrependChild(new W.ParagraphProperties(
                            new W.SpacingBetweenLines { Before = "120", After = "120" },
                            new W.Justification { Val = W.JustificationValues.Center }));
                        // Exact mode (1) and Grid mode (4) force Web Layout; ShrinkToFit (5) does not.
                        if (ctx.OversizedDiagramMode is 1 or 4)
                            ctx.ForceWebLayout = true;
                        target.Append(p);
                    }
                }
                else if (ctx.MermaidMode == 1 &&
                    MermaidDocxRenderer.TryRender(fence.Lines.ToString(), ctx.Theme, ctx.Settings, ctx.NextDrawingId++, out var diagram, out var oversizedDiagram,
                        forceFit: !ctx.MermaidExactLayout))
                {
                    System.Diagnostics.Debug.WriteLine($"[MERMAID-DIAG] fence#{idx}: TryRender succeeded");
                    Console.WriteLine($"[MERMAID-DIAG] fence#{idx}: TryRender succeeded");
                    ctx.ForceWebLayout |= oversizedDiagram;
                    target.Append(diagram);
                }
                // Generic path (the "no fallback" win): any diagram type a bespoke renderer doesn't
                // handle — state, C4, block, kanban, packet, sankey, etc. — rebuilt from mermaid's
                // harvested SVG primitives as native shapes, instead of a flat picture.
                else if (ctx.MermaidMode == 1 && gen is { IsEmpty: false })
                {
                    var md = gen.ToMDiagram(ctx.Theme);
                    if (ctx.OversizedDiagramMode == 3) // multi-page vertical
                    {
                        var drawId = ctx.NextDrawingId;
                        var bands = Mermaid.DocxShapeEmitter.ToMultiPageParagraphXml(md, ctx.Theme, ref drawId, ctx.SmartConnectors);
                        ctx.NextDrawingId = drawId;
                        foreach (var bandXml in bands)
                        {
                            var bp = new W.Paragraph { InnerXml = bandXml };
                            bp.PrependChild(new W.ParagraphProperties(
                                new W.SpacingBetweenLines { Before = "60", After = "60" },
                                new W.Justification { Val = W.JustificationValues.Center }));
                            target.Append(bp);
                        }
                    }
                    else
                    {
                        var xml = Mermaid.DocxShapeEmitter.ToParagraphXml(md, ctx.Theme, ctx.NextDrawingId++, out var oversizedGen,
                            oversizedMode: ctx.OversizedDiagramMode, gridSize: ctx.DiagramGridSize, smartConnectors: ctx.SmartConnectors);
                        var p = new W.Paragraph { InnerXml = xml };
                        p.PrependChild(new W.ParagraphProperties(
                            new W.SpacingBetweenLines { Before = "120", After = "120" },
                            new W.Justification { Val = W.JustificationValues.Center }));
                        ctx.ForceWebLayout |= oversizedGen;
                        target.Append(p);
                    }
                }
                else if (png is not null)
                {
                    System.Diagnostics.Debug.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to snapshot image ({png.Length} bytes)");
                    Console.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to snapshot image ({png.Length} bytes)");
                    target.Append(SnapshotParagraph(png, ctx));
                }
                else if (MermaidDocxRenderer.TryRender(fence.Lines.ToString(), ctx.Theme, ctx.Settings, ctx.NextDrawingId++, out var fallbackDiagram, out var fallbackOversized, forceFit: true))
                {
                    System.Diagnostics.Debug.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to native shapes succeeded");
                    Console.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to native shapes succeeded");
                    ctx.ForceWebLayout |= fallbackOversized;
                    target.Append(fallbackDiagram);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to code block (no png or shapes available)");
                    Console.WriteLine($"[MERMAID-DIAG] fence#{idx}: FALLBACK to code block (no png or shapes available)");
                    target.Append(CodeParagraph(fence.Lines.ToString(), fence.Info, ctx));
                }
                break;
            }

            // Diagram-plugin fences (```dot / ```plantuml / ```d2 / ```vega-lite / …, including the
            // ones DiagramFenceSniffer relabelled from a bare fence). The plugin renders the source
            // to SVG out-of-process; before this case existed the source dumped out as plain text
            // (it fell through to the CodeBlock case below). Same mode ladder as mermaid:
            //   ShapeForge (1) — parse the SVG's primitives (SvgShapeForge) and rebuild them as
            //                    native, editable Word shapes via the same GenericDiagram →
            //                    DocxShapeEmitter pipeline the mermaid generic harvest uses.
            //   Snapshot  (0) — or if the parse recovers too little — embed the SVG as a real
            //                    picture (crisp SVG for Word 2016+, rasterized PNG fallback).
            // Floor either way: the plain code block, never nothing.
            case FencedCodeBlock pluginFence when PluginDiagramLanguage(pluginFence.Info) is { } pluginLang:
            {
                var plugin = AppServices.Plugins.FindDiagramRenderer(pluginLang);
                var svg = plugin is not null ? AppServices.Plugins.RenderToSvgCached(plugin, pluginFence.Lines.ToString()) : null;

                if (svg is not null && ctx.MermaidMode == 1 &&
                    Mermaid.SvgShapeForge.Parse(svg) is { IsEmpty: false } forged)
                {
                    var md = forged.ToMDiagram(ctx.Theme);
                    if (ctx.OversizedDiagramMode == 3) // multi-page vertical
                    {
                        var drawId = ctx.NextDrawingId;
                        var bands = Mermaid.DocxShapeEmitter.ToMultiPageParagraphXml(md, ctx.Theme, ref drawId, ctx.SmartConnectors);
                        ctx.NextDrawingId = drawId;
                        foreach (var bandXml in bands)
                        {
                            var bp = new W.Paragraph { InnerXml = bandXml };
                            bp.PrependChild(new W.ParagraphProperties(
                                new W.SpacingBetweenLines { Before = "60", After = "60" },
                                new W.Justification { Val = W.JustificationValues.Center }));
                            target.Append(bp);
                        }
                    }
                    else
                    {
                        var xml = Mermaid.DocxShapeEmitter.ToParagraphXml(md, ctx.Theme, ctx.NextDrawingId++, out var oversized,
                            oversizedMode: ctx.OversizedDiagramMode, gridSize: ctx.DiagramGridSize, smartConnectors: ctx.SmartConnectors);
                        var p = new W.Paragraph { InnerXml = xml };
                        p.PrependChild(new W.ParagraphProperties(
                            new W.SpacingBetweenLines { Before = "120", After = "120" },
                            new W.Justification { Val = W.JustificationValues.Center }));
                        ctx.ForceWebLayout |= oversized;
                        target.Append(p);
                    }
                }
                else if (svg is not null && SvgDiagramParagraph(svg, ctx) is { } para)
                {
                    ctx.ForceWebLayout = true; // wide network/graph diagrams need Word's Web Layout to not clip
                    target.Append(para);
                }
                else
                {
                    // The plugin DID render an SVG but Word can't embed it (SvgShapeForge couldn't
                    // parse it AND rasterization failed — e.g. an SVG using a filter/foreignObject
                    // Svg.Skia chokes on). PDF/preview show the diagram, so silently dumping the raw
                    // @startuml source here as if it were meant to be code is the pipeline-divergence
                    // trap. Prefix a caption so the source reads as "diagram source (rendered in the
                    // preview/PDF)", not as an intended code listing. When svg is null (plugin not
                    // installed or render failed) the plain code block is correct and gets no caption.
                    if (svg is not null)
                        target.Append(DiagramSourceCaption(plugin!.Name, ctx));
                    target.Append(CodeParagraph(pluginFence.Lines.ToString(), pluginFence.Info ?? "", ctx));
                }
                break;
            }

            case CodeBlock code: // other fenced/indented code renders as a code block
                var info = (code as FencedCodeBlock)?.Info;
                target.Append(CodeParagraph(code.Lines.ToString(), info ?? "", ctx));
                break;

            case AlertBlock alert:
                RenderAlert(alert, target, ctx);
                break;

            case QuoteBlock quote:
            {
                var before = target.ChildElements.Count;
                foreach (var child in quote)
                    RenderBlock(child, target, ctx, -1);
                for (var i = before; i < target.ChildElements.Count; i++)
                    if (target.ChildElements[i] is W.Paragraph p)
                        ApplyQuoteFormatting(p, ctx);
                break;
            }

            case ListBlock list:
                RenderList(list, target, ctx, listLevel);
                break;

            case MdTable table:
                RenderTable(table, target, ctx);
                break;

            case ThematicBreakBlock:
                target.Append(new W.Paragraph(new W.ParagraphProperties(
                    new W.ParagraphBorders(new W.BottomBorder
                    {
                        Val = W.BorderValues.Wave, Size = 6, Space = 1, Color = ctx.BorderHex
                    }))));
                break;

            case HtmlBlock htmlBlock:
                // Block-level raw HTML used to be dropped ENTIRELY here — a raw <table> or <details>
                // an AI emitted just vanished from the Word doc (worse than the inline case, which
                // only lost formatting). Now the common, high-value shapes are recovered, and the
                // catch-all strips tags to text so content is never silently lost.
                RenderHtmlBlock(htmlBlock.Lines.ToString(), target, ctx);
                break;

            case ParagraphBlock p:
            {
                var para = new W.Paragraph();
                RenderInlines(para, p.Inline, ctx, default);
                target.Append(para);
                break;
            }

            case Markdig.Extensions.Footnotes.Footnote footnote:
            {
                // Render the footnote's own content, then tie it to the body's [n] superscript by
                // prefixing the first paragraph with its "[order] " label. Without this the
                // definition renders as an unlabeled orphan paragraph at the end of the document.
                var before = target.ChildElements.Count;
                foreach (var child in footnote)
                    RenderBlock(child, target, ctx, -1);
                if (target.ChildElements.Count > before && target.ChildElements[before] is W.Paragraph first)
                {
                    var label = new W.Run(new W.Text($"[{footnote.Order}] ") { Space = SpaceProcessingModeValues.Preserve });
                    if (first.ParagraphProperties is { } pp) first.InsertAfter(label, pp);
                    else first.PrependChild(label);
                }
                break;
            }

            case ContainerBlock container: // footnote groups, custom containers, etc.
                foreach (var child in container)
                    RenderBlock(child, target, ctx, -1);
                break;

            case LeafBlock leaf when leaf.Inline is not null:
            {
                var para = new W.Paragraph();
                RenderInlines(para, leaf.Inline, ctx, default);
                target.Append(para);
                break;
            }
        }
    }

    // Classic print typography: the opening letter becomes a 3-line dropped capital in its own
    // frame-anchored paragraph (w:framePr w:dropCap), the rest of the paragraph flows around it.
    private static bool TryRenderDropCap(ParagraphBlock p, OpenXmlCompositeElement target, Ctx ctx)
    {
        if (!ctx.Settings.BrandCoverPage) return false; // Only drop-cap opening paragraph when cover page branding is enabled
        if (p.Inline?.FirstChild is not LiteralInline literal) return false;
        var text = literal.Content.ToString();
        if (text.Length == 0 || !char.IsLetter(text[0])) return false;
        if (GetPlainText(p.Inline).Trim().Length < 60) return false; // too short to wrap a drop cap

        var drop = new W.Paragraph(
            new W.ParagraphProperties(
                new W.FrameProperties
                {
                    DropCap = W.DropCapLocationValues.Drop,
                    Lines = 3,
                    Wrap = W.TextWrappingValues.Around,
                    VerticalPosition = W.VerticalAnchorValues.Text,
                    HorizontalPosition = W.HorizontalAnchorValues.Text,
                },
                new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.Run(
                new W.RunProperties(
                    new W.Bold(),
                    new W.Color { Val = ctx.HeadingHex },
                    new W.FontSize { Val = "96" },
                    new W.FontSizeComplexScript { Val = "96" }),
                new W.Text(text[..1])));
        target.Append(drop);

        literal.Content = new Markdig.Helpers.StringSlice(text[1..]);
        var rest = new W.Paragraph();
        RenderInlines(rest, p.Inline, ctx, default);
        target.Append(rest);
        return true;
    }

    // Branding kit: a title cover page — optional logo, the document title in display size, a date
    // line — followed by a page break, so the content proper starts on page 2.
    private static void AppendCoverPage(W.Body body, Ctx ctx, AppSettings settings, string title)
    {
        // breathing room from the top of the page
        body.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { Before = "2400", After = "0" })));

        if (!string.IsNullOrWhiteSpace(settings.BrandLogoPath) && File.Exists(settings.BrandLogoPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(settings.BrandLogoPath);
                var isPng = bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P';
                var part = ctx.MainPart.AddImagePart(isPng ? ImagePartType.Png : ImagePartType.Jpeg);
                using (var ms = new MemoryStream(bytes)) part.FeedData(ms);
                var relId = ctx.MainPart.GetIdOfPart(part);

                var (pxW, pxH) = isPng ? PngDimensions(bytes) : JpegDimensions(bytes);
                double ptW = Math.Min(140, pxW * 0.75), ptH = pxH * (ptW / Math.Max(1, pxW));
                long cx = (long)(ptW * 12700), cy = (long)(ptH * 12700);
                var id = ctx.NextDrawingId++;
                var drawing = new W.Drawing
                {
                    InnerXml = $"""
                        <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                          <wp:extent cx="{cx}" cy="{cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>
                          <wp:docPr id="{id}" name="Brand logo"/><wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
                          <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                            <pic:pic><pic:nvPicPr><pic:cNvPr id="{id}" name="logo"/><pic:cNvPicPr/></pic:nvPicPr>
                            <pic:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                            </pic:pic></a:graphicData></a:graphic>
                        </wp:inline>
                        """
                };
                body.Append(new W.Paragraph(
                    new W.ParagraphProperties(
                        new W.SpacingBetweenLines { Before = "0", After = "360" },
                        new W.Justification { Val = W.JustificationValues.Center }),
                    new W.Run(drawing)));
            }
            catch { /* unreadable logo — cover continues without it */ }
        }

        var titlePara = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = "240" },
            new W.Justification { Val = W.JustificationValues.Center }));
        var titleRun = new W.Run(new W.Text(title));
        titleRun.PrependChild(new W.RunProperties(
            new W.Bold(),
            new W.Color { Val = ctx.HeadingHex },
            new W.FontSize { Val = "56" }));
        titlePara.Append(titleRun);
        body.Append(titlePara);

        var datePara = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "0", After = "0" },
            new W.Justification { Val = W.JustificationValues.Center }));
        var dateRun = new W.Run(new W.Text($"{DateTime.Now:d MMMM yyyy}"));
        dateRun.PrependChild(new W.RunProperties(new W.Color { Val = ctx.Theme.Text.TrimStart('#') }, new W.FontSize { Val = "24" }));
        datePara.Append(dateRun);
        body.Append(datePara);

        body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
    }

    // JPEG dimensions from the first SOF0/SOF2 frame header.
    private static (int W, int H) JpegDimensions(byte[] b)
    {
        int i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            byte marker = b[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3)
                return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            i += 2 + ((b[i + 2] << 8) | b[i + 3]);
        }
        return (400, 400);
    }

    // Snapshot mode: embed the pre-rasterized diagram PNG as a centered inline picture, scaled to
    // the printable width (the PNGs are rendered at 2x, so half their pixel size in points).
    // The fence language iff an installed diagram plugin claims it (never "mermaid" — that has its
    // own case). Returns null so the `when` guard falls through to the next case otherwise.
    private static string? PluginDiagramLanguage(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        var lang = info.Trim().Split(' ', '\t')[0].ToLowerInvariant();
        if (lang is "mermaid" or "") return null;
        return AppServices.Plugins.FindDiagramRenderer(lang) is not null ? lang : null;
    }

    // Embeds a diagram plugin's SVG as a Word picture: the SVG itself (Word 2016+ renders it
    // crisply at any zoom) plus a rasterized PNG fallback (older Word, and thumbnails/print). The
    // <asvg:svgBlip> ext under the PNG blip is exactly what Word writes when you Insert > Picture an
    // .svg — so the round-trip is native, not a hack. Returns null if the SVG can't be rasterized
    // (caller then falls back to the code block).
    private static W.Paragraph? SvgDiagramParagraph(string svg, Ctx ctx)
    {
        var png = SvgRasterizer.ToPng(svg);
        if (png is null) return null;

        var (svgW, svgH) = SvgRasterizer.Dimensions(svg);

        var pngPart = ctx.MainPart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) pngPart.FeedData(ms);
        var pngRel = ctx.MainPart.GetIdOfPart(pngPart);

        var svgPart = ctx.MainPart.AddNewPart<ImagePart>("image/svg+xml", null);
        using (var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg))) svgPart.FeedData(ms);
        var svgRel = ctx.MainPart.GetIdOfPart(svgPart);

        // Size the frame in points (SVG px ≈ pt at 96dpi → *0.75), capped to the text column width.
        double ptW = Math.Max(40, svgW * 0.75), ptH = Math.Max(20, svgH * 0.75);
        if (ptW > 460) { ptH *= 460 / ptW; ptW = 460; }
        long cx = (long)(ptW * 12700), cy = (long)(ptH * 12700);
        var id = ctx.NextDrawingId++;

        // GUID braces as plain strings so they don't collide with raw-string interpolation escaping.
        const string dpiExtUri = "{28A0092B-C50C-407E-A947-70E740481C1C}"; // a14:useLocalDpi ext
        const string svgExtUri = "{96DAC541-7B7A-43D3-8B76-37F632A7BC16}"; // asvg:svgBlip ext

        var drawing = new W.Drawing
        {
            InnerXml = $"""
                <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <wp:extent cx="{cx}" cy="{cy}"/>
                  <wp:effectExtent l="0" t="0" r="0" b="0"/>
                  <wp:docPr id="{id}" name="Diagram" descr="Diagram rendered by a MarkSmith plugin"/>
                  <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
                  <a:graphic>
                    <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                      <pic:pic>
                        <pic:nvPicPr><pic:cNvPr id="{id}" name="diagram.svg"/><pic:cNvPicPr/></pic:nvPicPr>
                        <pic:blipFill>
                          <a:blip r:embed="{pngRel}">
                            <a:extLst>
                              <a:ext uri="{dpiExtUri}">
                                <a14:useLocalDpi xmlns:a14="http://schemas.microsoft.com/office/drawing/2010/main" val="0"/>
                              </a:ext>
                              <a:ext uri="{svgExtUri}">
                                <asvg:svgBlip xmlns:asvg="http://schemas.microsoft.com/office/drawing/2016/SVG/main" r:embed="{svgRel}"/>
                              </a:ext>
                            </a:extLst>
                          </a:blip>
                          <a:stretch><a:fillRect/></a:stretch>
                        </pic:blipFill>
                        <pic:spPr>
                          <a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                          <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                        </pic:spPr>
                      </pic:pic>
                    </a:graphicData>
                  </a:graphic>
                </wp:inline>
                """
        };
        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "120", After = "120" },
                new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(drawing));
    }

    private static W.Paragraph SnapshotParagraph(byte[] png, Ctx ctx)
    {
        var part = ctx.MainPart.AddImagePart(ImagePartType.Png);
        using (var ms = new MemoryStream(png)) part.FeedData(ms);
        var relId = ctx.MainPart.GetIdOfPart(part);

        var (pxW, pxH) = PngDimensions(png);
        double ptW = Math.Max(40, pxW * 0.375), ptH = Math.Max(20, pxH * 0.375); // 2x render → 72/96/2
        if (ptW > 460) { ptH *= 460 / ptW; ptW = 460; }
        long cx = (long)(ptW * 12700), cy = (long)(ptH * 12700);
        var id = ctx.NextDrawingId++;

        var drawing = new W.Drawing
        {
            InnerXml = $"""
                <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <wp:extent cx="{cx}" cy="{cy}"/>
                  <wp:effectExtent l="0" t="0" r="0" b="0"/>
                  <wp:docPr id="{id}" name="Mermaid diagram" descr="Diagram snapshot rendered by MarkSmith"/>
                  <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
                  <a:graphic>
                    <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                      <pic:pic>
                        <pic:nvPicPr><pic:cNvPr id="{id}" name="mermaid.png"/><pic:cNvPicPr/></pic:nvPicPr>
                        <pic:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                        <pic:spPr>
                          <a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                          <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                        </pic:spPr>
                      </pic:pic>
                    </a:graphicData>
                  </a:graphic>
                </wp:inline>
                """
        };
        return new W.Paragraph(
            new W.ParagraphProperties( // schema order: spacing before jc
                new W.SpacingBetweenLines { Before = "120", After = "120" },
                new W.Justification { Val = W.JustificationValues.Center }),
            new W.Run(drawing));
    }

    // Width/height from the PNG IHDR chunk (big-endian u32s at offsets 16 and 20).
    private static (int W, int H) PngDimensions(byte[] png)
    {
        if (png.Length < 24) return (600, 400);
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return w > 0 && h > 0 ? (w, h) : (600, 400);
    }

    // An italic caption shown above diagram SOURCE that Word couldn't embed as a picture/shapes,
    // so the reader understands the code block below is the source of a diagram they can see in the
    // preview/PDF, not an ordinary code listing (prevents the silent PDF↔DOCX divergence).
    private static W.Paragraph DiagramSourceCaption(string pluginName, Ctx ctx)
    {
        var para = new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { Before = "120", After = "40" }));
        AddText(para, $"{pluginName} diagram — shown in the preview and PDF; source below:",
            new Fmt { Italic = true, Color = ctx.Theme.Text.TrimStart('#') });
        return para;
    }

    private static readonly OpenXmlSyntaxHighlighter CodeSyntaxHighlighter = new();

    private static void AppendHighlightedCodeRuns(W.Paragraph para, IEnumerable<W.Run> runs)
    {
        foreach (var run in runs)
        {
            var textObj = run.GetFirstChild<W.Text>();
            var rawText = textObj?.Text ?? "";
            var rPr = run.RunProperties?.CloneNode(true) as W.RunProperties;

            var lines = rawText.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    para.Append(new W.Run(new W.Break()));
                }
                if (lines[i].Length > 0)
                {
                    var partRun = new W.Run(new W.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
                    if (rPr != null)
                    {
                        partRun.RunProperties = rPr.CloneNode(true) as W.RunProperties;
                    }
                    para.Append(partRun);
                }
            }
        }
    }

    private static W.Paragraph CodeParagraph(string text, string info, Ctx ctx)
    {
        var isDiff = info?.Trim().Equals("diff", StringComparison.OrdinalIgnoreCase) == true;
        var pPr = new W.ParagraphProperties();
        pPr.KeepLines = new W.KeepLines();
        pPr.WordWrap = new W.WordWrap { Val = false }; // Explicitly add wordWrap to ensure it's kept in order
        pPr.ParagraphBorders = new W.ParagraphBorders(
            new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
            new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
            new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
            new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex });
        pPr.Shading = new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.CodeHex };
        
        var para = new W.Paragraph(pPr);

        var langToken = info?.Trim().Split(' ', '\t')[0].TrimStart('.') ?? "";
        if (string.IsNullOrWhiteSpace(langToken) && !isDiff)
        {
            if (Regex.IsMatch(text, @"\b(def|import|print\(|elif|self\.)\b")) langToken = "python";
            else if (Regex.IsMatch(text, @"\b(function|console\.log|const|let|var|document\.)\b")) langToken = "javascript";
            else if (Regex.IsMatch(text, @"\b(public|private|class|namespace|using System|void)\b")) langToken = "csharp";
            else if (Regex.IsMatch(text, @"\b(SELECT|INSERT|UPDATE|DELETE|FROM|WHERE)\b", RegexOptions.IgnoreCase)) langToken = "sql";
            else if (Regex.IsMatch(text, @"<[a-z][\s\S]*>")) langToken = "html";
        }

        if (!string.IsNullOrWhiteSpace(langToken) && !isDiff)
        {
            var highlighted = CodeSyntaxHighlighter.GetHighlightedRuns(text, langToken, ctx.Theme).ToList();
            if (highlighted.Count > 0)
            {
                AppendHighlightedCodeRuns(para, highlighted);
                return para;
            }
        }

        var colorStack = new Stack<string>();

        var first = true;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (!first) para.Append(new W.Run(new W.Break()));
            first = false;
            
            var baseColor = ctx.Theme.Text.TrimStart('#');
            if (isDiff)
            {
                if (line.StartsWith("+")) baseColor = "2ea043";      // green addition
                else if (line.StartsWith("-")) baseColor = "f85149";  // red deletion
                else if (line.StartsWith("@")) baseColor = "8b949e";  // gray hunk header
            }

            // Reset the stack to this line's base color. Inline <font>/<span> color
            // tags push/pop above it; starting fresh each line keeps a diff line's
            // +/- color from leaking into (or being buried by) the next line.
            colorStack.Clear();
            colorStack.Push(baseColor);

            int lastPos = 0;
            foreach (Match m in CodeColorRegex().Matches(line))
            {
                if (m.Index > lastPos)
                {
                    AddText(para, line.Substring(lastPos, m.Index - lastPos), new Fmt { Code = true, Color = colorStack.Peek() });
                }

                if (m.Groups[3].Success) // closing tag
                {
                    if (colorStack.Count > 1) colorStack.Pop();
                }
                else
                {
                    string cVal = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (cVal.StartsWith("#")) cVal = cVal.Substring(1);
                    else if (NamedColors.TryGetValue(cVal, out var hex)) cVal = hex;
                    colorStack.Push(cVal);
                }
                lastPos = m.Index + m.Length;
            }

            if (lastPos < line.Length)
            {
                AddText(para, line.Substring(lastPos), new Fmt { Code = true, Color = colorStack.Peek() });
            }
        }
        return para;
    }

    private static void ApplyQuoteFormatting(W.Paragraph p, Ctx ctx)
    {
        p.ParagraphProperties ??= new W.ParagraphProperties();
        p.ParagraphProperties.ParagraphBorders = new W.ParagraphBorders(
            new W.LeftBorder { Val = W.BorderValues.Single, Size = 18, Space = 8, Color = ctx.BorderHex });
        p.ParagraphProperties.Shading = new W.Shading
        {
            Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.SecondaryHex
        };
        
        int leftIndent = 360;
        if (p.ParagraphProperties.Indentation?.Left?.Value != null && int.TryParse(p.ParagraphProperties.Indentation.Left.Value, out int existing))
        {
            leftIndent += existing;
        }
        p.ParagraphProperties.Indentation = new W.Indentation { Left = leftIndent.ToString() };
    }

    private static void RenderAdvancedFeature(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        switch (node.Detector.FeatureName)
        {
            case "Columns":
                RenderColumns(node, target, ctx);
                break;
            case "Tabs":
                RenderTabs(node, target, ctx);
                break;
            case "AI Context":
                RenderAiContext(node, target, ctx);
                break;
            case "SmartArt":
            case "Workflow":
            case "Timeline":
                RenderSmartArtFallback(node, target, ctx);
                break;
            case "References":
                RenderReferences(node, target, ctx);
                break;
            case "Embed":
                RenderEmbed(node, target, ctx);
                break;
            case "Datagrid":
                RenderDatagrid(node, target, ctx);
                break;
            case "Chart":
                RenderChart(node, target, ctx);
                break;
            case "Canvas":
                RenderCanvas(node, target, ctx);
                break;
            case "Kanban":
                RenderKanban(node, target, ctx);
                break;
            default:
                // Placeholder for features not yet implemented — red bold label in the Word doc.
                var p = new W.Paragraph(new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "120" }));
                AddText(p, $"[Advanced Feature Reserved: {node.Detector.FeatureName}]",
                    new Fmt { Bold = true, Color = "d73a49" });
                target.Append(p);
                break;
        }
    }

    /// <summary>
    /// Renders a :::columns block as native Word multi-column layout using continuous section breaks.
    /// The content between the two section breaks is rendered into N equal-width columns.
    /// </summary>
    private static void RenderColumns(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        // Parse column count from attributes (default 2)
        int count = 2;
        if (node.Attributes.TryGetValue("count", out var countStr) && int.TryParse(countStr, out var parsed))
            count = Math.Clamp(parsed, 2, 6);

        // --- Opening continuous section break with column definition ---
        var openSect = new W.Paragraph(new W.ParagraphProperties(
            new W.SectionProperties(
                new W.SectionType { Val = W.SectionMarkValues.Continuous },
                new W.Columns { ColumnCount = (Int16Value)(short)count, EqualWidth = true, Space = "720" })));
        target.Append(openSect);

        // --- Render the inner markdown content as standard paragraphs ---
        // We re-parse the inner content through Markdig and render it inline.
        var innerDoc = Markdig.Markdown.Parse(node.InnerContent,
            ctx.NoEmoji ? PipelineNoEmoji : Pipeline);
        foreach (var block in innerDoc)
            RenderBlock(block, target, ctx, listLevel: -1);

        // --- Closing continuous section break that resets to single column ---
        var closeSect = new W.Paragraph(new W.ParagraphProperties(
            new W.SectionProperties(
                new W.SectionType { Val = W.SectionMarkValues.Continuous },
                new W.Columns { ColumnCount = (Int16Value)(short)1, EqualWidth = true })));
        target.Append(closeSect);
    }

    /// <summary>
    /// Renders a :::tabs block as OpenXML Native Tabs with modern visual styling:
    /// 1. A top 1-row visual Tab Header Bar Table (W.Table) with a cell per tab button.
    /// 2. Active tab has accent background shading (#EBF3FE / ctx.SecondaryHex) with bold title.
    /// 3. Inactive tabs have light/muted background shading (#F8F9FA) with standard title text.
    /// 4. Tab header titles are wrapped in W.Hyperlink anchors pointing to tab section bookmarks.
    /// 5. Each tab section heading is rendered with W.OutlineLevel (Val = 8) and W15.DefaultCollapsed
    ///    (Val = false / 0 for active tab, Val = true / 1 for inactive tabs).
    /// 6. Child Markdown body blocks are rendered under each tab section heading.
    /// </summary>
    private static void RenderTabs(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var tabs = ParseTabsFromContent(node.InnerContent);
        if (tabs.Count == 0) return;

        // 1. Render Tab Header Bar Table (W.Table)
        var table = new W.Table();
        var tableProps = new W.TableProperties(
            new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" }, // 100% width
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.None },
                new W.LeftBorder { Val = W.BorderValues.None },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 6, Space = 0, Color = ctx.BorderHex ?? "CBD5E1" },
                new W.RightBorder { Val = W.BorderValues.None },
                new W.InsideHorizontalBorder { Val = W.BorderValues.None },
                new W.InsideVerticalBorder { Val = W.BorderValues.None }
            ),
            new W.TableCellMarginDefault(
                new W.TopMargin { Width = "100", Type = W.TableWidthUnitValues.Dxa },
                new W.LeftMargin { Width = "150", Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = "100", Type = W.TableWidthUnitValues.Dxa },
                new W.RightMargin { Width = "150", Type = W.TableWidthUnitValues.Dxa }
            )
        );
        table.Append(tableProps);

        int totalTabs = tabs.Count;
        int cellWidth = totalTabs > 0 ? 9000 / totalTabs : 9000;

        var tableGrid = new W.TableGrid();
        for (int i = 0; i < totalTabs; i++)
        {
            tableGrid.Append(new W.GridColumn { Width = cellWidth.ToString() });
        }
        table.Append(tableGrid);

        var row = new W.TableRow();
        var bookmarkNames = new List<string>(totalTabs);

        for (int i = 0; i < totalTabs; i++)
        {
            bool isActive = (i == 0);
            var tabTitle = tabs[i].Title;
            var cleanId = node.StableId.Replace("-", "");
            var shortId = cleanId.Substring(0, Math.Min(12, cleanId.Length));
            var bookmarkName = $"tab_{shortId}_{i}";
            bookmarkNames.Add(bookmarkName);

            var cell = new W.TableCell();
            var cellProps = new W.TableCellProperties(
                new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = cellWidth.ToString() },
                new W.TableCellBorders(
                    new W.TopBorder { Val = W.BorderValues.None },
                    new W.LeftBorder { Val = W.BorderValues.None },
                    new W.BottomBorder
                    {
                        Val = isActive ? W.BorderValues.Single : W.BorderValues.None,
                        Size = (DocumentFormat.OpenXml.UInt32Value)(isActive ? 12U : 0U),
                        Space = 0,
                        Color = isActive ? (ctx.PrimaryHex ?? "2563EB") : "auto"
                    },
                    new W.RightBorder { Val = W.BorderValues.None }
                ),
                new W.Shading
                {
                    Val = W.ShadingPatternValues.Clear,
                    Color = "auto",
                    Fill = isActive ? "EBF3FE" : "F8F9FA"
                }
            );
            cell.Append(cellProps);

            var p = new W.Paragraph(new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "60", After = "60" },
                new W.Justification { Val = W.JustificationValues.Center }
            ));

            var hyperlink = new W.Hyperlink { Anchor = bookmarkName, History = true };
            AddText(hyperlink, tabTitle, new Fmt { Bold = isActive, Color = isActive ? (ctx.PrimaryHex ?? "1E293B") : "64748B" });
            p.Append(hyperlink);
            cell.Append(p);
            row.Append(cell);
        }

        table.Append(row);
        target.Append(table);

        // 2. Render Tab Section Headings and Content Blocks
        for (int i = 0; i < totalTabs; i++)
        {
            bool isActive = (i == 0);
            var tabTitle = tabs[i].Title;
            var tabContent = tabs[i].Content;
            var bookmarkName = bookmarkNames[i];
            var bmId = (ctx.NextBookmarkId++).ToString();

            var heading = new W.Paragraph(
                new W.ParagraphProperties(
                    new W.ParagraphStyleId { Val = "Heading3" },
                    new W.ParagraphBorders(
                        new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Space = 2, Color = isActive ? (ctx.PrimaryHex ?? "2563EB") : "CBD5E1" }
                    ),
                    new W.Shading
                    {
                        Val = W.ShadingPatternValues.Clear,
                        Color = "auto",
                        Fill = isActive ? "EBF3FE" : "F8F9FA"
                    },
                    new W.SpacingBetweenLines { Before = "160", After = "80" },
                    new W.OutlineLevel { Val = 8 },
                    new W15.DefaultCollapsed { Val = !isActive }
                )
            );

            heading.Append(new W.BookmarkStart { Name = bookmarkName, Id = bmId });
            AddText(heading, tabTitle, new Fmt { Bold = isActive });
            heading.Append(new W.BookmarkEnd { Id = bmId });

            target.Append(heading);

            if (!string.IsNullOrWhiteSpace(tabContent))
            {
                var innerDoc = Markdig.Markdown.Parse(tabContent, ctx.NoEmoji ? PipelineNoEmoji : Pipeline);
                foreach (var block in innerDoc)
                {
                    RenderBlock(block, target, ctx, listLevel: -1);
                }
            }
        }
    }

    private static void RenderKanban(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var kanban = MdToPdf.Core.Kanban.KanbanParser.Parse(node.Block.RawText, node.InnerContent, node.Attributes);
        if (kanban.Columns.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(kanban.Title))
        {
            var titlePara = new W.Paragraph(new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "180", After = "120" }));
            AddText(titlePara, kanban.Title, new Fmt { Bold = true, Color = ctx.HeadingHex });
            target.Append(titlePara);
        }

        MdToPdf.Core.Kanban.SmartArtKanbanBuilder.BuildKanban(kanban, ctx.MainPart, target, ctx.Theme, ref ctx.NextDrawingId, forceFallback: true);
    }

    private static List<(string Title, string Content)> ParseTabsFromContent(string innerContent)
    {
        var result = new List<(string Title, string Content)>();
        if (string.IsNullOrWhiteSpace(innerContent)) return result;

        var lines = innerContent.Split('\n');
        string? currentTitle = null;
        var currentBody = new List<string>();

        bool inCodeFence = false;
        string? codeFenceMarker = null;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.TrimStart();

            // Code fence tracking to avoid treating :::tab or == inside code blocks as headers
            if (!inCodeFence)
            {
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    inCodeFence = true;
                    codeFenceMarker = trimmed.StartsWith("```") ? "```" : "~~~";
                    if (currentTitle != null) currentBody.Add(line);
                    continue;
                }
            }
            else
            {
                if (currentTitle != null) currentBody.Add(line);
                if (trimmed.StartsWith(codeFenceMarker!))
                {
                    inCodeFence = false;
                    codeFenceMarker = null;
                }
                continue;
            }

            var tabMatch = Regex.Match(trimmed, @"^:::tab(?:\s+title=(?:""(?<t1>.*)""|(?<t2>\S+))|\s+(?<t3>[^\n]+))?$", RegexOptions.IgnoreCase);
            var headerMatch = Regex.Match(trimmed, @"^={2,3}\s+(?:""(?<t1>.*)""|(?<t3>[^\n]+))$", RegexOptions.IgnoreCase);

            if (tabMatch.Success || headerMatch.Success)
            {
                if (currentTitle != null)
                {
                    result.Add((currentTitle, string.Join("\n", currentBody).Trim()));
                    currentBody.Clear();
                }

                var match = tabMatch.Success ? tabMatch : headerMatch;
                var t1 = match.Groups["t1"].Value;
                var t2 = match.Groups["t2"].Value;
                var t3 = match.Groups["t3"].Value;

                var title = !string.IsNullOrWhiteSpace(t1) ? t1.Trim()
                    : !string.IsNullOrWhiteSpace(t2) ? t2.Trim()
                    : !string.IsNullOrWhiteSpace(t3) ? t3.Trim()
                    : $"Tab {result.Count + 1}";

                currentTitle = title;
                continue;
            }

            if (trimmed == ":::")
            {
                continue;
            }

            if (currentTitle != null)
            {
                currentBody.Add(line);
            }
        }

        if (currentTitle != null)
        {
            result.Add((currentTitle, string.Join("\n", currentBody).Trim()));
        }

        return result;
    }

    /// <summary>
    /// Renders a :::ai-context block by injecting metadata as Word Document Variables
    /// and displaying a styled compact metadata panel in the document body.
    /// </summary>
    private static void RenderAiContext(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        // Parse key: value pairs from inner content
        var kvPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in node.InnerContent.Split('\n'))
        {
            var trimmed = line.Trim();
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx > 0)
            {
                var key = trimmed[..colonIdx].Trim();
                var val = trimmed[(colonIdx + 1)..].Trim();
                kvPairs[key] = val;
            }
        }

        // --- Inject as Document Variables into the Settings part ---
        var settingsPart = ctx.MainPart.DocumentSettingsPart;
        if (settingsPart == null)
        {
            settingsPart = ctx.MainPart.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new W.Settings();
        }
        var settings = settingsPart.Settings;
        var docVars = settings.GetFirstChild<W.DocumentVariables>() ?? settings.AppendChild(new W.DocumentVariables());

        foreach (var kv in kvPairs)
        {
            var varName = $"MARKSMITH_AI_{kv.Key.ToUpperInvariant().Replace(' ', '_')}";
            var existing = docVars.Elements<W.DocumentVariable>()
                .FirstOrDefault(v => v.Name?.Value == varName);
            if (existing != null)
                existing.Val = kv.Value;
            else
                docVars.AppendChild(new W.DocumentVariable { Name = varName, Val = kv.Value });
        }

        // --- Render a styled metadata panel in the document body ---
        // Header bar
        var header = new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { Before = "200", After = "60" },
                new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.SecondaryHex },
                new W.ParagraphBorders(
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 18, Space = 8, Color = ctx.BorderHex }
                )));
        AddText(header, "🤖 AI Context", new Fmt { Bold = true });
        target.Append(header);

        // Key-value rows
        foreach (var kv in kvPairs)
        {
            var row = new W.Paragraph(
                new W.ParagraphProperties(
                    new W.SpacingBetweenLines { After = "40" },
                    new W.Indentation { Left = "360" }));
            AddText(row, $"{kv.Key}: ", new Fmt { Bold = true, Code = true });
            AddText(row, kv.Value, new Fmt { Code = true });
            target.Append(row);
        }

        // Closing spacer
        target.Append(new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { After = "120" })));
    }

    /// <summary>
    /// Renders :::smartart, :::workflow, and :::timeline as a styled numbered step-table.
    /// True SmartArt (DiagramDataPart) requires complex layout-to-data mapping that causes
    /// file corruption if not perfectly aligned, so we use a clean table fallback.
    /// </summary>
    private static void RenderSmartArtFallback(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var items = node.InnerContent
            .Split('\n')
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.StartsWith("-") || l.StartsWith("*"))
            .Select(l => l.TrimStart('-', '*').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (items.Count == 0) return;

        var icon = node.Detector.FeatureName switch
        {
            "Timeline" => "📅",
            "Workflow" => "âš™ï¸",
            _ => "â–¶ï¸"
        };

        var table = new W.Table();
        var tblPr = new W.TableProperties(
            new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 2, Color = ctx.SecondaryHex }
            ),
            new W.TableCellMarginDefault(
                new W.TopMargin { Width = "80", Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = "80", Type = W.TableWidthUnitValues.Dxa },
                new W.TableCellLeftMargin { Width = 120, Type = W.TableWidthValues.Dxa },
                new W.TableCellRightMargin { Width = 120, Type = W.TableWidthValues.Dxa }));
        table.AppendChild(tblPr);

        for (int i = 0; i < items.Count; i++)
        {
            var row = new W.TableRow();
            var numCell = new W.TableCell(
                new W.TableCellProperties(
                    new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = "720" },
                    new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.HeadingHex },
                    new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center }));
            var numPara = new W.Paragraph(new W.ParagraphProperties(
                new W.Justification { Val = W.JustificationValues.Center }));
            AddText(numPara, $"{icon} {i + 1}", new Fmt { Bold = true, Color = "FFFFFF" });
            numCell.Append(numPara);

            var contentCell = new W.TableCell(
                new W.TableCellProperties(
                    new W.TableCellVerticalAlignment { Val = W.TableVerticalAlignmentValues.Center }));
            var contentPara = new W.Paragraph();
            AddText(contentPara, items[i], default);
            contentCell.Append(contentPara);

            row.Append(numCell, contentCell);
            table.Append(row);
        }

        target.Append(table);
        target.Append(new W.Paragraph(new W.ParagraphProperties(
            new W.SpacingBetweenLines { After = "120" })));
    }

    /// <summary>
    /// Renders :::references as a native Word Bibliography by injecting b:Sources
    /// into a CustomXmlPart and inserting a BIBLIOGRAPHY field code.
    /// </summary>
    private static void RenderReferences(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        const string bNs = "http://schemas.openxmlformats.org/officeDocument/2006/bibliography";
        var xBns = System.Xml.Linq.XNamespace.Get(bNs);
        var sources = new System.Xml.Linq.XElement(xBns + "Sources",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns + "b", bNs),
            new System.Xml.Linq.XAttribute("SelectedStyle", "\\APA.XSL"),
            new System.Xml.Linq.XAttribute("StyleName", "APA"));

        if (string.IsNullOrWhiteSpace(node.InnerContent)) return;
        var blocks = node.InnerContent.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var parsedTags = new List<string>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
            if (lines.Length == 0 || !lines[0].TrimStart().StartsWith("@")) continue;
            var tag = lines[0].TrimStart().TrimStart('@').Trim();
            parsedTags.Add(tag);

            var source = new System.Xml.Linq.XElement(xBns + "Source");
            source.Add(new System.Xml.Linq.XElement(xBns + "Tag", tag));
            source.Add(new System.Xml.Linq.XElement(xBns + "SourceType", "JournalArticle"));

            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim().ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "author":
                        source.Add(new System.Xml.Linq.XElement(xBns + "Author",
                            new System.Xml.Linq.XElement(xBns + "Author",
                                new System.Xml.Linq.XElement(xBns + "NameList",
                                    new System.Xml.Linq.XElement(xBns + "Person",
                                        new System.Xml.Linq.XElement(xBns + "Last", value))))));
                        break;
                    case "title":
                        source.Add(new System.Xml.Linq.XElement(xBns + "Title", value));
                        break;
                    case "year":
                        source.Add(new System.Xml.Linq.XElement(xBns + "Year", value));
                        break;
                    case "journal":
                        source.Add(new System.Xml.Linq.XElement(xBns + "JournalName", value));
                        break;
                }
            }
            sources.Add(source);
        }

        var customPart = ctx.MainPart.AddCustomXmlPart(CustomXmlPartType.CustomXml);
        using (var stream = customPart.GetStream(System.IO.FileMode.Create, System.IO.FileAccess.Write))
        using (var writer = new System.IO.StreamWriter(stream))
            writer.Write(sources.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));

        var heading = new W.Paragraph(new W.ParagraphProperties(
            new W.ParagraphStyleId { Val = "Heading2" },
            new W.SpacingBetweenLines { Before = "240", After = "120" }));
        AddText(heading, "📚 Bibliography", new Fmt { Bold = true });
        target.Append(heading);

        var bibPara = new W.Paragraph();
        bibPara.Append(
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin }),
            new W.Run(new W.FieldCode(" BIBLIOGRAPHY \\l 1033 ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            new W.Run(new W.Text($"[{parsedTags.Count} sources — update fields to render bibliography]")),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End }));
        target.Append(bibPara);
    }

    /// <summary>
    /// Renders :::embed as a styled hyperlink panel with provider badge and optional caption.
    /// </summary>
    private static void RenderEmbed(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var src = node.Attributes.GetValueOrDefault("src", "");
        var caption = node.InnerContent?.Trim();
        var provider = node.Attributes.GetValueOrDefault("provider", "video");

        if (string.IsNullOrWhiteSpace(src))
        {
            var errP = new W.Paragraph();
            AddText(errP, "[Embed Error: No src URL provided]", new Fmt { Bold = true, Color = "d73a49" });
            target.Append(errP);
            return;
        }

        var rel = ctx.MainPart.AddHyperlinkRelationship(new Uri(src), true);
        var providerIcon = provider.ToLowerInvariant() switch
        {
            "youtube" => "🎬",
            "vimeo" => "🎥",
            "loom" => "📹",
            "figma" => "🎨",
            _ => "🔗"
        };

        var linkPara = new W.Paragraph(new W.ParagraphProperties(
            new W.Justification { Val = W.JustificationValues.Center },
            new W.SpacingBetweenLines { Before = "200", After = "60" },
            new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.SecondaryHex },
            new W.ParagraphBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = ctx.BorderHex })));

        var hyperlink = new W.Hyperlink { Id = rel.Id };
        hyperlink.Append(new W.Run(
            new W.RunProperties(
                new W.RunStyle { Val = "Hyperlink" },
                new W.Color { Val = ctx.LinkColor },
                new W.Underline { Val = W.UnderlineValues.Single },
                new W.Bold()),
            new W.Text($"{providerIcon} {provider.ToUpperInvariant()}: {src}") { Space = SpaceProcessingModeValues.Preserve }));
        linkPara.Append(hyperlink);
        target.Append(linkPara);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            var captionPara = new W.Paragraph(new W.ParagraphProperties(
                new W.Justification { Val = W.JustificationValues.Center },
                new W.SpacingBetweenLines { After = "120" }));
            AddText(captionPara, caption, new Fmt { Italic = true });
            target.Append(captionPara);
        }
    }

    private static void RenderList(ListBlock list, OpenXmlCompositeElement target, Ctx ctx, int parentLevel)
    {
        var level = Math.Min(parentLevel + 1, 8);
        // Bullets share one numbering instance; every ordered list gets its own so numbering
        // restarts per list (same behavior as HTML <ol>).
        var numId = list.IsOrdered
            ? NewOrderedInstance(ctx, int.TryParse(list.OrderedStart, out var s) ? s : 1, level)
            : 1;

        foreach (var itemBlock in list)
        {
            if (itemBlock is not ListItemBlock item) continue;
            var first = true;
            foreach (var child in item)
            {
                if (child is ParagraphBlock pb)
                {
                    var pPr = new W.ParagraphProperties();
                    
                    if (first)
                    {
                        pPr.NumberingProperties = new W.NumberingProperties(
                            new W.NumberingLevelReference { Val = level },
                            new W.NumberingId { Val = numId });
                        // contextualSpacing: suppress inter-paragraph spacing between siblings of
                        // the same list, the way Word's own List Paragraph style does.
                        pPr.ContextualSpacing = new W.ContextualSpacing();
                    }
                    else
                    {
                        pPr.Indentation = new W.Indentation { Left = ((level + 1) * 720).ToString() };
                    }
                    var para = new W.Paragraph(pPr);
                    RenderInlines(para, pb.Inline, ctx, default);
                    target.Append(para);
                    first = false;
                }
                else if (child is ListBlock nested)
                {
                    RenderBlock(nested, target, ctx, level);
                }
                else
                {
                    RenderBlock(child, target, ctx, -1);
                }
            }
        }
    }

    private static int NewOrderedInstance(Ctx ctx, int start, int level)
    {
        var id = ctx.NextNumId++;
        ctx.Numbering.Append(new W.NumberingInstance(
            new W.AbstractNumId { Val = 1 },
            new W.LevelOverride(new W.StartOverrideNumberingValue { Val = start }) { LevelIndex = level })
        { NumberID = id });
        return id;
    }

    // GitHub alert -> single-cell 100%-width table: colored left border + theme secondary fill,
    // bold colored "{icon} {KIND}" title line. Mirrors the HTML tables the Python app fed pandoc.
    private static void RenderAlert(AlertBlock alert, OpenXmlCompositeElement target, Ctx ctx)
    {
        var kind = alert.Kind.ToString();
        var (accent, icon) = ctx.Alerts.TryGetValue(kind, out var style) ? style : ctx.Alerts["note"];
        var accentHex = Hex(accent);

        // Tint the panel ~16% of the alert accent over the page background so a NOTE reads as a soft
        // blue card, a WARNING as amber, etc. — GitHub-style — instead of blending into the page.
        var panelFill = BlendHex(ctx.Theme.Background, accentHex, 0.16);

        var cell = new W.TableCell(new W.TableCellProperties(
            new W.TableCellWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
            new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = panelFill },
            new W.TableCellMargin(
                new W.TopMargin { Width = "100", Type = W.TableWidthUnitValues.Dxa },
                new W.LeftMargin { Width = "160", Type = W.TableWidthUnitValues.Dxa },
                new W.BottomMargin { Width = "100", Type = W.TableWidthUnitValues.Dxa },
                new W.RightMargin { Width = "160", Type = W.TableWidthUnitValues.Dxa })));

        var title = new W.Paragraph();
        var textGlyph = kind.ToLowerInvariant() switch
        {
            "note" => "ℹ️", "tip" => "💡", "important" => "📌", "warning" => "⚠️", "caution" => "🛑", _ => "ℹ️",
        };
        var titleText = $"{(ctx.NoEmoji ? textGlyph : icon)} {kind.ToUpperInvariant()}";
        var titleColor = ContrastGuard.EnsureLegibleText(accentHex, panelFill);
        AddText(title, titleText, new Fmt { Bold = true, Color = titleColor });
        cell.Append(title);

        foreach (var child in alert)
            RenderBlock(child, cell, ctx, -1);
        if (cell.LastChild is not W.Paragraph)
            cell.Append(new W.Paragraph());

        // The cell background is the tinted panel — force high-contrast text color against panelFill.
        foreach (var run in cell.Descendants<W.Run>())
        {
            run.RunProperties ??= new W.RunProperties();
            var existingColor = run.RunProperties.Color?.Val?.Value ?? ctx.Theme.Text.TrimStart('#');
            var newColorHex = ContrastGuard.EnsureLegibleText(existingColor, panelFill);
            if (run.RunProperties.Color != null)
            {
                run.RunProperties.Color.Val = newColorHex;
            }
            else
            {
                var newColor = new W.Color { Val = newColorHex };
                if (run.RunProperties.GetFirstChild<DocumentFormat.OpenXml.AlternateContent>() is { } alt)
                    run.RunProperties.InsertBefore(newColor, alt);
                else
                    run.RunProperties.Append(newColor);
            }
        }

        target.Append(new W.Table(
            new W.TableProperties(
                new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.None },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 30, Color = accentHex },
                    new W.BottomBorder { Val = W.BorderValues.None },
                    new W.RightBorder { Val = W.BorderValues.None },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.None },
                    new W.InsideVerticalBorder { Val = W.BorderValues.None })),
            new W.TableGrid(new W.GridColumn()),
            new W.TableRow(new W.TableRowProperties(new W.CantSplit()), cell)));
    }

    // ---------------------------------------------------------------- raw HTML blocks

    private static readonly Regex HtmlTagStrip = new("<[^>]+>");
    private static readonly Regex HtmlTableRow = new(@"<tr\b[^>]*>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HtmlTableCell = new(@"<(t[hd])\b[^>]*>(.*?)</t[hd]>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly System.Net.Http.HttpClient SharedImageHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static byte[]? FetchImageBytes(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return null;
        try
        {
            // 1. Data URI (e.g. data:image/png;base64,... or data:image/svg+xml;utf8,...)
            if (rawUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIdx = rawUrl.IndexOf(',');
                if (commaIdx < 0) return null;
                var header = rawUrl[..commaIdx];
                var payload = rawUrl[(commaIdx + 1)..];

                if (header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
                {
                    return Convert.FromBase64String(payload);
                }
                else
                {
                    var unescaped = Uri.UnescapeDataString(payload);
                    return System.Text.Encoding.UTF8.GetBytes(unescaped);
                }
            }

            // 2. HTTP / HTTPS Remote URL
            if (rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, rawUrl);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Marksmith/1.0");
                using var response = SharedImageHttpClient.Send(request);
                if (!response.IsSuccessStatusCode) return null;
                using var ms = new MemoryStream();
                response.Content.ReadAsStream().CopyTo(ms);
                return ms.ToArray();
            }

            // 3. Local File
            var path = rawUrl.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? rawUrl[8..].Replace('/', '\\')
                : (rawUrl.Length > 2 && rawUrl[1] == ':' ? rawUrl : null);

            if (path is null || !File.Exists(path))
            {
                var relative = rawUrl.Replace('/', '\\');
                var altPath1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative);
                var altPath2 = Path.Combine(Directory.GetCurrentDirectory(), relative);
                if (File.Exists(altPath1)) path = altPath1;
                else if (File.Exists(altPath2)) path = altPath2;
                else return null;
            }

            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    // Embed local files, remote URLs (PNG/JPG/SVG/WEBP), and base64 Data URIs as real embedded Word pictures.
    private static bool TryEmbedImage(OpenXmlCompositeElement target, LinkInline link, Ctx ctx)
    {
        try
        {
            var rawUrl = Uri.UnescapeDataString(link.GetDynamicUrl?.Invoke() ?? link.Url ?? "");
            var rawBytes = FetchImageBytes(rawUrl);
            if (rawBytes is null || rawBytes.Length == 0) return false;

            var alt = GetPlainText(link);
            double? hintW = null;
            var hint = Regex.Match(alt, @"\|(\d{2,4})$");
            if (hint.Success) { hintW = double.Parse(hint.Groups[1].Value); alt = alt[..hint.Index].Trim(); }

            // Check if payload or URL represents an SVG
            bool isSvg = rawUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                         rawUrl.Contains("image/svg", StringComparison.OrdinalIgnoreCase);

            string? svgString = null;
            if (isSvg)
            {
                try { svgString = System.Text.Encoding.UTF8.GetString(rawBytes); } catch { }
            }
            else if (rawBytes.Length > 4)
            {
                var head = System.Text.Encoding.UTF8.GetString(rawBytes, 0, Math.Min(rawBytes.Length, 120)).TrimStart();
                if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                    (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg")))
                {
                    isSvg = true;
                    svgString = System.Text.Encoding.UTF8.GetString(rawBytes);
                }
            }

            if (isSvg && !string.IsNullOrWhiteSpace(svgString))
            {
                var pngBytes = SvgRasterizer.ToPng(svgString);
                if (pngBytes is not null)
                {
                    var (svgW, svgH) = SvgRasterizer.Dimensions(svgString);
                    var pngPart = ctx.MainPart.AddImagePart(ImagePartType.Png);
                    using (var ms = new MemoryStream(pngBytes)) pngPart.FeedData(ms);
                    var pngRel = ctx.MainPart.GetIdOfPart(pngPart);

                    var svgPart = ctx.MainPart.AddNewPart<ImagePart>("image/svg+xml", null);
                    using (var ms = new MemoryStream(rawBytes)) svgPart.FeedData(ms);
                    var svgRel = ctx.MainPart.GetIdOfPart(svgPart);

                    double svgPtW = hintW ?? Math.Max(40, svgW * 0.75), svgPtH = Math.Max(20, svgH * 0.75 * (hintW is { } sHw ? sHw / (svgW > 0 ? svgW : 1) : 1));
                    if (svgPtW > 460) { svgPtH *= 460 / svgPtW; svgPtW = 460; }
                    long svgCx = (long)(svgPtW * 12700), svgCy = (long)(svgPtH * 12700);
                    var svgId = ctx.NextDrawingId++;
                    var svgAltEsc = System.Security.SecurityElement.Escape(alt) ?? "";

                    const string dpiExtUri = "{28A0092B-C50C-407E-A947-70E740481C1C}";
                    const string svgExtUri = "{96DAC541-7B7A-43D3-8B76-37F632A7BC16}";

                    target.Append(new W.Run(new W.Drawing
                    {
                        InnerXml = $"""
                            <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                              <wp:extent cx="{svgCx}" cy="{svgCy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>
                              <wp:docPr id="{svgId}" name="Image" descr="{svgAltEsc}"/>
                              <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
                              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                                <pic:pic>
                                  <pic:nvPicPr><pic:cNvPr id="{svgId}" name="image.svg"/><pic:cNvPicPr/></pic:nvPicPr>
                                  <pic:blipFill>
                                    <a:blip r:embed="{pngRel}">
                                      <a:extLst>
                                        <a:ext uri="{dpiExtUri}"><a14:useLocalDpi xmlns:a14="http://schemas.microsoft.com/office/drawing/2010/main" val="0"/></a:ext>
                                        <a:ext uri="{svgExtUri}"><asvg:svgBlip xmlns:asvg="http://schemas.microsoft.com/office/drawing/2016/SVG/main" r:embed="{svgRel}"/></a:ext>
                                      </a:extLst>
                                    </a:blip>
                                    <a:stretch><a:fillRect/></a:stretch>
                                  </pic:blipFill>
                                  <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{svgCx}" cy="{svgCy}"/></a:xfrm>
                                  <a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                                </pic:pic>
                              </a:graphicData></a:graphic>
                            </wp:inline>
                            """
                    }));
                    return true;
                }
            }

            // Bitmap decoding (PNG, JPG, WEBP, GIF, BMP)
            using var bitmap = SkiaSharp.SKBitmap.Decode(rawBytes);
            if (bitmap is null) return false;

            byte[] png;
            int pxW = bitmap.Width, pxH = bitmap.Height;
            const int maxDim = 1400;
            if (Math.Max(pxW, pxH) > maxDim)
            {
                var scale = (double)maxDim / Math.Max(pxW, pxH);
                pxW = (int)(pxW * scale); pxH = (int)(pxH * scale);
                using var resized = bitmap.Resize(new SkiaSharp.SKImageInfo(pxW, pxH), SkiaSharp.SKFilterQuality.High);
                using var img = SkiaSharp.SKImage.FromBitmap(resized);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                png = data.ToArray();
            }
            else
            {
                using var img = SkiaSharp.SKImage.FromBitmap(bitmap);
                using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
                png = data.ToArray();
            }

            var part = ctx.MainPart.AddImagePart(ImagePartType.Png);
            using (var ms = new MemoryStream(png)) part.FeedData(ms);
            var relId = ctx.MainPart.GetIdOfPart(part);

            double ptW = hintW ?? pxW * 0.75, ptH = pxH * 0.75 * (hintW is { } hw ? hw / pxW : 1);
            if (ptW > 460) { ptH *= 460 / ptW; ptW = 460; }
            long cx = (long)(ptW * 12700), cy = (long)(Math.Max(8, ptH) * 12700);
            var id = ctx.NextDrawingId++;
            var altEsc = System.Security.SecurityElement.Escape(alt) ?? "";

            target.Append(new W.Run(new W.Drawing
            {
                InnerXml = $"""
                    <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <wp:extent cx="{cx}" cy="{cy}"/><wp:effectExtent l="0" t="0" r="0" b="0"/>
                      <wp:docPr id="{id}" name="Image" descr="{altEsc}"/>
                      <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>
                      <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                        <pic:pic><pic:nvPicPr><pic:cNvPr id="{id}" name="image.png"/><pic:cNvPicPr/></pic:nvPicPr>
                        <pic:blipFill><a:blip r:embed="{relId}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                        <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>
                        </pic:pic></a:graphicData></a:graphic>
                    </wp:inline>
                    """
            }));
            return true;
        }
        catch { return false; }
    }

    private static string StripHtmlToText(string html)
    {
        // script/style CONTENT is code, not prose — drop it whole, or "alert(1)" from a pasted
        // <script> block would land in the document as body text.
        html = Regex.Replace(html, @"<(script|style)\b[^>]*>[\s\S]*?</\1\s*>", "", RegexOptions.IgnoreCase);
        // <br> → space so lines don't glue together; then drop all remaining tags and decode entities.
        var brToSpace = Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        var noTags = HtmlTagStrip.Replace(brToSpace, "");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static void RenderHtmlBlock(string raw, OpenXmlCompositeElement target, Ctx ctx)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return;
        
        var advancedMatch = Regex.Match(raw, @"^<!-- MARKSMITH_FEATURE:(?<id>[a-f0-9\-]+) -->$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (advancedMatch.Success)
        {
            var id = advancedMatch.Groups["id"].Value;
            if (ctx.AdvancedFeatures.TryGetValue(id, out var featureNode))
            {
                RenderAdvancedFeature(featureNode, target, ctx);
                return;
            }
        }

        // Page break (from DialectNormalizer's <!-- pagebreak -->/\pagebreak rewrite, or a raw
        // page-break-after div an AI emitted directly) → a REAL Word page break, the one thing
        // Word does better than any web renderer.
        if (raw.Contains("class=\"page-break\"", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(raw, @"^<div[^>]*page-break-after[^>]*>\s*(</div>)?$", RegexOptions.IgnoreCase))
        {
            target.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
            return;
        }

        // Code-fence filename captions and tab labels (DialectNormalizer output): styled one-liners.
        var labeled = Regex.Match(raw, "^<div class=\"(code-title|tab-label)\">(.*?)</div>$", RegexOptions.Singleline);
        if (labeled.Success)
        {
            var p = new W.Paragraph();
            AddText(p, System.Net.WebUtility.HtmlDecode(labeled.Groups[2].Value),
                new Fmt { Bold = true, Code = labeled.Groups[1].Value == "code-title" });
            target.Append(p);
            return;
        }

        // <hr> → a bordered rule paragraph, same look as a Markdown --- thematic break. NOT an
        // exact whole-block match: without a blank line after it, Markdig's HTML block swallows the
        // following text line into the SAME block ("<hr>\nBelow the rule."), so an exact match
        // missed the tag and the rule silently vanished while the text survived via the catch-all.
        // Split on every <hr> and render the segments around it instead.
        if (Regex.IsMatch(raw, @"<hr\s*/?>", RegexOptions.IgnoreCase))
        {
            var segments = Regex.Split(raw, @"<hr\s*/?>", RegexOptions.IgnoreCase);
            for (int i = 0; i < segments.Length; i++)
            {
                if (i > 0) // a rule between (and after) segments — one per <hr> that split them
                    target.Append(new W.Paragraph(new W.ParagraphProperties(new W.ParagraphBorders(
                        new W.BottomBorder { Val = W.BorderValues.Wave, Size = 6, Space = 1, Color = ctx.BorderHex }))));
                var segText = StripHtmlToText(segments[i]);
                if (segText.Length > 0) { var sp = new W.Paragraph(); AddText(sp, segText, default); target.Append(sp); }
            }
            return;
        }

        // <table> → a real Word table (the biggest data-loss fix: whole tables used to disappear).
        var table = Regex.Match(raw, @"<table\b.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (table.Success) { RenderHtmlTable(table.Value, target, ctx); return; }

        // Foldable-callout <details> whose body was split into following markdown blocks (blank
        // lines between <summary> and the body — see AdmonitionNormalizer). The opener arrives as a
        // block with no closing tag; render its summary as a Word-native collapsible heading
        // (outlineLvl 4 + collapsed) so the body blocks that follow fold under it, matching the
        // preview's collapsed-<details>. The bare </details> closer that arrives later is dropped.
        var openDetails = Regex.Match(raw, @"^<details\b[^>]*>\s*<summary\b[^>]*>(.*?)</summary>\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (openDetails.Success)
        {
            var head = new W.Paragraph(new W.ParagraphProperties(
                new W.OutlineLevel { Val = 8 }, new W15.DefaultCollapsed { Val = true }));
            AddText(head, StripHtmlToText(openDetails.Groups[1].Value), new Fmt { Bold = true, Color = ctx.TextHex });
            target.Append(head);
            return;
        }
        if (Regex.IsMatch(raw, @"^</details>$", RegexOptions.IgnoreCase)) return;

        // <details><summary>…</summary>…</details> → a GENUINELY collapsible section: the summary
        // paragraph carries an outline level (which is all Word needs to draw its native ▸ collapse
        // triangle and fold the following body under it) plus w15:collapsed so it starts folded,
        // matching <details>'s default-closed semantics. Outline level 8 keeps it out of the
        // document's TOC field (which collects levels 1–3 only) so a summary line never pollutes
        // the table of contents. No literal "▸" glyph — Word draws its own toggle, and a fake one
        // next to the real one reads as a control that does nothing.
        var details = Regex.Match(raw, @"<details\b[^>]*>(.*?)</details>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (details.Success)
        {
            var inner = details.Groups[1].Value;
            var summary = Regex.Match(inner, @"<summary\b[^>]*>(.*?)</summary>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var summaryText = summary.Success ? StripHtmlToText(summary.Groups[1].Value) : "Details";
            var body = summary.Success ? inner.Remove(summary.Index, summary.Length) : inner;

            var head = new W.Paragraph(new W.ParagraphProperties(
                new W.OutlineLevel { Val = 8 },
                new W15.DefaultCollapsed { Val = true }));
            AddText(head, summaryText, new Fmt { Bold = true, Color = ctx.TextHex });
            target.Append(head);

            var nestedTable = Regex.Match(body, @"<table\b.*?</table>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (nestedTable.Success) { RenderHtmlTable(nestedTable.Value, target, ctx); return; }
            if (body.Contains("<details", StringComparison.OrdinalIgnoreCase))
            {
                var innerDoc = Markdig.Markdown.Parse(body, ctx.NoEmoji ? PipelineNoEmoji : Pipeline);
                foreach (var block in innerDoc) RenderBlock(block, target, ctx, listLevel: -1);
                return;
            }
            var bodyText = StripHtmlToText(body);
            if (bodyText.Length > 0) { var bp = new W.Paragraph(); AddText(bp, bodyText, default); target.Append(bp); }
            return;
        }

        // Catch-all: never lose the content — strip tags and emit the remaining text.
        var text = StripHtmlToText(raw);
        if (text.Length > 0) { var p = new W.Paragraph(); AddText(p, text, default); target.Append(p); }
    }

    private static void RenderHtmlTable(string html, OpenXmlCompositeElement target, Ctx ctx)
    {
        var grid = new List<(string Text, bool Header)[]>();
        int maxCols = 0;
        foreach (Match r in HtmlTableRow.Matches(html))
        {
            var cells = HtmlTableCell.Matches(r.Groups[1].Value)
                .Select(c => (StripHtmlToText(c.Groups[2].Value), c.Groups[1].Value.Equals("th", StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (cells.Length == 0) continue;
            maxCols = Math.Max(maxCols, cells.Length);
            grid.Add(cells);
        }
        if (grid.Count == 0 || maxCols == 0) return;

        W.BorderType Border<T>() where T : W.BorderType, new() =>
            new T { Val = W.BorderValues.Single, Size = 6, Color = ctx.BorderHex };

        var wTable = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                new W.TableBorders(
                    Border<W.TopBorder>(), Border<W.LeftBorder>(), Border<W.BottomBorder>(),
                    Border<W.RightBorder>(), Border<W.InsideHorizontalBorder>(), Border<W.InsideVerticalBorder>())),
            new W.TableGrid(Enumerable.Range(0, maxCols).Select(_ => (OpenXmlElement)new W.GridColumn())));

        foreach (var row in grid)
        {
            bool headerRow = row.Length > 0 && row.All(c => c.Header);
            var wRow = new W.TableRow(headerRow
                ? new W.TableRowProperties(new W.CantSplit(), new W.TableHeader())
                : new W.TableRowProperties(new W.CantSplit()));

            for (int c = 0; c < maxCols; c++)
            {
                var (text, isHeaderCell) = c < row.Length ? row[c] : ("", false);
                bool shaded = headerRow || isHeaderCell;
                var wCell = new W.TableCell();
                if (shaded)
                    wCell.Append(new W.TableCellProperties(new W.Shading
                    { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.CodeHex }));
                var p = new W.Paragraph();
                AddText(p, text, new Fmt { Bold = shaded });
                wCell.Append(p);
                wRow.Append(wCell);
            }
            wTable.Append(wRow);
        }
        target.Append(wTable);
    }

    private static void RenderTable(MdTable table, OpenXmlCompositeElement target, Ctx ctx)
    {
        W.BorderType Border<T>() where T : W.BorderType, new() =>
            new T { Val = W.BorderValues.Single, Size = 6, Color = ctx.BorderHex };

        var tblPr = new W.TableProperties();
        tblPr.TableWidth = new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" };
        tblPr.TableBorders = new W.TableBorders(
            Border<W.TopBorder>(), Border<W.LeftBorder>(), Border<W.BottomBorder>(),
            Border<W.RightBorder>(), Border<W.InsideHorizontalBorder>(), Border<W.InsideVerticalBorder>());
        tblPr.TableCaption = new W.TableCaption { Val = "Table exported from Markdown" };
        tblPr.TableDescription = new W.TableDescription { Val = "Table exported from Markdown by MarkSmith" };

        var wTable = new W.Table(
            tblPr,
            new W.TableGrid(table.ColumnDefinitions.Select(_ => (OpenXmlElement)new W.GridColumn())));

        var dataRowIndex = 0;
        foreach (var rowObj in table)
        {
            if (rowObj is not MdTableRow row) continue;
            var wRow = new W.TableRow();
            var banded = false;
            if (row.IsHeader)
            {
                // tblHeader repeats the header row at the top of every page the table spans.
                wRow.Append(new W.TableRowProperties(new W.CantSplit(), new W.TableHeader()));
            }
            else
            {
                wRow.Append(new W.TableRowProperties(new W.CantSplit()));
                banded = ++dataRowIndex % 2 == 0;
            }

            for (var c = 0; c < row.Count; c++)
            {
                var wCell = new W.TableCell();
                if (row.IsHeader)
                    wCell.Append(new W.TableCellProperties(new W.Shading
                    {
                        Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.CodeHex
                    }));
                else if (banded)
                    wCell.Append(new W.TableCellProperties(new W.Shading
                    {
                        Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.SecondaryHex
                    }));

                if (row[c] is MdTableCell mdCell)
                    foreach (var child in mdCell)
                        RenderBlock(child, wCell, ctx, -1);
                if (wCell.LastChild is not W.Paragraph)
                    wCell.Append(new W.Paragraph());

                var align = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                if (align is TableColumnAlign.Center or TableColumnAlign.Right)
                {
                    foreach (var p in wCell.Descendants<W.Paragraph>())
                    {
                        p.ParagraphProperties ??= new W.ParagraphProperties();
                        p.ParagraphProperties.Justification = new W.Justification
                        {
                            Val = align == TableColumnAlign.Center
                                ? W.JustificationValues.Center
                                : W.JustificationValues.Right
                        };
                    }
                }

                var cellBg = (row.IsHeader ? ctx.CodeHex : (banded ? ctx.SecondaryHex : ctx.Theme.Background)).TrimStart('#');
                foreach (var run in wCell.Descendants<W.Run>())
                {
                    run.RunProperties ??= new W.RunProperties();
                    if (row.IsHeader) run.RunProperties.Bold ??= new W.Bold();
                    var curColor = run.RunProperties.Color?.Val?.Value ?? ctx.Theme.Text.TrimStart('#');
                    run.RunProperties.Color = new W.Color { Val = ContrastGuard.EnsureLegibleText(curColor, cellBg) };
                }

                wRow.Append(wCell);
            }
            wTable.Append(wRow);
        }

        target.Append(wTable);
        target.Append(SpacerParagraph());
    }

    // Word renders adjacent tables as one; a tiny paragraph keeps them (and following text) apart.
    private static W.Paragraph SpacerParagraph() => new(new W.ParagraphProperties(
        new W.SpacingBetweenLines { Before = "0", After = "0" },
        new W.ParagraphMarkRunProperties(new W.FontSize { Val = "8" })));

    // Real TOC field over Heading1-3 with hyperlinks (\h) — combined with w:updateFields, Word
    // rebuilds it (page numbers and all) the moment the document opens.
    private static void AppendTocField(W.Body body, Ctx ctx)
    {
        var heading = new W.Paragraph();
        AddText(heading, "Contents", new Fmt { Bold = true, Color = ctx.HeadingHex });
        heading.Descendants<W.RunProperties>().First().FontSize = new W.FontSize { Val = "32" };
        body.Append(heading);

        body.Append(new W.Paragraph(
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Begin, Dirty = true }),
            new W.Run(new W.FieldCode(" TOC \\o \"1-3\" \\h \\z \\u ") { Space = SpaceProcessingModeValues.Preserve }),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.Separate }),
            new W.Run(new W.Text("Table of contents — Word fills this in when the document opens.")),
            new W.Run(new W.FieldChar { FieldCharType = W.FieldCharValues.End })));
    }

    // ---------------------------------------------------------------- inlines

    private static void RenderInlines(OpenXmlCompositeElement target, ContainerInline? container, Ctx ctx, Fmt fmt)
    {
        if (container is null) return;

        // `current` starts as the inherited format and is mutated by raw inline HTML tags
        // (<sub>…</sub>, <mark>…</mark>, <span style="color:…">…</span>, etc.). Unlike Markdown
        // emphasis — which Markdig nests into EmphasisInline containers — raw HTML tags arrive as
        // FLAT siblings (H, <sub>, 2, </sub>, O), so the formatting they imply has to be tracked
        // statefully across the sibling loop rather than by recursion. `htmlFmtStack` snapshots the
        // format on each opening tag so the matching close restores it, which keeps nested tags
        // (<mark><sub>x</sub></mark>) and even mismatched/unclosed tags from corrupting later runs.
        var current = fmt;
        var htmlFmtStack = new Stack<Fmt>();

        foreach (var inline in container)
        {
            switch (inline)
            {
                case Markdig.Extensions.Emoji.EmojiInline emoji:
                    if (!ctx.NoEmoji) AddText(target, emoji.Content.ToString(), current, ctx);
                    break;

                case LiteralInline literal:
                    AddText(target, literal.Content.ToString(), current, ctx);
                    break;

                case EmphasisInline em:
                    RenderInlines(target, em, ctx, ApplyEmphasis(current, em));
                    break;

                case CodeInline code:
                    AddText(target, code.Content, current with { Code = true }, ctx);
                    break;

                case LineBreakInline br:
                    if (br.IsHard) target.Append(new W.Run(new W.Break()));
                    else AddText(target, " ", current, ctx);
                    break;

                case LinkInline link:
                    RenderLink(target, link, ctx, current);
                    break;

                case AutolinkInline auto:
                {
                    var relId = TryAddHyperlinkRel(ctx, auto.IsEmail ? $"mailto:{auto.Url}" : auto.Url);
                    if (relId is not null)
                    {
                        var hl = new W.Hyperlink { Id = relId };
                        AddText(hl, auto.Url, current with { Color = ctx.LinkColor, Underline = true }, ctx);
                        target.Append(hl);
                    }
                    else
                    {
                        AddText(target, auto.Url, current, ctx);
                    }
                    break;
                }

                case TaskList task:
                    // - [ ] / - [x] → a NATIVE Word checkbox content control (w14:checkbox), the
                    // same control Word's own Developer ribbon inserts — clickable in Word 2010+,
                    // toggling ☒/☐ in place. Uses Segoe UI Symbol (native on 100% of Windows/Office installs)
                    // so the checkbox renders immediately without initial font-cache glitching.
                    target.Append(new W.SdtRun(
                        new W.SdtProperties(
                            new W14.SdtContentCheckBox(
                                new W14.Checked { Val = task.Checked ? W14.OnOffValues.One : W14.OnOffValues.Zero },
                                new W14.CheckedState { Val = "2612", Font = "Segoe UI Symbol" },
                                new W14.UncheckedState { Val = "2610", Font = "Segoe UI Symbol" })),
                        new W.SdtContentRun(
                            new W.Run(
                                new W.RunProperties(new W.RunFonts { Ascii = "Segoe UI Symbol", HighAnsi = "Segoe UI Symbol", EastAsia = "Segoe UI Symbol", ComplexScript = "Segoe UI Symbol" }),
                                new W.Text(task.Checked ? "\u2612" : "\u2610")))));
                    break;

                case MathInline math:
                    // Inline math → an editable Word equation (OMML) dropped into the run flow.
                    target.Append(LatexToOmml.Build(math.Content.ToString()));
                    break;

                case FootnoteLink fn:
                    AddText(target, $"[{fn.Index}]", current with { Superscript = true }, ctx);
                    break;

                case HtmlEntityInline entity:
                    AddText(target, entity.Transcoded.ToString(), current, ctx);
                    break;

                case HtmlInline html:
                    // Raw inline HTML AI models emit constantly and that renders fine in the PDF
                    // (browser handles it) but used to be dropped here, silently un-formatting the
                    // text between the tags in Word. Now mapped to the equivalent run properties.
                    ApplyHtmlInlineTag(html.Tag, target, ref current, htmlFmtStack, ctx);
                    break;

                case ContainerInline nestedContainer:
                    RenderInlines(target, nestedContainer, ctx, current);
                    break;
            }
        }
    }

    // Basic CSS/HTML color names → 6-hex (Word wants RRGGBB, no leading '#'). Covers the palette AI
    // models actually reach for when they inline a colored <span>/<font>; anything unrecognized is
    // left alone (the text still renders, just in the default color) rather than guessed at.
    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "000000", ["white"] = "FFFFFF", ["red"] = "FF0000", ["green"] = "008000",
        ["blue"] = "0000FF", ["yellow"] = "FFFF00", ["orange"] = "FFA500", ["purple"] = "800080",
        ["gray"] = "808080", ["grey"] = "808080", ["silver"] = "C0C0C0", ["maroon"] = "800000",
        ["olive"] = "808000", ["lime"] = "00FF00", ["teal"] = "008080", ["navy"] = "000080",
        ["aqua"] = "00FFFF", ["cyan"] = "00FFFF", ["magenta"] = "FF00FF", ["fuchsia"] = "FF00FF",
        ["pink"] = "FFC0CB", ["brown"] = "A52A2A", ["gold"] = "FFD700", ["darkred"] = "8B0000",
        ["darkgreen"] = "006400", ["darkblue"] = "00008B", ["indigo"] = "4B0082", ["violet"] = "EE82EE",
    };

    private static W.UnderlineValues? ParseUnderlineStyle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        return s switch
        {
            "single" => W.UnderlineValues.Single,
            "words" => W.UnderlineValues.Words,
            "double" => W.UnderlineValues.Double,
            "dotted" or "dot" => W.UnderlineValues.Dotted,
            "thick" => W.UnderlineValues.Thick,
            "dash" or "dashed" => W.UnderlineValues.Dash,
            "dotdash" => W.UnderlineValues.DotDash,
            "dotdotdash" => W.UnderlineValues.DotDotDash,
            "wave" or "wavy" => W.UnderlineValues.Wave,
            "dottedheavy" or "heavydotted" => W.UnderlineValues.DottedHeavy,
            "dashedheavy" or "heavydashed" => W.UnderlineValues.DashedHeavy,
            "wavyheavy" or "heavywave" => W.UnderlineValues.WavyHeavy,
            "wavydouble" or "doublewave" => W.UnderlineValues.WavyDouble,
            _ => null
        };
    }

    private static W.HighlightColorValues? ParseHighlightColor(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var c = input.Trim().ToLowerInvariant();
        return c switch
        {
            "yellow" => W.HighlightColorValues.Yellow,
            "green" => W.HighlightColorValues.Green,
            "cyan" => W.HighlightColorValues.Cyan,
            "magenta" => W.HighlightColorValues.Magenta,
            "blue" => W.HighlightColorValues.Blue,
            "red" => W.HighlightColorValues.Red,
            "darkblue" => W.HighlightColorValues.DarkBlue,
            "darkcyan" => W.HighlightColorValues.DarkCyan,
            "darkgreen" => W.HighlightColorValues.DarkGreen,
            "darkmagenta" => W.HighlightColorValues.DarkMagenta,
            "darkred" => W.HighlightColorValues.DarkRed,
            "darkyellow" => W.HighlightColorValues.DarkYellow,
            "darkgray" or "darkgrey" => W.HighlightColorValues.DarkGray,
            "lightgray" or "lightgrey" => W.HighlightColorValues.LightGray,
            "black" => W.HighlightColorValues.Black,
            "white" => W.HighlightColorValues.White,
            _ => null
        };
    }

    private static string? ExtractAttribute(string tag, string attrName)
    {
        var m = Regex.Match(tag, $@"{attrName}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var val = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()).Trim('"', '\'').Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        var m2 = Regex.Match(tag, $@"{attrName}\s*=\s*([^\s/>]+)", RegexOptions.IgnoreCase);
        if (m2.Success)
        {
            var val = System.Net.WebUtility.HtmlDecode(m2.Groups[1].Value.Trim()).Trim('"', '\'').Trim();
            return string.IsNullOrWhiteSpace(val) ? null : val;
        }
        return null;
    }

    private static string? ExtractRevisionAuthor(string tag) => ExtractAttribute(tag, "author") ?? ExtractAttribute(tag, "by");

    private static DateTime? ExtractRevisionDate(string tag)
    {
        var dateStr = ExtractAttribute(tag, "date");
        if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var dt))
        {
            return dt;
        }
        return null;
    }

    private static W.UnderlineValues? ExtractUnderlineStyle(string tag, string tagName)
    {
        var uVal = ExtractAttribute(tag, "u-val") ?? ExtractAttribute(tag, "u-style");
        if (tagName is "u" or "ins")
        {
            uVal ??= ExtractAttribute(tag, "val") ?? ExtractAttribute(tag, "style-type");
        }
        if (!string.IsNullOrEmpty(uVal))
        {
            var parsed = ParseUnderlineStyle(uVal);
            if (parsed.HasValue) return parsed.Value;
        }

        var styleMatch = Regex.Match(tag, @"text-decoration(?:-style)?\s*[:=]\s*[""']?([^;""'>]+)", RegexOptions.IgnoreCase);
        if (styleMatch.Success)
        {
            var parts = styleMatch.Groups[1].Value.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var parsed = ParseUnderlineStyle(p);
                if (parsed.HasValue) return parsed.Value;
            }
        }
        return null;
    }

    private static string? ExtractUnderlineColor(string tag)
    {
        var colorAttr = ExtractAttribute(tag, "u-color") ?? ExtractAttribute(tag, "underline-color");
        if (!string.IsNullOrEmpty(colorAttr))
        {
            return CleanHexColor(colorAttr);
        }

        var styleMatch = Regex.Match(tag, @"text-decoration(?:-color)?\s*[:=]\s*[""']?([^;""'>]+)", RegexOptions.IgnoreCase);
        if (styleMatch.Success)
        {
            var parts = styleMatch.Groups[1].Value.Split(new[] { ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var hex = CleanHexColor(p);
                if (hex != null) return hex;
            }
        }
        return null;
    }

    private static W.HighlightColorValues? ExtractHighlightColor(string tag, string tagName)
    {
        var hlAttr = ExtractAttribute(tag, "highlight") ?? ExtractAttribute(tag, "hl");
        if (tagName is "mark")
        {
            hlAttr ??= ExtractAttribute(tag, "color") ?? ExtractAttribute(tag, "val");
        }
        if (!string.IsNullOrEmpty(hlAttr))
        {
            var parsed = ParseHighlightColor(hlAttr);
            if (parsed.HasValue) return parsed.Value;
        }

        var bgMatch = Regex.Match(tag, @"(?:background-color|background)\s*[:=]\s*[""']?\s*(#?[0-9a-zA-Z]+)", RegexOptions.IgnoreCase);
        if (bgMatch.Success)
        {
            var val = bgMatch.Groups[1].Value.Trim().TrimStart('#');
            var parsed = ParseHighlightColor(val);
            if (parsed.HasValue) return parsed.Value;
        }

        return null;
    }

    private static string? ExtractShadingColor(string tag)
    {
        var fillAttr = ExtractAttribute(tag, "fill") ?? ExtractAttribute(tag, "bg") ?? ExtractAttribute(tag, "shd");
        if (!string.IsNullOrEmpty(fillAttr))
        {
            var hex = CleanHexColor(fillAttr);
            if (hex != null) return hex;
        }

        var bgMatch = Regex.Match(tag, @"(?:background-color|background)\s*[:=]\s*[""']?\s*(#?[0-9a-zA-Z]+)", RegexOptions.IgnoreCase);
        if (bgMatch.Success)
        {
            var val = bgMatch.Groups[1].Value.Trim();
            var hex = CleanHexColor(val);
            if (hex != null) return hex;
        }

        return null;
    }

    private static string? CleanHexColor(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var c = input.Trim().TrimStart('#');
        if (Regex.IsMatch(c, "^[0-9a-fA-F]{6}$")) return c.ToUpperInvariant();
        if (Regex.IsMatch(c, "^[0-9a-fA-F]{3}$"))
            return string.Concat(c.Select(ch => new string(ch, 2))).ToUpperInvariant();
        return NamedColors.TryGetValue(c, out var hex) ? hex : null;
    }

    // Toggles `current`/`stack` for one raw inline HTML tag. Void tags (<br>) emit immediately;
    // opening tags push and apply; closing tags pop. Unknown tags are still pushed/popped so their
    // (formatting-neutral) presence doesn't unbalance the stack for tags nested inside them.
    private static void ApplyHtmlInlineTag(string rawTag, OpenXmlCompositeElement target, ref Fmt current, Stack<Fmt> stack, Ctx ctx)
    {
        var t = rawTag.Trim();
        if (t.Length < 2 || t[0] != '<') return;

        bool closing = t.StartsWith("</", StringComparison.Ordinal);
        bool selfClosing = t.EndsWith("/>", StringComparison.Ordinal);
        int nameStart = closing ? 2 : 1;
        int nameEnd = nameStart;
        while (nameEnd < t.Length && (char.IsLetterOrDigit(t[nameEnd]))) nameEnd++;
        if (nameEnd == nameStart) return;
        var name = t.Substring(nameStart, nameEnd - nameStart).ToLowerInvariant();

        // Line/word breaks: emit a real Word break, never tracked on the stack.
        if (name is "br")
        {
            var r = new W.Run(new W.Break());
            var rPr = BuildRunProperties(current);
            if (rPr != null && rPr.HasChildren) r.Append(rPr);
            target.Append(r);
            return;
        }
        if (name is "wbr" or "hr") return;

        if (closing)
        {
            if (stack.Count > 0)
            {
                var popped = stack.Pop();
                current = popped with { NoProof = current.NoProof || popped.NoProof };
            }
            return;
        }

        var htmlColor = ExtractHtmlColor(t);
        var uStyle = ExtractUnderlineStyle(t, name);
        var uColor = ExtractUnderlineColor(t);
        var hlColor = ExtractHighlightColor(t, name);
        var shdColor = ExtractShadingColor(t);
        var revAuthor = ExtractRevisionAuthor(t);
        var revDate = ExtractRevisionDate(t);

        var next = name switch
        {
            "sub" => current with { Subscript = true, Superscript = false, Color = htmlColor ?? current.Color },
            "sup" => current with { Superscript = true, Subscript = false, Color = htmlColor ?? current.Color },
            "mark" => current with {
                Highlight = true,
                HighlightColor = hlColor ?? current.HighlightColor,
                ShadingColor = shdColor ?? current.ShadingColor,
                Color = htmlColor ?? current.Color
            },
            "kbd" or "code" or "samp" or "tt" => current with { Code = true, Color = htmlColor ?? current.Color },
            "u" => current with {
                Underline = true,
                UnderlineStyle = uStyle ?? current.UnderlineStyle,
                UnderlineColor = uColor ?? current.UnderlineColor,
                Color = htmlColor ?? current.Color
            },
            "ins" => current with {
                Revision = RevisionKind.Insertion,
                RevisionAuthor = revAuthor ?? current.RevisionAuthor,
                RevisionDate = revDate ?? current.RevisionDate,
                Underline = true,
                UnderlineStyle = uStyle ?? current.UnderlineStyle,
                UnderlineColor = uColor ?? current.UnderlineColor,
                Color = htmlColor ?? current.Color
            },
            "del" or "s" or "strike" => current with {
                Revision = name == "del" ? RevisionKind.Deletion : current.Revision,
                RevisionAuthor = name == "del" ? (revAuthor ?? current.RevisionAuthor) : current.RevisionAuthor,
                RevisionDate = name == "del" ? (revDate ?? current.RevisionDate) : current.RevisionDate,
                Strike = true,
                Color = htmlColor ?? current.Color
            },
            "b" or "strong" => current with { Bold = true, Color = htmlColor ?? current.Color },
            "i" or "em" or "cite" or "var" or "dfn" => current with { Italic = true, Color = htmlColor ?? current.Color },
            "span" when t.Contains("wikilink", StringComparison.OrdinalIgnoreCase) => current with { WikiLink = true, UnderlineDash = true, NoProof = true, Color = ctx.Theme.Primary?.TrimStart('#') },
            "span" when t.Contains("md-tag", StringComparison.OrdinalIgnoreCase) => current with { NoProof = true, Color = ctx.Theme.Primary?.TrimStart('#') },
            _ => current with {
                Color = htmlColor ?? current.Color,
                UnderlineStyle = uStyle ?? current.UnderlineStyle,
                UnderlineColor = uColor ?? current.UnderlineColor,
                HighlightColor = hlColor ?? current.HighlightColor,
                ShadingColor = shdColor ?? current.ShadingColor
            }
        };

        // A self-closing formatting tag (<mark/>) has no content to affect, so it must not leave the
        // format changed for following siblings — only real open/close pairs push and pop.
        if (!selfClosing) { stack.Push(current); current = next; }
    }

    private static string? ExtractHtmlColor(string tag)
    {
        // Matches both style="color: X" and the legacy <font color="X"> attribute form.
        var m = Regex.Match(tag, @"color\s*[:=]\s*[""']?\s*(#?[0-9a-zA-Z]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var c = m.Groups[1].Value.Trim().TrimStart('#');
        if (Regex.IsMatch(c, "^[0-9a-fA-F]{6}$")) return c.ToUpperInvariant();
        if (Regex.IsMatch(c, "^[0-9a-fA-F]{3}$")) // #rgb shorthand → #rrggbb
            return string.Concat(c.Select(ch => new string(ch, 2))).ToUpperInvariant();
        return NamedColors.TryGetValue(c, out var hex) ? hex : null;
    }

    // EmphasisExtras: **bold* /*italic*, ~~strike~~, ~sub~, ^sup^, ==marked==, ++inserted++.
    private static Fmt ApplyEmphasis(Fmt fmt, EmphasisInline em) => em.DelimiterChar switch
    {
        '*' or '_' => em.DelimiterCount >= 2 ? fmt with { Bold = true } : fmt with { Italic = true },
        '~' => em.DelimiterCount >= 2 ? fmt with { Strike = true } : fmt with { Subscript = true },
        '^' => fmt with { Superscript = true },
        '=' => fmt with { Highlight = true },
        '+' => fmt with { Underline = true },
        _ => fmt,
    };

    private static void RenderLink(OpenXmlCompositeElement target, LinkInline link, Ctx ctx, Fmt fmt)
    {
        var url = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";

        if (link.IsImage)
        {
            // Embed local files, remote HTTP/HTTPS URLs (PNG/JPG/SVG/WEBP), and base64 Data URIs as real embedded Word pictures
            if (TryEmbedImage(target, link, ctx)) return;

            var alt = GetPlainText(link);
            var label = string.IsNullOrWhiteSpace(alt) ? "[Image]" : $"[Image: {alt}]";
            var relId = TryAddHyperlinkRel(ctx, url);
            if (relId is not null)
            {
                var hl = new W.Hyperlink { Id = relId };
                AddText(hl, label, fmt with { Italic = true, Color = ctx.LinkColor, Underline = true });
                target.Append(hl);
            }
            else
            {
                AddText(target, label, fmt with { Italic = true, Color = fmt.Color ?? "6A737D" });
            }
            return;
        }

        // #anchor links resolve to the bookmarks written on headings, so they navigate inside Word.
        if (url.StartsWith('#') && ctx.Anchors.TryGetValue(url[1..], out var bookmark))
        {
            var anchorLink = new W.Hyperlink { Anchor = bookmark };
            RenderInlines(anchorLink, link, ctx, fmt with { Color = ctx.LinkColor, Underline = true });
            target.Append(anchorLink);
            return;
        }

        var linkRelId = TryAddHyperlinkRel(ctx, url);
        if (linkRelId is not null)
        {
            var hl = new W.Hyperlink { Id = linkRelId };
            RenderInlines(hl, link, ctx, fmt with { Color = ctx.LinkColor, Underline = true });
            target.Append(hl);
        }
        else
        {
            RenderInlines(target, link, ctx, fmt); // unresolvable anchors / relative / malformed URLs
        }
    }

    private static string? TryAddHyperlinkRel(Ctx ctx, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#')) return null;
        try
        {
            return ctx.MainPart.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true).Id;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static string GetPlainText(ContainerInline container)
    {
        var sb = new StringBuilder();
        Collect(container, sb);
        return sb.ToString();

        static void Collect(ContainerInline c, StringBuilder sb)
        {
            foreach (var inline in c)
            {
                switch (inline)
                {
                    case LiteralInline l: sb.Append(l.Content.ToString()); break;
                    case CodeInline ci: sb.Append(ci.Content); break;
                    case ContainerInline nested: Collect(nested, sb); break;
                }
            }
        }
    }

    // Using [GeneratedRegex] instead of new Regex() inside tight loops or even
    // static fields eliminates repeated parsing/compilation overhead.
    [GeneratedRegex(@"([\u203C-\u3299]|[\uD83C-\uD83E][\uDC00-\uDFFF])")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"<(?:font\s+color\s*=\s*[""']?([^""'>]+)[""']?|span\s+style\s*=\s*[""']?color\s*:\s*([^;""'>]+)[^>]*|/(font|span))>", RegexOptions.IgnoreCase)]
    private static partial Regex CodeColorRegex();

    private static int _globalRevisionId = 0;

    private static void AddText(OpenXmlCompositeElement target, string text, Fmt fmt, Ctx? ctx = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var parts = EmojiRegex().Split(text);
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            var run = new W.Run();
            var rPr = BuildRunProperties(fmt);
            if (EmojiRegex().IsMatch(part))
            {
                rPr ??= new W.RunProperties();
                rPr.RemoveAllChildren<W.Color>();
                rPr.PrependChild(new W.RunFonts { Ascii = "Segoe UI Emoji", HighAnsi = "Segoe UI Emoji", EastAsia = "Segoe UI Emoji", ComplexScript = "Segoe UI Emoji" });
            }
            if (rPr != null && rPr.HasChildren) run.Append(rPr);

            if (fmt.Revision == RevisionKind.Insertion)
            {
                run.Append(new W.Text(part) { Space = SpaceProcessingModeValues.Preserve });
                var revId = fmt.RevisionId > 0
                    ? fmt.RevisionId.ToString()
                    : (ctx != null ? (ctx.NextRevisionId++).ToString() : Interlocked.Increment(ref _globalRevisionId).ToString());
                var rawAuthor = fmt.RevisionAuthor?.Trim('"', '\'').Trim();
                var author = !string.IsNullOrWhiteSpace(rawAuthor)
                    ? rawAuthor
                    : (ctx?.DefaultRevisionAuthor ?? "Marksmith AI");
                var date = fmt.RevisionDate ?? ctx?.DefaultRevisionDate ?? DateTime.UtcNow;

                var ins = new W.InsertedRun
                {
                    Id = revId,
                    Author = author,
                    Date = date
                };
                ins.Append(run);
                target.Append(ins);
            }
            else if (fmt.Revision == RevisionKind.Deletion)
            {
                run.Append(new W.DeletedText(part) { Space = SpaceProcessingModeValues.Preserve });
                var revId = fmt.RevisionId > 0
                    ? fmt.RevisionId.ToString()
                    : (ctx != null ? (ctx.NextRevisionId++).ToString() : Interlocked.Increment(ref _globalRevisionId).ToString());
                var rawAuthor = fmt.RevisionAuthor?.Trim('"', '\'').Trim();
                var author = !string.IsNullOrWhiteSpace(rawAuthor)
                    ? rawAuthor
                    : (ctx?.DefaultRevisionAuthor ?? "Marksmith AI");
                var date = fmt.RevisionDate ?? ctx?.DefaultRevisionDate ?? DateTime.UtcNow;

                var del = new W.DeletedRun
                {
                    Id = revId,
                    Author = author,
                    Date = date
                };
                del.Append(run);
                target.Append(del);
            }
            else
            {
                run.Append(new W.Text(part) { Space = SpaceProcessingModeValues.Preserve });
                target.Append(run);
            }
        }
    }

    private static W.RunProperties BuildRunProperties(Fmt fmt)
    {
        var rPr = new W.RunProperties();
        if (fmt.Code) rPr.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
        if (fmt.Bold) rPr.Append(new W.Bold());
        if (fmt.Italic) rPr.Append(new W.Italic());
        if (fmt.Strike) rPr.Append(new W.Strike());
        if (fmt.Code || fmt.NoProof || fmt.WikiLink) rPr.Append(new W.NoProof()); // disables spellcheck and auto-hyphenation

        // Highlight / Text Color (schema order: w:color -> w:sz -> w:highlight)
        var hlColor = fmt.EffectiveHighlightColor;
        if (hlColor is not null)
        {
            if (hlColor == W.HighlightColorValues.Yellow && fmt.Color is null)
            {
                rPr.Append(new W.Color { Val = "000000" });
            }
            else if (fmt.Color is not null)
            {
                rPr.Append(new W.Color { Val = fmt.Color });
            }
        }
        else if (fmt.Color is not null)
        {
            rPr.Append(new W.Color { Val = fmt.Color });
        }

        if (fmt.Code) rPr.Append(new W.FontSize { Val = "20" }); // 10pt for code

        if (hlColor is not null)
        {
            rPr.Append(new W.Highlight { Val = hlColor.Value });
        }

        // Underline (schema order: w:u)
        var uStyle = fmt.EffectiveUnderlineStyle;
        if (uStyle != W.UnderlineValues.None)
        {
            var u = new W.Underline { Val = uStyle };
            if (!string.IsNullOrEmpty(fmt.UnderlineColor))
            {
                u.Color = fmt.UnderlineColor;
            }
            rPr.Append(u);
        }

        // Shading (schema order: w:bdr -> w:shd)
        if (fmt.Code) // character border + shading: the rarely-touched w:bdr, boxing inline code
        {
            rPr.Append(new W.Border { Val = W.BorderValues.Single, Size = 4, Space = 1, Color = "auto" });
            rPr.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = "auto" });
        }
        else if (!string.IsNullOrEmpty(fmt.ShadingColor))
        {
            rPr.Append(new W.Shading
            {
                Val = W.ShadingPatternValues.Clear,
                Color = "auto",
                Fill = fmt.ShadingColor
            });
        }

        // Vertical text alignment (schema order: w:vertAlign)
        if (fmt.Superscript)
            rPr.Append(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript });
        else if (fmt.Subscript)
            rPr.Append(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Subscript });

        // OpenType typography on every text run (the only rPr the w14 extensions are schema-legal
        // in): kerned ligatures, old-style proportional numerals, contextual alternates.
        if (!fmt.Code)
        {
            rPr.Append(new AlternateContent(
                new AlternateContentChoice(
                    new W14.Ligatures { Val = W14.LigaturesValues.StandardContextual },
                    new W14.NumberingFormat { Val = W14.NumberFormValues.OldStyle },
                    new W14.NumberSpacing { Val = W14.NumberSpacingValues.Proportional },
                    new W14.ContextualAlternatives()
                ) { Requires = "w14" }));
        }
        return rPr;
    }

    // ------------------------------------------------------- document parts

    private static void AddStyles(MainDocumentPart main, Ctx ctx)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();

        // Doc-wide defaults carry the theme text color and kerning; the w14 OpenType features are
        // stamped per-run in BuildRunProperties (the only place they're schema-legal).
        // Segoe UI Variable is the Windows 11 system UI font — optical sizing keeps small body text
        // crisp and large headings elegant; it reads as a native, premium document rather than the
        // generic Office default. A branding kit can still restyle the whole document.
        var baseFont = ctx.BrandFont ?? "Segoe UI Variable";
        var defaultText = ContrastGuard.EnsureLegibleText(ctx.Theme.Text, ctx.Theme.Background);
        var headingColor = ContrastGuard.EnsureLegibleText(ctx.HeadingHex, ctx.Theme.Background);

        styles.Append(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = baseFont, HighAnsi = baseFont, EastAsia = baseFont, ComplexScript = baseFont },
                new W.Color { Val = defaultText },
                new W.Kern { Val = 16u },
                new W.FontSize { Val = "22" },
                new W.FontSizeComplexScript { Val = "22" })),
            new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                new W.SpacingBetweenLines { After = "160", Line = "259", LineRule = W.LineSpacingRuleValues.Auto }))));

        styles.Append(new W.Style(new W.StyleName { Val = "Normal" })
        {
            Type = W.StyleValues.Paragraph,
            StyleId = "Normal",
            Default = true,
        });

        // Heading color hierarchy: H1 carries the full theme accent; H2/H3 step down a ramp toward
        // the body text (65% / 35% accent), and H4–H6 settle into the body text — so each level has a
        // distinct place in the pecking order instead of every heading shouting in the same accent.
        var h1Color = headingColor;
        var h2Color = ContrastGuard.EnsureLegibleText(BlendHex(defaultText, headingColor, 0.65), ctx.Theme.Background);
        var h3Color = ContrastGuard.EnsureLegibleText(BlendHex(defaultText, headingColor, 0.35), ctx.Theme.Background);
        var h6Color = ContrastGuard.EnsureLegibleText("6A737D", ctx.Theme.Background);
        (string Size, string? Color)[] headings =
        {
            ("40", h1Color), ("32", h2Color), ("28", h3Color),
            ("24", defaultText), ("22", defaultText), ("22", h6Color),
        };
        for (var level = 1; level <= 6; level++)
        {
            var (size, color) = headings[level - 1];
            var pPr = new W.StyleParagraphProperties();
            pPr.Append(new W.KeepNext());
            if (level <= 2)
                pPr.Append(new W.ParagraphBorders(new W.BottomBorder
                {
                    Val = W.BorderValues.Single, Size = 12, Space = 4, Color = ctx.BorderHex
                }));
            pPr.Append(new W.SpacingBetweenLines { Before = "280", After = "140" });
            pPr.Append(new W.OutlineLevel { Val = level - 1 });

            var rPr = new W.StyleRunProperties();
            rPr.Append(new W.Bold());
            if (level == 1)
            {
                // Small caps + letterspacing on H1 — the kind of title treatment people do by hand.
                rPr.Append(new W.SmallCaps());
            }
            if (color is not null) rPr.Append(new W.Color { Val = color });
            if (level == 1) rPr.Append(new W.Spacing { Val = 20 });
            rPr.Append(new W.FontSize { Val = size });
            rPr.Append(new W.FontSizeComplexScript { Val = size });

            styles.Append(new W.Style(
                new W.StyleName { Val = $"heading {level}" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                pPr, rPr)
            {
                Type = W.StyleValues.Paragraph,
                StyleId = $"Heading{level}",
            });
        }

        var legibleLink = ContrastGuard.EnsureLegibleText(ctx.LinkColor, ctx.Theme.Background);
        styles.Append(new W.Style(
            new W.StyleName { Val = "Hyperlink" },
            new W.StyleRunProperties(
                new W.Color { Val = legibleLink },
                new W.Underline { Val = W.UnderlineValues.Single }))
        {
            Type = W.StyleValues.Character,
            StyleId = "Hyperlink",
        });

        for (int i = 1; i <= 3; i++)
        {
            styles.Append(new W.Style(
                new W.StyleName { Val = $"toc {i}" },
                new W.BasedOn { Val = "Normal" },
                new W.StyleRunProperties(new W.Color { Val = defaultText }))
            {
                Type = W.StyleValues.Paragraph,
                StyleId = $"TOC{i}",
            });
        }

        part.Styles = styles;
    }

    private static void AddSettings(MainDocumentPart main, bool updateFieldsOnOpen, bool webLayout, bool trackChanges)
    {
        var part = main.AddNewPart<DocumentSettingsPart>();
        var settings = new W.Settings();
        // "Single continuous page" is a PDF-only layout. Word has no page-less print layout, but Web
        // Layout view is the closest equivalent — one continuous flow with no page breaks (and, like
        // the continuous PDF, not meant for printing). w:view must precede w:zoom in the schema order.
        if (webLayout)
            settings.Append(new W.View { Val = W.ViewValues.Web });
        settings.Append(new W.Zoom { Percent = "110" });
        // Without this Word ignores w:background entirely — the pair is the whole trick.
        settings.Append(new W.DisplayBackgroundShape());
        // Track Changes is opt-in (default off): a converted chat should open clean, not littered
        // with revision markup. (w:trackChanges must precede w:autoHyphenation in the ECMA-376 schema.)
        if (trackChanges)
            settings.Append(new W.TrackRevisions { Val = true });
        settings.Append(new W.AutoHyphenation());
        if (updateFieldsOnOpen)
            settings.Append(new W.UpdateFieldsOnOpen { Val = true }); // TOC rebuilds itself on open
        part.Settings = settings;
    }

    private static W.SectionProperties BuildSectionProperties(
        MainDocumentPart main, Ctx ctx, AppSettings settings, string title)
    {
        var headerPart = main.AddNewPart<HeaderPart>();
        headerPart.Header = new W.Header(new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphBorders(new W.BottomBorder
                {
                    Val = W.BorderValues.Single, Size = 6, Space = 3, Color = ctx.BorderHex
                }),
                new W.SpacingBetweenLines { After = "0" },
                new W.Justification { Val = W.JustificationValues.Right }),
            new W.Run(
                new W.RunProperties(
                    new W.SmallCaps(),
                    new W.Color { Val = ctx.HeadingHex },
                    new W.Spacing { Val = 30 },
                    new W.FontSize { Val = "18" }),
                new W.Text(title) { Space = SpaceProcessingModeValues.Preserve })));

        static W.Run FooterRun(string text) => new(
            new W.RunProperties(new W.Color { Val = "808080" }, new W.FontSize { Val = "16" }),
            new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        static W.SimpleField Field(string instruction) => new(new W.Run(
            new W.RunProperties(new W.Color { Val = "808080" }, new W.FontSize { Val = "16" }),
            new W.Text("1")))
        { Instruction = instruction };

        var footerPart = main.AddNewPart<FooterPart>();
        footerPart.Footer = new W.Footer(new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { After = "0" },
                new W.Justification { Val = W.JustificationValues.Center }),
            FooterRun("Page "), Field(" PAGE "), FooterRun(" of "), Field(" NUMPAGES "),
            FooterRun("  \u00b7  MarkSmith")));

        // A4FixedWidth drives the physical page too, mirroring the PDF export geometry.
        var (width, height) = settings.A4FixedWidth ? (11906u, 16838u) : (12240u, 15840u);
        
        if (ctx.OversizedDiagramMode == 4)
        {
            width = (uint)(width * ctx.DiagramGridSize);
            height = (uint)(height * ctx.DiagramGridSize);
        }

        W.BorderType PageBorder<T>() where T : W.BorderType, new() =>
            new T { Val = W.BorderValues.Single, Size = 8, Space = 24, Color = ctx.BorderHex };

        var sp = new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
            new W.PageSize { Width = width, Height = height },
            new W.PageMargin
            {
                Top = 1440, Right = 1440, Bottom = 1440, Left = 1440,
                Header = 576, Footer = 576, Gutter = 0,
            });

        // The full page frame is opt-in (default off) — a converted document should read like a clean
        // document, not a framed certificate. When enabled it's drawn in the theme border color.
        if (settings.PageBorder)
        {
            sp.Append(new W.PageBorders(
                PageBorder<W.TopBorder>(), PageBorder<W.LeftBorder>(),
                PageBorder<W.BottomBorder>(), PageBorder<W.RightBorder>())
            {
                OffsetFrom = W.PageBorderOffsetValues.Page,
                Display = W.PageBorderDisplayValues.AllPages,
            });
        }
        return sp;
    }
    private static void RenderDatagrid(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var lines = (node.InnerContent ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var table = new W.Table(
            new W.TableProperties(
                new W.TableStyle { Val = "TableGrid" },
                new W.TableWidth { Width = "5000", Type = W.TableWidthUnitValues.Pct },
                new W.TableLook { Val = "04A0", FirstRow = true, LastRow = false, FirstColumn = true, LastColumn = false, NoHorizontalBand = false, NoVerticalBand = true }
            ));
        
        bool isHeader = true;
        foreach (var line in lines)
        {
            var cells = line.Split(new[] { ',', '\t' });
            var row = new W.TableRow();
            foreach (var cellText in cells)
            {
                var tc = new W.TableCell(
                    new W.TableCellProperties(
                        new W.TableCellWidth { Width = "0", Type = W.TableWidthUnitValues.Auto },
                        new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = isHeader ? (ctx.Theme.Primary?.TrimStart('#') ?? "F3F4F6") : "FFFFFF" }
                    ),
                    new W.Paragraph(
                        new W.ParagraphProperties(new W.SpacingBetweenLines { After = "120" }),
                        new W.Run(
                            new W.RunProperties(
                                new W.Color { Val = isHeader ? "FFFFFF" : (ctx.Theme.Text.TrimStart('#') ?? "000000") },
                                new W.Bold { Val = isHeader ? new DocumentFormat.OpenXml.OnOffValue(true) : new DocumentFormat.OpenXml.OnOffValue(false) }
                            ),
                            new W.Text(cellText.Trim())
                        )
                    )
                );
                row.Append(tc);
            }
            table.Append(row);
            isHeader = false;
        }

        target.Append(table);
        target.Append(new W.Paragraph(new W.ParagraphProperties(new W.SpacingBetweenLines { After = "120" })));
    }

    private static void RenderChart(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        var labels = new List<string>();
        var values = new List<double>();
        
        if (node.InnerContent != null && node.InnerContent.TrimStart().StartsWith("{"))
        {
            try 
            {
                using var j = JsonDocument.Parse(node.InnerContent);
                var data = j.RootElement.GetProperty("data");
                foreach (var l in data.GetProperty("labels").EnumerateArray())
                {
                    var s = l.GetString();
                    if (s != null) labels.Add(s);
                }
                foreach (var v in data.GetProperty("values").EnumerateArray()) values.Add(v.GetDouble());
            } 
            catch { }
        }
        else if (node.InnerContent != null)
        {
            var lines = node.InnerContent.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
            bool first = true;
            foreach(var line in lines)
            {
                if (first) { first = false; continue; } // Skip header
                var parts = line.Split(',');
                if (parts.Length >= 2 && double.TryParse(parts[1], out double val))
                {
                    labels.Add(parts[0] ?? "");
                    values.Add(val);
                }
            }
        }

        if (labels.Count == 0 || labels.Count != values.Count) return;

        string chartType = node.Attributes.ContainsKey("type") ? node.Attributes["type"].ToLower() : "bar";

        var chartPart = ctx.MainPart.AddNewPart<ChartPart>();
        string chartRelId = ctx.MainPart.GetIdOfPart(chartPart);
        
        var packagePart = chartPart.AddNewPart<EmbeddedPackagePart>(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        );
        string embedRelId = chartPart.GetIdOfPart(packagePart);

        using (var stream = packagePart.GetStream())
        using (var doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = doc.AddWorkbookPart();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            string sheetRelId = workbookPart.GetIdOfPart(worksheetPart);
            workbookPart.Workbook = new S.Workbook(new S.Sheets(new S.Sheet() { Id = sheetRelId, SheetId = 1, Name = "Sheet1" }));
            
            var sheetData = new S.SheetData();
            worksheetPart.Worksheet = new S.Worksheet(sheetData);

            var header = new S.Row() { RowIndex = 1 };
            header.Append(
                new S.Cell() { CellReference = "A1", DataType = S.CellValues.String, CellValue = new S.CellValue("Category") },
                new S.Cell() { CellReference = "B1", DataType = S.CellValues.String, CellValue = new S.CellValue("Value") }
            );
            sheetData.Append(header);

            for (int i = 0; i < labels.Count; i++)
            {
                uint rowIdx = (uint)(i + 2);
                var row = new S.Row() { RowIndex = rowIdx };
                row.Append(
                    new S.Cell() { CellReference = "A" + rowIdx, DataType = S.CellValues.String, CellValue = new S.CellValue(labels[i] ?? "") },
                    new S.Cell() { CellReference = "B" + rowIdx, DataType = S.CellValues.Number, CellValue = new S.CellValue(values[i].ToString(CultureInfo.InvariantCulture)) }
                );
                sheetData.Append(row);
            }
        }

        var categoryRef = new C.CategoryAxisData();
        var stringRef = new C.StringReference() { Formula = new C.Formula($"Sheet1!$A$2:$A${labels.Count + 1}") };
        var stringCache = new C.StringCache();
        stringCache.Append(new C.PointCount() { Val = (uint)labels.Count });
        for (int i = 0; i < labels.Count; i++)
        {
            stringCache.Append(new C.StringPoint() { Index = (uint)i, NumericValue = new C.NumericValue(labels[i] ?? "") });
        }
        stringRef.Append(stringCache);
        categoryRef.Append(stringRef);

        var valuesRef = new C.Values();
        var numRef = new C.NumberReference() { Formula = new C.Formula($"Sheet1!$B$2:$B${labels.Count + 1}") };
        var numCache = new C.NumberingCache();
        numCache.Append(new C.FormatCode("General"));
        numCache.Append(new C.PointCount() { Val = (uint)labels.Count });
        for (int i = 0; i < values.Count; i++)
        {
            numCache.Append(new C.NumericPoint() { Index = (uint)i, NumericValue = new C.NumericValue(values[i].ToString(CultureInfo.InvariantCulture)) });
        }
        numRef.Append(numCache);
        valuesRef.Append(numRef);

        var chartSpace = new C.ChartSpace();
        var chart = new C.Chart();
        var plotArea = new C.PlotArea();
        
        chart.Append(new C.AutoTitleDeleted() { Val = new DocumentFormat.OpenXml.BooleanValue(true) });

        if (chartType == "line")
        {
            var lineChart = new C.LineChart(new C.Grouping() { Val = C.GroupingValues.Standard });
            var series = new C.LineChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            lineChart.Append(series);
            lineChart.Append(new C.AxisId() { Val = 10000000 });
            lineChart.Append(new C.AxisId() { Val = 10000001 });
            plotArea.Append(lineChart);
        }
        else if (chartType == "pie")
        {
            var pieChart = new C.PieChart();
            var series = new C.PieChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            pieChart.Append(series);
            plotArea.Append(pieChart);
        }
        else
        {
            var barChart = new C.BarChart(
                new C.BarDirection() { Val = C.BarDirectionValues.Column },
                new C.BarGrouping() { Val = C.BarGroupingValues.Clustered }
            );
            var series = new C.BarChartSeries(
                new C.Index() { Val = 0 },
                new C.Order() { Val = 0 },
                (C.CategoryAxisData)categoryRef.CloneNode(true),
                (C.Values)valuesRef.CloneNode(true)
            );
            barChart.Append(series);
            barChart.Append(new C.AxisId() { Val = 10000000 });
            barChart.Append(new C.AxisId() { Val = 10000001 });
            plotArea.Append(barChart);
        }

        if (chartType != "pie")
        {
            plotArea.Append(new C.CategoryAxis(
                new C.AxisId() { Val = 10000000 },
                new C.Scaling(new C.Orientation() { Val = C.OrientationValues.MinMax }),
                new C.AxisPosition() { Val = C.AxisPositionValues.Bottom },
                new C.TickLabelPosition() { Val = C.TickLabelPositionValues.NextTo },
                new C.CrossingAxis() { Val = 10000001 },
                new C.Crosses() { Val = C.CrossesValues.AutoZero }
            ));
            plotArea.Append(new C.ValueAxis(
                new C.AxisId() { Val = 10000001 },
                new C.Scaling(new C.Orientation() { Val = C.OrientationValues.MinMax }),
                new C.AxisPosition() { Val = C.AxisPositionValues.Left },
                new C.MajorGridlines(),
                new C.TickLabelPosition() { Val = C.TickLabelPositionValues.NextTo },
                new C.CrossingAxis() { Val = 10000000 },
                new C.Crosses() { Val = C.CrossesValues.AutoZero },
                new C.CrossBetween() { Val = C.CrossBetweenValues.Between }
            ));
        }

        chart.Append(plotArea);
        chartSpace.Append(chart);
        
        chartSpace.Append(new C.ExternalData(new C.AutoUpdate() { Val = new DocumentFormat.OpenXml.BooleanValue(false) }) { Id = embedRelId });
        chartPart.ChartSpace = chartSpace;

        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent() { Cx = 5486400, Cy = 3200400 },
                new DW.EffectExtent() { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties() { Id = 1, Name = "Chart 1" },
                new DW.NonVisualGraphicFrameDrawingProperties(
                    new A.GraphicFrameLocks() { NoChangeAspect = true }
                ),
                new A.Graphic(
                    new A.GraphicData(
                        new C.ChartReference() { Id = chartRelId }
                    ) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart" }
                )
            )
        );

        target.Append(new W.Paragraph(new W.Run(drawing)));
    }

    private static void RenderCanvas(FeatureNode node, OpenXmlCompositeElement target, Ctx ctx)
    {
        string svgContent = node.InnerContent?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(svgContent)) return;

        List<string> paths = new List<string>();
        double vBoxW = 100, vBoxH = 100;
        
        if (svgContent.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var xDoc = XDocument.Parse(svgContent);
                var svgElement = xDoc.Root;

                var viewBox = svgElement?.Attribute("viewBox")?.Value;
                if (!string.IsNullOrEmpty(viewBox))
                {
                    var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double w) &&
                        double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double h))
                    {
                        vBoxW = w;
                        vBoxH = h;
                    }
                }
                
                foreach (var el in svgElement?.Descendants() ?? Enumerable.Empty<XElement>())
                {
                    var localName = el.Name.LocalName.ToLowerInvariant();
                    if (localName == "path")
                    {
                        var d = el.Attribute("d")?.Value;
                        if (!string.IsNullOrEmpty(d)) paths.Add(d);
                    }
                    else if (localName == "rect")
                    {
                        var x = el.Attribute("x")?.Value ?? "0";
                        var y = el.Attribute("y")?.Value ?? "0";
                        var w = el.Attribute("width")?.Value ?? "0";
                        var h = el.Attribute("height")?.Value ?? "0";
                        paths.Add($"M {x} {y} h {w} v {h} h -{w} Z");
                    }
                    else if (localName == "circle")
                    {
                        var cx = double.Parse(el.Attribute("cx")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var cy = double.Parse(el.Attribute("cy")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var r = double.Parse(el.Attribute("r")?.Value ?? "0", CultureInfo.InvariantCulture);
                        var kappa = 0.552284749831 * r;
                        paths.Add(
                            $"M {cx} {cy - r} " +
                            $"C {cx + kappa} {cy - r}, {cx + r} {cy - kappa}, {cx + r} {cy} " +
                            $"C {cx + r} {cy + kappa}, {cx + kappa} {cy + r}, {cx} {cy + r} " +
                            $"C {cx - kappa} {cy + r}, {cx - r} {cy + kappa}, {cx - r} {cy} " +
                            $"C {cx - r} {cy - kappa}, {cx - kappa} {cy - r}, {cx} {cy - r} Z"
                        );
                    }
                    else if (localName == "line")
                    {
                        var x1 = el.Attribute("x1")?.Value ?? "0";
                        var y1 = el.Attribute("y1")?.Value ?? "0";
                        var x2 = el.Attribute("x2")?.Value ?? "0";
                        var y2 = el.Attribute("y2")?.Value ?? "0";
                        paths.Add($"M {x1} {y1} L {x2} {y2}");
                    }
                    else if (localName == "polyline" || localName == "polygon")
                    {
                        var pts = el.Attribute("points")?.Value;
                        if (!string.IsNullOrEmpty(pts))
                        {
                            var coords = pts.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                            if (coords.Length >= 2)
                            {
                                var sb = new System.Text.StringBuilder();
                                sb.Append($"M {coords[0]} {coords[1]} ");
                                for (int i = 2; i < coords.Length - 1; i += 2)
                                {
                                    sb.Append($"L {coords[i]} {coords[i + 1]} ");
                                }
                                if (localName == "polygon") sb.Append("Z");
                                paths.Add(sb.ToString());
                            }
                        }
                    }
                }
            }
            catch { }
        }
        else
        {
            paths.Add(svgContent);
        }

        if (paths.Count == 0) return;

        long emuPerUnit = 9525;
        long shapeWidthEmu = (long)(vBoxW * emuPerUnit);
        long shapeHeightEmu = (long)(vBoxH * emuPerUnit);

        var pathList = new A.PathList();

        foreach (var d in paths)
        {
            var tokens = Regex.Matches(d, @"[a-zA-Z]|[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?").Cast<Match>().Select(m => m.Value).ToList();
            var aPath = new A.Path() { Width = shapeWidthEmu, Height = shapeHeightEmu };
            
            char currentCommand = 'M';
            int index = 0;
            double currentX = 0, currentY = 0;

            while (index < tokens.Count)
            {
                string token = tokens[index];
                if (char.IsLetter(token[0]))
                {
                    currentCommand = token[0];
                    index++;
                }

                bool isRelative = char.IsLower(currentCommand);
                char cmd = char.ToUpperInvariant(currentCommand);

                double ReadNext() => double.Parse(tokens[index++], CultureInfo.InvariantCulture);
                
                A.Point P(double x, double y) => new A.Point 
                { 
                    X = new StringValue(((long)(x * emuPerUnit)).ToString()), 
                    Y = new StringValue(((long)(y * emuPerUnit)).ToString()) 
                };

                switch (cmd)
                {
                    case 'M':
                    case 'L':
                    case 'T':
                        if (index + 1 >= tokens.Count) break;
                        double mx = ReadNext(), my = ReadNext();
                        currentX = isRelative ? currentX + mx : mx;
                        currentY = isRelative ? currentY + my : my;
                        
                        if (cmd == 'M') aPath.Append(new A.MoveTo() { Point = P(currentX, currentY) });
                        else aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        
                        if (cmd == 'M') currentCommand = isRelative ? 'l' : 'L';
                        break;
                    case 'H':
                        if (index >= tokens.Count) break;
                        double hx = ReadNext();
                        currentX = isRelative ? currentX + hx : hx;
                        aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        break;
                    case 'V':
                        if (index >= tokens.Count) break;
                        double vy = ReadNext();
                        currentY = isRelative ? currentY + vy : vy;
                        aPath.Append(new A.LineTo() { Point = P(currentX, currentY) });
                        break;
                    case 'C':
                        if (index + 5 >= tokens.Count) break;
                        double cx1 = ReadNext(), cy1 = ReadNext();
                        double cx2 = ReadNext(), cy2 = ReadNext();
                        double cx3 = ReadNext(), cy3 = ReadNext();

                        if (isRelative)
                        {
                            cx1 += currentX; cy1 += currentY;
                            cx2 += currentX; cy2 += currentY;
                            cx3 += currentX; cy3 += currentY;
                        }

                        aPath.Append(new A.CubicBezierCurveTo(
                            new A.Point() { X = ((long)(cx1 * emuPerUnit)).ToString(), Y = ((long)(cy1 * emuPerUnit)).ToString() },
                            new A.Point() { X = ((long)(cx2 * emuPerUnit)).ToString(), Y = ((long)(cy2 * emuPerUnit)).ToString() },
                            new A.Point() { X = ((long)(cx3 * emuPerUnit)).ToString(), Y = ((long)(cy3 * emuPerUnit)).ToString() }
                        ));
                        currentX = cx3;
                        currentY = cy3;
                        break;
                    case 'Z':
                        aPath.Append(new A.CloseShapePath());
                        break;
                }
            }
            pathList.Append(aPath);
        }

        var customGeom = new A.CustomGeometry(pathList);

        var solidFill = new A.SolidFill(new A.RgbColorModelHex() { Val = ctx.Theme.Text.TrimStart('#') ?? "000000" });
        var outline = new A.Outline(new A.SolidFill(new A.RgbColorModelHex() { Val = ctx.Theme.Text.TrimStart('#') ?? "000000" })) { Width = 12700 };

        var wpsShape = new Wps.WordprocessingShape(
            new Wps.NonVisualDrawingProperties() { Id = 1U, Name = "SVG Shape" },
            new Wps.NonVisualDrawingShapeProperties(new A.ShapeLocks() { NoGrouping = true }),
            new Wps.ShapeProperties(
                new A.Transform2D(
                    new A.Offset() { X = 0L, Y = 0L },
                    new A.Extents() { Cx = shapeWidthEmu, Cy = shapeHeightEmu }),
                customGeom,
                solidFill,
                outline
            )
        );

        var inline = new DW.Inline(
            new DW.Extent() { Cx = shapeWidthEmu, Cy = shapeHeightEmu },
            new DW.EffectExtent() { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties() { Id = 1U, Name = "Picture" },
            new DW.NonVisualGraphicFrameDrawingProperties(),
            new A.Graphic(
                new A.GraphicData(wpsShape) { Uri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape" }
            )
        );

        var run = new W.Run(new W.Drawing(inline));
        
        if (target is W.Paragraph p)
        {
            p.Append(run);
        }
        else
        {
            target.Append(new W.Paragraph(run));
        }
    }

    private static W.Numbering AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();

        var bullet = new W.AbstractNum(new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel })
        {
            AbstractNumberId = 0
        };
        string[] glyphs = { "•", "○", "▪" };
        for (var i = 0; i < 9; i++)
            bullet.Append(new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                new W.LevelText { Val = glyphs[i % 3] },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation
                {
                    Left = ((i + 1) * 720).ToString(), Hanging = "360"
                }))
            { LevelIndex = i });

        var ordered = new W.AbstractNum(new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel })
        {
            AbstractNumberId = 1
        };
        for (var i = 0; i < 9; i++)
            ordered.Append(new W.Level(
                new W.StartNumberingValue { Val = 1 },
                new W.NumberingFormat { Val = W.NumberFormatValues.Decimal },
                new W.LevelText { Val = $"%{i + 1}." },
                new W.LevelJustification { Val = W.LevelJustificationValues.Left },
                new W.PreviousParagraphProperties(new W.Indentation
                {
                    Left = ((i + 1) * 720).ToString(), Hanging = "360"
                }))
            { LevelIndex = i });

        var numbering = new W.Numbering(bullet, ordered,
            new W.NumberingInstance(new W.AbstractNumId { Val = 0 }) { NumberID = 1 });
        part.Numbering = numbering;
        return numbering;
    }
}

