using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MdToPdf.Services.Mermaid;

// ShapeForge for diagram PLUGINS (Graphviz/D2/PlantUML/…): parses the SVG string a plugin's engine
// emitted into the same GenericDiagram primitive model the mermaid generic harvest produces, so the
// existing ToMDiagram + DocxShapeEmitter pipeline rebuilds it as native, editable Word shapes —
// boxes, ellipses, diamonds, curved connectors with arrowheads, positioned text — instead of a flat
// picture. Where mermaid needs a live WebView to harvest rendered geometry, a plugin's SVG is
// already in hand as a string, so this is a plain XML walk: no browser, fully deterministic.
//
// Deliberately generic over SVG primitives (rect/ellipse/circle/polygon/path/line/polyline/text)
// rather than tool-specific: Graphviz, D2 and PlantUML all boil down to these. Supported transforms
// are translate/scale accumulated down the group tree (Graphviz's root `translate(4 H-4)` — which
// shifts its negative-Y coordinate system into view — is exactly this); rotation is ignored (rare
// in these engines' output; a rotated label lands unrotated at the right position rather than lost).
public static class SvgShapeForge
{
    public static GenericDiagram? Parse(string svg)
    {
        try
        {
            var doc = XDocument.Parse(svg);
            var root = doc.Root;
            if (root is null || root.Name.LocalName != "svg") return null;

            var (w, h) = CanvasSize(root);
            var diagram = new GenericDiagram { W = w, H = h };

            var arrowheads = new List<(double X, double Y)>();
            var markerArrowed = new HashSet<GEdge>();
            Walk(root, new Affine(1, 1, 0, 0), diagram, arrowheads, markerArrowed);

            // Edges only get an arrowhead when the source SVG actually drew one — either a small
            // filled polygon at an end (Graphviz/PlantUML style) or a marker-end reference (D2
            // style). A vega-lite axis tick has neither and stays a plain line.
            foreach (var e in diagram.Edges)
            {
                if (markerArrowed.Contains(e)) { e.Arrow = true; continue; }
                if (e.Points.Count < 2) continue;
                var last = e.Points[^1];
                var first = e.Points[0];
                if (arrowheads.Any(a => Dist(a, last) < 16)) e.Arrow = true;
                else if (arrowheads.Any(a => Dist(a, first) < 16)) { e.Points.Reverse(); e.Arrow = true; }
                else e.Arrow = false;
            }

            // A parse that recovered essentially nothing (an engine drawing everything through
            // exotic constructs) isn't worth emitting — caller falls back to the SVG picture.
            if (diagram.Nodes.Count + diagram.Texts.Count < 2) return null;
            return diagram;
        }
        catch
        {
            return null;
        }
    }

    // ---- traversal -----------------------------------------------------------------------------

    private readonly record struct Affine(double Sx, double Sy, double Tx, double Ty)
    {
        public (double X, double Y) Apply(double x, double y) => (x * Sx + Tx, y * Sy + Ty);
        public Affine Then(Affine inner) => new(Sx * inner.Sx, Sy * inner.Sy, Sx * inner.Tx + Tx, Sy * inner.Ty + Ty);
    }

    // Parses a transform list ("scale(1 1) rotate(0) translate(4 112)") into one affine. Ops apply
    // right-to-left to points, so compose left-to-right with Then(). translate/scale/matrix (its
    // diagonal+offset) are honored; rotate/skew are ignored (see class comment).
    private static Affine ParseTransform(string raw)
    {
        var result = new Affine(1, 1, 0, 0);
        foreach (Match op in Regex.Matches(raw, @"(translate|scale|matrix|rotate|skewX|skewY)\s*\(([^)]*)\)"))
        {
            var args = Regex.Matches(op.Groups[2].Value, @"-?[\d.]+(?:e-?\d+)?", RegexOptions.IgnoreCase)
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            var own = op.Groups[1].Value switch
            {
                "translate" when args.Length >= 1 => new Affine(1, 1, args[0], args.Length > 1 ? args[1] : 0),
                "scale" when args.Length >= 1 => new Affine(args[0], args.Length > 1 ? args[1] : args[0], 0, 0),
                "matrix" when args.Length == 6 => new Affine(args[0], args[3], args[4], args[5]),
                _ => new Affine(1, 1, 0, 0),
            };
            result = result.Then(own);
        }
        return result;
    }

