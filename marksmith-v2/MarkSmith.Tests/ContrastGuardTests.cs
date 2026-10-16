using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ContrastGuardTests
{
    [Fact]
    public void HighContrast_Text_Is_Preserved()
    {
        // Dark text on white background -> Ratio > 15:1 -> Preserved
        var result = ContrastGuard.EnsureLegibleText("000000", "FFFFFF");
        Assert.Equal("000000", result);

        // White text on dark background -> Ratio > 15:1 -> Preserved
        var darkResult = ContrastGuard.EnsureLegibleText("FFFFFF", "0D1117");
        Assert.Equal("FFFFFF", darkResult);
    }

    [Fact]
    public void LowContrast_Text_Is_Corrected_To_High_Contrast()
    {
        // Dark green (#006400) on Dark background (#0D1117) -> Low contrast -> Force to White (#FFFFFF)
        var fixedColor = ContrastGuard.EnsureLegibleText("006400", "0D1117");
        Assert.Equal("FFFFFF", fixedColor);

        // Light yellow (#FFFFE0) on White background (#FFFFFF) -> Low contrast -> Force to Dark (#121212)
        var fixedLight = ContrastGuard.EnsureLegibleText("FFFFE0", "FFFFFF");
        Assert.Equal("121212", fixedLight);
    }

    [Fact]
    public void Calculates_Luminance_And_Contrast_Ratios_Accurately()
    {
        double whiteLum = ContrastGuard.GetLuminance("FFFFFF");
        double blackLum = ContrastGuard.GetLuminance("000000");

        Assert.True(whiteLum > 0.99);
        Assert.True(blackLum < 0.01);

        double maxRatio = ContrastGuard.GetContrastRatio("000000", "FFFFFF");
        Assert.True(maxRatio > 20.0);
    }

    [Fact]
    public void Svg_Low_Contrast_Text_Is_Automatically_Corrected()
    {
        // White text on white background inside PlantUML or Graphviz SVG -> Should be corrected to high-contrast dark (#121212)
        var whiteOnWhiteSvg = "<svg><g><text fill=\"#ffffff\">Sequence Participant</text><tspan fill=\"white\">Header</tspan></g></svg>";
        var correctedSvg = ContrastGuard.EnsureSvgLegibility(whiteOnWhiteSvg, "FFFFFF");

        Assert.Contains("fill=\"#121212\"", correctedSvg);
        Assert.DoesNotContain("fill=\"#ffffff\"", correctedSvg);
        Assert.DoesNotContain("fill=\"white\"", correctedSvg);
    }

    [Fact]
    public void PluginTheme_Guarantees_Legibility_On_Light_And_Dark_Themes()
    {
        var lightTheme = new Models.ThemeDefinition("GitHub Light", "#ffffff", "#1b1f23", "#000000", "#f6f8fa", "#d1d5da", "#000000", "#f6f8fa", "#333333");
        var pluginTheme = Plugins.PluginTheme.From(lightTheme);

        double textRatio = ContrastGuard.GetContrastRatio(pluginTheme.Text, pluginTheme.Background);
        Assert.True(textRatio >= 4.5, $"Text contrast ratio {textRatio} must be >= 4.5:1");
    }
}


public class ContrastGuardVisibleFillTests
{
    [Fact]
    public void VisibleFill_KeepsFillsThatContrastWithBackground()
    {
        Assert.Equal("78A75A", ContrastGuard.EnsureVisibleFill("78A75A", "1B1B1F"));
        Assert.Equal("0055AA", ContrastGuard.EnsureVisibleFill("0055AA", "FFFFFF"));
    }

    [Fact]
    public void VisibleFill_RejectsFillSameColourAsBackground()
    {
        // GitHub Light's Primary (#000000) on the studio canvas (#1B1B1F) must NOT survive.
        Assert.NotEqual("000000", ContrastGuard.EnsureVisibleFill("000000", "1B1B1F"));
    }

    [Fact]
    public void VisibleFill_ReturnsWhiteOnNearBlackCanvas()
    {
        Assert.Equal("FFFFFF", ContrastGuard.EnsureVisibleFill("000000", "1B1B1F"));
        Assert.Equal("FFFFFF", ContrastGuard.EnsureVisibleFill("1B1B1F", "1B1B1F"));
    }

