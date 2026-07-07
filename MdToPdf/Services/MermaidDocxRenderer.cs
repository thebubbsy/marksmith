using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MdToPdf.Models;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Services;

// Renders Mermaid FLOWCHARTS as native, editable Word shapes — real boxes, diamonds and arrows built
// from Word's own drawing machinery (a WordprocessingGroup of wps shapes), not an embedded picture.
// The algorithm: parse a tolerant subset of `graph`/`flowchart` syntax, assign layered ranks
// (Sugiyama-lite), position nodes on a canvas, then emit the group as DrawingML themed with the
// selected Marksmith theme. Honesty rule: if any line of the diagram can't be FULLY parsed (or the
// diagram isn't a flowchart), TryRender returns false and the caller keeps the code-block fallback —
// we never ship a half-understood diagram as shapes.
public static class MermaidDocxRenderer
{
    private const double EmuPerPt = 12700;
    private const double NodeH = 34, VGap = 46, HGap = 28, Margin = 10;
    private const double MaxCanvasW = 460, MaxCanvasH = 640; // pt — fits the printable page
    private const int MaxNodes = 150;

    private sealed class Node
    {
        public required string Id;
        public string Label = "";
        public string Shape = "rect"; // rect | round | ellipse | diamond | parallelogram | hexagon
        public int Rank;
        public double X, Y, W, H;
    }

    private sealed record Edge(Node From, Node To, string? Label, bool Dashed, bool Thick, bool Arrow);

    private sealed class Graph
    {
        public bool LeftRight;
        public readonly List<Node> Nodes = new();
        public readonly Dictionary<string, Node> ById = new();
        public readonly List<Edge> Edges = new();
        public double W, H;
        public double Scale = 1;  // set by scale-to-fit; fonts follow it
        public bool Oversized;     // too big for print layout even wrapped -> document opens in Web Layout
    }

    // The ShapeForge renderer registry: one geometry renderer per diagram family (see
    // Services/Mermaid). Flowcharts keep the battle-tested path below; everything else goes
    // renderer → MDiagram → DocxShapeEmitter.
    private static readonly Mermaid.IMermaidRenderer[] Renderers =
    {
        new Mermaid.MermaidSequenceRenderer(),
        new Mermaid.MermaidClassErRenderer(),
        new Mermaid.MermaidTreesRenderer(),
        new Mermaid.MermaidChartsRenderer(),
    };

    public static bool TryRender(string source, ThemeDefinition theme, uint drawingId, out W.Paragraph paragraph, out bool oversized)
    {
        paragraph = null!;
        oversized = false;
        try
        {
            var type = FirstWord(source);
            if (type is not ("graph" or "flowchart"))
            {
                var renderer = Renderers.FirstOrDefault(r => r.CanRender(type));
                if (renderer is null) return false; // unknown family → snapshot/code fallback
                var d = renderer.Render(source, theme); // MermaidParseException → catch below
                if (d.Shapes.Count == 0 && d.Connectors.Count == 0) return false;
                paragraph = new W.Paragraph { InnerXml = Mermaid.DocxShapeEmitter.ToParagraphXml(d, theme, drawingId, out oversized) };
                paragraph.PrependChild(new W.ParagraphProperties( // schema order: spacing before jc
                    new W.SpacingBetweenLines { Before = "120", After = "120" },
                    new W.Justification { Val = W.JustificationValues.Center }));
                return true;
            }

            var g = Parse(source);
            if (g is null || g.Nodes.Count == 0 || g.Nodes.Count > MaxNodes) return false;
            Layout(g);
            oversized = g.Oversized;

            var drawing = new W.Drawing { InnerXml = BuildInlineXml(g, theme, drawingId) };
            paragraph = new W.Paragraph(
                new W.ParagraphProperties( // schema order: spacing before jc
                    new W.SpacingBetweenLines { Before = "120", After = "120" },
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(drawing));
            return true;
        }
        catch
        {
            return false; // any surprise → snapshot/code-block fallback, never a broken document
        }
    }

    // Lowercased first token of the first meaningful line — the diagram family
    // ("sequencediagram", "pie", "mindmap", ...). Renderers expect it lowercased.
    private static string FirstWord(string source)
    {
        foreach (var raw in source.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%")) continue;
            var m = Regex.Match(line, @"^([A-Za-z][A-Za-z0-9-]*)");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
        }
        return "";
    }

    // ---------------- parsing ----------------

    private static Graph? Parse(string src)
    {
        string? dir = null;
        var g = new Graph();
        foreach (var raw in src.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%")) continue;
            if (dir is null)
            {
                var m = Regex.Match(line, @"^(?:graph|flowchart)\s+(TD|TB|LR|RL|BT)\b", RegexOptions.IgnoreCase);
                if (!m.Success) return null; // not a flowchart — unsupported diagram type
                dir = m.Groups[1].Value.ToUpperInvariant();
                continue;
            }
            // Grouping/styling lines don't affect the shape graph; skip them.
            if (Regex.IsMatch(line, @"^(subgraph\b|end\b|direction\b|classDef\b|class\b|style\b|linkStyle\b|click\b|accTitle\b|accDescr\b)")) continue;
            if (!ParseLine(line, g)) return null; // partially-understood line → refuse (fallback)
        }
        if (dir is null) return null;
        g.LeftRight = dir is "LR" or "RL";
        return g;
    }

    private static bool ParseLine(string line, Graph g)
    {
        line = line.TrimEnd(';').Trim();
        int i = 0;
        var left = ReadNodeGroup(line, ref i, g);
        if (left is null) return false;
        while (true)
        {
            if (SkipWs(line, ref i)) return true; // clean end of line
            var arrow = ReadArrow(line, ref i);
            if (arrow is null) return false;
            var right = ReadNodeGroup(line, ref i, g);
            if (right is null) return false;
            bool first = true;
            foreach (var u in left)
                foreach (var v in right)
                {
                    g.Edges.Add(new Edge(u, v, first ? arrow.Value.Label : null,
                        arrow.Value.Dashed, arrow.Value.Thick, arrow.Value.Head));
                    first = false;
                }
            left = right;
        }
    }

    private static bool SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i >= s.Length;
    }

