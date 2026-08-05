using DocumentFormat.OpenXml.Wordprocessing;
using MarkSmith.Models;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Guards the hand-built wps/wpg XML in DocxShapeEmitter: every emitted paragraph must parse as
/// well-formed XML (a malformed attribute like the old "<a:cap flat/>" killed the whole DOCX
/// export with "'/' is an unexpected token. The expected token is '='.").
/// </summary>
public class DocxShapeEmitterXmlTests
{
    private static ThemeDefinition Theme() =>
        new("T", "#ffffff", "#1b1f23", "#000000", "#f6f8fa", "#d1d5da", "#000000", "#f6f8fa", "#333333");

    [Fact]
    public void StraightTwoPointConnector_WithoutHeads_EmitsFlatCap_NotCapFlat()
    {
        var d = new MDiagram();
        d.Connectors.Add(new MConnector
        {
            X1 = 10, Y1 = 10, X2 = 100, Y2 = 10,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None,
            // SvgShapeForge produces 2-point traced connectors — this is the exact path that
            // emitted the malformed "<a:cap flat/>" (valueless attribute) for PlantUML arrows.
            Points = new[] { (10.0, 10.0), (100.0, 10.0) },
        });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Theme(), 1, out _,
            smartConnectors: false, connectorRouting: "default");

        // The regression: "<a:cap flat/>" was emitted (a valueless attribute) and the XML
        // reader threw "'/' is an unexpected token. The expected token is '='." on load.
        Assert.Contains("<a:flat/>", xml);
        Assert.DoesNotContain("<a:cap flat", xml);

        // Must round-trip through the OpenXML SDK without throwing.
        var p = new Paragraph { InnerXml = xml };
        Assert.NotNull(p);
    }

    [Fact]
    public void ArrowHeadConnector_EmitsHeadAndTailEnds()
    {
        var d = new MDiagram();
        d.Connectors.Add(new MConnector
        {
            X1 = 0, Y1 = 0, X2 = 50, Y2 = 50,
            StartHead = ArrowHead.None, EndHead = ArrowHead.Triangle,
        });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Theme(), 1, out _,
            smartConnectors: false, connectorRouting: "default");

        Assert.Contains("<a:headEnd", xml);
        Assert.Contains("<a:tailEnd", xml);
        var p = new Paragraph { InnerXml = xml };
        Assert.NotNull(p);
    }

    [Fact]
    public void DashedConnector_EmitsPrstDash()
    {
        var d = new MDiagram();
        d.Connectors.Add(new MConnector
        {
            X1 = 0, Y1 = 0, X2 = 50, Y2 = 0,
            Dashed = true, StartHead = ArrowHead.None, EndHead = ArrowHead.None,
            Points = new[] { (0.0, 0.0), (50.0, 0.0) },
        });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Theme(), 1, out _,
            smartConnectors: false, connectorRouting: "default");

        Assert.Contains("<a:prstDash val=\"dash\"/>", xml);
        var p = new Paragraph { InnerXml = xml };
        Assert.NotNull(p);
    }
}
