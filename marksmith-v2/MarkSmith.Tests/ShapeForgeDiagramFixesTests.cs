using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests;

// Regression coverage for two ShapeForge export bugs surfaced by product-spec.md:
//  (1) a dark document theme painted every diagram shape near-black ("mostly black diagram");
//  (2) harvested subgraph (cluster) containers collapsing onto the origin (0,0) in Word.
public class ShapeForgeDiagramFixesTests
{
    private static ThemeDefinition Dark => new ThemeCatalog().GetOrDefault("Dracula");
    private static ThemeDefinition Light => new ThemeCatalog().GetOrDefault("GitHub Light");

    // ---- Issue 1: diagrams follow the document theme (match the live preview) -----------------

    [Fact]
    public void ForDiagram_dark_theme_keeps_its_dark_palette()
    {
        var d = Dark.ForDiagram();
        // Diagrams follow the document theme instead of being forced light: a dark theme keeps its
        // dark canvas and node fills so Word matches the preview (ContrastGuard keeps text legible).
        Assert.False(ThemeDefinition.IsLight(d.Background), "diagram canvas must follow the dark theme");
        Assert.False(ThemeDefinition.IsLight(d.Code), "default node fill must follow the dark theme");
        Assert.False(ThemeDefinition.IsLight(d.Secondary), "default subgraph fill must follow the dark theme");
        Assert.Equal(Dark.Background, d.Background);
        Assert.Equal(Dark.Code, d.Code);
    }

    [Fact]
    public void ForDiagram_light_theme_stays_light()
    {
        var d = Light.ForDiagram();
        Assert.True(ThemeDefinition.IsLight(d.Background));
        Assert.True(ThemeDefinition.IsLight(d.Code));
    }

