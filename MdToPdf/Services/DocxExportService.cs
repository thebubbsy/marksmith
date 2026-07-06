using System.Text;
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
using MdToPdf.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;
using W14 = DocumentFormat.OpenXml.Office2010.Word;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

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
// GitHub alerts render as single-cell tables with the same theme accent palette the Python DOCX
// path used. Mermaid fences render as plain code blocks — the Python screenshot-and-splice step
// needs a live browser (MermaidRenderService), which this service intentionally doesn't depend on.
public sealed class DocxExportService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .UseAlertBlocks()
        .UseMathematics()
        .Build();

    private static readonly ThemeCatalog Themes = new();

    // Same alert accent colors as MarkdownHtmlService / the Python app's DOCX alert rendering.
    private static readonly Dictionary<string, (string Color, string Icon)> AlertStyles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = ("#0969da", "ℹ️"),
            ["tip"] = ("#1f883d", "💡"),
            ["important"] = ("#8250df", "📢"),
            ["warning"] = ("#bf8700", "⚠️"),
            ["caution"] = ("#cf222e", "🛑"),
        };

    private static readonly Dictionary<string, (string Color, string Icon)> AlertStylesDark =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = ("#58a6ff", "ℹ️"),
            ["tip"] = ("#3fb950", "💡"),
            ["important"] = ("#a371f7", "📢"),
            ["warning"] = ("#d29922", "⚠️"),
            ["caution"] = ("#f85149", "🛑"),
        };

    public Task ExportAsync(string markdown, string docxPath, AppSettings settings) =>
        Task.Run(() =>
        {
            if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
            markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
            markdown = FormattingService.Apply(markdown, settings);
            var doc = Markdown.Parse(markdown, Pipeline);
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

            var ctx = new Ctx
            {
                MainPart = main,
                Numbering = AddNumbering(main),
                Theme = theme,
                Alerts = isDark ? AlertStylesDark : AlertStyles,
                LinkColor = isDark ? "6CB6FF" : "0563C1",
                NoEmoji = settings.NoEmoji,
            };

            AddStyles(main, ctx);
            AddSettings(main, updateFieldsOnOpen: settings.IncludeToc, webLayout: settings.UnlimitedHeight);
            CollectAnchors(doc, ctx);

            if (settings.IncludeToc) AppendTocField(body, ctx);

            foreach (var block in doc)
                RenderBlock(block, body, ctx, listLevel: -1);

            body.Append(BuildSectionProperties(main, ctx, settings, title));
            main.Document.Save();
        });

    private sealed class Ctx
    {
        public required MainDocumentPart MainPart { get; init; }
        public required W.Numbering Numbering { get; init; }
        public required ThemeDefinition Theme { get; init; }
        public required Dictionary<string, (string Color, string Icon)> Alerts { get; init; }
        public required string LinkColor { get; init; }
        public required bool NoEmoji { get; init; }
        public int NextNumId = 2; // numId 1 is the shared bullet instance
        public int NextBookmarkId = 1;
        public bool DropCapPending = true;
        public readonly Dictionary<string, string> Anchors = new(); // markdig heading id -> bookmark name

        public string TextHex => Hex(Theme.Text);
        public string HeadingHex => Hex(Theme.Heading);
        public string BorderHex => Hex(Theme.Border);
        public string CodeHex => Hex(Theme.Code);
        public string SecondaryHex => Hex(Theme.Secondary);
    }

    // Inline formatting state threaded through the inline walker.
    private readonly record struct Fmt(
        bool Bold, bool Italic, bool Strike, bool Code, bool Superscript, bool Subscript,
        bool Highlight, bool Underline, string? Color);

    private static string Hex(string cssColor) => cssColor.TrimStart('#').ToUpperInvariant();

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
                target.Append(CodeParagraph(math.Lines.ToString(), ctx));
                break;

            case CodeBlock code: // FencedCodeBlock included; mermaid fences render as code too
                target.Append(CodeParagraph(code.Lines.ToString(), ctx));
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

            case HtmlBlock:
                break; // raw HTML has no faithful OOXML mapping; skipped (pandoc also mangles most of it)

            case ParagraphBlock p:
            {
                var para = new W.Paragraph();
                RenderInlines(para, p.Inline, ctx, default);
                target.Append(para);
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

    private static W.Paragraph CodeParagraph(string text, Ctx ctx)
    {
        var para = new W.Paragraph(new W.ParagraphProperties(
            new W.KeepLines(), // don't let a page break shear a code block in half
            new W.ParagraphBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Space = 4, Color = ctx.BorderHex }),
            new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.CodeHex }));

        var first = true;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (!first) para.Append(new W.Run(new W.Break()));
            first = false;
            AddText(para, line, new Fmt { Code = true, Color = ctx.TextHex });
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
        p.ParagraphProperties.Indentation = new W.Indentation { Left = "360" };
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
                        pPr.Append(new W.NumberingProperties(
                            new W.NumberingLevelReference { Val = level },
                            new W.NumberingId { Val = numId }));
                        // contextualSpacing: suppress inter-paragraph spacing between siblings of
                        // the same list, the way Word's own List Paragraph style does.
                        pPr.Append(new W.ContextualSpacing());
                    }
                    else
                    {
                        pPr.Append(new W.Indentation { Left = ((level + 1) * 720).ToString() });
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

        var cell = new W.TableCell(new W.TableCellProperties(
            new W.TableCellWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
            new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = ctx.SecondaryHex }));

        var title = new W.Paragraph();
        var titleText = ctx.NoEmoji ? kind.ToUpperInvariant() : $"{icon} {kind.ToUpperInvariant()}";
        AddText(title, titleText, new Fmt { Bold = true, Color = accentHex });
        cell.Append(title);

        foreach (var child in alert)
            RenderBlock(child, cell, ctx, -1);
        if (!cell.Elements<W.Paragraph>().Any())
            cell.Append(new W.Paragraph());

        // The cell background is the theme secondary color, which is dark for dark themes — force
        // the theme text color onto runs that don't already carry one so content stays readable.
        foreach (var run in cell.Descendants<W.Run>())
        {
            run.RunProperties ??= new W.RunProperties();
            run.RunProperties.Color ??= new W.Color { Val = ctx.TextHex };
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
        target.Append(SpacerParagraph());
    }

    private static void RenderTable(MdTable table, OpenXmlCompositeElement target, Ctx ctx)
    {
        W.BorderType Border<T>() where T : W.BorderType, new() =>
            new T { Val = W.BorderValues.Single, Size = 6, Color = ctx.BorderHex };

        var wTable = new W.Table(
            new W.TableProperties(
                new W.TableWidth { Type = W.TableWidthUnitValues.Pct, Width = "5000" },
                new W.TableBorders(
                    Border<W.TopBorder>(), Border<W.LeftBorder>(), Border<W.BottomBorder>(),
                    Border<W.RightBorder>(), Border<W.InsideHorizontalBorder>(), Border<W.InsideVerticalBorder>()),
                // Accessibility alt text — the w:tbl equivalent of an <img alt>, almost nobody sets it.
                new W.TableCaption { Val = "Table exported from Markdown" },
                new W.TableDescription { Val = "Table exported from Markdown by Marksmith" }),
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
                if (!wCell.Elements<W.Paragraph>().Any())
                    wCell.Append(new W.Paragraph());

                var align = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                if (align is TableColumnAlign.Center or TableColumnAlign.Right)
                {
                    foreach (var p in wCell.Elements<W.Paragraph>())
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

                if (row.IsHeader)
                {
                    // Header fill is the theme code color (dark on dark themes) — bold the text and
                    // give it the theme text color, matching the HTML th styling.
                    foreach (var run in wCell.Descendants<W.Run>())
                    {
                        run.RunProperties ??= new W.RunProperties();
                        run.RunProperties.Bold ??= new W.Bold();
                        run.RunProperties.Color ??= new W.Color { Val = ctx.TextHex };
                    }
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
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddText(target, literal.Content.ToString(), fmt);
                    break;

                case EmphasisInline em:
                    RenderInlines(target, em, ctx, ApplyEmphasis(fmt, em));
                    break;

                case CodeInline code:
                    AddText(target, code.Content, fmt with { Code = true });
                    break;

                case LineBreakInline br:
                    if (br.IsHard) target.Append(new W.Run(new W.Break()));
                    else AddText(target, " ", fmt);
                    break;

                case LinkInline link:
                    RenderLink(target, link, ctx, fmt);
                    break;

                case AutolinkInline auto:
                {
                    var relId = TryAddHyperlinkRel(ctx, auto.IsEmail ? $"mailto:{auto.Url}" : auto.Url);
                    if (relId is not null)
                    {
                        var hl = new W.Hyperlink { Id = relId };
                        AddText(hl, auto.Url, fmt with { Color = ctx.LinkColor, Underline = true });
                        target.Append(hl);
                    }
                    else
                    {
                        AddText(target, auto.Url, fmt);
                    }
                    break;
                }

                case TaskList task:
                    // No trailing space — the literal that follows the checkbox already has one.
                    AddText(target, ctx.NoEmoji
                        ? (task.Checked ? "[x]" : "[ ]")
                        : (task.Checked ? "☑" : "☐"), fmt);
                    break;

                case MathInline math:
                    AddText(target, math.Content.ToString(), fmt with { Italic = true });
                    break;

                case FootnoteLink fn:
                    AddText(target, $"[{fn.Index}]", fmt with { Superscript = true });
                    break;

                case HtmlEntityInline entity:
                    AddText(target, entity.Transcoded.ToString(), fmt);
                    break;

                case HtmlInline:
                    break; // raw inline HTML skipped, same rationale as HtmlBlock

                case ContainerInline nestedContainer:
                    RenderInlines(target, nestedContainer, ctx, fmt);
                    break;
            }
        }
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
            // Images aren't embedded (would need dimension probing + local files only); the alt
            // text survives, linked to the source when the URL is resolvable.
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

    private static void AddText(OpenXmlCompositeElement target, string text, Fmt fmt)
    {
        var run = new W.Run();
        var rPr = BuildRunProperties(fmt);
        if (rPr is not null) run.Append(rPr);
        run.Append(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        target.Append(run);
    }

    private static W.RunProperties BuildRunProperties(Fmt fmt)
    {
        var rPr = new W.RunProperties();
        if (fmt.Code) rPr.Append(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
        if (fmt.Bold) rPr.Append(new W.Bold());
        if (fmt.Italic) rPr.Append(new W.Italic());
        if (fmt.Strike) rPr.Append(new W.Strike());
        // Highlight ink is always yellow, so pin the text to black regardless of theme.
        if (fmt.Highlight) rPr.Append(new W.Color { Val = "000000" });
        else if (fmt.Color is not null) rPr.Append(new W.Color { Val = fmt.Color });
        if (fmt.Code) rPr.Append(new W.FontSize { Val = "20" }); // 10pt for code
        if (fmt.Highlight) rPr.Append(new W.Highlight { Val = W.HighlightColorValues.Yellow });
        if (fmt.Underline) rPr.Append(new W.Underline { Val = W.UnderlineValues.Single });
        if (fmt.Code) // character border + shading: the rarely-touched w:bdr, boxing inline code
        {
            rPr.Append(new W.Border { Val = W.BorderValues.Single, Size = 4, Space = 1, Color = "auto" });
            rPr.Append(new W.Shading { Val = W.ShadingPatternValues.Clear, Color = "auto", Fill = "auto" });
        }
        if (fmt.Superscript)
            rPr.Append(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Superscript });
        else if (fmt.Subscript)
            rPr.Append(new W.VerticalTextAlignment { Val = W.VerticalPositionValues.Subscript });
        // OpenType typography on every text run (the only rPr the w14 extensions are schema-legal
        // in): kerned ligatures, old-style proportional numerals, contextual alternates.
        if (!fmt.Code)
        {
            rPr.Append(new W14.Ligatures { Val = W14.LigaturesValues.StandardContextual });
            rPr.Append(new W14.NumberingFormat { Val = W14.NumberFormValues.OldStyle });
            rPr.Append(new W14.NumberSpacing { Val = W14.NumberSpacingValues.Proportional });
            rPr.Append(new W14.ContextualAlternatives());
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
        styles.Append(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" },
                new W.Color { Val = ctx.TextHex },
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

        // (size half-points, gray color for h6) — roughly GitHub's heading scale on an 11pt base.
        (string Size, string? Color)[] headings =
        {
            ("40", ctx.HeadingHex), ("32", ctx.HeadingHex), ("28", ctx.HeadingHex),
            ("24", ctx.HeadingHex), ("22", ctx.HeadingHex), ("22", "6A737D"),
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

        styles.Append(new W.Style(
            new W.StyleName { Val = "Hyperlink" },
            new W.StyleRunProperties(
                new W.Color { Val = ctx.LinkColor },
                new W.Underline { Val = W.UnderlineValues.Single }))
        {
            Type = W.StyleValues.Character,
            StyleId = "Hyperlink",
        });

        part.Styles = styles;
    }

    private static void AddSettings(MainDocumentPart main, bool updateFieldsOnOpen, bool webLayout)
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
            FooterRun("  ·  Marksmith")));

        // A4FixedWidth drives the physical page too, mirroring the PDF export geometry.
        var (width, height) = settings.A4FixedWidth ? (11906u, 16838u) : (12240u, 15840u);

        W.BorderType PageBorder<T>() where T : W.BorderType, new() =>
            new T { Val = W.BorderValues.Single, Size = 8, Space = 24, Color = ctx.BorderHex };

        return new W.SectionProperties(
            new W.HeaderReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(headerPart) },
            new W.FooterReference { Type = W.HeaderFooterValues.Default, Id = main.GetIdOfPart(footerPart) },
            new W.PageSize { Width = width, Height = height },
            new W.PageMargin
            {
                Top = 1440, Right = 1440, Bottom = 1440, Left = 1440,
                Header = 576, Footer = 576, Gutter = 0,
            },
            // w:pgBorders — a full page frame in the theme border color, measured from the page edge.
            new W.PageBorders(
                PageBorder<W.TopBorder>(), PageBorder<W.LeftBorder>(),
                PageBorder<W.BottomBorder>(), PageBorder<W.RightBorder>())
            {
                OffsetFrom = W.PageBorderOffsetValues.Page,
                Display = W.PageBorderDisplayValues.AllPages,
            });
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
