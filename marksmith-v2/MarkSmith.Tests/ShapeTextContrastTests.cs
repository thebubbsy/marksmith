using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MarkSmith.Core.Composer;
using MarkSmith.Plugins;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// The CONTRAST RULE for font on top of MLShape shapes: wherever a label is rendered on a filled
// shape (DOCX text box, SVG text, studio canvas), the label colour is guarded against the SHAPE'S
// FILL — never the page — so text can't land on a similar-coloured fill in any output.
public class ShapeTextContrastTests
{
    private static ComposedShape Labeled(string prst, string fill, string text, string? tcolor = null) => new()
    {
        Prst = prst, X = 0, Y = 0, W = 2, H = 1, Fill = fill, Text = text, TextColor = tcolor
    };

    [Fact]
    public void Docx_ShapeText_IsGuardedAgainstTheFill()
    {
        // Dark fill -> label forced to white (a dark label would vanish on it).
        string dark = ShapeComposerDocxWriter.BuildInlineXml(
            new List<ComposedShape> { Labeled("ellipse", "101418", "note") }, 2, 1);
        Assert.Contains("<w:color w:val=\"FFFFFF\"/>", dark);

        // Light fill -> label forced to dark.
        string light = ShapeComposerDocxWriter.BuildInlineXml(
            new List<ComposedShape> { Labeled("ellipse", "F5F5F5", "note") }, 2, 1);
        Assert.Contains("<w:color w:val=\"121212\"/>", light);

        // Mid-grey fill -> either white or dark, but NEVER a near-fill shade.
        string mid = ShapeComposerDocxWriter.BuildInlineXml(
            new List<ComposedShape> { Labeled("ellipse", "808080", "note") }, 2, 1);
        Assert.Matches("<w:color w:val=\"(FFFFFF|121212)\"/>", mid);

        // An EXPLICIT tcolor that clashes with the fill is still corrected, not honoured.
        string clash = ShapeComposerDocxWriter.BuildInlineXml(
            new List<ComposedShape> { Labeled("ellipse", "101418", "note", "0A0A0A") }, 2, 1);
        Assert.Contains("<w:color w:val=\"FFFFFF\"/>", clash);
    }

    [Fact]
    public void Docx_ShapeText_Package_IsSchemaValid()
    {
        string docx = Path.Combine(Path.GetTempPath(), $"shape-text-{System.Guid.NewGuid():N}.docx");
        try
        {
            ShapeComposerDocxWriter.WriteDocx(docx,
                new List<ComposedShape>
                {
                    Labeled("ellipse", "101418", "dark label"),
                    Labeled("roundrect", "F5F5F5", "light label"),
                    Labeled("chevron", "2E6FB7", "A & B <3")
                }, 3, 1.5, null);

            using var doc = WordprocessingDocument.Open(docx, false);
            var errors = new OpenXmlValidator().Validate(doc).ToList();
            Assert.Empty(errors);
        }
        finally { if (File.Exists(docx)) File.Delete(docx); }
    }

    [Fact]
    public void Svg_ShapeText_IsGuardedAgainstFill_AndMarked()
    {
        string svg = ImageShapeComposer.RenderSvg(
            new List<ComposedShape> { Labeled("ellipse", "101418", "note") }, 2, 1);
        // Label colour = white on the dark fill, tagged so the page rule leaves it alone.
        Assert.Contains("<text", svg);
        Assert.Contains("fill=\"#FFFFFF\"", svg);
        Assert.Contains("data-guarded=\"shape\"", svg);
        Assert.Contains(">note</text>", svg);

        // And it must SURVIVE the page-background guard (white page would otherwise flip it dark).
        string sanitized = SvgSanitizer.Sanitize(svg, "FFFFFF");
        Assert.Contains("fill=\"#FFFFFF\"", sanitized);
    }

    [Fact]
    public void EnsureSvgLegibility_SkipsShapeGuardedText_ButGuardsOthers()
    {
        string svg =
            "<svg><rect width=\"10\" height=\"10\" fill=\"#ffffff\"/>" +
            "<text x=\"1\" y=\"1\" fill=\"#FFFFFF\" data-guarded=\"shape\">on shape</text>" +
            "<text x=\"2\" y=\"2\" fill=\"#EEEEEE\">on page</text></svg>";

        string guarded = ContrastGuard.EnsureSvgLegibility(svg, "FFFFFF");
        // The shape-guarded white stays white; the unmarked near-white page text is flipped dark.
        Assert.Contains("<text x=\"1\" y=\"1\" fill=\"#FFFFFF\" data-guarded=\"shape\">on shape</text>", guarded);
        Assert.Contains("<text x=\"2\" y=\"2\" fill=\"#121212\">on page</text>", guarded);
    }

    [Fact]
    public void Shape_TextLabel_RoundTrips_ThroughMarkdown()
    {
        var shapes = new List<ComposedShape>
        {
            Labeled("ellipse", "101418", "Hello, Word! \"quoted\" & <tag>", "FFFFFF"),
            Labeled("roundrect", "F5F5F5", "plain"),
            Labeled("chevron", "0078D4", "no colour")
        };
        string block = ShapeMarkdownCodec.Serialize(shapes);
        var parsed = ShapeMarkdownCodec.Parse(block);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("Hello, Word! \"quoted\" & <tag>", parsed[0].Text);
        Assert.Equal("FFFFFF", parsed[0].TextColor);
        Assert.Equal("plain", parsed[1].Text);
        Assert.Null(parsed[1].TextColor);
        Assert.Equal("no colour", parsed[2].Text);
        Assert.Null(parsed[2].TextColor);

        // Round-trip is stable.
        Assert.Equal(block, ShapeMarkdownCodec.Serialize(parsed));
    }

    [Fact]
    public void Shape_TextLabel_DoesNotAppear_OnTracedLinesOrPlainShapes()
    {
        var line = new ComposedShape
        {
            Prst = "sketch", X = 0, Y = 0, W = 2, H = 0.02, Fill = "000000",
            PathPoints = new List<(double X, double Y)> { (0, 50), (100, 50) },
            StrokeWidthPt = 1.5, Text = "should not render"
        };
        string svg = ImageShapeComposer.RenderSvg(new List<ComposedShape> { line }, 2, 0.02);
        Assert.DoesNotContain("<text", svg); // thin strokes are not text containers

        string docx = ShapeComposerDocxWriter.BuildInlineXml(new List<ComposedShape> { line }, 2, 0.02);
        Assert.DoesNotContain("txbx", docx);
    }
}
