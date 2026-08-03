using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Services;

internal static class OpenXmlFormatter
{
    public static void AddStyles(MainDocumentPart main, Ctx ctx)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new W.Styles();

        // Doc-wide defaults carry the theme text color and kerning; the w14 OpenType features are
        // stamped per-run in BuildRunProperties (the only place they're schema-legal).
        var baseFont = ctx.BrandFont ?? "Calibri"; // branding kit can restyle the whole document
        styles.Append(new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = baseFont, HighAnsi = baseFont, EastAsia = baseFont, ComplexScript = baseFont },
                new W.Color { Val = ctx.Theme.Text.TrimStart('#') },
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

    public static void AddSettings(MainDocumentPart main, bool updateFieldsOnOpen, bool webLayout)
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
}

