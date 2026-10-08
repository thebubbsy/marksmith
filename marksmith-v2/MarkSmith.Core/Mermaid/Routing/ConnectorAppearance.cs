using System.Globalization;
using System.Text;

namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>What sits on one end of a Diagram Studio connector.</summary>
public enum ConnectorMarker
{
    None,
    /// <summary>Filled triangle (flowchart <c>--&gt;</c>, sequence <c>-&gt;&gt;</c>, class association).</summary>
    Arrow,
    /// <summary>Two open strokes (sequence async <c>-)</c>).</summary>
    OpenArrow,
    /// <summary>Filled dot (flowchart <c>--o</c>).</summary>
    Circle,
    /// <summary>An X (flowchart <c>--x</c>, sequence <c>-x</c>).</summary>
    Cross,
    /// <summary>Hollow triangle (class inheritance / realization).</summary>
    HollowTriangle,
    /// <summary>Hollow diamond (class aggregation).</summary>
    HollowDiamond,
    /// <summary>Filled diamond (class composition).</summary>
    FilledDiamond,
}

/// <summary>How a connector is drawn: dash, weight and the marker on each end.</summary>
public readonly record struct ConnectorLook(bool Dashed, double WidthScale, ConnectorMarker Start, ConnectorMarker End);

/// <summary>A marker's outline, ready for a XAML <c>Path.Data</c>, and whether it is filled with
/// the line colour (true) or with the canvas background so the line doesn't show through (false).
/// Stroke-only markers (open arrow, cross) are never filled.</summary>
public readonly record struct ConnectorMarkerShape(string PathData, bool FilledWithStroke, bool StrokeOnly);

