using System.Text.RegularExpressions;
using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

// ---- Generic diagram harvest (the "no fallback" path) --------------------------------------------
// Every Mermaid diagram type — state, C4, requirement, block, kanban, packet, sankey, architecture,
// and future ones — renders to the same SVG primitives (shapes + paths + text). Instead of a
// hand-written parser+layout per family, we harvest those primitives from mermaid's own rendered
// SVG (positions, sizes, mermaid's actual fill/stroke colors, curved edge points) and rebuild them
// as native, editable Word shapes. If mermaid can draw it, ShapeForge can reproduce it.

public sealed class GNode
{
    public double X { get; set; }   // top-left, px (root coords)
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Kind { get; set; } = "Rect"; // Rect|RoundRect|Diamond|Ellipse|Circle
    public string? Fill { get; set; }   // css color from mermaid, or null
    public string? Stroke { get; set; }
}

public sealed class GEdge
{
    public List<double[]> Points { get; set; } = new(); // sampled along the path, px
    public string? Stroke { get; set; }
    public bool Dashed { get; set; }
}

public sealed class GText
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Text { get; set; } = "";
    public string? Color { get; set; }
}

public sealed class GenericDiagram
{
    public double W { get; set; }
    public double H { get; set; }
    public List<GNode> Nodes { get; set; } = new();
    public List<GEdge> Edges { get; set; } = new();
    public List<GText> Texts { get; set; } = new();

    public bool IsEmpty => Nodes.Count == 0 && Edges.Count == 0 && Texts.Count == 0;

    // Rebuild the harvested primitives as an MDiagram: colored shapes, curved connectors, and text
    // overlays positioned exactly where mermaid placed them.
    public MDiagram ToMDiagram(ThemeDefinition theme)
    {
        var d = new MDiagram { Width = Math.Max(1, W), Height = Math.Max(1, H) };

        foreach (var n in Nodes)
        {
            var kind = n.Kind switch
            {
                "RoundRect" => ShapeKind.RoundRect,
                "Diamond" => ShapeKind.Diamond,
                "Ellipse" => ShapeKind.Ellipse,
                "Circle" => ShapeKind.Circle,
                _ => ShapeKind.Rect,
            };
            d.Shapes.Add(new MShape
            {
                Kind = kind,
                X = n.X, Y = n.Y, W = Math.Max(1, n.W), H = Math.Max(1, n.H),
                Fill = Css(n.Fill), Stroke = Css(n.Stroke),
            });
        }

        foreach (var e in Edges)
        {
            if (e.Points.Count < 2) continue;
            d.Connectors.Add(new MConnector
            {
                X1 = e.Points[0][0], Y1 = e.Points[0][1],
                X2 = e.Points[^1][0], Y2 = e.Points[^1][1],
                Points = e.Points.Select(p => (p[0], p[1])).ToList(),
                Stroke = Css(e.Stroke), Dashed = e.Dashed, EndHead = ArrowHead.Triangle,
            });
        }

        foreach (var t in Texts)
        {
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text,
                X = t.X, Y = t.Y, W = Math.Max(8, t.W + 4), H = Math.Max(8, t.H),
                Text = t.Text.Replace("\r", ""),
                TextColor = Css(t.Color),
                FontSize = Math.Clamp(t.H * 0.62, 6, 14),
            });
        }
        return d;
    }

    // css "rgb(r, g, b)" / "rgba(...)" / "#rrggbb" -> "#RRGGBB"; "none"/transparent/empty -> null.
    private static string? Css(string? c)
    {
        if (string.IsNullOrEmpty(c) || c == "none" || c == "transparent") return null;
        var m = Regex.Match(c, @"rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([0-9.]+))?");
        if (m.Success)
        {
            if (m.Groups[4].Success && double.TryParse(m.Groups[4].Value, out var a) && a < 0.05) return null; // fully transparent
            return $"#{int.Parse(m.Groups[1].Value):X2}{int.Parse(m.Groups[2].Value):X2}{int.Parse(m.Groups[3].Value):X2}";
        }
        return c.StartsWith('#') ? c : null;
    }
}

