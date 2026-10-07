namespace MarkSmith.Core.Composer;

/// <summary>
/// Word's own proportions for the preset shapes Shape Studio draws, at their real size and with
/// the default adjust values the DOCX writer emits (an empty <c>a:avLst</c>). The canvas and the
/// document preview used to stretch a fixed 100×100 outline instead, so a wide chevron's notch,
/// a trapezoid's slope and a hexagon's points all came out up to twice the size Word draws them,
/// and every label was centred on the whole bounding box rather than the shape's text area.
/// Formulas follow presetShapeDefinitions.xml (ECMA-376 part 1, §20.1.10.56).
/// </summary>
public static class PresetGeometry
{
    /// <summary>The closed outline as absolute points inside a <paramref name="w"/>×<paramref name="h"/>
    /// box, for the straight-edged presets whose proportions depend on the box. Null for shapes
    /// that scale uniformly (rect, diamond, triangle) or are curved (ellipse, roundrect, cylinder).</summary>
    public static IReadOnlyList<(double X, double Y)>? Outline(string? prst, double w, double h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        double ss = Math.Min(w, h);
        switch ((prst ?? "").ToLowerInvariant())
        {
            case "chevron":
            {
                double a = Math.Min(50000, 100000 * w / ss);
                double x1 = ss * a / 100000, x2 = w - x1;
                return new[] { (0d, 0d), (x2, 0d), (w, h / 2), (x2, h), (0d, h), (x1, h / 2) };
            }
            case "homeplate":
            {
                double a = Math.Min(50000, 100000 * w / ss);
                double x1 = w - ss * a / 100000;
                return new[] { (0d, 0d), (x1, 0d), (w, h / 2), (x1, h), (0d, h) };
            }
            case "trapezoid":
            {
                // Wide edge at the bottom, as Word draws it.
                double a = Math.Min(25000, 50000 * w / ss);
                double x2 = ss * a / 100000;
                return new[] { (0d, h), (x2, 0d), (w - x2, 0d), (w, h) };
            }
            case "hexagon":
            {
                double a = Math.Min(25000, 50000 * w / ss);
                double x1 = ss * a / 100000;
                // shd2 · sin 60° with vf = 115470 lands the flat edges exactly on the box.
                double dy1 = h / 2 * 1.1547 * Math.Sin(Math.PI / 3);
                double y1 = h / 2 - dy1, y2 = h / 2 + dy1;
                return new[] { (0d, h / 2), (x1, y1), (w - x1, y1), (w, h / 2), (w - x1, y2), (x1, y2) };
            }
            default:
                return null;
        }
    }

    /// <summary>Corner radius Word gives a roundRect: a sixth of the shorter side.</summary>
    public static double RoundRectRadius(double w, double h) => Math.Min(w, h) / 6.0;

    /// <summary>Half-height of a cylinder's ("can") end ellipses.</summary>
    public static double CylinderCapRadius(double w, double h) => Math.Min(w, h) / 8.0;

    /// <summary>Largest label size, in points. Diagram labels read as captions beside 11 pt body text.</summary>
    public const double MaxLabelPt = 10;

    /// <summary>Smallest label size, in points, before a label is allowed to overflow.</summary>
    public const double MinLabelPt = 6;

    /// <summary>
    /// The one label-sizing rule shared by the canvas, the document preview and the Word export:
    /// the largest size up to <see cref="MaxLabelPt"/> at which the label's longest word fits the
    /// shape's text area and its wrapped lines fit the height. Sizes were derived from the shape's
    /// height alone before, so Word printed a lane header's label at ~24 pt (breaking "MANAGEMENT"
    /// mid-word) while the canvas showed it at 8 pt.
    /// </summary>
    /// <param name="text">The label; '\n' separates explicit lines.</param>
    /// <param name="prst">Preset geometry, for its text area.</param>
    /// <param name="widthPx">Shape width in 96-dpi pixels.</param>
    /// <param name="heightPx">Shape height in 96-dpi pixels.</param>
    public static (double Pt, List<string> Lines) FitLabel(string text, string? prst, double widthPx, double heightPx)
    {
        var ins = TextInsets(prst, widthPx, heightPx);
        // Word's bodyPr insets (1 pt / 0.5 pt) on each side.
        double boxW = Math.Max(4, widthPx - ins[0] - ins[2] - 2 * 96 / 72.0);
        double boxH = Math.Max(4, heightPx - ins[1] - ins[3] - 96 / 72.0);
        var paragraphs = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        double longestEm = paragraphs.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(WidthEm).DefaultIfEmpty(0).Max();
        for (double pt = MaxLabelPt; ; pt -= 0.5)
        {
            double px = pt * 96 / 72;
            var lines = Wrap(paragraphs, boxW, px);
            bool fits = longestEm * px <= boxW && lines.Count * px * LineSpacing <= boxH;
            if (fits || pt <= MinLabelPt) return (pt, lines);
        }
    }