/// <summary>
/// The one place that turns a Diagram Studio connector's stored style strings into what the
/// canvas draws. The strings come from three different Mermaid grammars: flowchart edges keep
/// <c>LineStyle</c> (Solid/Dashed/Thick) and <c>EndHead</c> (Normal/Cross/Circle/None); sequence
/// messages keep their <c>SequenceMessageType</c> name in <c>LineStyle</c>; class relationships keep
/// their <c>ClassRelationshipType</c> name in <c>EndHead</c>. Class relationships are stored in the
/// generator's canonical left-pointing form (<c>Animal &lt;|-- Dog</c>), so their UML marker sits
/// on the SOURCE end.
/// </summary>
public static class ConnectorAppearance
{
    public static ConnectorLook Resolve(string? lineStyle, string? startHead, string? endHead)
    {
        lineStyle ??= "Solid";
        endHead ??= "Normal";

        // Sequence messages: the message type carries both the dash and the head.
        switch (lineStyle)
        {
            case "SolidArrow": return new(false, 1, ConnectorMarker.None, ConnectorMarker.Arrow);
            case "DashedArrow": return new(true, 1, ConnectorMarker.None, ConnectorMarker.Arrow);
            case "SolidOpen": return new(false, 1, ConnectorMarker.None, ConnectorMarker.None);
            case "DashedOpen": return new(true, 1, ConnectorMarker.None, ConnectorMarker.None);
            case "CrossArrow": return new(false, 1, ConnectorMarker.None, ConnectorMarker.Cross);
            case "PointArrow": return new(false, 1, ConnectorMarker.None, ConnectorMarker.OpenArrow);
            case "DashedCross": return new(true, 1, ConnectorMarker.None, ConnectorMarker.Cross);
            case "DashedPoint": return new(true, 1, ConnectorMarker.None, ConnectorMarker.OpenArrow);
        }

        // Class relationships: the relationship type carries the dash and the (source-end) marker.
        switch (endHead)
        {
            case "Inheritance": return new(false, 1, ConnectorMarker.HollowTriangle, ConnectorMarker.None);
            case "Realization": return new(true, 1, ConnectorMarker.HollowTriangle, ConnectorMarker.None);
            case "Aggregation": return new(false, 1, ConnectorMarker.HollowDiamond, ConnectorMarker.None);
            case "Composition": return new(false, 1, ConnectorMarker.FilledDiamond, ConnectorMarker.None);
            case "Association": return new(false, 1, ConnectorMarker.None, ConnectorMarker.Arrow);
            case "Dependency": return new(true, 1, ConnectorMarker.None, ConnectorMarker.Arrow);
        }

        bool dashed = string.Equals(lineStyle, "Dashed", StringComparison.OrdinalIgnoreCase);
        double width = string.Equals(lineStyle, "Thick", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        return new(dashed, width, HeadMarker(startHead, ConnectorMarker.None), HeadMarker(endHead, ConnectorMarker.Arrow));
    }

    private static ConnectorMarker HeadMarker(string? head, ConnectorMarker fallback) => head switch
    {
        null or "" => fallback,
        "Normal" => ConnectorMarker.Arrow,
        "Cross" => ConnectorMarker.Cross,
        "Circle" => ConnectorMarker.Circle,
        "None" => ConnectorMarker.None,
        _ => fallback,
    };

    /// <summary>
    /// The outline of <paramref name="marker"/> with its point at <paramref name="tip"/>, facing
    /// along <paramref name="dirX"/>/<paramref name="dirY"/> (the direction the line travels as it
    /// arrives at the tip). Scales gently with the stroke width so a thick line gets a bigger head.
    /// Returns null for <see cref="ConnectorMarker.None"/> or a zero-length direction.
    /// </summary>
    public static ConnectorMarkerShape? Shape(ConnectorMarker marker, Point tip, double dirX, double dirY, double strokeWidth)
    {
        double len = Math.Sqrt(dirX * dirX + dirY * dirY);
        if (marker == ConnectorMarker.None || len < 1e-6) return null;
        double ux = dirX / len, uy = dirY / len; // forward
        double nx = -uy, ny = ux;                // left-hand normal
        double s = Math.Max(1, strokeWidth);
        Point At(double back, double side) => new(tip.X - ux * back + nx * side, tip.Y - uy * back + ny * side);

        switch (marker)
        {
            case ConnectorMarker.Arrow:
            {
                double l = 9 + 1.5 * s, w = 4.5 + s;
                return new(Poly(tip, At(l, w), At(l, -w)), true, false);
            }
            case ConnectorMarker.OpenArrow:
            {
                double l = 9 + 1.5 * s, w = 4.5 + s;
                var a = At(l, w); var b = At(l, -w);
                return new(Invariant($"M {a.X:F1},{a.Y:F1} L {tip.X:F1},{tip.Y:F1} L {b.X:F1},{b.Y:F1}"), false, true);
            }
            case ConnectorMarker.Cross:
            {
                double r = 4 + s;
                var c = At(r + 1, 0);
                var a1 = new Point(c.X + (ux + nx) * r * 0.7071, c.Y + (uy + ny) * r * 0.7071);
                var a2 = new Point(c.X - (ux + nx) * r * 0.7071, c.Y - (uy + ny) * r * 0.7071);
                var b1 = new Point(c.X + (ux - nx) * r * 0.7071, c.Y + (uy - ny) * r * 0.7071);
                var b2 = new Point(c.X - (ux - nx) * r * 0.7071, c.Y - (uy - ny) * r * 0.7071);
                return new(Invariant($"M {a1.X:F1},{a1.Y:F1} L {a2.X:F1},{a2.Y:F1} M {b1.X:F1},{b1.Y:F1} L {b2.X:F1},{b2.Y:F1}"), false, true);
            }
            case ConnectorMarker.Circle:
            {
                double r = 3.5 + 0.75 * s;
                var c = At(r, 0);
                return new(Invariant($"M {c.X - r:F1},{c.Y:F1} A {r:F1},{r:F1} 0 1 0 {c.X + r:F1},{c.Y:F1} A {r:F1},{r:F1} 0 1 0 {c.X - r:F1},{c.Y:F1} Z"), true, false);
            }
            case ConnectorMarker.HollowTriangle:
            {
                double l = 13 + s, w = 7 + s;
                return new(Poly(tip, At(l, w), At(l, -w)), false, false);
            }
            case ConnectorMarker.HollowDiamond:
            case ConnectorMarker.FilledDiamond:
            {
                double l = 16 + 1.5 * s, w = 5.5 + s;
                return new(Poly(tip, At(l / 2, w), At(l, 0), At(l / 2, -w)), marker == ConnectorMarker.FilledDiamond, false);
            }
        }
        return null;
    }

    private static string Poly(params Point[] pts)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < pts.Length; i++)
            sb.Append(i == 0 ? "M " : " L ").Append(Invariant($"{pts[i].X:F1},{pts[i].Y:F1}"));
        return sb.Append(" Z").ToString();
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
