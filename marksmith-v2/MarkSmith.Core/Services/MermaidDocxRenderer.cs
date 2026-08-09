using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Models;
using MarkSmith.Services.Mermaid;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MarkSmith.Services;

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
        // The lossless Mermaid shape this node was parsed from — carried into the emitted shape's
        // structured name so the reverse importer (DocxShapeParser) can rebuild the exact AST node.
        // `Shape` above is lossy (several FlowNodeShapes collapse onto one Word preset), so this is
        // the source of truth for round-tripping.
        public FlowNodeShape FlowShape = FlowNodeShape.Rectangle;
        public int Rank;
        public double X, Y, W, H;
        public uint XmlId;
    }

    private sealed record Edge(Node From, Node To, string? Label, bool Dashed, bool Thick, ArrowHead StartHead, ArrowHead EndHead);

    private sealed class Graph
    {
        public bool LeftRight;
        public string Dir = "TD"; // the declared flow direction (TD|TB|LR|RL|BT) — tagged for round-trip
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

    // Would this flowchart overflow a printed page (and thus benefit from exact-layout + Web Layout)?
    // Runs the real parse+layout but emits nothing — used to decide whether to prompt the user.
    public static bool WouldOverflow(string source)
    {
        try
        {
            var type = FirstWord(source);
            if (type is not ("graph" or "flowchart")) return false;
            var g = Parse(source);
            if (g is null || g.Nodes.Count == 0 || g.Nodes.Count > MaxNodes) return false;
            Layout(g);
            return g.Oversized || g.H > 480;
        }
        catch { return false; }
    }

    // Diagram families a bespoke renderer handles directly (no generic harvest needed).
    private static readonly HashSet<string> BespokeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "graph", "flowchart", "sequencediagram", "classdiagram", "erdiagram",
        "mindmap", "timeline", "journey", "gitgraph",
        "pie", "gantt", "quadrantchart", "xychart-beta", "xychart",
    };

    // True if the document has any mermaid fence whose type ISN'T handled by a bespoke renderer —
    // i.e. one that needs the generic harvested-geometry path (state, C4, block, kanban, packet,
    // sankey, requirement, architecture, …) rather than falling back to a picture.
    public static bool HasUnsupportedFence(string markdown)
    {
        var fences = Regex.Matches(
            markdown.Replace("\r\n", "\n").Replace('\r', '\n'),
            "```mermaid[ \\t]*\\n(.*?)```", RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match f in fences)
            if (!BespokeTypes.Contains(FirstWord(f.Groups[1].Value))) return true;
        return false;
    }

    // Any mermaid fence in the document that would overflow — the trigger for the export-time prompt.
    public static bool AnyWouldOverflow(string markdown)
    {
        var fences = Regex.Matches(
            markdown.Replace("\r\n", "\n").Replace('\r', '\n'),
            "```mermaid[ \\t]*\\n(.*?)```", RegexOptions.Singleline);
        foreach (System.Text.RegularExpressions.Match f in fences)
            if (WouldOverflow(f.Groups[1].Value)) return true;
        return false;
    }

    // forceFit: true when the caller explicitly chose "reflow to fit the page" (as opposed to
    // "keep exact layout") — in that case a diagram too big to fit at a readable 75% scale should
    // shrink as far as it takes to actually fit, not stop at 75% and overflow the page anyway. Only
    // the flowchart path below currently honors it (the bespoke sequence/class-er/trees/charts
    // renderers compute `oversized` their own way, via DocxShapeEmitter) — narrower scope than
    // ideal, but that's not the path the reported overflow came from.
    public static bool TryRender(string source, ThemeDefinition theme, AppSettings settings, uint drawingId, out W.Paragraph paragraph, out bool oversized, bool forceFit = false)
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
                
                paragraph = new W.Paragraph { InnerXml = Mermaid.DocxShapeEmitter.ToParagraphXml(d, theme, drawingId, out oversized, smartConnectors: settings.SmartConnectors, connectorRouting: settings.ConnectorRouting) };
                paragraph.PrependChild(new W.ParagraphProperties( // schema order: spacing before jc
                    new W.SpacingBetweenLines { Before = "120", After = "120" },
                    new W.Justification { Val = W.JustificationValues.Center }));
                return true;
            }

            var g = Parse(source);
            if (g is null || g.Nodes.Count == 0 || g.Nodes.Count > MaxNodes) {
                System.Diagnostics.Debug.WriteLine($"[MERMAID] parse returned null or out of bounds (nodes={g?.Nodes.Count})");
                return false;
            }
            Layout(g, forceFit);
            oversized = g.Oversized;

            var drawing = new W.Drawing { InnerXml = BuildInlineXml(g, theme, settings, drawingId) };
            paragraph = new W.Paragraph(
                new W.ParagraphProperties( // schema order: spacing before jc
                    new W.SpacingBetweenLines { Before = "120", After = "120" },
                    new W.Justification { Val = W.JustificationValues.Center }),
                new W.Run(drawing));
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TryRender exception: {ex}");
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
            if (m.Success)
            {
                var word = m.Groups[1].Value.ToLowerInvariant();
                if (word is "graph" or "flowchart" || BespokeTypes.Contains(word)) return word;
            }
        }
        return "";
    }

    // ---------------- parsing ----------------

    private static Graph? Parse(string src)
    {
        string? dir = null;
        var g = new Graph();
        
        // Normalize multiline pipes created by AIs to single-line pipes
        src = Regex.Replace(src, @"\|\s*\r?\n\s*([^|]*?)\s*\r?\n\s*\|", "|$1|");
        
        foreach (var raw in src.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%")) continue;
            if (dir is null)
            {
                var m = Regex.Match(line, @"^(?:graph|flowchart)\s+(TD|TB|LR|RL|BT)\b", RegexOptions.IgnoreCase);
                if (!m.Success) continue; // not a flowchart declaration yet, keep looking
                dir = m.Groups[1].Value.ToUpperInvariant();
                continue;
            }
            // Grouping/styling lines don't affect the shape graph; skip them.
            if (Regex.IsMatch(line, @"^(subgraph\b|end\b|direction\b|classDef\b|class\b|style\b|linkStyle\b|click\b|accTitle\b|accDescr\b)")) continue;
            if (!ParseLine(line, g)) {
                System.Diagnostics.Debug.WriteLine($"[MERMAID] ParseLine failed on: '{line}'");
                return null; // partially-understood line → refuse (fallback)
            }
        }
        if (dir is null) return null;
        g.Dir = dir;
        g.LeftRight = dir is "LR" or "RL";
        return g;
    }

    private static bool ParseLine(string line, Graph g)
    {
        line = line.TrimEnd(';').Trim();
        int i = 0;
        var left = ReadNodeGroup(line, ref i, g);
        if (left is null)
        {
            // If the line starts directly with an arrow (e.g. `-->B{Decision}`), chain from the last node
            if (g.Nodes.Count > 0 && ReadArrow(line, ref i) is { } startArrow)
            {
                var lastNode = g.Nodes[^1];
                var right = ReadNodeGroup(line, ref i, g);
                if (right is null) return false;
                bool first = true;
                foreach (var v in right)
                {
                    g.Edges.Add(new Edge(lastNode, v, first ? startArrow.Label : null,
                        startArrow.Dashed, startArrow.Thick, startArrow.StartHead, startArrow.EndHead));
                    first = false;
                }
                left = right;
            }
            else
            {
                return false;
            }
        }
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
                        arrow.Value.Dashed, arrow.Value.Thick, arrow.Value.StartHead, arrow.Value.EndHead));
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
        @"\G\s*(<|o|x|X|O)?(-\.+-[>oxXO]?|-{2,3}[>oxXO]?|={2,3}[>oxXO]?)\s*(?:\|([^|]*)\||""([^""]*)"")?\s*", RegexOptions.Compiled);

    private static (string? Label, bool Dashed, bool Thick, ArrowHead StartHead, ArrowHead EndHead)? ReadArrow(string s, ref int i)
    {
        var m = ArrowRe.Match(s, i);
        if (!m.Success || m.Index != i) return null;
        i = m.Index + m.Length;
        var startChar = m.Groups[1].Value.ToLowerInvariant();
        var tok = m.Groups[2].Value.ToLowerInvariant();
        var label = m.Groups[3].Success ? Clean(m.Groups[3].Value) : m.Groups[4].Success ? Clean(m.Groups[4].Value) : null;
        
        ArrowHead startHead = startChar switch { "<" => ArrowHead.Triangle, "o" => ArrowHead.Oval, "x" => ArrowHead.Diamond, _ => ArrowHead.None };
        ArrowHead endHead = ArrowHead.None;
        if (tok.EndsWith('>')) endHead = ArrowHead.Triangle;
        else if (tok.EndsWith('o')) endHead = ArrowHead.Oval;
        else if (tok.EndsWith('x')) endHead = ArrowHead.Diamond;

        return (string.IsNullOrWhiteSpace(label) ? null : label, tok.Contains('.'), tok.StartsWith('='), startHead, endHead);
    }

    private static readonly Regex IdRe = new(@"\G\s*([A-Za-z0-9_](?:(?!--|-\.)[A-Za-z0-9_.:-])*)", RegexOptions.Compiled);
    private static readonly Regex AmpRe = new(@"\G\s*&\s*", RegexOptions.Compiled);

    // Bracket pairs, longest openers first, mapped to Word preset geometries AND the lossless
    // Mermaid FlowNodeShape each opener denotes (carried on the node for reverse-import tagging).
    private static readonly (string Open, string Close, string Shape, FlowNodeShape Flow)[] Brackets =
    {
        ("((", "))", "ellipse", FlowNodeShape.Circle),
        ("([", "])", "round", FlowNodeShape.Stadium),
        ("[[", "]]", "rect", FlowNodeShape.Subroutine),
        ("[/", "/]", "parallelogram", FlowNodeShape.Parallelogram),
        ("[\\", "\\]", "parallelogram", FlowNodeShape.Parallelogram),
        ("[/", "\\]", "trapezoid", FlowNodeShape.Trapezoid),
        ("[\\", "/]", "trapezoid", FlowNodeShape.Trapezoid),
        ("{{", "}}", "hexagon", FlowNodeShape.Hexagon),
        ("[(", ")]", "database", FlowNodeShape.CylindricalDatabase),
        ("[", "]", "rect", FlowNodeShape.Rectangle),
        ("(", ")", "round", FlowNodeShape.RoundedRectangle),
        ("{", "}", "diamond", FlowNodeShape.RhombusDiamond),
        (">", "]", "rect", FlowNodeShape.Asymmetric),
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

        foreach (var (open, close, shape, flow) in Brackets)
        {
            if (i + open.Length > s.Length || s.Substring(i, open.Length) != open) continue;
            var end = s.IndexOf(close, i + open.Length, StringComparison.Ordinal);
            if (end < 0) return null; // unterminated bracket → unsupported line
            node.Label = Clean(s.Substring(i + open.Length, end - i - open.Length));
            node.Shape = shape;
            node.FlowShape = flow;
            i = end + close.Length;
            break;
        }
        return node;
    }

    private static string Clean(string s)
    {
        s = s.Replace("\\n", "\n"); // Mermaid literal \n
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase); // real line breaks in Word
        s = WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", ""));
        return s.Trim().Trim('"').Replace("**", "").Replace("`", "").Trim();
    }

    // ---------------- layout ----------------

    private static void Layout(Graph g, bool forceFit = false)
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
                "database" => baseW * 1.15,
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
        // EXCEPT when the caller explicitly asked for "reflow to fit the page" (forceFit): that
        // choice's entire point is "prioritize staying on the page over staying big," so the 75%
        // floor doesn't apply there — it shrinks as far as it takes to actually fit, full stop.
        // Without this, a large diagram under "reflow" still hit the floor, got marked Oversized,
        // forced Web Layout view anyway, AND visually overflowed the page — the reflow choice
        // silently did nothing.
        double s = Math.Min(1, Math.Min(MaxCanvasW / g.W, MaxCanvasH / g.H));
        if (s < 0.75 && !forceFit) { g.Oversized = true; s = 0.75; }
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

    private static string BuildInlineXml(Graph g, ThemeDefinition t, AppSettings settings, uint drawingId)
    {
        long CX = Emu(g.W), CY = Emu(g.H);
        bool isDarkDoc = !ThemeDefinition.IsLight(t.Background);
        string nodeFill = Hex(t.Secondary);
        if (isDarkDoc && ThemeDefinition.IsLight("#" + nodeFill)) nodeFill = "2B303B";
        string border = Hex(t.Border ?? t.Line);
        string text = ContrastGuard.EnsureLegibleText(Hex(t.Text ?? t.Primary), nodeFill);
        string line = Hex(t.Line);
        string bg = Hex(t.Background);

        var sb = new StringBuilder();
        uint id = drawingId * 100;

        // Pre-assign XML IDs so connectors can anchor to them.
        foreach (var n in g.Nodes) n.XmlId = ++id;

        foreach (var e in g.Edges) sb.Append(ConnectorXml(e, g, line, ++id, t, settings));
        foreach (var n in g.Nodes) sb.Append(NodeXml(n, nodeFill, border, text, n.XmlId, g.Scale));
        foreach (var e in g.Edges)
            if (e.Label is not null) sb.Append(EdgeLabelXml(e, g, bg, text, ++id, g.Scale));

        return $"""
            <wp:inline distT="0" distB="0" distL="0" distR="0" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:wpg="http://schemas.microsoft.com/office/word/2010/wordprocessingGroup" xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <wp:extent cx="{CX}" cy="{CY}"/>
              <wp:effectExtent l="0" t="0" r="0" b="0"/>
              <wp:docPr id="{drawingId}" name="Mermaid diagram" descr="Marksmith mermaid flowchart dir={g.Dir}"/>
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
            "parallelogram" => "parallelogram", "hexagon" => "hexagon", "database" => "can", _ => "rect",
        };
        return $"""
            <wps:wsp>
              <wps:cNvPr id="{id}" name="{XmlEsc(NodeTag(n))}"/>
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

    private static string ConnectorXml(Edge e, Graph g, string line, uint id, ThemeDefinition t, AppSettings settings)
    {
        double x1, y1, x2, y2;
        int stIdx, endIdx;
        if (!g.LeftRight)
        {
            // bottom-centre of source → top-centre of target (or sideways for same-rank edges)
            if (e.To.Rank > e.From.Rank)
            {
                x1 = e.From.X + e.From.W / 2; y1 = e.From.Y + e.From.H;
                x2 = e.To.X + e.To.W / 2; y2 = e.To.Y;
                stIdx = 2; // Bottom
                endIdx = 0; // Top
            }
            else
            {
                x1 = e.From.X + e.From.W / 2; y1 = e.From.Y;
                x2 = e.To.X + e.To.W / 2; y2 = e.To.Y + e.To.H;
                stIdx = 0; // Top
                endIdx = 2; // Bottom
            }
        }
        else
        {
            if (e.To.Rank > e.From.Rank)
            {
                x1 = e.From.X + e.From.W; y1 = e.From.Y + e.From.H / 2;
                x2 = e.To.X; y2 = e.To.Y + e.To.H / 2;
                stIdx = 3; // Right
                endIdx = 1; // Left
            }
            else
            {
                x1 = e.From.X; y1 = e.From.Y + e.From.H / 2;
                x2 = e.To.X + e.To.W; y2 = e.To.Y + e.To.H / 2;
                stIdx = 1; // Left
                endIdx = 3; // Right
            }
        }

        long ox = Emu(Math.Min(x1, x2)), oy = Emu(Math.Min(y1, y2));
        long cx = Math.Abs(Emu(x2) - Emu(x1)), cy = Math.Abs(Emu(y2) - Emu(y1));
        string flips = (x2 < x1 ? " flipH=\"1\"" : "") + (y2 < y1 ? " flipV=\"1\"" : "");
        string dash = e.Dashed ? "<a:prstDash val=\"dash\"/>" : "";
        
        ArrowHead sHead = e.StartHead;
        ArrowHead eHead = e.EndHead;
        
        // Apply Fallbacks if ArrowHead.None and setting is something else
        if (sHead == ArrowHead.None && settings.ConnectorArrowhead is not "default" and not "none")
            sHead = ParseArrowHeadSetting(settings.ConnectorArrowhead, isStart: true);
        if (eHead == ArrowHead.None && settings.ConnectorArrowhead is not "default" and not "none")
            eHead = ParseArrowHeadSetting(settings.ConnectorArrowhead, isStart: false);

        string startHead = HeadXml("headEnd", sHead);
        string endHead = HeadXml("tailEnd", eHead);
        long weight = e.Thick ? 28575 : 12700;
        
        string cxnAttr = "";
        // If the theme/settings disable smart connectors, we skip this
        if (settings.SmartConnectors && e.From.XmlId > 0 && e.To.XmlId > 0)
        {
            cxnAttr = $"<a:stCxn id=\"{e.From.XmlId}\" idx=\"{stIdx}\"/><a:endCxn id=\"{e.To.XmlId}\" idx=\"{endIdx}\"/>";
        }
        
        string prst = settings.ConnectorRouting switch
        {
            "elbow" => "bentConnector3",
            "curved" => "curveConnector3",
            "straight" => "straightConnector1",
            _ => "straightConnector1", // "default": straight (this renderer has no per-edge elbow data)
        };

        return $"""
            <wps:wsp>
              <wps:cNvPr id="{id}" name="{XmlEsc(EdgeTag(e))}"/>
              <wps:cNvCnPr>{cxnAttr}</wps:cNvCnPr>
              <wps:spPr>
                <a:xfrm{flips}><a:off x="{ox}" y="{oy}"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm>
                <a:prstGeom prst="{prst}"><a:avLst/></a:prstGeom>
                <a:noFill/>
                <a:ln w="{weight}"><a:solidFill><a:srgbClr val="{line}"/></a:solidFill>{dash}{startHead}{endHead}</a:ln>
              </wps:spPr>
              <wps:bodyPr/>
            </wps:wsp>
            """;
    }

    private static string HeadXml(string el, ArrowHead h) => h switch
    {
        ArrowHead.None => $"<a:{el} type=\"none\"/>",
        ArrowHead.Triangle => $"<a:{el} type=\"triangle\" w=\"med\" len=\"med\"/>",
        ArrowHead.Open => $"<a:{el} type=\"arrow\" w=\"med\" len=\"med\"/>",
        ArrowHead.Diamond => $"<a:{el} type=\"diamond\" w=\"med\" len=\"med\"/>",
        ArrowHead.Oval => $"<a:{el} type=\"oval\" w=\"med\" len=\"med\"/>",
        ArrowHead.Stealth => $"<a:{el} type=\"stealth\" w=\"med\" len=\"med\"/>",
        _ => $"<a:{el} type=\"none\"/>",
    };
    
    private static ArrowHead ParseArrowHeadSetting(string setting, bool isStart) => setting.ToLowerInvariant() switch
    {
        "triangle" => ArrowHead.Triangle,
        "open" => ArrowHead.Open,
        "diamond" => ArrowHead.Diamond,
        "oval" => ArrowHead.Oval,
        "stealth" => ArrowHead.Stealth,
        _ => ArrowHead.None
    };

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
              <wps:cNvPr id="{id}" name="{XmlEsc(EdgeLabelTag(e))}"/>
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

    // ---- reverse-import identity tags --------------------------------------------------------
    // Structured names written into each shape's wps:cNvPr name so the DOCX->Mermaid importer
    // (DocxShapeParser) can recover the diagram losslessly. Convention:
    //   node:  ms:node=<id>;kind=<FlowNodeShape>
    //   edge:  ms:edge=<from>--><to>;style=<Solid|Dashed|Thick>;start=<ArrowHead>;end=<ArrowHead>
    //   label: ms:edge=<from>--><to>;label=<text>          (label is LAST — may contain ';')
    private static string NodeTag(Node n) => $"ms:node={n.Id};kind={n.FlowShape}";

    private static string EdgeTag(Edge e) =>
        $"ms:edge={e.From.Id}-->{e.To.Id};style={EdgeStyleTag(e)};start={e.StartHead};end={e.EndHead}";

    private static string EdgeLabelTag(Edge e) => $"ms:edge={e.From.Id}-->{e.To.Id};label={e.Label}";

    private static string EdgeStyleTag(Edge e) => e.Thick ? "Thick" : e.Dashed ? "Dashed" : "Solid";
}