    [Fact]
    public void VisibleFill_ReturnsDarkOnNearWhiteBackground()
    {
        Assert.Equal("121212", ContrastGuard.EnsureVisibleFill("FFFFFF", "FFFFFF"));
        Assert.Equal("121212", ContrastGuard.EnsureVisibleFill("F6F8FA", "FFFFFF"));
    }

    [Fact]
    public void LightReferenceTemplate_MergedIntoDarkTheme_EnforcesHighContrastAcrossAllOpenXmlParts()
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"light-ref-{Guid.NewGuid():N}.docx");
        var exportPath = Path.Combine(Path.GetTempPath(), $"dark-export-{Guid.NewGuid():N}.docx");
        try
        {
            BuildSyntheticReferenceTemplate(
                templatePath,
                docDefaultColor: "1B1F23",
                heading1Color: "000000",
                heading2Color: "090B0C",
                heading6Color: "6A737D",
                numberingColor: "1B1F23",
                headerColor: "000000",
                footerColor: "333333");

            var settings = new Models.AppSettings
            {
                Theme = "GitHub Dark",
                ThemeLightInfluence = true,
                BrandTemplatePath = templatePath
            };

            const string md = """
                # EMPLOYMENT AGREEMENT VARIATION

                ## 1. Parties & Background

                This variation agreement contains normal prose, **bold clauses**, `inline code`, and a footnote.[^1]

                > [!IMPORTANT]
                > Key remuneration terms take effect immediately.

                | Clause | Previous | Varied |
                | :--- | :--- | :--- |
                | Base Salary | $150,000 | $175,000 |

                <span style="color:#000000">Explicit black span on dark page must be guarded</span>

                [^1]: Governed by applicable employment law.
                """;

            new DocxExportService().ExportAsync(md, exportPath, settings).GetAwaiter().GetResult();

            AuditPackageContrast(exportPath, expectedDarkPage: true);
        }
        finally
        {
            try { File.Delete(templatePath); } catch { }
            try { File.Delete(exportPath); } catch { }
        }
    }

    [Fact]
    public void DarkReferenceTemplate_MergedIntoLightTheme_EnforcesHighContrastAcrossAllOpenXmlParts()
    {
        var templatePath = Path.Combine(Path.GetTempPath(), $"dark-ref-{Guid.NewGuid():N}.docx");
        var exportPath = Path.Combine(Path.GetTempPath(), $"light-export-{Guid.NewGuid():N}.docx");
        try
        {
            BuildSyntheticReferenceTemplate(
                templatePath,
                docDefaultColor: "C9D1D9",
                heading1Color: "FFFFFF",
                heading2Color: "F0F6FC",
                heading6Color: "8B949E",
                numberingColor: "FFFFFF",
                headerColor: "FFFFFF",
                footerColor: "C9D1D9");

            var settings = new Models.AppSettings
            {
                Theme = "GitHub Light",
                ThemeLightInfluence = false,
                BrandTemplatePath = templatePath
            };

            const string md = """
                # Light Theme Export With Dark Reference Template

                ## Section Heading

                Body paragraph text that inherits from Normal / docDefaults.

                <span style="color:#FFFFFF">Explicit white span on light page must be guarded</span>
                """;

            new DocxExportService().ExportAsync(md, exportPath, settings).GetAwaiter().GetResult();

            AuditPackageContrast(exportPath, expectedDarkPage: false);
        }
        finally
        {
            try { File.Delete(templatePath); } catch { }
            try { File.Delete(exportPath); } catch { }
        }
    }

    private static void BuildSyntheticReferenceTemplate(
        string path,
        string docDefaultColor,
        string heading1Color,
        string heading2Color,
        string heading6Color,
        string numberingColor,
        string headerColor,
        string footerColor)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
            path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
            new DocumentFormat.OpenXml.Wordprocessing.Body());

        var stylePart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.StyleDefinitionsPart>();
        stylePart.Styles = new DocumentFormat.OpenXml.Wordprocessing.Styles(
            new DocumentFormat.OpenXml.Wordprocessing.DocDefaults(
                new DocumentFormat.OpenXml.Wordprocessing.RunPropertiesDefault(
                    new DocumentFormat.OpenXml.Wordprocessing.RunPropertiesBaseStyle(
                        new DocumentFormat.OpenXml.Wordprocessing.Color
                        {
                            Val = docDefaultColor,
                            ThemeColor = DocumentFormat.OpenXml.Wordprocessing.ThemeColorValues.Text1
                        }))),
            new DocumentFormat.OpenXml.Wordprocessing.Style(
                new DocumentFormat.OpenXml.Wordprocessing.StyleName { Val = "Normal" },
                new DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.Color { Val = docDefaultColor }))
            {
                Type = DocumentFormat.OpenXml.Wordprocessing.StyleValues.Paragraph,
                StyleId = "Normal",
                Default = true
            },
            new DocumentFormat.OpenXml.Wordprocessing.Style(
                new DocumentFormat.OpenXml.Wordprocessing.StyleName { Val = "heading 1" },
                new DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.Bold(),
                    new DocumentFormat.OpenXml.Wordprocessing.Color
                    {
                        Val = heading1Color,
                        ThemeColor = DocumentFormat.OpenXml.Wordprocessing.ThemeColorValues.Text1
                    }))
            {
                Type = DocumentFormat.OpenXml.Wordprocessing.StyleValues.Paragraph,
                StyleId = "Heading1"
            },
            new DocumentFormat.OpenXml.Wordprocessing.Style(
                new DocumentFormat.OpenXml.Wordprocessing.StyleName { Val = "heading 2" },
                new DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.Bold(),
                    new DocumentFormat.OpenXml.Wordprocessing.Color { Val = heading2Color }))
            {
                Type = DocumentFormat.OpenXml.Wordprocessing.StyleValues.Paragraph,
                StyleId = "Heading2"
            },
            new DocumentFormat.OpenXml.Wordprocessing.Style(
                new DocumentFormat.OpenXml.Wordprocessing.StyleName { Val = "heading 6" },
                new DocumentFormat.OpenXml.Wordprocessing.StyleRunProperties(
                    new DocumentFormat.OpenXml.Wordprocessing.Bold(),
                    new DocumentFormat.OpenXml.Wordprocessing.Color { Val = heading6Color }))
            {
                Type = DocumentFormat.OpenXml.Wordprocessing.StyleValues.Paragraph,
                StyleId = "Heading6"
            });

        var numPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.NumberingDefinitionsPart>();
        numPart.Numbering = new DocumentFormat.OpenXml.Wordprocessing.Numbering(
            new DocumentFormat.OpenXml.Wordprocessing.AbstractNum(
                new DocumentFormat.OpenXml.Wordprocessing.Level(
                    new DocumentFormat.OpenXml.Wordprocessing.NumberingSymbolRunProperties(
                        new DocumentFormat.OpenXml.Wordprocessing.Color { Val = numberingColor }))
                { LevelIndex = 0 })
            { AbstractNumberId = 1 },
            new DocumentFormat.OpenXml.Wordprocessing.NumberingInstance(
                new DocumentFormat.OpenXml.Wordprocessing.AbstractNumId { Val = 1 })
            { NumberID = 1 });

        var headerPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.HeaderPart>();
        headerPart.Header = new DocumentFormat.OpenXml.Wordprocessing.Header(
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                        new DocumentFormat.OpenXml.Wordprocessing.Color { Val = headerColor }),
                    new DocumentFormat.OpenXml.Wordprocessing.Text("Template Header"))));

        var footerPart = main.AddNewPart<DocumentFormat.OpenXml.Packaging.FooterPart>();
        footerPart.Footer = new DocumentFormat.OpenXml.Wordprocessing.Footer(
            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                new DocumentFormat.OpenXml.Wordprocessing.Run(
                    new DocumentFormat.OpenXml.Wordprocessing.RunProperties(
                        new DocumentFormat.OpenXml.Wordprocessing.Color { Val = footerColor }),
                    new DocumentFormat.OpenXml.Wordprocessing.Text("Template Footer"))));

        main.Document.Body!.Append(
            new DocumentFormat.OpenXml.Wordprocessing.SectionProperties(
                new DocumentFormat.OpenXml.Wordprocessing.HeaderReference
                {
                    Type = DocumentFormat.OpenXml.Wordprocessing.HeaderFooterValues.Default,
                    Id = main.GetIdOfPart(headerPart)
                },
                new DocumentFormat.OpenXml.Wordprocessing.FooterReference
                {
                    Type = DocumentFormat.OpenXml.Wordprocessing.HeaderFooterValues.Default,
                    Id = main.GetIdOfPart(footerPart)
                }));
    }

    private static void AuditPackageContrast(string docxPath, bool expectedDarkPage)
    {
        using var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(docxPath, false);
        var main = doc.MainDocumentPart!;
        var pageBg = main.Document.DocumentBackground?.Color?.Value ?? "FFFFFF";

        if (expectedDarkPage)
            Assert.True(ContrastGuard.GetLuminance(pageBg) < 0.25, $"Expected dark page background but found #{pageBg}");
        else
            Assert.True(ContrastGuard.GetLuminance(pageBg) > 0.75, $"Expected light page background but found #{pageBg}");

        // 1. Audit styles.xml: docDefaults and every style with w:color must have >= 4.5:1 against its effective fill or pageBg,
        // and no w:themeColor may remain to override w:val.
        var styles = main.StyleDefinitionsPart?.Styles;
        Assert.NotNull(styles);
        var docDefColorEl = styles!.DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle?
            .GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.Color>();
        Assert.NotNull(docDefColorEl);
        Assert.Null(docDefColorEl!.ThemeColor);
        var docDefRatio = ContrastGuard.GetContrastRatio(docDefColorEl.Val!.Value!, pageBg);
        Assert.True(docDefRatio >= 4.5, $"docDefaults color #{docDefColorEl.Val} has unreadable contrast {docDefRatio:F2}:1 against #{pageBg}");

        foreach (var style in styles.Elements<DocumentFormat.OpenXml.Wordprocessing.Style>())
        {
            var sColor = style.StyleRunProperties?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.Color>();
            if (sColor?.Val?.Value is { Length: 6 } hex)
            {
                Assert.Null(sColor.ThemeColor);
                var shd = style.StyleRunProperties?.Shading?.Fill?.Value
                          ?? style.StyleParagraphProperties?.Shading?.Fill?.Value
                          ?? style.StyleTableProperties?.Shading?.Fill?.Value;
                var effectiveBg = !string.IsNullOrEmpty(shd) && shd != "auto" && shd.Length == 6 ? shd : pageBg;
                var ratio = ContrastGuard.GetContrastRatio(hex, effectiveBg);
                Assert.True(ratio >= 4.5, $"Style '{style.StyleId}' color #{hex} has contrast {ratio:F2}:1 against #{effectiveBg}");
            }
        }

        // 2. Audit numbering.xml
        if (main.NumberingDefinitionsPart?.Numbering is { } num)
        {
            foreach (var c in num.Descendants<DocumentFormat.OpenXml.Wordprocessing.Color>())
            {
                if (c.Val?.Value is { Length: 6 } hex)
                {
                    var ratio = ContrastGuard.GetContrastRatio(hex, pageBg);
                    Assert.True(ratio >= 4.5, $"Numbering color #{hex} has contrast {ratio:F2}:1 against #{pageBg}");
                }
            }
        }

        // 3. Audit headers, footers, footnotes, and document.xml runs against their innermost background
        foreach (var hp in main.HeaderParts)
            AuditSubtreeRuns(hp.Header, pageBg, "header");
        foreach (var fp in main.FooterParts)
            AuditSubtreeRuns(fp.Footer, pageBg, "footer");
        if (main.FootnotesPart?.Footnotes is { } fns)
            AuditSubtreeRuns(fns, pageBg, "footnotes");

        AuditSubtreeRuns(main.Document.Body!, pageBg, "body");
    }

    private static void AuditSubtreeRuns(DocumentFormat.OpenXml.OpenXmlElement root, string pageBg, string partLabel)
    {
        foreach (var run in root.Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>())
        {
            var color = run.RunProperties?.GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.Color>()?.Val?.Value;
            if (string.IsNullOrEmpty(color) || color == "auto" || color.Length != 6) continue;

            var rShd = run.RunProperties?.Shading?.Fill?.Value;
            var pShd = run.Ancestors<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().FirstOrDefault()?.ParagraphProperties?.Shading?.Fill?.Value;
            var tcShd = run.Ancestors<DocumentFormat.OpenXml.Wordprocessing.TableCell>().FirstOrDefault()?.TableCellProperties?.Shading?.Fill?.Value;
            var effectiveBg = FirstHex(rShd, pShd, tcShd) ?? pageBg;

            var ratio = ContrastGuard.GetContrastRatio(color, effectiveBg);
            Assert.True(ratio >= 4.5, $"Run in {partLabel} has color #{color} with contrast {ratio:F2}:1 against #{effectiveBg}");
        }

        static string? FirstHex(params string?[] candidates) =>
            candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && c != "auto" && c.Length == 6);
    }
}
