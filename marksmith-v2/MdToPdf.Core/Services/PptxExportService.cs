using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MdToPdf.Services;

// PPTX export. Splits the Markdown on H1/H2 headings (each becomes a slide), lays the body out as
// bullet levels, and themes the deck from the selected Marksmith theme. Built with
// DocumentFormat.OpenXml using strongly-typed PresentationML and DrawingML objects, wired with
// explicit relationship ids. No external dependency.
public sealed class PptxExportService
{
    public const string Extension = "pptx";

    private static readonly ThemeCatalog Themes = new();

    public Task ExportAsync(string markdown, string pptxPath, AppSettings settings) => Task.Run(() =>
    {
        markdown = TextNormalizer.Newlines(markdown);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);

        var theme = Themes.GetOrDefault(settings.Theme);
        var slides = BuildSlides(markdown, HistoryEntry.ExtractTitle(markdown) ?? "Marksmith");

        var dir = Path.GetDirectoryName(pptxPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(pptxPath)) File.Delete(pptxPath);

        using var doc = PresentationDocument.Create(pptxPath, PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();

        var masterPart = presPart.AddNewPart<SlideMasterPart>("rIdMaster");
        var themePart = masterPart.AddNewPart<ThemePart>("rIdTheme");
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>("rIdLayout");
        layoutPart.AddPart(masterPart, "rIdMasterFromLayout");

        masterPart.SlideMaster = CreateSlideMaster(theme);
        layoutPart.SlideLayout = CreateSlideLayout();
        themePart.Theme = CreateTheme(theme);

        for (int i = 0; i < slides.Count; i++)
        {
            var rId = $"rIdSlide{i + 1}";
            var slidePart = presPart.AddNewPart<SlidePart>(rId);
            slidePart.AddPart(layoutPart, "rIdLayoutFromSlide");
            slidePart.Slide = CreateSlide(slides[i], theme);
        }

        presPart.Presentation = CreatePresentation(slides.Count);
    });

    private sealed record Slide(string Title, List<(int Level, string Text)> Bullets);

