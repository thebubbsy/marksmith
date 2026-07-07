using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

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