    private static readonly Regex ArrowRe = new(
        @"\G\s*(-\.+->|-{2,3}>|={2,3}>|-{3}|={3})\s*(?:\|([^|]*)\|)?\s*", RegexOptions.Compiled);

    private static (string? Label, bool Dashed, bool Thick, bool Head)? ReadArrow(string s, ref int i)
    {
        var m = ArrowRe.Match(s, i);
        if (!m.Success || m.Index != i) return null;
        i = m.Index + m.Length;
        var tok = m.Groups[1].Value;
        var label = m.Groups[2].Success ? Clean(m.Groups[2].Value) : null;
        return (string.IsNullOrWhiteSpace(label) ? null : label, tok.Contains('.'), tok.StartsWith('='), tok.EndsWith('>'));
    }

    private static readonly Regex IdRe = new(@"\G\s*([A-Za-z0-9_][A-Za-z0-9_.:-]*)", RegexOptions.Compiled);
    private static readonly Regex AmpRe = new(@"\G\s*&\s*", RegexOptions.Compiled);

    // Bracket pairs, longest openers first, mapped to Word preset geometries.
    private static readonly (string Open, string Close, string Shape)[] Brackets =
    {
        ("((", "))", "ellipse"), ("([", "])", "round"), ("[[", "]]", "rect"),
        ("[/", "/]", "parallelogram"), ("[\\", "\\]", "parallelogram"), ("{{", "}}", "hexagon"),
        ("[", "]", "rect"), ("(", ")", "round"), ("{", "}", "diamond"),
    };

    private static List<Node>? ReadNodeGroup(string s, ref int i, Graph g)
    {
        List<Node>? group = null;
        while (true)
        {
            var n = ReadNode(s, ref i, g);
            if (n is null) return group;
            (group ??= new()).Add(n);
            var amp = AmpRe.Match(s, i);
            if (!amp.Success || amp.Index != i) return group;
            i = amp.Index + amp.Length;
        }
    }