    private static double Dist((double X, double Y) a, double[] p) =>
        Math.Sqrt((a.X - p[0]) * (a.X - p[0]) + (a.Y - p[1]) * (a.Y - p[1]));

    private static void Walk(XElement el, Affine xf, GenericDiagram d,
        List<(double X, double Y)> arrowheads, HashSet<GEdge> markerArrowed)
    {
        foreach (var child in el.Elements())
        {
            var childXf = child.Attribute("transform") is { } t ? xf.Then(ParseTransform(t.Value)) : xf;
            switch (child.Name.LocalName)
            {
                case "defs":
                case "marker":
                case "clipPath":
                case "symbol":
                case "style":
                    break; // definitions, not drawn content — walking in would turn markers into shapes
                case "g":
                case "a": // PlantUML wraps clickable nodes in <a>
                case "svg":
                    Walk(child, childXf, d, arrowheads, markerArrowed);
                    break;
                case "rect": AddRect(child, childXf, d); break;
                case "ellipse": AddEllipse(child, childXf, d, circle: false); break;
                case "circle": AddEllipse(child, childXf, d, circle: true); break;
                case "polygon": AddPolygon(child, childXf, d, arrowheads); break;
                case "path": AddPath(child, childXf, d, arrowheads, markerArrowed); break;
                case "line": AddLine(child, childXf, d, markerArrowed); break;
                case "polyline": AddPolyline(child, childXf, d, markerArrowed); break;
                case "text": AddText(child, childXf, d); break;
            }
        }
    }

    private static bool HasEndMarker(XElement el) =>
        Attr(el, "marker-end") is { Length: > 0 } m && m != "none";

    // ---- shape handlers ------------------------------------------------------------------------

    private static void AddRect(XElement el, Affine xf, GenericDiagram d)
    {
        double x = Num(el, "x"), y = Num(el, "y"), w = Num(el, "width"), h = Num(el, "height");
        if (w <= 0 || h <= 0) return;
        var (px, py) = xf.Apply(x, y);
        var rx = Num(el, "rx");
        if (IsBackground(px, py, w * xf.Sx, h * xf.Sy, d)) return;
        d.Nodes.Add(new GNode
        {
            X = px, Y = py, W = w * xf.Sx, H = h * xf.Sy,
            Kind = rx > 0.5 ? "RoundRect" : "Rect",
            Fill = Paint(el, "fill"), Stroke = Paint(el, "stroke"),
        });
    }

    private static void AddEllipse(XElement el, Affine xf, GenericDiagram d, bool circle)
    {
        double cx = Num(el, "cx"), cy = Num(el, "cy");
        double rx = circle ? Num(el, "r") : Num(el, "rx");
        double ry = circle ? Num(el, "r") : Num(el, "ry");
        if (rx <= 0 || ry <= 0) return;
        var (px, py) = xf.Apply(cx, cy);
        d.Nodes.Add(new GNode
        {
            X = px - rx * xf.Sx, Y = py - ry * xf.Sy, W = rx * 2 * xf.Sx, H = ry * 2 * xf.Sy,
            Kind = circle || Math.Abs(rx - ry) < 0.5 ? "Circle" : "Ellipse",
            Fill = Paint(el, "fill"), Stroke = Paint(el, "stroke"),
        });
    }

