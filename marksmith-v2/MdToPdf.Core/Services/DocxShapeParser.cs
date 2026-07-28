using System.Xml.Linq;
using MdToPdf.Mermaid.Ast;
using MdToPdf.Mermaid.Generator;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Services;

// The inverse of the Mermaid shape emitters (MermaidDocxRenderer for flowcharts, DocxShapeEmitter
// for the bespoke families). Given a <w:drawing> paragraph the forward engine produced, it rebuilds
// a FlowchartDiagramAst and regenerates canonical Mermaid text via MermaidCodeGenerator — so a
// diagram survives MD -> DOCX -> MD as real, editable source, not a stranded picture.
//
// Recovery is driven by the structured identity tags the forward emitters write into each shape's
// wps:cNvPr name:
//   node:  ms:node=<id>;kind=<FlowNodeShape>
//   edge:  ms:edge=<from>--><to>;style=<Solid|Dashed|Thick>;start=<ArrowHead>;end=<ArrowHead>
//   label: ms:edge=<from>--><to>;label=<text>                 (label is LAST — may contain ';')
//   dir:   wp:docPr descr="Marksmith mermaid flowchart dir=<TD|TB|LR|RL|BT>"
// For an untagged group (shapes drawn in Word by hand, or from another tool) it falls back to
// geometry: preset geometry classifies nodes, connector connection sites (a:stCxn/a:endCxn) or
// nearest-shape proximity infer the edges. That path is best-effort (synthetic node ids).
public static class DocxShapeParser
{
    private const double EmuPerPt = 12700;

    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
    private static readonly XNamespace Wpg = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>
    /// Recover the Mermaid source for a drawing, or null when the drawing holds no recoverable
    /// shapes (e.g. an embedded picture). The returned text is the diagram body WITHOUT the
    /// ```mermaid fence — the caller wraps it.
    /// </summary>
    public static string? TryParseMermaid(W.Drawing drawing)
    {
        try
        {
            var doc = XDocument.Parse(drawing.InnerXml);

            string dir = "TD";
            var descr = doc.Descendants(Wp + "docPr").Attributes("descr").FirstOrDefault()?.Value ?? "";
            var dirIdx = descr.IndexOf("dir=", StringComparison.Ordinal);
            if (dirIdx >= 0) dir = descr[(dirIdx + 4)..].Trim();

            // Walk every shape in document order so recovered nodes keep their source order (the
            // generator emits standalone nodes in dictionary-insertion order — order matters for a
            // byte-for-byte round-trip).
            var nodes = new List<FlowNode>();
            var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var edges = new List<FlowEdge>();
            var labelTags = new List<(string From, string To, string Label)>();
            var rawShapes = new List<RawShape>();
            var rawConnectors = new List<RawConnector>();
            bool anyTagged = false;

            foreach (var wsp in doc.Descendants(Wps + "wsp"))
            {
                var name = wsp.Elements(Wps + "cNvPr").Attributes("name").FirstOrDefault()?.Value ?? "";
                var (geom, cx, cy, cw, ch, flipH, flipV) = ReadGeom(wsp);
                var preset = wsp.Elements(Wps + "spPr").Elements(A + "prstGeom").Attributes("prst").FirstOrDefault()?.Value;
                bool isConnector = geom is "straightConnector1" or "bentConnector3" or "bentConnector5" or "curvedConnector3";

                if (name.StartsWith("ms:node=", StringComparison.Ordinal))
                {
                    anyTagged = true;
                    var fields = ParseTag(name);
                    var id = fields["node"];
                    if (nodeIds.Add(id))
                    {
                        var kind = fields.TryGetValue("kind", out var k) && Enum.TryParse<FlowNodeShape>(k, out var fs)
                            ? fs : FlowNodeShape.Rectangle;
                        nodes.Add(new FlowNode { Id = id, Text = ReadText(wsp), Shape = kind });
                    }
                }
                else if (name.StartsWith("ms:edge=", StringComparison.Ordinal))
                {
                    anyTagged = true;
                    var key = name["ms:edge=".Length..];
                    var (from, to) = SplitEdgeKey(key);
                    if (from is null || to is null) continue;
                    var fields = ParseTag(name);
                    if (fields.ContainsKey("label"))
                    {
                        labelTags.Add((from, to, fields["label"]));
                    }
                    else
                    {
                        var style = fields.TryGetValue("style", out var st) && Enum.TryParse<FlowLineStyle>(st, out var ls)
                            ? ls : FlowLineStyle.Solid;
                        edges.Add(new FlowEdge
                        {
                            FromId = from,
                            ToId = to,
                            LineStyle = style,
                            StartHead = MapHead(fields.GetValueOrDefault("start")),
                            EndHead = MapHead(fields.GetValueOrDefault("end")),
                        });
                    }
                }
                else if (isConnector)
                {
                    rawConnectors.Add(new RawConnector
                    {
                        StartCxn = ReadCxn(wsp, A + "stCxn"),
                        EndCxn = ReadCxn(wsp, A + "endCxn"),
                        X1 = cx + (flipH ? cw : 0),
                        Y1 = cy + (flipV ? ch : 0),
                        X2 = cx + (flipH ? 0 : cw),
                        Y2 = cy + (flipV ? 0 : ch),
                    });
                }
                else if (geom is not null)
                {
                    rawShapes.Add(new RawShape
                    {
                        XmlId = uint.TryParse((string?)wsp.Elements(Wps + "cNvPr").Attributes("id").FirstOrDefault(), out var parsed)
                            ? parsed : null,
                        Preset = preset ?? "rect",
                        Cx = cx, Cy = cy, Cw = cw, Ch = ch,
                        Text = ReadText(wsp),
                    });
                }
            }

            FlowchartDiagramAst ast;
            if (anyTagged)
            {
                // Merge edge labels (emitted as separate text shapes) back onto their edges.
                foreach (var (from, to, label) in labelTags)
                {
                    var edge = edges.FirstOrDefault(e =>
                        string.Equals(e.FromId, from, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.ToId, to, StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrEmpty(e.Label));
                    if (edge is not null) edge.Label = label;
                }
                ast = BuildAst(dir, nodes, edges);
            }
            else
            {
                if (rawShapes.Count == 0) return null; // a picture / nothing recoverable
                ast = BuildFallbackAst(dir, rawShapes, rawConnectors);
            }

            if (ast.Nodes.Count == 0) return null;
            return MermaidCodeGenerator.Generate(ast);
        }
        catch
        {
            return null; // never break reimport on a surprise drawing
        }
    }

    // ---- tagged recovery ---------------------------------------------------------------------

    private static FlowchartDiagramAst BuildAst(string dir, List<FlowNode> nodes, List<FlowEdge> edges)
    {
        var ast = new FlowchartDiagramAst
        {
            Direction = Enum.TryParse<FlowDirection>(dir, true, out var d) ? d : FlowDirection.TD,
        };
        foreach (var n in nodes) ast.Nodes[n.Id] = n;
        foreach (var e in edges) ast.Edges.Add(e);
        return ast;
    }

    // ---- untagged geometry fallback ----------------------------------------------------------

    private static FlowchartDiagramAst BuildFallbackAst(string dir, List<RawShape> shapes, List<RawConnector> connectors)
    {
        var ast = new FlowchartDiagramAst
        {
            Direction = Enum.TryParse<FlowDirection>(dir, true, out var d) ? d : FlowDirection.TD,
        };

        // Node ids n1..nN in document order; remember each shape's center for edge inference.
        var centers = new List<(double X, double Y)>();
        var idByXmlId = new Dictionary<uint, string>();
        int idx = 0;
        foreach (var s in shapes)
        {
            var id = "n" + (++idx);
            if (s.XmlId is { } xmlId) idByXmlId[xmlId] = id;
            centers.Add((s.Cx + s.Cw / 2, s.Cy + s.Ch / 2));
            ast.Nodes[id] = new FlowNode
            {
                Id = id,
                Text = string.IsNullOrWhiteSpace(s.Text) ? id : s.Text,
                Shape = PresetToFlow(s.Preset),
            };
        }

        foreach (var c in connectors)
        {
            string? from = null, to = null;
            // Prefer explicit connection sites (a:stCxn/a:endCxn) when the connector is anchored.
            if (c.StartCxn is { } sc && idByXmlId.TryGetValue(sc.Id, out var sf)) from = sf;
            if (c.EndCxn is { } ec && idByXmlId.TryGetValue(ec.Id, out var st)) to = st;
            from ??= Nearest(centers, c.X1, c.Y1, shapes, to);
            to ??= Nearest(centers, c.X2, c.Y2, shapes, from);
            if (from is null || to is null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;
            ast.Edges.Add(new FlowEdge { FromId = from, ToId = to });
        }

        return ast;
    }

    private static string? Nearest(List<(double X, double Y)> centers, double x, double y, List<RawShape> shapes, string? exclude)
    {
        string? best = null;
        double bestD = double.MaxValue;
        for (int i = 0; i < centers.Count; i++)
        {
            var id = "n" + (i + 1);
            if (string.Equals(id, exclude, StringComparison.OrdinalIgnoreCase)) continue;
            var dx = centers[i].X - x;
            var dy = centers[i].Y - y;
            var d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = id; }
        }
        return best;
    }

    private static FlowNodeShape PresetToFlow(string? preset) => preset switch
    {
        "roundRect" => FlowNodeShape.RoundedRectangle,
        "diamond" => FlowNodeShape.RhombusDiamond,
        "ellipse" => FlowNodeShape.Circle,
        "can" => FlowNodeShape.CylindricalDatabase,
        "hexagon" => FlowNodeShape.Hexagon,
        "parallelogram" => FlowNodeShape.Parallelogram,
        "trapezoid" => FlowNodeShape.Trapezoid,
        _ => FlowNodeShape.Rectangle,
    };

    // ---- helpers -----------------------------------------------------------------------------

    private static FlowArrowHead MapHead(string? v) => v switch
    {
        "Triangle" or "Stealth" or "Open" => FlowArrowHead.Normal,
        "Oval" => FlowArrowHead.Circle,
        "Diamond" => FlowArrowHead.Cross,
        _ => FlowArrowHead.None,
    };

    // "A-->B;style=Solid;..." -> ("A","B"). The label tag carries no style/head fields.
    private static (string? From, string? To) SplitEdgeKey(string key)
    {
        var sep = key.IndexOf("-->", StringComparison.Ordinal);
        if (sep < 0) return (null, null);
        var from = key[..sep];
        var rest = key[(sep + 3)..];
        var semi = rest.IndexOf(';');
        var to = semi >= 0 ? rest[..semi] : rest;
        return (from, to);
    }

    // "ms:node=A;kind=Rectangle" -> {node:A, kind:Rectangle}. Everything after "label=" is the
    // literal label (it may contain ';' or '='), so it is consumed verbatim, not split.
    private static Dictionary<string, string> ParseTag(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = name;
        if (body.StartsWith("ms:", StringComparison.Ordinal)) body = body[3..];

        var labelIdx = body.IndexOf(";label=", StringComparison.Ordinal);
        if (labelIdx >= 0)
        {
            result["label"] = body[(labelIdx + ";label=".Length)..];
            body = body[..labelIdx];
        }

        foreach (var part in body.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            result[part[..eq]] = part[(eq + 1)..];
        }
        return result;
    }

    private static (string? geom, double cx, double cy, double cw, double ch, bool flipH, bool flipV) ReadGeom(XElement wsp)
    {
        var xfrm = wsp.Elements(Wps + "spPr").Elements(A + "xfrm").FirstOrDefault();
        if (xfrm is null) return (null, 0, 0, 0, 0, false, false);
        var off = xfrm.Element(A + "off");
        var ext = xfrm.Element(A + "ext");
        double cx = Pt(off?.Attribute("x")?.Value);
        double cy = Pt(off?.Attribute("y")?.Value);
        double cw = Pt(ext?.Attribute("cx")?.Value);
        double ch = Pt(ext?.Attribute("cy")?.Value);
        bool flipH = xfrm.Attribute("flipH")?.Value == "1" || xfrm.Attribute("flipH")?.Value == "true";
        bool flipV = xfrm.Attribute("flipV")?.Value == "1" || xfrm.Attribute("flipV")?.Value == "true";
        var geom = wsp.Elements(Wps + "spPr").Elements(A + "prstGeom").Attributes("prst").FirstOrDefault()?.Value;
        return (geom, cx, cy, cw, ch, flipH, flipV);
    }

    private static (uint Id, int Idx)? ReadCxn(XElement wsp, XName el)
    {
        var cxn = wsp.Elements(Wps + "cNvCnPr").Elements(el).FirstOrDefault();
        if (cxn is null) return null;
        if (!uint.TryParse(cxn.Attribute("id")?.Value, out var id)) return null;
        int.TryParse(cxn.Attribute("idx")?.Value, out var idx);
        return (id, idx);
    }

    private static string ReadText(XElement wsp)
    {
        var content = wsp.Elements(Wps + "txbx").Elements(W + "txbxContent").FirstOrDefault();
        if (content is null) return "";
        var paras = content.Elements(W + "p")
            .Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))
            .ToList();
        return string.Join("\n", paras).Trim();
    }

    private static double Pt(string? emu) => double.TryParse(emu, out var v) ? v / EmuPerPt : 0;

    private sealed class RawShape
    {
        public uint? XmlId;
        public string Preset = "rect";
        public double Cx, Cy, Cw, Ch;
        public string Text = "";
    }

    private sealed class RawConnector
    {
        public (uint Id, int Idx)? StartCxn;
        public (uint Id, int Idx)? EndCxn;
        public double X1, Y1, X2, Y2;
    }
}
