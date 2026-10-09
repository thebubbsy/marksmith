using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkSmith.Services.Email;
using SkiaSharp;

namespace MarkSmith.Services.Presentation;

public sealed class SlideDeckOptions
{
    /// <summary>Title used when the document has no H1 or front-matter title.</summary>
    public string FallbackTitle { get; init; } = "Marksmith";
    public string? AuthorName { get; init; }
    /// <summary>Pre-rendered PNGs of the ```mermaid fences, in document order (2x scale).</summary>
    public IReadOnlyList<byte[]?>? MermaidPngs { get; init; }
    public bool NoEmoji { get; init; }
    /// <summary>Loads an image destination; defaults to the Word exporter's loader (local files
    /// relative to the document folder, data: URIs, safe web addresses).</summary>
    public Func<string, byte[]?>? LoadImage { get; init; }
}

/// <summary>
/// Turns Markdown into a paginated slide deck.
///
/// H1 and H2 start slides; H3–H6 become subheadings on the slide; "---" is a slide break. A
/// leading H1 followed by other sections becomes the title slide (its short first paragraph is
/// the subtitle), and a heading with nothing under it becomes a section divider. Lists keep
/// their bullets, numbers and nesting, tables stay tables, code keeps its lines and colours,
/// images and diagrams are pictures, and a section too long for one slide continues on the next
/// ("Title (continued)") instead of running off the bottom.
/// </summary>
public static class SlideDeckBuilder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseMathematics()
        .UseEmojiAndSmiley(enableSmileys: false).Build();

    private static readonly MarkdownPipeline PipelineNoEmoji = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseMathematics().Build();

    internal static readonly Dictionary<string, (string Color, string Label)> AlertStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["note"] = ("0969DA", "Note"),
        ["tip"] = ("1F883D", "Tip"),
        ["important"] = ("8250DF", "Important"),
        ["warning"] = ("BF8700", "Warning"),
        ["caution"] = ("CF222E", "Caution"),
    };

    /// <summary>A subtitle longer than this stays on a content slide instead.</summary>
    private const int MaxSubtitleLength = 200;

    private sealed class Section
    {
        public string Title = "";
        public int Level;              // 0 = before any heading, 1 = H1, 2 = H2
        public bool IsNotes;
        public readonly List<SlideBlock> Flow = new();
    }

    public static PptxDeck Build(string markdown, SlideDeckOptions? options = null)
    {
        options ??= new SlideDeckOptions();
        var doc = Markdown.Parse(markdown ?? "", options.NoEmoji ? PipelineNoEmoji : Pipeline);
        var ctx = new Ctx(options, doc);

        var title = FindTitle(doc) ?? options.FallbackTitle;
        var deck = new PptxDeck { Title = title };

        var sections = new List<Section>();
        Section? cur = null;
        FootnoteGroup? footnotes = null;
        bool breakPending = false;
        foreach (var block in doc)
        {
            switch (block)
            {
                case YamlFrontMatterBlock or LinkReferenceDefinitionGroup or BlankLineBlock:
                    continue;
                case FootnoteGroup fg:
                    footnotes = fg;
                    continue;
                case HeadingBlock { Level: <= 2 } h:
                    cur = new Section { Title = InlineText(h.Inline), Level = h.Level };
                    if (cur.Title.Length == 0) cur.Title = title;
                    sections.Add(cur);
                    breakPending = false;
                    continue;
                case ThematicBreakBlock:
                    // An explicit slide break: the next content starts a new slide with the same
                    // title. A rule right before a heading changes nothing (no empty slide).
                    breakPending = cur is { Flow.Count: > 0 };
                    continue;
            }
            if (breakPending && cur is not null)
            {
                cur = new Section { Title = cur.Title, Level = cur.Level == 1 ? 2 : cur.Level };
                sections.Add(cur);
                breakPending = false;
            }
            if (cur is null)
            {
                cur = new Section { Title = title, Level = 0 };
                sections.Add(cur);
            }
            ctx.Convert(block, cur.Flow, level: 0);
        }

        if (footnotes is not null)
        {
            var notes = new Section { Title = "Notes", Level = 2, IsNotes = true };
            ctx.ConvertFootnotes(footnotes, notes.Flow);
            if (notes.Flow.Count > 0) sections.Add(notes);
        }

        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (i == 0 && s.Level == 1)
            {
                var slide = new DeckSlide { Kind = SlideKind.Title, Title = s.Title };
                slide.Subtitle = TakeSubtitle(s.Flow);
                if (!string.IsNullOrWhiteSpace(options.AuthorName)) slide.Byline = options.AuthorName.Trim();
                deck.Slides.Add(slide);
                Paginate(s.Title, s.Flow, deck.Slides);
                continue;
            }
            if (s.Flow.Count == 0 && !s.IsNotes)
            {
                deck.Slides.Add(new DeckSlide { Kind = SlideKind.Section, Title = s.Title });
                continue;
            }
            Paginate(s.Title, s.Flow, deck.Slides);
        }

        if (deck.Slides.Count == 0) deck.Slides.Add(new DeckSlide { Kind = SlideKind.Title, Title = title });
        return deck;
    }

    /// <summary>The front-matter title, else the document's first H1.</summary>
    internal static string? FindTitle(MarkdownDocument doc)
    {
        foreach (var b in doc)
        {
            if (b is not YamlFrontMatterBlock y) continue;
            foreach (var line in y.Lines.Lines)
            {
                var s = line.ToString().Trim();
                if (s.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    var t = s[6..].Trim().Trim('"', '\'').Trim();
                    if (t.Length > 0) return t;
                }
            }
        }
        var h1 = doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        var text = h1 is null ? "" : InlineText(h1.Inline);
        return text.Length > 0 ? text : null;
    }

    // The H1's opening line becomes the title slide's subtitle when it's short and plain.
    private static string? TakeSubtitle(List<SlideBlock> flow)
    {
        if (flow.FirstOrDefault() is not TextBlock { Panel: PanelKind.None } tb) return null;
        var first = tb.Paragraphs.FirstOrDefault();
        if (first is not { Style: ParaStyle.Body }) return null;
        var text = first.PlainText.Replace('\n', ' ').Trim();
        if (text.Length == 0 || text.Length > MaxSubtitleLength) return null;
        tb.Paragraphs.RemoveAt(0);
        if (tb.Paragraphs.Count == 0) flow.RemoveAt(0);
        return text;
    }

    // ── pagination ───────────────────────────────────────────────────────────

    private const double MinPictureHeightPt = 150;
    private const int MinCodeLinesOnSlide = 3;
    private const double MinTextScale = 0.6;

    private static void Paginate(string title, List<SlideBlock> flow, List<DeckSlide> output)
    {
        if (flow.Count == 0) return;
        var slide = new DeckSlide { Kind = SlideKind.Content, Title = title };
        output.Add(slide);
        double used = 0;

        DeckSlide Next()
        {
            slide = new DeckSlide { Kind = SlideKind.Content, Title = title + " (continued)", IsContinuation = true };
            output.Add(slide);
            used = 0;
            return slide;
        }
        double Gap() => slide.Blocks.Count == 0 ? 0 : SlideGeometry.BlockGapPt;
        double Room() => SlideGeometry.BodyHeightPt - used - Gap();
        void Place(SlideBlock b, double h)
        {
            used += Gap() + h;
            b.HeightPt = h;
            slide.Blocks.Add(b);
        }

        foreach (var block in flow)
        {
            switch (block)
            {
                case TextBlock tb:
                    PlaceText(tb);
                    break;
                case CodeSlideBlock code:
                    PlaceCode(code);
                    break;
                case TableSlideBlock table:
                    PlaceTable(table);
                    break;
                case PictureSlideBlock pic:
                {
                    var (_, fh) = SlideGeometry.FitPicture(pic, Room());
                    var (_, fullH) = SlideGeometry.FitPicture(pic, SlideGeometry.BodyHeightPt);
                    if (slide.Blocks.Count > 0 && fh < Math.Min(fullH, MinPictureHeightPt)) Next();
                    var (_, h) = SlideGeometry.FitPicture(pic, Room());
                    Place(pic, h + SlideGeometry.CaptionHeight(pic.Caption));
                    break;
                }
                case MissingPictureBlock missing:
                    if (slide.Blocks.Count > 0 && Room() < SlideGeometry.MissingPictureHeightPt) Next();
                    Place(missing, SlideGeometry.MissingPictureHeightPt);
                    break;
            }
        }

        void PlaceText(TextBlock tb)
        {
            var width = SlideGeometry.TextWidth(tb);
            var pending = tb.Paragraphs.ToList();
            while (pending.Count > 0)
            {
                // How many of the pending paragraphs fit in the room left on this slide.
                var room = Room();
                int fit = 0;
                for (int n = 1; n <= pending.Count; n++)
                {
                    if (SlideGeometry.TextHeight(pending.GetRange(0, n), tb.Panel, width) > room) break;
                    fit = n;
                }
                // Never end a slide on a subheading: it belongs with what follows it.
                while (fit > 0 && fit < pending.Count && pending[fit - 1].KeepWithNext) fit--;

                if (fit == 0)
                {
                    if (slide.Blocks.Count > 0) { Next(); continue; }
                    // One paragraph taller than a whole slide: shrink it to fit.
                    var part = Clone(tb, pending.GetRange(0, 1));
                    var natural = SlideGeometry.TextHeight(part);
                    part.FontScale = Math.Max(MinTextScale, Math.Min(1, SlideGeometry.BodyHeightPt / natural * 0.98));
                    Place(part, Math.Min(SlideGeometry.BodyHeightPt, SlideGeometry.TextHeight(part)));
                    pending.RemoveAt(0);
                    if (pending.Count > 0) Next();
                    continue;
                }

                var piece = Clone(tb, pending.GetRange(0, fit));
                Place(piece, SlideGeometry.TextHeight(piece));
                pending.RemoveRange(0, fit);
                if (pending.Count > 0) Next();
            }
        }

        void PlaceCode(CodeSlideBlock code)
        {
            int start = 0;
            bool first = true;
            while (start < code.Lines.Count || first)
            {
                var part = new CodeSlideBlock
                {
                    Language = code.Language,
                    Caption = first ? code.Caption : null,
                    ContinuedFromPrevious = !first,
                };
                var room = Room();
                int fit = 0;
                for (int n = 1; start + n <= code.Lines.Count; n++)
                {
                    if (SlideGeometry.CodeHeight(part, 0, 0) + LinesHeight(code, start, n) > room) break;
                    fit = n;
                }
                var remaining = code.Lines.Count - start;
                if (fit < Math.Min(MinCodeLinesOnSlide, remaining) && slide.Blocks.Count > 0) { Next(); continue; }
                if (fit == 0) fit = Math.Max(1, Math.Min(remaining, 1));
                part.Lines.AddRange(code.Lines.Skip(start).Take(fit));
                Place(part, SlideGeometry.CodeHeight(part, 0, part.Lines.Count));
                start += fit;
                first = false;
                if (start < code.Lines.Count) Next();
                else break;
            }
        }

        static double LinesHeight(CodeSlideBlock c, int from, int count)
        {
            var font = SlideGeometry.CodeFont(c);
            int rows = 0;
            for (int i = from; i < from + count; i++) rows += SlideGeometry.CodeLineRows(c.Lines[i], font);
            return rows * SlideGeometry.CodeLineHeight(c);
        }

        void PlaceTable(TableSlideBlock table)
        {
            double headerH = table.Header.Count > 0 ? SlideGeometry.RowHeight(table, table.Header) : 0;
            int start = 0;
            while (true)
            {
                var room = Room();
                double h = headerH;
                int fit = 0;
                for (int r = start; r < table.Rows.Count; r++)
                {
                    var rh = SlideGeometry.RowHeight(table, table.Rows[r]);
                    if (h + rh > room) break;
                    h += rh;
                    fit++;
                }
                var remaining = table.Rows.Count - start;
                if (fit < Math.Min(2, remaining) && slide.Blocks.Count > 0) { Next(); continue; }
                if (fit == 0 && remaining > 0) { fit = 1; h = headerH + SlideGeometry.RowHeight(table, table.Rows[start]); }

                var part = new TableSlideBlock();
                part.Header.AddRange(table.Header);
                part.Align.AddRange(table.Align);
                part.Widths.AddRange(table.Widths);
                part.Rows.AddRange(table.Rows.Skip(start).Take(fit));
                Place(part, Math.Min(h, SlideGeometry.BodyHeightPt));
                start += fit;
                if (start >= table.Rows.Count) break;
                Next();
            }
        }
    }

    private static TextBlock Clone(TextBlock source, IEnumerable<SlideParagraph> paragraphs)
    {
        var tb = new TextBlock { Panel = source.Panel, PanelColor = source.PanelColor };
        tb.Paragraphs.AddRange(paragraphs);
        return tb;
    }

    // ── inline text helpers ──────────────────────────────────────────────────

    internal static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var sb = new StringBuilder();
        foreach (var i in inline.Descendants())
        {
            switch (i)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case MathInline math: sb.Append(LatexText.ToReadable(math.Content.ToString())); break;
                case HtmlEntityInline e: sb.Append(e.Transcoded.ToString()); break;
                case AutolinkInline a: sb.Append(a.Url); break;
                case LineBreakInline: sb.Append(' '); break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // ── block conversion ─────────────────────────────────────────────────────

    private sealed class Ctx
    {
        private readonly SlideDeckOptions _options;
        private readonly List<FencedCodeBlock> _mermaid;
        private readonly Dictionary<string, PictureSlideBlock?> _images = new(StringComparer.Ordinal);

        public Ctx(SlideDeckOptions options, MarkdownDocument doc)
        {
            _options = options;
            _mermaid = doc.Descendants<FencedCodeBlock>().Where(IsMermaid).ToList();
        }

        private static bool IsMermaid(FencedCodeBlock f) =>
            f.Info?.Trim().StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true;

        private static TextBlock Text(List<SlideBlock> flow)
        {
            if (flow.LastOrDefault() is TextBlock { Panel: PanelKind.None } tb) return tb;
            tb = new TextBlock();
            flow.Add(tb);
            return tb;
        }

        public void Convert(Block block, List<SlideBlock> flow, int level)
        {
            switch (block)
            {
                case YamlFrontMatterBlock or LinkReferenceDefinitionGroup or BlankLineBlock or ThematicBreakBlock:
                    return;
                case HeadingBlock h:
                {
                    var para = new SlideParagraph { Style = ParaStyle.Subheading, HeadingLevel = Math.Max(3, h.Level) };
                    AddRuns(h.Inline, para.Runs, new Style(Bold: true), images: null);
                    if (para.Runs.Count > 0) Text(flow).Paragraphs.Add(para);
                    return;
                }
                case ParagraphBlock p:
                    AddParagraph(p.Inline, flow, ParaStyle.Body, 0);
                    return;
                case ListBlock list:
                    AddList(list, flow, 0);
                    return;
                case MathBlock math:
                {
                    var tex = math.Lines.ToString().Trim();
                    var para = new SlideParagraph { Style = ParaStyle.Math };
                    para.Runs.Add(new TextRun(LatexText.ToReadable(tex)) { Math = true });
                    Text(flow).Paragraphs.Add(para);
                    return;
                }
                case FencedCodeBlock fenced when IsMermaid(fenced):
                    AddDiagram(fenced, flow);
                    return;
                case CodeBlock code:
                {
                    var c = new CodeSlideBlock { Language = (code as FencedCodeBlock)?.Info?.Trim().Split(' ')[0] ?? "" };
                    var lines = code.Lines.ToString().Replace("\r", "").Split('\n').ToList();
                    while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                    if (lines.Count == 0) return;
                    c.Lines.AddRange(lines);
                    flow.Add(c);
                    return;
                }
                case AlertBlock alert:
                {
                    var kind = alert.Kind.ToString();
                    var (color, label) = AlertStyles.TryGetValue(kind, out var s) ? s : AlertStyles["note"];
                    var panel = new TextBlock { Panel = PanelKind.Alert, PanelColor = color };
                    var head = new SlideParagraph { Style = ParaStyle.Body };
                    head.Runs.Add(new TextRun(label) { Bold = true, Color = color });
                    panel.Paragraphs.Add(head);
                    AddPanel(alert, panel, flow);
                    return;
                }
                case QuoteBlock quote:
                    AddPanel(quote, new TextBlock { Panel = PanelKind.Quote }, flow);
                    return;
                case Table table:
                    AddTable(table, flow);
                    return;
                case HtmlBlock html:
                    AddHtml(html, flow);
                    return;
                case DefinitionList dl:
                    foreach (var item in dl.OfType<DefinitionItem>())
                    {
                        foreach (var child in item)
                        {
                            if (child is DefinitionTerm term)
                            {
                                var para = new SlideParagraph { Style = ParaStyle.Body };
                                AddRuns(term.Inline, para.Runs, new Style(Bold: true), images: null);
                                if (para.Runs.Count > 0) Text(flow).Paragraphs.Add(para);
                            }
                            else if (child is ParagraphBlock def)
                            {
                                AddParagraph(def.Inline, flow, ParaStyle.Continuation, 0);
                            }
                            else Convert(child, flow, level);
                        }
                    }
                    return;
                case FigureCaption caption:
                {
                    var para = new SlideParagraph { Style = ParaStyle.Caption };
                    AddRuns(caption.Inline, para.Runs, new Style(Italic: true), images: null);
                    if (para.Runs.Count > 0) Text(flow).Paragraphs.Add(para);
                    return;
                }
                case Figure or FooterBlock or CustomContainer:
                    foreach (var child in (ContainerBlock)block) Convert(child, flow, level);
                    return;
                case LeafBlock leaf when leaf.Inline is not null:
                    AddParagraph(leaf.Inline, flow, ParaStyle.Body, 0);
                    return;
                case ContainerBlock container:
                    foreach (var child in container) Convert(child, flow, level);
                    return;
            }
        }

        // A quote or alert: its text goes on one panel; anything that can't sit in a text box
        // (code, a table, a picture) follows it.
        private void AddPanel(ContainerBlock container, TextBlock panel, List<SlideBlock> flow)
        {
            var inner = new List<SlideBlock>();
            foreach (var child in container) Convert(child, inner, 0);
            var after = new List<SlideBlock>();
            foreach (var b in inner)
            {
                if (b is TextBlock { Panel: PanelKind.None } t) panel.Paragraphs.AddRange(t.Paragraphs);
                else after.Add(b);
            }
            if (panel.Paragraphs.Count > 0) flow.Add(panel);
            flow.AddRange(after);
        }

        private void AddList(ListBlock list, List<SlideBlock> flow, int level)
        {
            int number = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
            foreach (var item in list.OfType<ListItemBlock>())
            {
                bool firstPara = true;
                foreach (var child in item)
                {
                    if (child is ParagraphBlock p && firstPara)
                    {
                        firstPara = false;
                        var task = p.Inline?.FirstChild is { } fc ? EmailHtmlRenderer.CheckboxState(fc) : null;
                        var style = task is not null ? ParaStyle.Task : list.IsOrdered ? ParaStyle.Numbered : ParaStyle.Bullet;
                        AddParagraph(p.Inline, flow, style, Math.Min(4, level), number, task == true);
                    }
                    else if (child is ParagraphBlock more)
                    {
                        AddParagraph(more.Inline, flow, ParaStyle.Continuation, Math.Min(4, level));
                    }
                    else if (child is ListBlock nested)
                    {
                        if (firstPara)
                        {
                            // "- - x": an empty item holding a list. Give it its marker anyway.
                            Text(flow).Paragraphs.Add(new SlideParagraph
                            {
                                Style = list.IsOrdered ? ParaStyle.Numbered : ParaStyle.Bullet,
                                Level = Math.Min(4, level),
                                Number = number,
                            });
                            firstPara = false;
                        }
                        AddList(nested, flow, level + 1);
                    }
                    else
                    {
                        Convert(child, flow, level + 1);
                    }
                }
                if (firstPara)
                {
                    Text(flow).Paragraphs.Add(new SlideParagraph
                    {
                        Style = list.IsOrdered ? ParaStyle.Numbered : ParaStyle.Bullet,
                        Level = Math.Min(4, level),
                        Number = number,
                    });
                }
                number++;
            }
        }

        // A paragraph's text, plus its images as pictures after it. An image-only paragraph is
        // just its pictures; an image that can't be loaded keeps its description in the text.
        private void AddParagraph(ContainerInline? inline, List<SlideBlock> flow, ParaStyle style, int level, int number = 0, bool isChecked = false)
        {
            var images = new List<(string Src, string Alt, string? Title)>();
            var para = new SlideParagraph { Style = style, Level = level, Number = number, Checked = isChecked };
            AddRuns(inline, para.Runs, default, images);

            var pictures = new List<SlideBlock>();
            var textless = para.Runs.All(r => r.LineBreak || r.Text.Trim().Length == 0);
            foreach (var (src, alt, title) in images)
            {
                var pic = LoadPicture(src, alt, title);
                if (pic is not null) { pictures.Add(pic); continue; }
                if (textless && images.Count == 1)
                {
                    pictures.Add(new MissingPictureBlock { Description = alt, Source = src });
                    continue;
                }
                if (alt.Length > 0) para.Runs.Add(new TextRun((para.Runs.Count > 0 ? " " : "") + alt) { Italic = true });
            }

            TrimRuns(para.Runs);
            bool hasText = para.Runs.Any(r => !r.LineBreak && r.Text.Trim().Length > 0);
            if (hasText || (style is ParaStyle.Bullet or ParaStyle.Numbered or ParaStyle.Task && pictures.Count == 0))
                Text(flow).Paragraphs.Add(para);
            flow.AddRange(pictures);
        }

        private static void TrimRuns(List<TextRun> runs)
        {
            while (runs.Count > 0 && (runs[^1].LineBreak || runs[^1].Text.Length == 0)) runs.RemoveAt(runs.Count - 1);
            while (runs.Count > 0 && (runs[0].LineBreak || runs[0].Text.Trim().Length == 0)) runs.RemoveAt(0);
            if (runs.Count > 0 && !runs[0].Code) runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            if (runs.Count > 0 && !runs[^1].Code) runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        }

        private void AddDiagram(FencedCodeBlock fenced, List<SlideBlock> flow)
        {
            var index = _mermaid.IndexOf(fenced);
            var source = fenced.Lines.ToString();
            var label = EpubExportService.DiagramLabel(source, index + 1);
            var png = _options.MermaidPngs is { } pngs && index >= 0 && index < pngs.Count ? pngs[index] : null;
            if (png is { Length: > 0 } && Measure(png) is { } size)
            {
                flow.Add(new PictureSlideBlock
                {
                    Data = png,
                    ContentType = "image/png",
                    PixelWidth = size.W,
                    PixelHeight = size.H,
                    // Rendered at 2x for sharpness.
                    NaturalWidthPt = size.W * 0.375,
                    NaturalHeightPt = size.H * 0.375,
                    IsDiagram = true,
                    Description = label,
                });
                return;
            }
            var code = new CodeSlideBlock { Language = "mermaid", Caption = label + " (diagram source)" };
            code.Lines.AddRange(source.Replace("\r", "").TrimEnd().Split('\n'));
            flow.Add(code);
        }

        private void AddTable(Table table, List<SlideBlock> flow)
        {
            var t = new TableSlideBlock();
            var rows = table.OfType<TableRow>().ToList();
            // Markdig adds a trailing column definition for "| a | b |", so count real cells.
            int cols = rows.Count == 0 ? table.ColumnDefinitions.Count : rows.Max(r => r.Count);
            if (cols == 0) return;
            for (int c = 0; c < cols; c++)
            {
                var align = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                t.Align.Add(align switch { TableColumnAlign.Center => CellAlign.Center, TableColumnAlign.Right => CellAlign.Right, _ => CellAlign.Left });
            }
            foreach (var row in rows)
            {
                var cells = new List<List<SlideParagraph>>();
                for (int c = 0; c < cols; c++)
                {
                    var cell = new List<SlideParagraph>();
                    if (c < row.Count && row[c] is TableCell tc)
                    {
                        foreach (var child in tc)
                        {
                            if (child is not LeafBlock { Inline: { } inl }) continue;
                            var para = new SlideParagraph { Style = ParaStyle.Body };
                            var imgs = new List<(string, string, string?)>();
                            AddRuns(inl, para.Runs, row.IsHeader ? new Style(Bold: true) : default, imgs);
                            foreach (var (_, alt, _) in imgs) if (alt.Length > 0) para.Runs.Add(new TextRun(alt) { Italic = true });
                            TrimRuns(para.Runs);
                            cell.Add(para);
                        }
                    }
                    if (cell.Count == 0) cell.Add(new SlideParagraph { Style = ParaStyle.Body });
                    cells.Add(cell);
                }
                if (row.IsHeader) t.Header.AddRange(cells);
                else t.Rows.Add(cells);
            }

            // Wider columns for longer text, but never so lopsided that a short column is crushed.
            var weights = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                var longest = t.Header.Count > c ? t.Header[c].Sum(p => p.PlainText.Length) : 0;
                foreach (var r in t.Rows) longest = Math.Max(longest, r[c].Sum(p => p.PlainText.Length));
                weights[c] = Math.Sqrt(Math.Clamp(longest, 3, 80));
            }
            var total = weights.Sum();
            t.Widths.AddRange(weights.Select(w => w / total));
            flow.Add(t);
        }

        private static readonly Regex HtmlImg = new(@"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlAlt = new(@"\balt\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlDrop = new(@"<!--.*?-->|<(script|style|noscript|template)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);

        // HTML blocks: README-style <p align="center"><img …></p> keeps its pictures; other markup
        // keeps its text.
        private int _htmlDepth;

        private void AddHtml(HtmlBlock html, List<SlideBlock> flow)
        {
            var raw = HtmlDrop.Replace(html.Lines.ToString(), "");
            if (raw.Trim().Length == 0) return;
            // Through the HTML importer first, so <b>, <a>, lists and tables keep their meaning.
            if (_htmlDepth == 0)
            {
                string? md = null;
                try { md = Import.HtmlToMarkdown.Convert(raw); } catch { }
                if (!string.IsNullOrWhiteSpace(md))
                {
                    _htmlDepth++;
                    try
                    {
                        foreach (var block in Markdown.Parse(md, _options.NoEmoji ? PipelineNoEmoji : Pipeline))
                            Convert(block, flow, 0);
                    }
                    finally { _htmlDepth--; }
                    return;
                }
            }
            foreach (Match m in HtmlImg.Matches(raw))
            {
                var alt = HtmlAlt.Match(m.Value) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value) : "";
                var src = WebUtility.HtmlDecode(m.Groups[1].Value);
                if (LoadPicture(src, alt, null) is { } pic) flow.Add(pic);
                else flow.Add(new MissingPictureBlock { Description = alt, Source = src });
            }
            var text = Regex.Replace(raw, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"</(p|div|li|h[1-6]|tr)\s*>", "\n", RegexOptions.IgnoreCase);
            text = WebUtility.HtmlDecode(HtmlTag.Replace(text, ""));
            foreach (var line in text.Split('\n').Select(l => Regex.Replace(l, @"\s+", " ").Trim()).Where(l => l.Length > 0))
            {
                var para = new SlideParagraph { Style = ParaStyle.Body };
                para.Runs.Add(new TextRun(line));
                Text(flow).Paragraphs.Add(para);
            }
        }

        public void ConvertFootnotes(FootnoteGroup group, List<SlideBlock> flow)
        {
            foreach (var fn in group.OfType<Footnote>().OrderBy(f => f.Order))
            {
                var para = new SlideParagraph { Style = ParaStyle.Body };
                para.Runs.Add(new TextRun(fn.Order.ToString(CultureInfo.InvariantCulture)) { Baseline = 30000, Bold = true });
                para.Runs.Add(new TextRun(" "));
                bool first = true;
                foreach (var child in fn)
                {
                    if (child is not ParagraphBlock p) continue;
                    if (!first) para.Runs.Add(new TextRun("") { LineBreak = true });
                    AddRuns(p.Inline, para.Runs, default, images: null);
                    first = false;
                }
                TrimRuns(para.Runs);
                Text(flow).Paragraphs.Add(para);
            }
        }

        // ── images ──

        private PictureSlideBlock? LoadPicture(string src, string alt, string? title)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            if (!_images.TryGetValue(src, out var cached))
            {
                cached = Decode(src);
                _images[src] = cached;
            }
            if (cached is null) return null;
            return new PictureSlideBlock
            {
                Data = cached.Data,
                ContentType = cached.ContentType,
                PixelWidth = cached.PixelWidth,
                PixelHeight = cached.PixelHeight,
                NaturalWidthPt = cached.NaturalWidthPt,
                NaturalHeightPt = cached.NaturalHeightPt,
                Description = alt.Length > 0 ? alt : DocumentImages.AltFromFileName(src),
                Caption = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            };
        }

        private PictureSlideBlock? Decode(string src)
        {
            byte[]? bytes;
            try
            {
                var url = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? src : SafeUnescape(src);
                bytes = (_options.LoadImage ?? DocxExportService.FetchImageBytes)(url);
            }
            catch { bytes = null; }
            if (bytes is not { Length: > 0 }) return null;

            if (LooksLikeSvg(bytes, src))
            {
                var svg = Encoding.UTF8.GetString(bytes);
                var png = SvgRasterizer.ToPng(svg, 2.0, transparent: true);
                if (png is null || Measure(png) is not { } ps) return null;
                return new PictureSlideBlock
                {
                    Data = png, ContentType = "image/png", PixelWidth = ps.W, PixelHeight = ps.H,
                    NaturalWidthPt = ps.W * 0.375, NaturalHeightPt = ps.H * 0.375,
                };
            }

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(bytes));
                if (codec is null) return null;
                var (w, h) = (codec.Info.Width, codec.Info.Height);
                if (w <= 0 || h <= 0) return null;
                string? type = codec.EncodedFormat switch
                {
                    SKEncodedImageFormat.Png => "image/png",
                    SKEncodedImageFormat.Jpeg => "image/jpeg",
                    SKEncodedImageFormat.Gif => "image/gif",
                    SKEncodedImageFormat.Bmp => "image/bmp",
                    _ => null,
                };
                if (type is null)
                {
                    // WebP, ICO and the like: PowerPoint can't show them, so convert to PNG.
                    using var bmp = SKBitmap.Decode(bytes);
                    if (bmp is null) return null;
                    using var img = SKImage.FromBitmap(bmp);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                    bytes = data.ToArray();
                    type = "image/png";
                }
                return new PictureSlideBlock
                {
                    Data = bytes, ContentType = type, PixelWidth = w, PixelHeight = h,
                    NaturalWidthPt = w * 0.75, NaturalHeightPt = h * 0.75,
                };
            }
            catch { return null; }
        }

        private static (int W, int H)? Measure(byte[] png)
        {
            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(png));
                return codec is null ? null : (codec.Info.Width, codec.Info.Height);
            }
            catch { return null; }
        }

        private static bool LooksLikeSvg(byte[] bytes, string src)
        {
            if (src.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || src.Contains("image/svg", StringComparison.OrdinalIgnoreCase)) return true;
            var head = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 200)).TrimStart('﻿', ' ', '\r', '\n', '\t');
            return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
                || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeUnescape(string s)
        {
            try { return Uri.UnescapeDataString(s); } catch { return s; }
        }

        // ── inlines ──

        private readonly record struct Style(
            bool Bold = false, bool Italic = false, bool Strike = false, bool Underline = false,
            bool Code = false, int Baseline = 0, string? Url = null);

        private static readonly HashSet<string> SkippedTags = new(StringComparer.OrdinalIgnoreCase)
            { "script", "style", "iframe", "object", "noscript", "template", "textarea", "select" };

        private void AddRuns(ContainerInline? container, List<TextRun> runs, Style style, List<(string Src, string Alt, string? Title)>? images)
        {
            if (container is null) return;
            var htmlStack = new Stack<(string Tag, Style Before)>();
            string? skipUntil = null;
            var current = style;
            foreach (var inline in container)
                AddInline(inline, runs, ref current, images, htmlStack, ref skipUntil);
        }

        private void AddInline(Inline inline, List<TextRun> runs, ref Style s, List<(string Src, string Alt, string? Title)>? images,
            Stack<(string Tag, Style Before)> html, ref string? skipUntil)
        {
            if (skipUntil is not null)
            {
                if (inline is HtmlInline end && EmailHtmlRenderer.IsClosingTag(end.Tag, skipUntil)) skipUntil = null;
                return;
            }
            switch (inline)
            {
                case TaskList:
                    return; // drawn as the paragraph's checkbox bullet
                case HtmlInline h when EmailHtmlRenderer.CheckboxState(h) is not null:
                    return;
                case LiteralInline lit:
                    Append(runs, lit.Content.ToString(), s);
                    return;
                case HtmlEntityInline entity:
                    Append(runs, entity.Transcoded.ToString(), s);
                    return;
                case CodeInline code:
                    Append(runs, code.Content, s with { Code = true });
                    return;
                case MathInline math:
                    runs.Add(Run(LatexText.ToReadable(math.Content.ToString()), s) with { Math = true, Italic = true });
                    return;
                case LineBreakInline br:
                    if (br.IsHard) runs.Add(new TextRun("") { LineBreak = true });
                    else Append(runs, " ", s);
                    return;
                case AutolinkInline auto:
                {
                    var href = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                    Append(runs, auto.Url, s with { Url = SafeUrl(href) });
                    return;
                }
                case FootnoteLink fl:
                    if (!fl.IsBackLink)
                        runs.Add(Run(fl.Footnote.Order.ToString(CultureInfo.InvariantCulture), s) with { Baseline = 30000 });
                    return;
                case LinkInline link when link.IsImage:
                {
                    var src = link.GetDynamicUrl?.Invoke() ?? link.Url ?? "";
                    var alt = InlineText(link);
                    if (images is not null) images.Add((src, alt, link.Title));
                    else if (alt.Length > 0) Append(runs, alt, s with { Italic = true });
                    return;
                }
                case LinkInline link:
                {
                    var url = SafeUrl(link.GetDynamicUrl?.Invoke() ?? link.Url ?? "");
                    var inner = s with { Url = url ?? s.Url };
                    foreach (var child in link) AddInline(child, runs, ref inner, images, html, ref skipUntil);
                    return;
                }
                case EmphasisInline em:
                {
                    var inner = (em.DelimiterChar, em.DelimiterCount) switch
                    {
                        ('*' or '_', >= 2) => s with { Bold = true },
                        ('*' or '_', _) => s with { Italic = true },
                        ('~', >= 2) => s with { Strike = true },
                        ('~', _) => s with { Baseline = -25000 },
                        ('^', _) => s with { Baseline = 30000 },
                        ('+', _) => s with { Underline = true },
                        ('=', _) => s with { Bold = true },
                        _ => s,
                    };
                    foreach (var child in em) AddInline(child, runs, ref inner, images, html, ref skipUntil);
                    return;
                }
                case HtmlInline h:
                    ApplyHtml(h.Tag, runs, ref s, images, html, ref skipUntil);
                    return;
                case ContainerInline container:
                    foreach (var child in container) AddInline(child, runs, ref s, images, html, ref skipUntil);
                    return;
            }
        }

        private static readonly Regex TagName = new(@"^<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)", RegexOptions.Compiled);

        private static void ApplyHtml(string tag, List<TextRun> runs, ref Style s, List<(string Src, string Alt, string? Title)>? images,
            Stack<(string Tag, Style Before)> stack, ref string? skipUntil)
        {
            var m = TagName.Match(tag);
            if (!m.Success) return;
            var closing = m.Groups[1].Value == "/";
            var name = m.Groups[2].Value.ToLowerInvariant();
            if (name == "br") { if (!closing) runs.Add(new TextRun("") { LineBreak = true }); return; }
            if (name == "img" && !closing)
            {
                var src = HtmlImg.Match(tag) is { Success: true } im ? WebUtility.HtmlDecode(im.Groups[1].Value) : null;
                var alt = HtmlAlt.Match(tag) is { Success: true } a ? WebUtility.HtmlDecode(a.Groups[1].Value) : "";
                if (src is not null && images is not null) images.Add((src, alt, null));
                else if (alt.Length > 0) Append(runs, alt, s with { Italic = true });
                return;
            }
            if (!closing && SkippedTags.Contains(name) && !tag.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
            {
                skipUntil = name;
                return;
            }
            if (closing)
            {
                // Pop back to the matching opener; an unmatched closer is ignored.
                if (stack.Any(e => e.Tag == name))
                {
                    while (stack.Count > 0)
                    {
                        var (t, before) = stack.Pop();
                        s = before;
                        if (t == name) break;
                    }
                }
                return;
            }
            var next = name switch
            {
                "b" or "strong" or "mark" => s with { Bold = true },
                "i" or "em" or "cite" => s with { Italic = true },
                "u" or "ins" => s with { Underline = true },
                "s" or "del" or "strike" => s with { Strike = true },
                "sub" => s with { Baseline = -25000 },
                "sup" => s with { Baseline = 30000 },
                "code" or "kbd" or "samp" => s with { Code = true },
                _ => (Style?)null,
            };
            if (next is null) return;
            if (tag.TrimEnd().EndsWith("/>", StringComparison.Ordinal)) return;
            stack.Push((name, s));
            s = next.Value;
        }

        private static TextRun Run(string text, Style s) => new(text)
        {
            Bold = s.Bold, Italic = s.Italic, Strike = s.Strike, Underline = s.Underline,
            Code = s.Code, Baseline = s.Baseline, Url = s.Url,
        };

        // Appends text, merging it into the previous run when the styling is identical.
        private static void Append(List<TextRun> runs, string text, Style s)
        {
            if (text.Length == 0) return;
            var run = Run(text, s);
            if (runs.Count > 0 && runs[^1] is { LineBreak: false, Math: false, Color: null } last
                && last with { Text = "" } == run with { Text = "" })
            {
                runs[^1] = last with { Text = last.Text + text };
                return;
            }
            runs.Add(run);
        }

        /// <summary>Web and mail links only; "#section" anchors and scripts aren't links on a slide.</summary>
        internal static string? SafeUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();
            if (url.StartsWith("<") && url.EndsWith(">")) url = url[1..^1];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            return uri.Scheme is "http" or "https" or "mailto" ? uri.AbsoluteUri : null;
        }
    }
}