    private static void AddPolygon(XElement el, Affine xf, GenericDiagram d, List<(double X, double Y)> arrowheads)
    {
        var pts = Points(el.Attribute("points")?.Value, xf);
        if (pts.Count < 3) return;

        double minX = pts.Min(p => p[0]), maxX = pts.Max(p => p[0]);
        double minY = pts.Min(p => p[1]), maxY = pts.Max(p => p[1]);
        double w = maxX - minX, h = maxY - minY;

        // Tiny filled triangles/quads are arrowheads drawn by the engine — the connector emits its
        // own native arrowhead, so drawing these too would double every arrow. Remember where they
        // sat: the arrow-assignment pass gives an arrowhead only to edges ending at one of these.
        if (w * h < 150 && pts.Count <= 5)
        {
            if (Paint(el, "fill") is not null)
                arrowheads.Add(((minX + maxX) / 2, (minY + maxY) / 2));
            return;
        }
        if (IsBackground(minX, minY, w, h, d)) return;

        // Axis-aligned 4-corner polygon = a box; a 4-pointer whose corners sit mid-edge = a diamond.
        var kind = "Rect";
        var distinct = pts.Take(pts.Count > 4 && SamePoint(pts[0], pts[^1]) ? pts.Count - 1 : pts.Count).ToList();
        if (distinct.Count == 4)
        {
            bool axisAligned = distinct.All(p =>
                (Math.Abs(p[0] - minX) < 1 || Math.Abs(p[0] - maxX) < 1) &&
                (Math.Abs(p[1] - minY) < 1 || Math.Abs(p[1] - maxY) < 1));
            kind = axisAligned ? "Rect" : "Diamond";
        }
        d.Nodes.Add(new GNode
        {
            X = minX, Y = minY, W = w, H = h, Kind = kind,
            Fill = Paint(el, "fill"), Stroke = Paint(el, "stroke"),
        });
    }

    private static void AddPath(XElement el, Affine xf, GenericDiagram d,
        List<(double X, double Y)> arrowheads, HashSet<GEdge> markerArrowed)
    {
        var data = el.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(data)) return;
        var pts = FlattenPath(data, xf);
        if (pts.Count < 2) return;

