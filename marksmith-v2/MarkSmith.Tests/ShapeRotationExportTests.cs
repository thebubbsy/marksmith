using System.Collections.Generic;
using MarkSmith.Core.Composer;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// ComposedShape.Rot is in degrees; DrawingML xfrm@rot is in 60,000ths of a degree. The writer
/// used to emit raw degrees, so every rotated shape (e.g. the Funnel preset's 180° trapezoids)
/// came out effectively unrotated in Word.
/// </summary>
public class ShapeRotationExportTests
{
    [Theory]
    [InlineData(0, 0L)]
    [InlineData(15, 900000L)]
    [InlineData(180, 10800000L)]
    [InlineData(-90, 16200000L)]
    [InlineData(360, 0L)]
    [InlineData(450, 5400000L)]
    public void DrawingMlRotation_converts_degrees_to_sixty_thousandths(int degrees, long expected)
    {
        Assert.Equal(expected, ShapeComposerDocxWriter.DrawingMlRotation(degrees));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(91, true)]
    [InlineData(180, true)]
    [InlineData(-180, true)]
    [InlineData(269, true)]
    [InlineData(270, false)]
    [InlineData(-30, false)]
    public void IsMostlyUpsideDown_flags_labels_past_a_quarter_turn(int degrees, bool expected)
    {
        Assert.Equal(expected, ShapeComposerDocxWriter.IsMostlyUpsideDown(degrees));
    }

    [Fact]
    public void Rotated_shape_xml_uses_drawingml_units_and_keeps_its_label_upright()
    {
        var shapes = new List<ComposedShape>
        {
            new() { Prst = "trapezoid", X = 1, Y = 1, W = 2, H = 0.8, Fill = "0078D4", Rot = 180, Text = "AWARENESS" },
            new() { Prst = "rect", X = 1, Y = 2, W = 2, H = 0.8, Fill = "107C41", Rot = 15, Text = "Tilted" },
        };

        string xml = ShapeComposerDocxWriter.BuildInlineXml(shapes, 4, 4);

        Assert.Contains(@"<a:xfrm rot=""10800000"">", xml);
        Assert.Contains(@"<a:xfrm rot=""900000"">", xml);
        Assert.DoesNotContain(@"<a:xfrm rot=""180"">", xml);
        // Only the upside-down shape's label is levelled.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(xml, @"upright=""1"""));
    }

    [Fact]
    public void Svg_preview_levels_upside_down_labels_too()
    {
        var shapes = new List<ComposedShape>
        {
            new() { Prst = "trapezoid", X = 0, Y = 0, W = 2, H = 1, Fill = "0078D4", Rot = 180, Text = "AWARENESS" },
        };

        string svg = ImageShapeComposer.RenderSvg(shapes, 2, 1);

        // The geometry still turns; the <text> carries no rotation.
        Assert.Contains("rotate(180", svg);
        var text = svg[svg.IndexOf("<text", System.StringComparison.Ordinal)..];
        Assert.DoesNotContain("rotate(", text[..text.IndexOf('>')]);
    }
}