    private static Node? ReadNode(string s, ref int i, Graph g)
    {
        var m = IdRe.Match(s, i);
        if (!m.Success || m.Index != i) return null;
        i = m.Index + m.Length;
        var id = m.Groups[1].Value;

        if (!g.ById.TryGetValue(id, out var node))
        {
            node = new Node { Id = id, Label = id };
            g.ById[id] = node;
            g.Nodes.Add(node);
        }

        foreach (var (open, close, shape) in Brackets)
        {
            if (i + open.Length > s.Length || s.Substring(i, open.Length) != open) continue;
            var end = s.IndexOf(close, i + open.Length, StringComparison.Ordinal);
            if (end < 0) return null; // unterminated bracket → unsupported line
            node.Label = Clean(s.Substring(i + open.Length, end - i - open.Length));
            node.Shape = shape;
            i = end + close.Length;
            break;
        }
        return node;
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase); // real line breaks in Word
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", ""));
        return s.Trim().Trim('"').Replace("**", "").Replace("`", "").Trim();
    }

    // ---------------- layout ----------------

    private static void Layout(Graph g)
    {
        // Layered ranks: relax rank[to] >= rank[from]+1, bounded so cycles can't spin forever.
        for (int pass = 0; pass < g.Nodes.Count; pass++)
        {
            bool changed = false;
            foreach (var e in g.Edges)
                if (e.To.Rank < e.From.Rank + 1 && e.From != e.To)
                {
                    // Only raise if it doesn't create an unbounded loop (cap at node count).
                    if (e.From.Rank + 1 < g.Nodes.Count) { e.To.Rank = e.From.Rank + 1; changed = true; }
                }
            if (!changed) break;
        }

        foreach (var n in g.Nodes)
        {
            // Size from the text so labels never clip: width from the longest explicit line,
            // height grown per line — both the <br> lines and an estimate of Word's own wrapping
            // once the label exceeds the max box width.
            var lines = n.Label.Length == 0 ? new[] { "" } : n.Label.Split('\n');
            int longest = lines.Max(l => l.Length);
            var baseW = Math.Clamp(22 + longest * 6.0, 74, 260);
            n.W = n.Shape switch
            {
                "diamond" => baseW * 1.45,
                "ellipse" => baseW * 1.35, // text region of an ellipse is ~70% of its width
                "hexagon" => baseW * 1.25,
                _ => baseW,
            };

            double usableW = n.W - (n.Shape == "diamond" ? n.W * 0.38 : 14); // diamonds squeeze text
            double charsPerLine = Math.Max(6, usableW / 6.2);
            int textLines = lines.Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / charsPerLine)));

            double baseH = n.Shape is "diamond" or "ellipse" ? 44 : NodeH;
            n.H = baseH + Math.Max(0, textLines - 1) * (n.Shape == "diamond" ? 20 : 14) + (textLines > 1 ? 4 : 0);
        }

        var ranks = g.Nodes.GroupBy(n => n.Rank).OrderBy(r => r.Key).ToList();

        if (!g.LeftRight)
        {
            // Wide ranks WRAP into multiple sub-rows instead of forcing a microscopic global scale —
            // big fan-outs stay page-width and readable. Two passes: pack, then centre.
            double usable = MaxCanvasW - 2 * Margin;
            var rows = new List<List<Node>>();
            foreach (var rank in ranks)
            {
                var cur = new List<Node>();
                double w = 0;
                foreach (var n in rank)
                {
                    double add = (cur.Count > 0 ? HGap : 0) + n.W;
                    if (cur.Count > 0 && w + add > usable) { rows.Add(cur); cur = new(); w = 0; add = n.W; }
                    cur.Add(n); w += add;
                }
                if (cur.Count > 0) rows.Add(cur);
            }

            double canvasW = rows.Max(r => r.Sum(n => n.W) + (r.Count - 1) * HGap) + 2 * Margin;
            double y = Margin;
            foreach (var row in rows)
            {
                double slot = row.Max(n => n.H);
                double rowW = row.Sum(n => n.W) + (row.Count - 1) * HGap;
                double x = (canvasW - rowW) / 2;
                foreach (var n in row) { n.X = x; n.Y = y + (slot - n.H) / 2; x += n.W + HGap; }
                y += slot + VGap;
            }
            g.W = canvasW; g.H = y - VGap + Margin;
        }
        else
        {
            // LR: tall ranks wrap into multiple sub-columns, symmetrically.
            double usable = MaxCanvasH - 2 * Margin;
            var cols = new List<List<Node>>();
            foreach (var rank in ranks)
            {
                var cur = new List<Node>();
                double h = 0;
                foreach (var n in rank)
                {
                    double add = (cur.Count > 0 ? HGap : 0) + n.H;
                    if (cur.Count > 0 && h + add > usable) { cols.Add(cur); cur = new(); h = 0; add = n.H; }
                    cur.Add(n); h += add;
                }
                if (cur.Count > 0) cols.Add(cur);
            }

            double canvasH = cols.Max(c => c.Sum(n => n.H) + (c.Count - 1) * HGap) + 2 * Margin;
            double x = Margin;
            foreach (var col in cols)
            {
                double colW = col.Max(n => n.W);
                double colH = col.Sum(n => n.H) + (col.Count - 1) * HGap;
                double y = (canvasH - colH) / 2;
                foreach (var n in col) { n.X = x + (colW - n.W) / 2; n.Y = y; y += n.H + HGap; }
                x += colW + VGap;
            }
            g.W = x - VGap + Margin; g.H = canvasH;
        }

        // Scale uniformly to fit the printable page — but never below 75%, where text stops being
        // readable. A diagram that would need more shrink keeps 75% and is flagged Oversized; the
        // exporter then opens the document in Word's Web Layout view, which scrolls instead of clips.
        double s = Math.Min(1, Math.Min(MaxCanvasW / g.W, MaxCanvasH / g.H));
        if (s < 0.75) { g.Oversized = true; s = 0.75; }
        if (s < 1)
        {
            foreach (var n in g.Nodes) { n.X *= s; n.Y *= s; n.W *= s; n.H *= s; }
            g.W *= s; g.H *= s;
            g.Scale = s;
        }
        // A diagram that dominates the page reads far better in Web Layout too — page breaks and
        // print margins just fight it. (Word still prints; the view is for reading/editing.)
        if (g.H > 480) g.Oversized = true;
    }

    // ---------------- DrawingML emit ----------------

    private static string BuildInlineXml(Graph g, ThemeDefinition t, uint drawingId)
    {
        long CX = Emu(g.W), CY = Emu(g.H);
        string fill = Hex(t.Background), border = Hex(t.Line), text = Hex(t.Primary),
               line = Hex(t.Line), bg = Hex(t.Background);

        var sb = new StringBuilder();
        uint id = drawingId * 100;

        foreach (var e in g.Edges) sb.Append(ConnectorXml(e, g, line, ++id));
        foreach (var n in g.Nodes) sb.Append(NodeXml(n, fill, border, text, ++id, g.Scale));
        foreach (var e in g.Edges)
            if (e.Label is not null) sb.Append(EdgeLabelXml(e, g, bg, text, ++id, g.Scale));

        return $"""
            <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <wp:extent cx="{CX}" cy="{CY}"/>
              <wp:effectExtent l="0" t="0" r="0" b="0"/>
              <wp:docPr id="{drawingId}" name="Mermaid diagram" descr="Flowchart rendered as native Word shapes by Marksmith"/>
              <wp:cNvGraphicFramePr/>
              <a:graphic>
                <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup">
                  <wpg:wgp>
                    <wpg:cNvGrpSpPr/>
                    <wpg:grpSpPr>
                      <a:xfrm><a:off x="0" y="0"/><a:ext cx="{CX}" cy="{CY}"/><a:chOff x="0" y="0"/><a:chExt cx="{CX}" cy="{CY}"/></a:xfrm>
                    </wpg:grpSpPr>
                    {sb}
                  </wpg:wgp>
                </a:graphicData>
              </a:graphic>
            </wp:inline>
            """;
    }

    private static string NodeXml(Node n, string fill, string border, string text, uint id, double scale)
    {
        var prst = n.Shape switch
        {
            "round" => "roundRect", "ellipse" => "ellipse", "diamond" => "diamond",
            "parallelogram" => "parallelogram", "hexagon" => "hexagon", _ => "rect",
        };
        return $"""
            <wps:wsp>
              <wps:cNvPr id="{id}" name="{XmlEsc(n.Id)}"/>
              <wps:cNvSpPr/>
              <wps:spPr>
                <a:xfrm><a:off x="{Emu(n.X)}" y="{Emu(n.Y)}"/><a:ext cx="{Emu(n.W)}" cy="{Emu(n.H)}"/></a:xfrm>
                <a:prstGeom prst="{prst}"><a:avLst/></a:prstGeom>
                <a:solidFill><a:srgbClr val="{fill}"/></a:solidFill>
                <a:ln w="12700"><a:solidFill><a:srgbClr val="{border}"/></a:solidFill></a:ln>
              </wps:spPr>
              <wps:txbx>
                <w:txbxContent>
                  {string.Join("", n.Label.Split('\n').Select(l =>
                      $"<w:p><w:pPr><w:suppressAutoHyphens/><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"216\" w:lineRule=\"auto\"/><w:jc w:val=\"center\"/></w:pPr>" +
                      $"<w:r><w:rPr><w:color w:val=\"{text}\"/><w:sz w:val=\"{Math.Max(12, (int)Math.Round(18 * scale))}\"/></w:rPr><w:t xml:space=\"preserve\">{XmlEsc(l)}</w:t></w:r></w:p>"))}
                </w:txbxContent>
              </wps:txbx>
              <wps:bodyPr lIns="27432" tIns="9144" rIns="27432" bIns="9144" anchor="ctr"><a:noAutofit/></wps:bodyPr>
            </wps:wsp>
            """;
    }

    private static string ConnectorXml(Edge e, Graph g, string line, uint id)
    {
        double x1, y1, x2, y2;
        if (!g.LeftRight)
        {
            // bottom-centre of source → top-centre of target (or sideways for same-rank edges)
            if (e.To.Rank > e.From.Rank)
            { x1 = e.From.X + e.From.W / 2; y1 = e.From.Y + e.From.H; x2 = e.To.X + e.To.W / 2; y2 = e.To.Y; }
            else
            { x1 = e.From.X + e.From.W / 2; y1 = e.From.Y; x2 = e.To.X + e.To.W / 2; y2 = e.To.Y + e.To.H; }
        }
        else
        {
            if (e.To.Rank > e.From.Rank)
            { x1 = e.From.X + e.From.W; y1 = e.From.Y + e.From.H / 2; x2 = e.To.X; y2 = e.To.Y + e.To.H / 2; }
            else
            { x1 = e.From.X; y1 = e.From.Y + e.From.H / 2; x2 = e.To.X + e.To.W; y2 = e.To.Y + e.To.H / 2; }
        }

        long ox = Emu(Math.Min(x1, x2)), oy = Emu(Math.Min(y1, y2));
        long cx = Math.Abs(Emu(x2) - Emu(x1)), cy = Math.Abs(Emu(y2) - Emu(y1));
        string flips = (x2 < x1 ? " flipH=\"1\"" : "") + (y2 < y1 ? " flipV=\"1\"" : "");
        string dash = e.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        string head = e.Arrow ? "<a:tailEnd type=\"triangle\" w=\"med\" len=\"med\"/>" : "";
        long weight = e.Thick ? 28575 : 12700;

        return $"""
            <wps:wsp>
              <wps:cNvPr id="{id}" name="edge"/>
              <wps:cNvCnPr/>
              <wps:spPr>
                <a:xfrm{flips}><a:off x="{ox}" y="{oy}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                <a:prstGeom prst="straightConnector1"><a:avLst/></a:prstGeom>
                <a:noFill/>
                <a:ln w="{weight}"><a:solidFill><a:srgbClr val="{line}"/></a:solidFill>{dash}{head}</a:ln>
              </wps:spPr>
              <wps:bodyPr/>
            </wps:wsp>
            """;
    }

    private static string EdgeLabelXml(Edge e, Graph g, string bg, string text, uint id, double scale)
    {
        // Sit the label a FIXED ~24pt along the edge from the source box — proportional placement
        // lands on other rows' boxes once ranks wrap. Very short edges skip their label entirely.
        double x1 = e.From.X + e.From.W / 2, y1 = e.From.Y + e.From.H / 2;
        double x2 = e.To.X + e.To.W / 2, y2 = e.To.Y + e.To.H / 2;
        double len = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        if (len < 30 * scale) return "";
        double tt = Math.Min(0.42, (24 * scale + e.From.H / 2) / len);
        double mx = x1 + (x2 - x1) * tt;
        double my = y1 + (y2 - y1) * tt;
        double w = Math.Clamp((10 + e.Label!.Length * 4.6) * scale, 20, 160), h = 15 * scale;
        return $"""
            <wps:wsp>
              <wps:cNvPr id="{id}" name="edge label"/>
              <wps:cNvSpPr/>
              <wps:spPr>
                <a:xfrm><a:off x="{Emu(mx - w / 2)}" y="{Emu(my - h / 2)}"/><a:ext cx="{Emu(w)}" cy="{Emu(h)}"/></a:xfrm>
                <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                <a:solidFill><a:srgbClr val="{bg}"/></a:solidFill>
                <a:ln><a:noFill/></a:ln>
              </wps:spPr>
              <wps:txbx>
                <w:txbxContent>
                  <w:p>
                    <w:pPr><w:spacing w:before="0" w:after="0"/><w:jc w:val="center"/></w:pPr>
                    <w:r><w:rPr><w:color w:val="{text}"/><w:sz w:val="15"/></w:rPr><w:t xml:space="preserve">{XmlEsc(e.Label)}</w:t></w:r>
                  </w:p>
                </w:txbxContent>
              </wps:txbx>
              <wps:bodyPr lIns="0" tIns="0" rIns="0" bIns="0" anchor="ctr"><a:noAutofit/></wps:bodyPr>
            </wps:wsp>
            """;
    }

    private static long Emu(double pt) => (long)Math.Round(pt * EmuPerPt);
    private static string Hex(string css) => css.TrimStart('#').ToUpperInvariant().PadLeft(6, '0')[..6];
    private static string XmlEsc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
