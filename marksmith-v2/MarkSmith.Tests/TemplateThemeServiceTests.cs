using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Tests for TemplateThemeService: .dotx parsing, prompt engineering, and AI response parsing.
/// </summary>
public class TemplateThemeServiceTests
{
    // ---- ParseDotx ---------------------------------------------------------------------------

    [Fact]
    public void ParseDotx_extracts_fonts_and_colors_from_programmatic_dotx()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-dotx-{Guid.NewGuid():N}.dotx");
        try
        {
            CreateTestDotx(path, bodyFont: "Segoe UI", headingFont: "Georgia",
                h1Color: "1F4E79", h1SizeHalfPts: "56", accent1: "2E75B6", accent2: "ED7D31",
                dk1: "000000", lt1: "FFFFFF", hlink: "0563C1");

            var summary = TemplateThemeService.ParseDotx(path);

            Assert.Equal("Segoe UI", summary.BodyFont);
            Assert.Equal("Georgia", summary.HeadingFont);
            Assert.Equal("#1F4E79", summary.Heading1Color);
            Assert.Equal("28", summary.Heading1SizePt); // 56 half-pts = 28pt
            Assert.Equal("#2E75B6", summary.PrimaryAccent);
            Assert.Equal("#ED7D31", summary.SecondaryAccent);
            Assert.Equal("#FFFFFF", summary.Background);
            Assert.Equal("#0563C1", summary.HyperlinkColor);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void ParseDotx_defaults_when_styles_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mk-dotx-{Guid.NewGuid():N}.dotx");
        try
        {
            // Minimal docx with no theme part and no custom styles.
            using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Hello")))));
                main.Document.Save();
            }

            var summary = TemplateThemeService.ParseDotx(path);

            Assert.Equal("Calibri", summary.BodyFont); // default
            Assert.Equal("Calibri", summary.HeadingFont);
            Assert.Null(summary.Heading1Color);
            Assert.Null(summary.PrimaryAccent);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- BuildPrompt -------------------------------------------------------------------------

    [Fact]
    public void BuildPrompt_contains_style_facts()
    {
        var summary = new TemplateThemeService.TemplateStyleSummary(
            BodyFont: "Arial", HeadingFont: "Helvetica", Heading1Color: "#003366",
            Heading1SizePt: "24", PrimaryAccent: "#0066CC", SecondaryAccent: "#FF6600",
            Background: "#FAFAFA", HyperlinkColor: "#0563C1");

        var prompt = TemplateThemeService.BuildPrompt(summary);

        Assert.Contains("Arial", prompt);
        Assert.Contains("Helvetica", prompt);
        Assert.Contains("#003366", prompt);
        Assert.Contains("24pt", prompt);
        Assert.Contains("#0066CC", prompt);
        Assert.Contains("#FF6600", prompt);
        Assert.Contains("#FAFAFA", prompt);
        Assert.Contains("#0563C1", prompt);
        Assert.Contains("JSON", prompt);
        Assert.Contains("fontFamily", prompt);
    }

    [Fact]
    public void BuildPrompt_omits_null_facts()
    {
        var summary = new TemplateThemeService.TemplateStyleSummary(
            BodyFont: "Calibri", HeadingFont: "Calibri", Heading1Color: null,
            Heading1SizePt: null, PrimaryAccent: null, SecondaryAccent: null,
            Background: null, HyperlinkColor: null);

        var prompt = TemplateThemeService.BuildPrompt(summary);

        Assert.DoesNotContain("Heading 1 Color:", prompt);
        Assert.DoesNotContain("Primary Accent:", prompt);
        Assert.Contains("Body Font: Calibri", prompt);
    }

    // ---- ParseAiResponse ---------------------------------------------------------------------

    [Fact]
    public void ParseAiResponse_handles_clean_json()
    {
        var json = """{"name":"Test","background":"#FFFFFF","text":"#111111","heading":"#222222","code":"#F0F0F0","border":"#CCCCCC","primary":"#0066CC","secondary":"#004488","line":"#E0E0E0"}""";

        var theme = TemplateThemeService.ParseAiResponse(json);

        Assert.NotNull(theme);
        Assert.Equal("Test", theme!.Name);
        Assert.Equal("#FFFFFF", theme.Background);
        Assert.Equal("#111111", theme.Text);
        Assert.Equal("#0066CC", theme.Primary);
    }

    [Fact]
    public void ParseAiResponse_handles_code_fenced_json()
    {
        var reply = """
            Here's your theme:
            ```json
            {"name":"Fenced","background":"#FEFEFE","text":"#222222","heading":"#333333","code":"#F8F8F8","border":"#DDDDDD","primary":"#1177DD","secondary":"#0055AA","line":"#EEEEEE"}
            ```
            Hope that helps!
            """;

        var theme = TemplateThemeService.ParseAiResponse(reply);

        Assert.NotNull(theme);
        Assert.Equal("Fenced", theme!.Name);
        Assert.Equal("#FEFEFE", theme.Background);
        Assert.Equal("#1177DD", theme.Primary);
    }

    [Fact]
    public void ParseAiResponse_handles_prose_wrapped_json()
    {
        var reply = """
            Based on your corporate template, I recommend the following theme:

            {"name":"ProseWrap","background":"#FDFDFD","text":"#1A1A1A","heading":"#003366","code":"#F5F5F5","border":"#CCCCCC","primary":"#2E75B6","secondary":"#ED7D31","line":"#E8E8E8"}

            Let me know if you'd like adjustments.
            """;

        var theme = TemplateThemeService.ParseAiResponse(reply);

        Assert.NotNull(theme);
        Assert.Equal("ProseWrap", theme!.Name);
        Assert.Equal("#2E75B6", theme.Primary);
    }

    [Fact]
    public void ParseAiResponse_defaults_missing_fields()
    {
        // Only provide name and primary — everything else should default.
        var json = """{"name":"Sparse","primary":"#FF0000"}""";

        var theme = TemplateThemeService.ParseAiResponse(json);

        Assert.NotNull(theme);
        Assert.Equal("Sparse", theme!.Name);
        Assert.Equal("#FF0000", theme.Primary);
        Assert.Equal("#FFFFFF", theme.Background);  // default
        Assert.Equal("#1A1A1A", theme.Text);        // default
        Assert.Equal("#003366", theme.Heading);     // default
        Assert.Equal("#F5F5F5", theme.Code);        // default
        Assert.Equal("#DDDDDD", theme.Border);      // default
        Assert.Equal("#004488", theme.Secondary);   // default
        Assert.Equal("#E0E0E0", theme.Line);        // default
    }

    [Fact]
    public void ParseAiResponse_tolerates_trailing_commas()
    {
        var json = """{"name":"Trailing","background":"#FFFFFF","text":"#000000","heading":"#111111","code":"#F0F0F0","border":"#CCC","primary":"#00F","secondary":"#009","line":"#EEE",}""";

        var theme = TemplateThemeService.ParseAiResponse(json);

        Assert.NotNull(theme);
        Assert.Equal("Trailing", theme!.Name);
    }

    [Fact]
    public void ParseAiResponse_returns_null_for_garbage()
    {
        Assert.Null(TemplateThemeService.ParseAiResponse("I cannot help with that."));
        Assert.Null(TemplateThemeService.ParseAiResponse(""));
        Assert.Null(TemplateThemeService.ParseAiResponse(null));
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static void CreateTestDotx(string path, string bodyFont, string headingFont,
        string h1Color, string h1SizeHalfPts, string accent1, string accent2,
        string dk1, string lt1, string hlink)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("Test")))));

        // -- styles part --
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        var wNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        stylesPart.Styles = new W.Styles();
        stylesPart.Styles.AddNamespaceDeclaration("w", wNs);

        var docDefaults = new W.DocDefaults(
            new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                new W.RunFonts { Ascii = bodyFont, HighAnsi = bodyFont })));
        stylesPart.Styles.Append(docDefaults);

        var h1 = new W.Style { Type = W.StyleValues.Paragraph, StyleId = "Heading1" };
        h1.Append(new W.StyleName { Val = "heading 1" });
        h1.Append(new W.StyleRunProperties(
            new W.RunFonts { Ascii = headingFont, HighAnsi = headingFont },
            new W.Color { Val = h1Color },
            new W.FontSize { Val = h1SizeHalfPts }));
        stylesPart.Styles.Append(h1);
        stylesPart.Styles.Save();

        // -- theme part (typed API for reliable serialization) --
        var themePart = main.AddNewPart<ThemePart>();
        themePart.Theme = new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.RgbColorModelHex { Val = dk1 }),
                    new A.Light1Color(new A.RgbColorModelHex { Val = lt1 }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = accent1 }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = accent2 }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = hlink }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" })
                ) { Name = "Custom" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = headingFont }),
                    new A.MinorFont(new A.LatinFont { Typeface = bodyFont })
                ) { Name = "Office" },
                new A.FormatScheme(
                    new A.FillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                    new A.LineStyleList(new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 6350 }, new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 12700 }, new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 19050 }),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }), new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }))
                ) { Name = "Office" }
            ),
            new A.ObjectDefaults()
        ) { Name = "Test Theme" };
        themePart.Theme.Save();

        main.Document.Save();
    }
}