    /// <summary>Estimated advance of <paramref name="text"/> in ems, for a semibold sans label
    /// (Segoe UI on the canvas, Calibri in Word). Capitals run much wider than lower case, and
    /// diagram labels are often set in capitals.</summary>
    public static double WidthEm(string text)
    {
        double em = 0;
        foreach (char c in text)
        {
            em += c switch
            {
                ' ' => 0.28,
                'i' or 'l' or 'j' or 'I' or '.' or ',' or ':' or ';' or '\'' or '|' or '!' => 0.28,
                'f' or 't' or 'r' or '(' or ')' or '-' => 0.38,
                'm' or 'w' => 0.82,
                'M' or 'W' => 0.9,
                >= 'A' and <= 'Z' => 0.66,
                >= '0' and <= '9' => 0.56,
                >= 'a' and <= 'z' => 0.53,
                _ => c > 0x2000 ? 0.9 : 0.6, // symbols and emoji
            };
        }
        return em;
    }

    /// <summary>Label line height as a multiple of the font size.</summary>
    public const double LineSpacing = 1.18;

    /// <summary>Greedy word wrap against the estimated advance width.</summary>
    public static List<string> Wrap(IEnumerable<string> paragraphs, double maxWidthPx, double fontPx)
    {
        var lines = new List<string>();
        foreach (var paragraph in paragraphs)
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) { lines.Add(""); continue; }
            var current = new System.Text.StringBuilder();
            foreach (var word in words)
            {
                if (current.Length == 0) current.Append(word);
                else if (WidthEm(current + " " + word) * fontPx <= maxWidthPx) current.Append(' ').Append(word);
                else { lines.Add(current.ToString()); current.Clear().Append(word); }
            }
            if (current.Length > 0) lines.Add(current.ToString());
        }
        return lines;
    }

    /// <summary>
    /// Where Word lays the label out inside the shape, as left/top/right/bottom insets from the
    /// box, before any rotation. A label centred on the whole box put a triangle's text in its
    /// narrow apex and ran a chevron's caption into the notch.
    /// </summary>
    public static double[] TextInsets(string? prst, double w, double h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        double ss = Math.Min(w, h);
        switch ((prst ?? "").ToLowerInvariant())
        {
            case "triangle":
                return new[] { w / 4, h / 2, w / 4, 0d };
            case "rttriangle":
                return new[] { w / 12, h * 7 / 12, w * 5 / 12, h / 12 };
            case "diamond":
                return new[] { w / 4, h / 4, w / 4, h / 4 };
            case "ellipse" or "circle":
                // The inscribed rectangle at 45°.
                return new[] { w * 0.146, h * 0.146, w * 0.146, h * 0.146 };
            case "chevron":
            {
                // il/ir = x1 only while the notch and point don't meet (w > ss).
                double a = Math.Min(50000, 100000 * w / ss), x1 = ss * a / 100000;
                return w - 2 * x1 > 0 ? new[] { x1, 0d, x1, 0d } : new[] { 0d, 0d, 0d, 0d };
            }
            case "homeplate":
            {
                double a = Math.Min(50000, 100000 * w / ss), dx2 = ss * a / 100000;
                return new[] { 0d, 0d, dx2 / 2, 0d };
            }
            case "trapezoid":
            {
                double maxAdj = 50000 * w / ss, a = Math.Min(25000, maxAdj);
                double il = w / 3 * a / maxAdj, it = h / 3 * a / maxAdj;
                return new[] { il, it, il, 0d };
            }
            case "hexagon":
            {
                double maxAdj = 50000 * w / ss, a = Math.Min(25000, maxAdj);
                double q1 = maxAdj * -0.5, q2 = a + q1;
                double q3 = q2 > 0 ? 4 : 2, q4 = q2 > 0 ? 3 : 2, q5 = q2 > 0 ? q1 : 0;
                double q8 = q3 - (a + q5) / q1 * q4;
                double il = w * q8 / 24, it = h * q8 / 24;
                return new[] { il, it, il, it };
            }
            case "cylinder" or "can":
            {
                // Below the top end ellipse, above the bottom curve.
                double y1 = CylinderCapRadius(w, h);
                return new[] { 0d, 2 * y1, 0d, y1 };
            }
            case "roundrect":
            {
                double i = RoundRectRadius(w, h) * 0.29289;
                return new[] { i, i, i, i };
            }
            default:
                return new[] { 0d, 0d, 0d, 0d };
        }
    }
}