    [Fact]
    public void Emitter_dark_theme_uses_theme_node_fill()
    {
        var d = new MDiagram { Width = 120, Height = 60 };
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 10, Y = 10, W = 100, H = 40, Text = "Hi" });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Dark.ForDiagram(), 1, out _);

        // The node is filled with the dark theme's code colour (its default node fill) — diagrams
        // follow the document theme rather than being repainted a forced light grey.
        Assert.Contains("44475A", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Emitter_emits_theme_background_card()
    {
        var d = new MDiagram { Width = 120, Height = 60 };
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 10, Y = 10, W = 100, H = 40, Text = "Hi" });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Dark.ForDiagram(), 1, out _);

        // A full-bleed card in the theme's own background sits behind the shapes so the page never
        // bleeds through the group's transparent canvas — for a dark theme that card is dark.
        Assert.Contains("Diagram background", xml);
        Assert.Contains("282A36", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Emitter_background_card_is_not_recovered_as_roundtrip_node()
    {
        var d = new MDiagram { Width = 200, Height = 120 };
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 100, Y = 100, W = 60, H = 30, Text = "Real" });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Light.ForDiagram(), 1, out _);
        var drawing = new DocumentFormat.OpenXml.Wordprocessing.Drawing { InnerXml = xml };

        var mermaid = DocxShapeParser.TryParseMermaid(drawing);

        // The decoration must not surface as a node; the genuine shape still round-trips.
        Assert.DoesNotContain("__bg__", mermaid ?? "");
        Assert.DoesNotContain("background", mermaid ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Real", mermaid);
    }

    // ---- Issue 3: the harvest canvas must never collapse smaller than its content --------------

    [Fact]
    public void ToMDiagram_canvas_grows_to_enclose_content_when_harvested_size_is_degenerate()
    {
        // A state-diagram render that omits its viewBox used to harvest W=H=0 -> a 1pt canvas: a
        // tiny backdrop card while the shapes, harvested at their real coordinates, spilled off the
        // right edge of the page. The canvas must grow to enclose its content instead.
        var g = new GenericDiagram { W = 0, H = 0 };
        g.Nodes.Add(new GNode { X = 0, Y = 0, W = 200, H = 60, Kind = "Rect", Fill = "#1a2f1a", Stroke = "#78a75a" });
        g.Nodes.Add(new GNode { X = 700, Y = 300, W = 220, H = 60, Kind = "Rect", Fill = "#1a2f1a", Stroke = "#78a75a" });

        var d = g.ToMDiagram(Light);

        Assert.True(d.Width >= 920, $"canvas width {d.Width} must enclose content reaching x=920");
        Assert.True(d.Height >= 360, $"canvas height {d.Height} must enclose content reaching y=360");
    }

    [Fact]
    public void ToMDiagram_canvas_grows_to_enclose_edge_points_when_harvested_size_is_degenerate()
    {
        var g = new GenericDiagram { W = 0, H = 0 };
        g.Edges.Add(new GEdge { Points = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 500.0, 250.0 } }, Stroke = "#78a75a" });

        var d = g.ToMDiagram(Light);

        Assert.True(d.Width >= 500, $"canvas width {d.Width} must enclose the edge reaching x=500");
        Assert.True(d.Height >= 250, $"canvas height {d.Height} must enclose the edge reaching y=250");
    }

    // ---- Issue 2: harvested subgraphs must not collapse to the origin --------------------------

    [Fact]
    public void ToMDiagram_subgraph_collapsed_to_origin_is_grown_to_enclose_members()
    {
        var h = new HarvestedDiagram { W = 400, H = 300 };
        // Old-harvester bug: the cluster centre read (0,0); its members live far away.
        h.Nodes.Add(new HNode { Id = "sg", Kind = "Subgraph", Label = "Group", Cx = 0, Cy = 0, W = 100, H = 40 });
        h.Nodes.Add(new HNode { Id = "a", Kind = "Rect", Label = "Node A", Cx = 100, Cy = 100, W = 60, H = 30 });
        h.Nodes.Add(new HNode { Id = "b", Kind = "Rect", Label = "Node B", Cx = 300, Cy = 200, W = 60, H = 30 });

        var d = h.ToMDiagram(Light);
        var sg = d.Shapes.First(s => s.Kind == ShapeKind.Subgraph);
        var a = d.Shapes.First(s => s.Text == "Node A");

        // No longer stranded at the top-left corner; grown to actually contain its member.
        Assert.True(sg.X + sg.W > a.X, "subgraph must reach its member's left edge");
        Assert.True(sg.Y + sg.H > a.Y, "subgraph must reach its member's top edge");
        Assert.True(sg.W > 100 && sg.H > 40, "subgraph must be grown beyond its harvested 0-size box");
    }

    [Fact]
    public void ToMDiagram_correctly_placed_subgraph_is_left_where_harvested()
    {
        var h = new HarvestedDiagram { W = 400, H = 300 };
        // Post-fix harvest: the cluster carries a real centre away from the origin.
        h.Nodes.Add(new HNode { Id = "sg", Kind = "Subgraph", Label = "Group", Cx = 200, Cy = 150, W = 200, H = 120 });
        h.Nodes.Add(new HNode { Id = "a", Kind = "Rect", Label = "Node A", Cx = 200, Cy = 150, W = 60, H = 30 });

        var d = h.ToMDiagram(Light);
        var sg = d.Shapes.First(s => s.Kind == ShapeKind.Subgraph);

        Assert.Equal(100, sg.X, 1); // 200 - 200/2
        Assert.Equal(90, sg.Y, 1);  // 150 - 120/2
        Assert.Equal(200, sg.W, 1);
        Assert.Equal(120, sg.H, 1);
    }

    [Fact]
    public void ToMDiagram_subgraphs_paint_behind_member_nodes()
    {
        var h = new HarvestedDiagram { W = 400, H = 300 };
        // Clusters harvest first in DOM order; the emitter draws list order back-to-front, so the
        // converted diagram must place regular nodes BEFORE subgraphs to keep containers behind.
        h.Nodes.Add(new HNode { Id = "sg", Kind = "Subgraph", Label = "Group", Cx = 200, Cy = 150, W = 200, H = 120 });
        h.Nodes.Add(new HNode { Id = "a", Kind = "Rect", Label = "Node A", Cx = 200, Cy = 150, W = 60, H = 30 });

        var d = h.ToMDiagram(Light);
        var sgIndex = d.Shapes.FindIndex(s => s.Kind == ShapeKind.Subgraph);
        var nodeIndex = d.Shapes.FindIndex(s => s.Text == "Node A");

        Assert.True(nodeIndex < sgIndex, "regular nodes must be emitted before (behind-painted-by) subgraphs");
    }

    [Fact]
    public void Emitter_subgraph_container_is_emitted_behind_its_member_nodes()
    {
        // Word paints later children on top, so the opaque subgraph panel must be emitted BEFORE
        // the node boxes it contains — otherwise it covers them (the reported bug where a subgraph
        // sat on top of the smaller squares, fixed manually by send-to-back + bring-forward-once).
        var d = new MDiagram { Width = 300, Height = 200 };
        d.Shapes.Add(new MShape { Kind = ShapeKind.Subgraph, X = 10, Y = 10, W = 280, H = 180, Text = "Payments Core" });
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 30, Y = 40, W = 100, H = 30, Text = "Payments API" });
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 30, Y = 90, W = 100, H = 30, Text = "Risk Engine" });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Light.ForDiagram(), 1, out _);

        int subgraphPos = xml.IndexOf("Payments Core", StringComparison.Ordinal);
        int apiPos = xml.IndexOf("Payments API", StringComparison.Ordinal);
        int riskPos = xml.IndexOf("Risk Engine", StringComparison.Ordinal);

        Assert.True(subgraphPos >= 0 && apiPos >= 0 && riskPos >= 0);
        Assert.True(subgraphPos < apiPos, "subgraph container must be emitted before (behind) its member nodes");
        Assert.True(subgraphPos < riskPos, "subgraph container must be emitted before (behind) its member nodes");
    }

    // ---- Overlay labels must contrast the shape they sit on, not the page ----------------------
    // Harvested diagrams (state, C4, kanban, ...) rebuild labels as separate Text shapes floating
    // over the node boxes. Nearly every shape carries words, so the emitter's ContrastGuard must
    // judge each label against the fill of the node beneath it — not the page background — or a
    // label can be "corrected" into a colour that clashes with its own box.

    [Fact]
    public void ToMDiagram_label_on_dark_node_keeps_its_light_text()
    {
        // A dark-green state box harvested with white label text. Judged against the page (white)
        // the guard would wrongly "fix" the white text to black — unreadable on the dark box.
        // Judged against the box's own fill, the white text passes 4.5:1 and must be kept.
        var g = new GenericDiagram { W = 300, H = 120 };
        g.Nodes.Add(new GNode { X = 20, Y = 20, W = 200, H = 60, Kind = "Rect", Fill = "rgb(27, 94, 32)", Stroke = "rgb(0, 0, 0)" });
        g.Texts.Add(new GText { X = 60, Y = 40, W = 120, H = 20, Text = "Authorized", Color = "rgb(255, 255, 255)" });

        var d = g.ToMDiagram(Light);
        var xml = DocxShapeEmitter.ToParagraphXml(d, Light.ForDiagram(), 1, out _);

        Assert.Equal("FFFFFF", LabelColor(xml), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToMDiagram_label_on_light_node_is_darkened_when_text_is_too_close()
    {
        // A white state box on a dark page, harvested with a light-grey label ("not black enough").
        // Judged against the dark page the grey would pass and stay illegible on the white box;
        // judged against the box's own white fill it fails 4.5:1 and must be forced dark.
        var g = new GenericDiagram { W = 300, H = 120 };
        g.Nodes.Add(new GNode { X = 20, Y = 20, W = 200, H = 60, Kind = "Rect", Fill = "rgb(255, 255, 255)", Stroke = "rgb(98, 114, 164)" });
        g.Texts.Add(new GText { X = 60, Y = 40, W = 120, H = 20, Text = "Captured", Color = "rgb(170, 170, 170)" });

        var d = g.ToMDiagram(Dark);
        var xml = DocxShapeEmitter.ToParagraphXml(d, Dark.ForDiagram(), 1, out _);

        var color = LabelColor(xml);
        Assert.NotEqual("AAAAAA", color, StringComparer.OrdinalIgnoreCase);
        Assert.True(ContrastGuard.GetContrastRatio(color, "FFFFFF") >= 4.5,
            $"label colour #{color} must be legible (>=4.5:1) on the white node it sits on");
    }

    // The emitted colour of the harvested label. Generic-harvest node boxes carry no text of their
    // own and the background card has none either, so the only w:color in the drawing belongs to
    // the overlay label — the first match is it.
    private static string LabelColor(string xml)
    {
        var m = System.Text.RegularExpressions.Regex.Match(xml, "<w:color w:val=\"([0-9A-Fa-f]{6})\"");
        Assert.True(m.Success, "expected a text-run colour in the emitted diagram");
        return m.Groups[1].Value.ToUpperInvariant();
    }

    // ---- Aggressive shrink must keep edges glued to their nodes ------------------------------
    // The oversized "shrink spacing / shapes / both" modes (6/7/8) re-space the nodes and throw
    // away the harvested curve path. The edge therefore has to be re-anchored as a Word smart
    // connector (a:stCxn/a:endCxn) so it stays attached to its nodes; before the fix the anchors
    // were only inferred from the curve AFTER it was discarded, so nothing was ever connected.

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Emitter_aggressive_shrink_keeps_edges_connected_to_nodes(int mode)
    {
        // A diagram too large for the page, with one edge running from node A's right border to
        // node B's left border (sampled curve points, as the harvester produces).
        var d = new MDiagram { Width = 2000, Height = 1000 };
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 0, Y = 0, W = 100, H = 50, Text = "A" });
        d.Shapes.Add(new MShape { Kind = ShapeKind.Rect, X = 1900, Y = 950, W = 100, H = 50, Text = "B" });
        d.Connectors.Add(new MConnector
        {
            X1 = 100, Y1 = 25, X2 = 1900, Y2 = 975,
            Points = new[] { (100.0, 25.0), (1000.0, 500.0), (1900.0, 975.0) },
        });

        var xml = DocxShapeEmitter.ToParagraphXml(d, Light.ForDiagram(), 1, out _,
            oversizedMode: mode, smartConnectors: true);

        // Both ends must be glued to a shape — without stCxn/endCxn the edge is a bare line whose
        // coordinates no longer match the re-spaced nodes, i.e. it floats free of the diagram.
        Assert.Contains("<a:stCxn", xml);
        Assert.Contains("<a:endCxn", xml);
    }
}