    // ---- markdown -> slides (headings split; lists/paragraphs become bullets) ----
    private static List<Slide> BuildSlides(string markdown, string deckTitle)
    {
        var slides = new List<Slide>();
        Slide? cur = null;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                var text = Plain(h.Groups[2].Value);
                if (level <= 2) { cur = new Slide(text, new()); slides.Add(cur); }
                else { cur ??= NewSlide(deckTitle, slides); cur.Bullets.Add((0, text)); }
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (Regex.IsMatch(line, @"^\s*(```|~~~)")) continue; // skip code fences markers
            var bullet = Regex.Match(line, @"^(\s*)(?:[-*+]|\d+\.)\s+(.*)$");
            cur ??= NewSlide(deckTitle, slides);
            if (bullet.Success)
                cur.Bullets.Add((Math.Min(4, bullet.Groups[1].Value.Length / 2), Plain(bullet.Groups[2].Value)));
            else
                cur.Bullets.Add((0, Plain(line)));
        }
        if (slides.Count == 0) slides.Add(new Slide(deckTitle, new()));
        return slides;
    }

    private static Slide NewSlide(string title, List<Slide> slides) { var s = new Slide(title, new()); slides.Add(s); return s; }

    private static string Plain(string md) =>
        Regex.Replace(md, @"(\*\*|__|\*|_|`|~~)", "").Trim();

    private static string Hex(string css) => css.TrimStart('#').ToUpperInvariant().PadLeft(6, '0')[..6];

    // ---- OpenXML object factories ----

    private static P.Presentation CreatePresentation(int slideCount)
    {
        var presentation = new P.Presentation();

        var slideMasterIdList = new P.SlideMasterIdList();
        slideMasterIdList.Append(new P.SlideMasterId { Id = 2147483648U, RelationshipId = "rIdMaster" });
        presentation.Append(slideMasterIdList);

        var slideIdList = new P.SlideIdList();
        for (int i = 0; i < slideCount; i++)
        {
            var rId = $"rIdSlide{i + 1}";
            slideIdList.Append(new P.SlideId { Id = (uint)(256 + i), RelationshipId = rId });
        }
        presentation.Append(slideIdList);

        presentation.Append(new P.SlideSize { Cx = 12192000, Cy = 6858000 });
        presentation.Append(new P.NotesSize { Cx = 6858000, Cy = 9144000 });

        return presentation;
    }

    private static P.SlideMaster CreateSlideMaster(ThemeDefinition t)
    {
        var slideMaster = new P.SlideMaster();

        var cSld = new P.CommonSlideData();

        var bg = new P.Background();
        var bgPr = new P.BackgroundProperties();
        bgPr.Append(new A.SolidFill(new A.RgbColorModelHex { Val = Hex(t.Background) }));
        bgPr.Append(new A.EffectList());
        bg.Append(bgPr);
        cSld.Append(bg);

        cSld.Append(CreateShapeTree());
        slideMaster.Append(cSld);

        var clrMap = new P.ColorMap
        {
            Background1 = A.ColorSchemeIndexValues.Light1,
            Text1 = A.ColorSchemeIndexValues.Dark1,
            Background2 = A.ColorSchemeIndexValues.Light2,
            Text2 = A.ColorSchemeIndexValues.Dark2,
            Accent1 = A.ColorSchemeIndexValues.Accent1,
            Accent2 = A.ColorSchemeIndexValues.Accent2,
            Accent3 = A.ColorSchemeIndexValues.Accent3,
            Accent4 = A.ColorSchemeIndexValues.Accent4,
            Accent5 = A.ColorSchemeIndexValues.Accent5,
            Accent6 = A.ColorSchemeIndexValues.Accent6,
            Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
            FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
        };
        slideMaster.Append(clrMap);

        var slideLayoutIdList = new P.SlideLayoutIdList();
        slideLayoutIdList.Append(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = "rIdLayout" });
        slideMaster.Append(slideLayoutIdList);

        return slideMaster;
    }

    private static P.SlideLayout CreateSlideLayout()
    {
        var slideLayout = new P.SlideLayout
        {
            Type = P.SlideLayoutValues.Object,
            Preserve = true
        };

        var cSld = new P.CommonSlideData { Name = "Title and Content" };
        cSld.Append(CreateShapeTree());
        slideLayout.Append(cSld);

        var clrMapOvr = new P.ColorMapOverride();
        clrMapOvr.Append(new A.OverrideColorMapping
        {
            Background1 = A.ColorSchemeIndexValues.Light1,
            Text1 = A.ColorSchemeIndexValues.Dark1,
            Background2 = A.ColorSchemeIndexValues.Light2,
            Text2 = A.ColorSchemeIndexValues.Dark2,
            Accent1 = A.ColorSchemeIndexValues.Accent1,
            Accent2 = A.ColorSchemeIndexValues.Accent2,
            Accent3 = A.ColorSchemeIndexValues.Accent3,
            Accent4 = A.ColorSchemeIndexValues.Accent4,
            Accent5 = A.ColorSchemeIndexValues.Accent5,
            Accent6 = A.ColorSchemeIndexValues.Accent6,
            Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
            FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
        });
        slideLayout.Append(clrMapOvr);

        return slideLayout;
    }

    private static P.ShapeTree CreateShapeTree()
    {
        var spTree = new P.ShapeTree();

        var nvGrpSpPr = new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties()
        );
        spTree.Append(nvGrpSpPr);

        var grpSpPr = new P.GroupShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 0L, Y = 0L },
                new A.Extents { Cx = 0L, Cy = 0L },
                new A.ChildOffset { X = 0L, Y = 0L },
                new A.ChildExtents { Cx = 0L, Cy = 0L }
            )
        );
        spTree.Append(grpSpPr);

        return spTree;
    }

    private static P.Slide CreateSlide(Slide slide, ThemeDefinition t)
    {
        var slideObj = new P.Slide();
        var cSld = new P.CommonSlideData();
        var spTree = CreateShapeTree();

        // Title Shape
        var titleShape = new P.Shape();
        var titleNvSpPr = new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = 2U, Name = "Title" },
            new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
            new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.Title })
        );
        titleShape.Append(titleNvSpPr);

        var titleSpPr = new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 685800L, Y = 381000L },
                new A.Extents { Cx = 10820400L, Cy = 1143000L }
            ),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }
        );
        titleShape.Append(titleSpPr);

        var titleTxBody = new P.TextBody(
            new A.BodyProperties(),
            new A.ListStyle(),
            new A.Paragraph(
                new A.Run(
                    new A.RunProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = Hex(t.Heading) })
                    )
                    {
                        Language = "en-US",
                        FontSize = 3200,
                        Bold = true,
                        Dirty = false
                    },
                    new A.Text(slide.Title)
                )
            )
        );
        titleShape.Append(titleTxBody);
        spTree.Append(titleShape);

        // Content Shape
        var contentShape = new P.Shape();
        var contentNvSpPr = new P.NonVisualShapeProperties(
            new P.NonVisualDrawingProperties { Id = 3U, Name = "Content" },
            new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
            new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = P.PlaceholderValues.Body, Index = 1U })
        );
        contentShape.Append(contentNvSpPr);

        var contentSpPr = new P.ShapeProperties(
            new A.Transform2D(
                new A.Offset { X = 685800L, Y = 1600200L },
                new A.Extents { Cx = 10820400L, Cy = 4800600L }
            ),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }
        );
        contentShape.Append(contentSpPr);

        var contentTxBody = new P.TextBody(
            new A.BodyProperties(),
            new A.ListStyle()
        );

        if (slide.Bullets.Count == 0)
        {
            contentTxBody.Append(new A.Paragraph(new A.EndParagraphRunProperties { Language = "en-US" }));
        }
        else
        {
            foreach (var (level, text) in slide.Bullets)
            {
                var paragraph = new A.Paragraph(
                    new A.ParagraphProperties { Level = level },
                    new A.Run(
                        new A.RunProperties(
                            new A.SolidFill(new A.RgbColorModelHex { Val = Hex(t.Text) })
                        )
                        {
                            Language = "en-US",
                            Dirty = false
                        },
                        new A.Text(text)
                    )
                );
                contentTxBody.Append(paragraph);
            }
        }

        contentShape.Append(contentTxBody);
        spTree.Append(contentShape);

        cSld.Append(spTree);
        slideObj.Append(cSld);

        return slideObj;
    }

    private static A.Theme CreateTheme(ThemeDefinition t)
    {
        string dk1 = Hex(t.Text), lt1 = Hex(t.Background), dk2 = Hex(t.Heading), lt2 = Hex(t.Secondary);
        string a1 = Hex(t.Heading), a2 = Hex(t.Primary), a3 = Hex(t.Line), a4 = Hex(t.Code), a5 = Hex(t.Border), a6 = Hex(t.Primary);

        var theme = new A.Theme { Name = "Marksmith" };

        var colorScheme = new A.ColorScheme(
            new A.Dark1Color(new A.RgbColorModelHex { Val = dk1 }),
            new A.Light1Color(new A.RgbColorModelHex { Val = lt1 }),
            new A.Dark2Color(new A.RgbColorModelHex { Val = dk2 }),
            new A.Light2Color(new A.RgbColorModelHex { Val = lt2 }),
            new A.Accent1Color(new A.RgbColorModelHex { Val = a1 }),
            new A.Accent2Color(new A.RgbColorModelHex { Val = a2 }),
            new A.Accent3Color(new A.RgbColorModelHex { Val = a3 }),
            new A.Accent4Color(new A.RgbColorModelHex { Val = a4 }),
            new A.Accent5Color(new A.RgbColorModelHex { Val = a5 }),
            new A.Accent6Color(new A.RgbColorModelHex { Val = a6 }),
            new A.Hyperlink(new A.RgbColorModelHex { Val = a2 }),
            new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = a2 })
        )
        { Name = "Marksmith" };

        var fontScheme = new A.FontScheme(
            new A.MajorFont(
                new A.LatinFont { Typeface = "Calibri Light" },
                new A.EastAsianFont { Typeface = "" },
                new A.ComplexScriptFont { Typeface = "" }
            ),
            new A.MinorFont(
                new A.LatinFont { Typeface = "Calibri" },
                new A.EastAsianFont { Typeface = "" },
                new A.ComplexScriptFont { Typeface = "" }
            )
        )
        { Name = "Marksmith" };

        var fillStyleList = new A.FillStyleList(
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })
        );

        var lineStyleList = new A.LineStyleList(
            new A.Outline(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.PresetDash { Val = A.PresetLineDashValues.Solid }
            )
            { Width = 6350, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center },
            new A.Outline(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.PresetDash { Val = A.PresetLineDashValues.Solid }
            )
            { Width = 12700, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center },
            new A.Outline(
                new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                new A.PresetDash { Val = A.PresetLineDashValues.Solid }
            )
            { Width = 19050, CapType = A.LineCapValues.Flat, CompoundLineType = A.CompoundLineValues.Single, Alignment = A.PenAlignmentValues.Center }
        );

        var effectStyleList = new A.EffectStyleList(
            new A.EffectStyle(new A.EffectList()),
            new A.EffectStyle(new A.EffectList()),
            new A.EffectStyle(new A.EffectList())
        );

        var bgFillStyleList = new A.BackgroundFillStyleList(
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })
        );

        var formatScheme = new A.FormatScheme(
            fillStyleList,
            lineStyleList,
            effectStyleList,
            bgFillStyleList
        )
        { Name = "Marksmith" };

        theme.Append(new A.ThemeElements(colorScheme, fontScheme, formatScheme));

        return theme;
    }
}