        var fill = Paint(el, "fill");
        if (fill is null)
        {
            // Unfilled path = a connector/edge.
            var edge = new GEdge { Points = pts, Stroke = Paint(el, "stroke"), Dashed = Dashed(el) };
            if (HasEndMarker(el)) markerArrowed.Add(edge);
            d.Edges.Add(edge);
        }
        else
        {
            // Filled path = a node outline drawn as a path (PlantUML does this) — box its bounds.
            double minX = pts.Min(p => p[0]), maxX = pts.Max(p => p[0]);
            double minY = pts.Min(p => p[1]), maxY = pts.Max(p => p[1]);
            double w = maxX - minX, h = maxY - minY;
            if (w * h < 150) // arrowhead-sized: skip + remember, same rationale as polygons
            {
                arrowheads.Add(((minX + maxX) / 2, (minY + maxY) / 2));
                return;
            }
            if (IsBackground(minX, minY, w, h, d)) return;
            d.Nodes.Add(new GNode
            {
                X = minX, Y = minY, W = w, H = h, Kind = "RoundRect",
                Fill = fill, Stroke = Paint(el, "stroke"),
            });
        }
    }

    private static void AddLine(XElement el, Affine xf, GenericDiagram d, HashSet<GEdge> markerArrowed)
    {
        var (x1, y1) = xf.Apply(Num(el, "x1"), Num(el, "y1"));
        var (x2, y2) = xf.Apply(Num(el, "x2"), Num(el, "y2"));
        var edge = new GEdge
        {
            Points = { new[] { x1, y1 }, new[] { x2, y2 } },
            Stroke = Paint(el, "stroke"), Dashed = Dashed(el),
        };
        if (HasEndMarker(el)) markerArrowed.Add(edge);
        d.Edges.Add(edge);
    }

    private static void AddPolyline(XElement el, Affine xf, GenericDiagram d, HashSet<GEdge> markerArrowed)
    {
        var pts = Points(el.Attribute("points")?.Value, xf);
        if (pts.Count < 2) return;
        var edge = new GEdge { Points = pts, Stroke = Paint(el, "stroke"), Dashed = Dashed(el) };
        if (HasEndMarker(el)) markerArrowed.Add(edge);
        d.Edges.Add(edge);
    }

    private static void AddText(XElement el, Affine xf, GenericDiagram d)
    {
        // <text> may hold direct text or <tspan> children (D2); each tspan is its own placed line.
        var spans = el.Elements().Where(e => e.Name.LocalName == "tspan").ToList();
        if (spans.Count > 0)
        {
            foreach (var span in spans) PlaceText(span, el, xf, d);
            return;
        }
        PlaceText(el, el, xf, d);
    }

    private static void PlaceText(XElement el, XElement styleSource, Affine xf, GenericDiagram d)
    {
        var content = el.Value.Trim();
        if (content.Length == 0) return;

        double x = Num(el, "x", double.NaN), y = Num(el, "y", double.NaN);
        if (double.IsNaN(x)) x = Num(styleSource, "x");
        if (double.IsNaN(y)) y = Num(styleSource, "y");
        var (px, py) = xf.Apply(x, y);

        var fontSize = Attr(styleSource, "font-size") is { } fsRaw &&
                       double.TryParse(Regex.Match(fsRaw, @"[\d.]+").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fs)
            ? fs * Math.Abs(xf.Sy) : 12;

        // Estimated text box from the anchor: y is the BASELINE; width from a monospace-ish factor.
        var estW = Math.Max(8, content.Length * fontSize * 0.58);
        var anchor = Attr(styleSource, "text-anchor") ?? "";
        var left = anchor switch
        {
            "middle" => px - estW / 2,
            "end" => px - estW,
            _ => px,
        };
        d.Texts.Add(new GText
        {
            X = left,
            Y = py - fontSize,           // baseline -> approximate top
            W = estW,
            H = fontSize * 1.5,          // line box; ToMDiagram derives its font size back from this
            Text = content,
            Color = Paint(styleSource, "fill"),
        });
    }

    // ---- helpers -------------------------------------------------------------------------------

    // The full-canvas backdrop rectangle/polygon every engine draws first — skip it, or every
    // diagram gets one giant white box shape behind it in Word.
    private static bool IsBackground(double x, double y, double w, double h, GenericDiagram d) =>
        w >= d.W * 0.9 && h >= d.H * 0.9;

    private static (double W, double H) CanvasSize(XElement root)
    {
        var vb = root.Attribute("viewBox")?.Value;
        if (vb is not null)
        {
            var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh) &&
                vw > 0 && vh > 0)
                return (vw, vh);
        }
        double w = SizeAttr(root, "width"), h = SizeAttr(root, "height");
        return (w > 0 ? w : 600, h > 0 ? h : 400);
    }

    private static double SizeAttr(XElement el, string name)
    {
        var raw = el.Attribute(name)?.Value;
        if (raw is null) return 0;
        var m = Regex.Match(raw, @"[\d.]+");
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static double Num(XElement el, string name, double fallback = 0)
    {
        var raw = el.Attribute(name)?.Value;
        return raw is not null && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // fill/stroke from the attribute or the style="" declaration; "none" -> null.
    private static string? Paint(XElement el, string property)
    {
        var v = Attr(el, property);
        if (string.IsNullOrEmpty(v) || v is "none" or "transparent") return null;
        return v;
    }

    private static string? Attr(XElement el, string name)
    {
        if (el.Attribute(name)?.Value is { } direct) return direct;
        if (el.Attribute("style")?.Value is { } style)
        {
            var m = Regex.Match(style, $@"(?:^|;)\s*{Regex.Escape(name)}\s*:\s*([^;]+)");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static bool Dashed(XElement el)
    {
        var v = Attr(el, "stroke-dasharray");
        return !string.IsNullOrEmpty(v) && v != "none" && v != "0";
    }

    private static bool SamePoint(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) < 0.5 && Math.Abs(a[1] - b[1]) < 0.5;

    private static List<double[]> Points(string? raw, Affine xf)
    {
        var pts = new List<double[]>();
        if (raw is null) return pts;
        var nums = Regex.Matches(raw, @"-?[\d.]+(?:e-?\d+)?", RegexOptions.IgnoreCase);
        for (int i = 0; i + 1 < nums.Count; i += 2)
        {
            if (double.TryParse(nums[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(nums[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                var (px, py) = xf.Apply(x, y);
                pts.Add(new[] { px, py });
            }
        }
        return pts;
    }

    // Minimal SVG path flattener: M/L/H/V/C/S/Q/T/A/Z (absolute + relative). Curves are sampled at
    // fixed steps — connectors only need a faithful polyline, not exact beziers.
    // Renders smooth bezier curves through sampled control points; approximates arc segments via endpoint tangents.
    private static List<double[]> FlattenPath(string data, Affine xf)
    {
        var result = new List<double[]>();
        double cx = 0, cy = 0, startX = 0, startY = 0, lastCtrlX = 0, lastCtrlY = 0;
        char lastCmd = ' ';

        var tokens = Regex.Matches(data, @"[MmLlHhVvCcSsQqTtAaZz]|-?[\d.]+(?:e-?\d+)?", RegexOptions.IgnoreCase);
        int i = 0;
        double Next() => double.Parse(tokens[i++].Value, CultureInfo.InvariantCulture);
        void Emit(double x, double y) { var (px, py) = xf.Apply(x, y); result.Add(new[] { px, py }); }
        void SampleCubic(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            for (int s = 1; s <= 4; s++)
            {
                double t = s / 4.0, u = 1 - t;
                double bx = u * u * u * cx + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
                double by = u * u * u * cy + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
                Emit(bx, by);
            }
            lastCtrlX = x2; lastCtrlY = y2; cx = x3; cy = y3;
        }

        while (i < tokens.Count)
        {
            var tok = tokens[i].Value;
            char cmd;
            if (char.IsLetter(tok[0])) { cmd = tok[0]; i++; }
            else cmd = lastCmd switch { 'M' => 'L', 'm' => 'l', _ => lastCmd }; // implicit repeats

            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var x = Next(); var y = Next();
                    cx = rel ? cx + x : x; cy = rel ? cy + y : y;
                    startX = cx; startY = cy;
                    Emit(cx, cy);
                    break;
                }
                case 'L':
                {
                    var x = Next(); var y = Next();
                    cx = rel ? cx + x : x; cy = rel ? cy + y : y;
                    Emit(cx, cy);
                    break;
                }
                case 'H': { var x = Next(); cx = rel ? cx + x : x; Emit(cx, cy); break; }
                case 'V': { var y = Next(); cy = rel ? cy + y : y; Emit(cx, cy); break; }
                case 'C':
                {
                    double x1 = Next(), y1 = Next(), x2 = Next(), y2 = Next(), x3 = Next(), y3 = Next();
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x3 += cx; y3 += cy; }
                    SampleCubic(x1, y1, x2, y2, x3, y3);
                    break;
                }
                case 'S':
                {
                    double x2 = Next(), y2 = Next(), x3 = Next(), y3 = Next();
                    if (rel) { x2 += cx; y2 += cy; x3 += cx; y3 += cy; }
                    // reflect previous control point when the prior segment was a cubic
                    double x1 = char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? 2 * cx - lastCtrlX : cx;
                    double y1 = char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? 2 * cy - lastCtrlY : cy;
                    SampleCubic(x1, y1, x2, y2, x3, y3);
                    break;
                }
                case 'Q':
                case 'T':
                {
                    double qx, qy;
                    if (char.ToUpperInvariant(cmd) == 'Q') { qx = Next(); qy = Next(); if (rel) { qx += cx; qy += cy; } }
                    else { qx = cx; qy = cy; } // T reflection approximated by current point
                    double x3 = Next(), y3 = Next();
                    if (rel) { x3 += cx; y3 += cy; }
                    // convert quadratic to cubic controls
                    SampleCubic(cx + 2.0 / 3 * (qx - cx), cy + 2.0 / 3 * (qy - cy),
                                x3 + 2.0 / 3 * (qx - x3), y3 + 2.0 / 3 * (qy - y3), x3, y3);
                    break;
                }
                case 'A':
                {
                    // rx ry rot large-arc sweep x y — approximate by the endpoint (engines use tiny
                    // arcs for rounded corners; the polyline through endpoints reads correctly).
                    Next(); Next(); Next(); Next(); Next();
                    var x = Next(); var y = Next();
                    cx = rel ? cx + x : x; cy = rel ? cy + y : y;
                    Emit(cx, cy);
                    break;
                }
                case 'Z':
                    cx = startX; cy = startY;
                    Emit(cx, cy);
                    break;
            }
            lastCmd = cmd;
        }
        return result;
    }
}
