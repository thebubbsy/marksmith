using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.Services.Mermaid;
using Xunit;

namespace MarkSmith.Core.Tests;

// SvgShapeForge (plugin SVG -> native-Word-shape primitives) and the custom-theme store.
public class ShapeForgeAndThemeTests
{
    private static string Svg(string body, int w = 400, int h = 300) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\">{body}</svg>";

    [Fact] public void Rect_becomes_node()
    {
        var d = SvgShapeForge.Parse(Svg("<rect x=\"10\" y=\"10\" width=\"80\" height=\"40\" fill=\"#fff\"/><text x=\"20\" y=\"30\">A</text>"))!;
        Assert.Single(d.Nodes); Assert.Equal("Rect", d.Nodes[0].Kind);
    }
    [Fact] public void Rounded_rect_detected()
    {
        var d = SvgShapeForge.Parse(Svg("<rect x=\"1\" y=\"1\" width=\"80\" height=\"40\" rx=\"6\"/><text x=\"5\" y=\"20\">A</text>"))!;
        Assert.Equal("RoundRect", d.Nodes[0].Kind);
    }
    [Fact] public void Ellipse_becomes_node()
    {
        var d = SvgShapeForge.Parse(Svg("<ellipse cx=\"50\" cy=\"50\" rx=\"40\" ry=\"20\"/><text x=\"30\" y=\"50\">E</text>"))!;
        Assert.Equal("Ellipse", d.Nodes[0].Kind);
    }
    [Fact] public void Root_translate_applied()
    {
        var d = SvgShapeForge.Parse(Svg("<g transform=\"translate(4 296)\"><ellipse cx=\"50\" cy=\"-50\" rx=\"30\" ry=\"15\"/><text x=\"30\" y=\"-45\">N</text></g>"))!;
        Assert.True(d.Nodes[0].Y >= 0, "graphviz-style negative-Y coords must land in view after the root translate");
    }
    [Fact] public void Small_filled_polygon_is_arrowhead_not_node()
    {
        var d = SvgShapeForge.Parse(Svg(
            "<path fill=\"none\" stroke=\"#000\" d=\"M 10,50 L 90,50\"/>" +
            "<polygon fill=\"#000\" points=\"90,46 98,50 90,54\"/>" +
            "<rect x=\"100\" y=\"20\" width=\"60\" height=\"30\"/><text x=\"110\" y=\"40\">B</text>"))!;
        Assert.Single(d.Nodes);                 // the rect only — the tiny triangle is not a node
        Assert.True(d.Edges[0].Arrow);          // ...but it proves the edge is directed
    }
    [Fact] public void Edge_without_arrowhead_stays_plain()
    {
        var d = SvgShapeForge.Parse(Svg(
            "<line x1=\"0\" y1=\"10\" x2=\"100\" y2=\"10\" stroke=\"#000\"/>" +
            "<rect x=\"10\" y=\"30\" width=\"60\" height=\"30\"/><text x=\"20\" y=\"50\">L</text>"))!;
        Assert.False(d.Edges[0].Arrow);
    }
    [Fact] public void Marker_end_counts_as_arrow()
    {
        var d = SvgShapeForge.Parse(Svg(
            "<defs><marker id=\"m\"><polygon points=\"0,0 4,2 0,4\"/></marker></defs>" +
            "<path fill=\"none\" stroke=\"#000\" marker-end=\"url(#m)\" d=\"M 0,0 L 50,50\"/>" +
            "<rect x=\"10\" y=\"60\" width=\"60\" height=\"30\"/><text x=\"20\" y=\"80\">M</text>"))!;
        Assert.True(d.Edges[0].Arrow);
    }
    [Fact] public void Marker_defs_do_not_leak_shapes()
    {
        var d = SvgShapeForge.Parse(Svg(
            "<defs><rect x=\"0\" y=\"0\" width=\"500\" height=\"500\"/></defs>" +
            "<rect x=\"10\" y=\"10\" width=\"60\" height=\"30\"/><text x=\"20\" y=\"30\">D</text>"))!;
        Assert.Single(d.Nodes);
    }
    [Fact] public void Background_rect_skipped()
    {
        var d = SvgShapeForge.Parse(Svg(
            "<rect x=\"0\" y=\"0\" width=\"400\" height=\"300\" fill=\"#fff\"/>" +
            "<rect x=\"10\" y=\"10\" width=\"60\" height=\"30\"/><text x=\"20\" y=\"30\">C</text>"))!;
        Assert.Single(d.Nodes);
    }
    [Fact] public void Glyph_soup_returns_null_for_picture_fallback() =>
        Assert.Null(SvgShapeForge.Parse(Svg("<path fill=\"#000\" d=\"M 1,1 L 3,3\"/>")));
    [Fact] public void Tspan_text_positions_each_line()
    {
        var d = SvgShapeForge.Parse(Svg("<rect x=\"5\" y=\"5\" width=\"90\" height=\"60\"/><text font-size=\"12\"><tspan x=\"10\" y=\"20\">one</tspan><tspan x=\"10\" y=\"36\">two</tspan></text>"))!;
        Assert.Equal(2, d.Texts.Count);
    }
    [Fact] public void Generic_to_mdiagram_respects_arrow_flag()
    {
        var g = new GenericDiagram { W = 100, H = 100 };
        g.Edges.Add(new GEdge { Points = { new[] { 0.0, 0.0 }, new[] { 50.0, 50.0 } }, Arrow = false });
        var d = g.ToMDiagram(new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Equal(ArrowHead.None, d.Connectors[0].EndHead);
    }

    // ---- custom themes ------------------------------------------------------------------------
    [Fact] public void Custom_theme_roundtrip_visible_to_all_catalogs()
    {
        var name = $"Test Theme {Guid.NewGuid():N}";
        try
        {
            CustomThemeStore.AddOrUpdate(new ThemeDefinition(name, "#111111", "#eeeeee", "#ff0000", "#222222", "#333333", "#eeeeee", "#222222", "#888888"));
            Assert.Contains(new ThemeCatalog().All, t => t.Name == name);           // fresh catalog sees it
            Assert.Equal("#ff0000", new ThemeCatalog().GetOrDefault(name).Heading); // and resolves it
        }
        finally { CustomThemeStore.Remove(name); }
    }
    [Fact] public void Builtin_names_are_flagged()
    {
        var c = new ThemeCatalog();
        Assert.True(c.IsBuiltin("Nordic"));
        Assert.False(c.IsBuiltin("Definitely Not A Theme"));
    }
    [Fact] public void Removed_theme_gone()
    {
        var name = $"Gone {Guid.NewGuid():N}";
        CustomThemeStore.AddOrUpdate(new ThemeDefinition(name, "#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8"));
        CustomThemeStore.Remove(name);
        Assert.DoesNotContain(new ThemeCatalog().All, t => t.Name == name);
    }
}