// The "exact layout" path. Instead of ShapeForge running its OWN layered layout (which reorders a
// diagram to fit the page), we harvest the geometry mermaid.js itself computed — the exact node
// positions, sizes and edge endpoints from the rendered SVG — and rebuild THAT as native Word
// shapes. The result matches the preview node-for-node. Fed by MainWindow's WebView harvester
// (HarvestMermaidGeometryAsync); converted to an MDiagram here and emitted by DocxShapeEmitter.
//
// Units: mermaid works in CSS px with the origin top-left; we carry px straight through as pt
// (DocxShapeEmitter scales the whole group to fit / flags oversized), so 1 px -> 1 pt.

public sealed class HNode
{
    public string Id { get; set; } = "";
    public double Cx { get; set; }   // center, px
    public double Cy { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Kind { get; set; } = "Rect"; // Rect|RoundRect|Diamond|Ellipse|Circle|Hexagon|Cylinder
    public string Label { get; set; } = "";     // '\n' between wrapped lines
}

public sealed class HEdge
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public bool Dashed { get; set; }
    public string? Label { get; set; }
    public double Lx { get; set; }   // label center, px
    public double Ly { get; set; }
    public List<double[]> Points { get; set; } = new(); // sampled [x,y] along mermaid's curve, px
}

public sealed class HarvestedDiagram
{
    public double W { get; set; }    // viewBox size, px
    public double H { get; set; }
    public List<HNode> Nodes { get; set; } = new();
    public List<HEdge> Edges { get; set; } = new();

    public bool IsEmpty => Nodes.Count == 0;

    // Convert mermaid's harvested geometry into the emitter's MDiagram, preserving positions exactly.
    public MDiagram ToMDiagram(ThemeDefinition theme)
    {
        var d = new MDiagram { Width = Math.Max(1, W), Height = Math.Max(1, H) };

        foreach (var n in Nodes)
        {
            // Fall back to a label-derived box when the harvest couldn't measure the shape (0-size).
            double w = n.W, h = n.H;
            if (w < 2 || h < 2)
            {
                var lines = string.IsNullOrEmpty(n.Label) ? new[] { "" } : n.Label.Split('\n');
                int longest = lines.Max(l => l.Length);
                w = Math.Clamp(24 + longest * 7.0, 60, 260);
                h = 30 + Math.Max(0, lines.Length - 1) * 15;
            }
            var kind = n.Kind switch
            {
                "RoundRect" => ShapeKind.RoundRect,
                "Diamond" => ShapeKind.Diamond,
                "Ellipse" => ShapeKind.Ellipse,
                "Circle" => ShapeKind.Circle,
                "Hexagon" => ShapeKind.Hexagon,
                "Cylinder" => ShapeKind.Cylinder,
                _ => ShapeKind.Rect,
            };
            d.Shapes.Add(new MShape
            {
                Kind = kind,
                X = n.Cx - w / 2,
                Y = n.Cy - h / 2,
                W = w,
                H = h,
                Text = n.Label,
                FontSize = 9,
            });
        }

        foreach (var e in Edges)
        {
            var c = new MConnector
            {
                X1 = e.X1, Y1 = e.Y1, X2 = e.X2, Y2 = e.Y2,
                Dashed = e.Dashed,
                EndHead = ArrowHead.Triangle,
            };
            // Follow mermaid's exact curve when we harvested its path points; else a straight line.
            if (e.Points is { Count: >= 2 })
                c.Points = e.Points.Select(p => (p[0], p[1])).ToList();
            if (!string.IsNullOrWhiteSpace(e.Label))
            {
                double lw = Math.Clamp(10 + e.Label!.Length * 5.0, 24, 160);
                c.Label = e.Label;
                c.LabelW = lw; c.LabelH = 15;
                c.LabelX = e.Lx - lw / 2;
                c.LabelY = e.Ly - 7.5;
            }
            d.Connectors.Add(c);
        }

        return d;
    }
}
