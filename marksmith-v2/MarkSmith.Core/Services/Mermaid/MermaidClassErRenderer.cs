using System.Text.RegularExpressions;
using MarkSmith.Models;

namespace MarkSmith.Services.Mermaid;

// Renders mermaid "classDiagram" and "erDiagram" sources into pure geometry (MDiagram).
// Classes/entities become stacked compartment boxes (header Rect + members Rect with one Text
// shape per member line); relationships become border-to-border connectors with UML / crow's-foot
// approximated arrow heads. Layout is a degree-ordered grid with one pair-swap improvement pass.
public sealed class MermaidClassErRenderer : IMermaidRenderer
{
    private const double CharW = 6.0;        // crude text width estimate per char at ~10pt
    private const double LineH = 15.0;       // member line height
    private const double MinBoxW = 110.0;
    private const double HeaderHPlain = 24.0;
    private const double HeaderHAnnotated = 36.0;
    private const double GapX = 60.0;
    private const double GapY = 50.0;
    private const double Margin = 10.0;

    // Mermaid paints ER attribute ROWS (not the page background): odd rows white, even rows light
    // grey — fixed colours, independent of the theme (confirmed against mermaid's attributeBoxOdd /
    // attributeBoxEven for both light and dark palettes). Reproducing them keeps the entity reading
    // as a coloured header over a white table instead of one solid block of the theme colour.
    private const string ErAttrOddFill = "#ffffff";
    private const string ErAttrEvenFill = "#f2f2f2";

    public bool CanRender(string diagramType) =>
        diagramType is "classdiagram" or "erdiagram";

    public MDiagram Render(string source, ThemeDefinition theme)
    {
        var lines = Preprocess(source);
        if (lines.Count == 0)
            throw new MermaidParseException("Empty mermaid source.");

        string head = lines[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        List<Node> nodes;
        List<Rel> rels;
        switch (head)
        {
            case "classdiagram":
                (nodes, rels) = ParseClassDiagram(lines);
                break;
            case "erdiagram":
                (nodes, rels) = ParseErDiagram(lines);
                break;
            default:
                throw new MermaidParseException($"Unsupported diagram type '{head}'.");
        }

        if (nodes.Count == 0)
            throw new MermaidParseException("Diagram declares no classes/entities.");

        foreach (var n in nodes) Measure(n);
        Layout(nodes, rels);
        return Emit(nodes, rels, theme);
    }

    // ------------------------------------------------------------------ model

    private sealed class Node
    {
        public required string Name { get; init; }
        public string? Annotation;                 // e.g. "interface" / "abstract"
        public readonly List<string> Attrs = new();
        public readonly List<string> Methods = new();
        public bool IsClass;                       // class => attrs+methods compartments + divider
        public int Order;                          // declaration order (stable sort key)
        public int Degree;
        public double W, H, HeaderH, AttrsH, MethodsH;
        public double X, Y;
        public int Row, Col;
        public double CX => X + W / 2;
        public double CY => Y + H / 2;
    }

    private sealed class Rel
    {
        public required Node From { get; init; }
        public required Node To { get; init; }
        public ArrowHead StartHead;                // at From end
        public ArrowHead EndHead;                  // at To end
        public bool Dashed;
        public string? Label;
        public string? FromCard;                   // class: role-name text; ER: raw cardinality token
        public string? ToCard;
        public bool IsEr;                          // erDiagram rel → graphical crow's-foot markers, not text
    }

    // ------------------------------------------------------------ preprocess

    private static List<string> Preprocess(string source)
    {
        var result = new List<string>();
        foreach (var raw in source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length > 0) result.Add(line);
        }
        return result;
    }

    private static string StripComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            char c = line[i];
            if (c == '"') inQuote = !inQuote;
            else if (!inQuote && c == '%' && line[i + 1] == '%') return line[..i];
        }
        return line;
    }

    // ---------------------------------------------------------- classDiagram

    private static readonly Regex ClassBlockStart = new(
        @"^class\s+([A-Za-z_][\w~.]*)\s*(\{)?\s*$", RegexOptions.Compiled);

    private static readonly Regex ClassAnnotationLine = new(
        @"^<<\s*([\w\s]+?)\s*>>\s+([A-Za-z_][\w~.]*)$", RegexOptions.Compiled);

    private static readonly Regex ClassMemberLine = new(
        @"^([A-Za-z_][\w~.]*)\s*:\s*(.+)$", RegexOptions.Compiled);

    private static readonly Regex ClassRelLine = new(
        @"^([A-Za-z_][\w~.]*)\s*(?:""([^""]*)""\s*)?((?:<\||<|\*|o)?(?:--|\.\.)(?:\|>|>|\*|o)?)\s*(?:""([^""]*)""\s*)?([A-Za-z_][\w~.]*)\s*(?::\s*(.*))?$",
        RegexOptions.Compiled);

    private static (List<Node>, List<Rel>) ParseClassDiagram(List<string> lines)
    {
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        var nodes = new List<Node>();
        var rels = new List<Rel>();

        Node GetOrAdd(string name)
        {
            if (!byName.TryGetValue(name, out var n))
            {
                n = new Node { Name = name, IsClass = true, Order = nodes.Count };
                byName[name] = n;
                nodes.Add(n);
            }
            return n;
        }

        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("direction ", StringComparison.OrdinalIgnoreCase)) continue;

            var block = ClassBlockStart.Match(line);
            if (block.Success)
            {
                var node = GetOrAdd(block.Groups[1].Value);
                if (block.Groups[2].Success) // "class Name {" — consume until "}"
                {
                    i++;
                    for (; i < lines.Count && lines[i] != "}"; i++)
                    {
                        var member = lines[i].TrimEnd(';').Trim();
                        if (member.Length == 0) continue;
                        var ann = Regex.Match(member, @"^<<\s*([\w\s]+?)\s*>>$");
                        if (ann.Success) { node.Annotation = ann.Groups[1].Value; continue; }
                        AddClassMember(node, member);
                    }
                    if (i >= lines.Count)
                        throw new MermaidParseException($"Unterminated class block for '{node.Name}'.");
                }
                continue;
            }

            var annLine = ClassAnnotationLine.Match(line);
            if (annLine.Success)
            {
                GetOrAdd(annLine.Groups[2].Value).Annotation = annLine.Groups[1].Value;
                continue;
            }

            var rel = ClassRelLine.Match(line);
            if (rel.Success && IsClassArrow(rel.Groups[3].Value))
            {
                string arrow = rel.Groups[3].Value;
                bool dashed = arrow.Contains("..");
                SplitClassArrow(arrow, out string left, out string right);
                rels.Add(new Rel
                {
                    From = GetOrAdd(rel.Groups[1].Value),
                    To = GetOrAdd(rel.Groups[5].Value),
                    StartHead = ClassHead(left, dashed),
                    EndHead = ClassHead(right, dashed),
                    Dashed = dashed,
                    FromCard = rel.Groups[2].Success ? rel.Groups[2].Value : null,
                    ToCard = rel.Groups[4].Success ? rel.Groups[4].Value : null,
                    Label = rel.Groups[6].Success && rel.Groups[6].Value.Trim().Length > 0
                        ? rel.Groups[6].Value.Trim() : null,
                });
                continue;
            }

            var member2 = ClassMemberLine.Match(line);
            if (member2.Success)
            {
                AddClassMember(GetOrAdd(member2.Groups[1].Value), member2.Groups[2].Value.Trim());
                continue;
            }

            throw new MermaidParseException($"Cannot parse classDiagram line: '{line}'.");
        }

        CountDegrees(rels);
        return (nodes, rels);
    }

    private static void AddClassMember(Node node, string member)
    {
        if (member.Contains('(')) node.Methods.Add(member);
        else node.Attrs.Add(member);
    }

    private static bool IsClassArrow(string arrow) =>
        arrow.Contains("--") || arrow.Contains("..");

    private static void SplitClassArrow(string arrow, out string left, out string right)
    {
        int idx = arrow.IndexOf("--", StringComparison.Ordinal);
        if (idx < 0) idx = arrow.IndexOf("..", StringComparison.Ordinal);
        left = arrow[..idx];
        right = arrow[(idx + 2)..];
    }

    private static ArrowHead ClassHead(string marker, bool dashed) => marker switch
    {
        "" => ArrowHead.None,
        "<|" or "|>" => ArrowHead.Open,                         // inheritance / realization
        "*" => ArrowHead.Diamond,                               // composition
        "o" => ArrowHead.Diamond,                               // aggregation (open diamond unsupported)
        "<" or ">" => dashed ? ArrowHead.Open : ArrowHead.Triangle, // dependency / association
        _ => throw new MermaidParseException($"Unknown class relationship marker '{marker}'."),
    };

    // ------------------------------------------------------------- erDiagram

    private static readonly Regex ErRelLine = new(
        @"^([A-Za-z_][\w-]*)\s*(\|\||\|o|o\||\}o|\}\||o\{|\|\{)(--|\.\.)(\|\||\|o|o\||\}o|\}\||o\{|\|\{)\s*([A-Za-z_][\w-]*)\s*(?::\s*(.+))?$",
        RegexOptions.Compiled);

    private static readonly Regex ErEntityBlockStart = new(
        @"^([A-Za-z_][\w-]*)\s*\{$", RegexOptions.Compiled);

    private static readonly Regex ErBareEntity = new(
        @"^[A-Za-z_][\w-]*$", RegexOptions.Compiled);

    private static (List<Node>, List<Rel>) ParseErDiagram(List<string> lines)
    {
        var byName = new Dictionary<string, Node>(StringComparer.Ordinal);
        var nodes = new List<Node>();
        var rels = new List<Rel>();

        Node GetOrAdd(string name)
        {
            if (!byName.TryGetValue(name, out var n))
            {
                n = new Node { Name = name, IsClass = false, Order = nodes.Count };
                byName[name] = n;
                nodes.Add(n);
            }
            return n;
        }

        for (int i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.StartsWith("direction ", StringComparison.OrdinalIgnoreCase)) continue;

            var rel = ErRelLine.Match(line);
            if (rel.Success)
            {
                string leftTok = rel.Groups[2].Value, rightTok = rel.Groups[4].Value;
                string? label = rel.Groups[6].Success ? rel.Groups[6].Value.Trim().Trim('"') : null;
                rels.Add(new Rel
                {
                    From = GetOrAdd(rel.Groups[1].Value),
                    To = GetOrAdd(rel.Groups[5].Value),
                    // ER crow's-foot notation has no arrowheads — the cardinality markers replace them.
                    StartHead = ArrowHead.None,
                    EndHead = ArrowHead.None,
                    Dashed = rel.Groups[3].Value == "..",       // non-identifying
                    Label = string.IsNullOrEmpty(label) ? null : label,
                    FromCard = leftTok,                          // raw token → graphical marker at emit
                    ToCard = rightTok,
                    IsEr = true,
                });
                continue;
            }

            var block = ErEntityBlockStart.Match(line);
            if (block.Success)
            {
                var node = GetOrAdd(block.Groups[1].Value);
                i++;
                for (; i < lines.Count && lines[i] != "}"; i++)
                    node.Attrs.Add(ParseErAttribute(lines[i], node.Name));
                if (i >= lines.Count)
                    throw new MermaidParseException($"Unterminated entity block for '{node.Name}'.");
                continue;
            }

            if (ErBareEntity.IsMatch(line)) { GetOrAdd(line); continue; }

            throw new MermaidParseException($"Cannot parse erDiagram line: '{line}'.");
        }

        CountDegrees(rels);
        return (nodes, rels);
    }

    private static string ParseErAttribute(string line, string entity)
    {
        int quote = line.IndexOf('"');                          // strip trailing comment
        if (quote >= 0) line = line[..quote];
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new MermaidParseException($"Cannot parse attribute '{line.Trim()}' in entity '{entity}'.");
        var keys = parts.Skip(2)
            .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Where(k => k is "PK" or "FK" or "UK")
            .ToList();
        string text = $"{parts[0]} {parts[1]}";
        if (keys.Count > 0) text += " " + string.Join(",", keys);
        return text;
    }

    // Crow's-foot cardinality is drawn graphically at emit time (see DrawErCardinality) — mermaid
    // never renders "1" / "0..N" text, so neither do we.

    private static void CountDegrees(List<Rel> rels)
    {
        foreach (var r in rels)
        {
            r.From.Degree++;
            if (!ReferenceEquals(r.From, r.To)) r.To.Degree++;
        }
    }

    // ----------------------------------------------------------------- sizing

    private static void Measure(Node n)
    {
        double maxLen = n.Name.Length;
        if (n.Annotation != null) maxLen = Math.Max(maxLen, n.Annotation.Length + 2); // «...»
        foreach (var m in n.Attrs) maxLen = Math.Max(maxLen, m.Length);
        foreach (var m in n.Methods) maxLen = Math.Max(maxLen, m.Length);

        n.W = Math.Max(MinBoxW, maxLen * CharW + 24);
        n.HeaderH = n.Annotation != null ? HeaderHAnnotated : HeaderHPlain;
        n.AttrsH = Math.Max(10, n.Attrs.Count * LineH + 4);
        n.MethodsH = n.IsClass ? Math.Max(10, n.Methods.Count * LineH + 4) : 0;
        n.H = n.HeaderH + n.AttrsH + n.MethodsH;
    }

    // ----------------------------------------------------------------- layout

    private static void Layout(List<Node> nodes, List<Rel> rels)
    {
        // Layered top-to-bottom layout — the same family as mermaid's dagre (rankdir TB). Edges
        // flow From -> To downward, so a relationship chain A -> B -> C stacks vertically in
        // declaration order instead of spilling into an L-shaped grid. Layers come from the longest
        // path out of the sources (in-degree 0); nodes within a layer are ordered by the barycentre
        // of their neighbours to uncross edges; each layer is centred horizontally.

        // Directed adjacency (From -> To), ignoring self loops.
        var children = new Dictionary<Node, List<Node>>();
        var parents = new Dictionary<Node, List<Node>>();
        var remaining = new Dictionary<Node, int>();
        foreach (var n in nodes) { children[n] = new(); parents[n] = new(); remaining[n] = 0; }
        foreach (var r in rels)
        {
            if (ReferenceEquals(r.From, r.To)) continue;
            children[r.From].Add(r.To);
            parents[r.To].Add(r.From);
            remaining[r.To]++;
        }

        // Longest-path layering via Kahn's topological order (stable by declaration order).
        var layer = new Dictionary<Node, int>();
        var queue = new Queue<Node>(nodes.Where(n => remaining[n] == 0).OrderBy(n => n.Order));
        foreach (var n in queue) layer[n] = 0;
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (var c in children[n].OrderBy(x => x.Order))
            {
                layer[c] = Math.Max(layer.GetValueOrDefault(c, 0), layer[n] + 1);
                if (--remaining[c] == 0) queue.Enqueue(c);
            }
        }
        // Anything still unplaced sits on a cycle — park it below the deepest placed layer.
        int maxLayer = layer.Count > 0 ? layer.Values.Max() : -1;
        foreach (var n in nodes.Where(n => !layer.ContainsKey(n)).OrderBy(n => n.Order))
            layer[n] = ++maxLayer;

        // Group into layers, seeded in declaration order.
        int layerCount = layer.Values.Max() + 1;
        var layers = new List<List<Node>>(layerCount);
        for (int i = 0; i < layerCount; i++) layers.Add(new());
        foreach (var n in nodes.OrderBy(n => n.Order)) layers[layer[n]].Add(n);

        // Barycentre sweeps: order each layer by the mean slot of its parents (sweeping down) then
        // its children (sweeping up), so edges run straight down where the topology allows.
        for (int iter = 0; iter < 4; iter++)
        {
            for (int li = 1; li < layerCount; li++)
                OrderLayer(layers[li], n => parents[n], layers, layer, downward: true);
            for (int li = layerCount - 2; li >= 0; li--)
                OrderLayer(layers[li], n => children[n], layers, layer, downward: false);
        }

        // Coordinates: layer index -> Y (top to bottom); each layer centred on the widest one.
        double cellH = nodes.Max(n => n.H) + GapY;
        double LayerWidth(List<Node> l) => l.Sum(n => n.W) + GapX * Math.Max(0, l.Count - 1);
        double centerX = Margin + layers.Max(LayerWidth) / 2;

        for (int li = 0; li < layerCount; li++)
        {
            var l = layers[li];
            double x = centerX - LayerWidth(l) / 2;
            for (int pi = 0; pi < l.Count; pi++)
            {
                var n = l[pi];
                n.Row = li;                       // routing heuristics read Row/Col
                n.Col = pi;
                n.X = x;
                n.Y = Margin + li * cellH + (cellH - n.H) / 2;
                x += n.W + GapX;
            }
        }
    }

    // Reorder `layerNodes` in place by the barycentre (mean slot) of their neighbours in the
    // adjacent fixed layer — parents when sweeping down, children when sweeping up. Nodes with no
    // neighbour in that layer keep their current slot so the order stays stable.
    private static void OrderLayer(List<Node> layerNodes, Func<Node, List<Node>> neighbours,
        List<List<Node>> layers, Dictionary<Node, int> layer, bool downward)
    {
        var pos = new Dictionary<Node, int>();
        for (int i = 0; i < layerNodes.Count; i++) pos[layerNodes[i]] = i;

        double Barycentre(Node n)
        {
            int adjLayer = layer[n] + (downward ? -1 : 1);
            var adj = neighbours(n).Where(a => layer.GetValueOrDefault(a, -1) == adjLayer).ToList();
            if (adj.Count == 0) return pos[n];
            double sum = 0; int cnt = 0;
            foreach (var a in adj)
            {
                int idx = layers[layer[a]].IndexOf(a);
                if (idx >= 0) { sum += idx; cnt++; }
            }
            return cnt > 0 ? sum / cnt : pos[n];
        }

        var ordered = layerNodes
            .Select((n, i) => (n, key: Barycentre(n), i))
            .OrderBy(t => t.key).ThenBy(t => t.i)
            .Select(t => t.n)
            .ToList();
        layerNodes.Clear();
        layerNodes.AddRange(ordered);
    }

    // ------------------------------------------------------------------- emit

    private static MDiagram Emit(List<Node> nodes, List<Rel> rels, ThemeDefinition theme)
    {
        var d = new MDiagram();

        foreach (var n in nodes)
        {
            // Header compartment.
            var header = new MShape
            {
                Kind = ShapeKind.Rect,
                X = n.X, Y = n.Y, W = n.W, H = n.HeaderH,
                Fill = theme.Background, Stroke = theme.Line,
                TextColor = theme.Primary, Bold = true, FontSize = 10,
            };
            if (n.Annotation is null)
            {
                header.Text = n.Name;
                d.Shapes.Add(header);
            }
            else
            {
                d.Shapes.Add(header);
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = n.X, Y = n.Y + 3, W = n.W, H = 14,
                    Text = n.Name, Bold = true, FontSize = 10, TextColor = theme.Heading,
                });
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = n.X, Y = n.Y + 19, W = n.W, H = 13,
                    Text = $"«{n.Annotation}»", FontSize = 8.5, TextColor = theme.Text,
                });
            }

            // Members compartment. Class boxes keep one shared theme-coloured panel with a divider
            // between the attribute and method sections. ER entities instead paint each attribute
            // ROW the way mermaid does — odd rows white, even rows light grey — so the entity reads
            // as a coloured header over a white table, not one solid theme-coloured block.
            double membersY = n.Y + n.HeaderH;

            void AddMemberLines(List<string> members, double top, string textColor)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    d.Shapes.Add(new MShape
                    {
                        Kind = ShapeKind.Text,
                        X = n.X + 6, Y = top + 2 + i * LineH,
                        W = Math.Min(members[i].Length * CharW, n.W - 12), H = LineH,
                        Text = members[i], FontSize = 9, TextColor = textColor,
                    });
                }
            }

            if (n.IsClass)
            {
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Rect,
                    X = n.X, Y = membersY, W = n.W, H = n.AttrsH + n.MethodsH,
                    Fill = theme.Background, Stroke = theme.Line,
                });
                AddMemberLines(n.Attrs, membersY, theme.Text);
                AddMemberLines(n.Methods, membersY + n.AttrsH, theme.Text);
                // Divider between the attribute and method compartments, spanning box width.
                d.Connectors.Add(new MConnector
                {
                    X1 = n.X, Y1 = membersY + n.AttrsH,
                    X2 = n.X + n.W, Y2 = membersY + n.AttrsH,
                    StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                    Stroke = theme.Border, StrokeWidth = 0.75,
                });
            }
            else
            {
                // One bordered rect per attribute row, alternating white / light grey; the attribute
                // text uses the theme's primary text colour (mermaid's primaryTextColor).
                for (int i = 0; i < n.Attrs.Count; i++)
                {
                    d.Shapes.Add(new MShape
                    {
                        Kind = ShapeKind.Rect,
                        X = n.X, Y = membersY + i * LineH, W = n.W, H = LineH,
                        Fill = i % 2 == 0 ? ErAttrOddFill : ErAttrEvenFill,
                        Stroke = theme.Line, StrokeWidth = 0.75,
                    });
                }
                AddMemberLines(n.Attrs, membersY, theme.Primary);
            }
        }

        foreach (var r in rels)
            EmitRelationship(d, r, theme);

        Normalize(d);
        return d;
    }

    private static void EmitRelationship(MDiagram d, Rel r, ThemeDefinition theme)
    {
        double x1, y1, x2, y2;
        bool elbow = false;

        if (ReferenceEquals(r.From, r.To))
        {
            // Self reference: right-side center to top center, bent.
            (x1, y1) = (r.From.X + r.From.W, r.From.CY);
            (x2, y2) = (r.From.CX, r.From.Y);
            elbow = true;
        }
        else
        {
            var a = r.From;
            var b = r.To;
            bool sameRowFar = a.Row == b.Row && Math.Abs(a.Col - b.Col) > 1;
            bool sameColFar = a.Col == b.Col && Math.Abs(a.Row - b.Row) > 1;
            if (sameRowFar)
            {
                // Route over the boxes in between.
                (x1, y1) = (a.CX, a.Y);
                (x2, y2) = (b.CX, b.Y);
                elbow = true;
            }
            else if (sameColFar)
            {
                (x1, y1) = (a.X, a.CY);
                (x2, y2) = (b.X, b.CY);
                elbow = true;
            }
            else
            {
                double dx = b.CX - a.CX, dy = b.CY - a.CY;
                if (Math.Abs(dx) >= Math.Abs(dy))
                {
                    (x1, y1) = dx >= 0 ? (a.X + a.W, a.CY) : (a.X, a.CY);
                    (x2, y2) = dx >= 0 ? (b.X, b.CY) : (b.X + b.W, b.CY);
                }
                else
                {
                    (x1, y1) = dy >= 0 ? (a.CX, a.Y + a.H) : (a.CX, a.Y);
                    (x2, y2) = dy >= 0 ? (b.CX, b.Y) : (b.CX, b.Y + b.H);
                }
                elbow = a.Row != b.Row && a.Col != b.Col;
            }
        }

        // erDiagram: cardinality is drawn as crow's-foot graphics (bars / punched circle / foot),
        // never text, and the connector stays straight so the markers sit exactly on the line.
        if (r.IsEr)
        {
            elbow = false;
            double mdx = x2 - x1, mdy = y2 - y1;
            double mlen = Math.Sqrt(mdx * mdx + mdy * mdy);
            if (mlen < 1e-6) { mdx = 1; mdy = 0; mlen = 1; }
            double ux = mdx / mlen, uy = mdy / mlen;      // unit vector From → To
            if (r.FromCard != null) DrawErCardinality(d, r.FromCard, x1, y1, ux, uy, theme);
            if (r.ToCard != null) DrawErCardinality(d, r.ToCard, x2, y2, -ux, -uy, theme);
        }

        var conn = new MConnector
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Elbow = elbow,
            StartHead = r.StartHead, EndHead = r.EndHead,
            Dashed = r.Dashed,
            Stroke = theme.Line,
        };
        if (r.Label != null)
        {
            conn.Label = r.Label;
            conn.LabelW = Math.Max(24, r.Label.Length * 5.5 + 8);
            conn.LabelH = 13;
            conn.LabelX = (x1 + x2) / 2 - conn.LabelW / 2;
            conn.LabelY = (y1 + y2) / 2 - conn.LabelH / 2 - 2;
        }
        d.Connectors.Add(conn);

        // classDiagram role names stay as text; ER cardinality was already drawn graphically above.
        if (!r.IsEr)
        {
            if (r.FromCard != null) AddEndText(d, r.FromCard, x1, y1, x2, y2, theme);
            if (r.ToCard != null) AddEndText(d, r.ToCard, x2, y2, x1, y1, theme);
        }
    }

    // Crow's-foot cardinality markers for erDiagram, mirroring mermaid's graphical notation (which
    // never emits "1" / "0..N" text): "|" → a bar across the line, "||" → two bars (the "double
    // line"), "o" → a background-filled circle punched out of the line, "{" / "}" → a foot whose two
    // side toes fan toward the entity (the relationship line itself is the middle toe).
    // (ex,ey) is the point on the entity border; (ux,uy) points away from that entity along the line.
    private static void DrawErCardinality(MDiagram d, string token, double ex, double ey,
        double ux, double uy, ThemeDefinition theme)
    {
        double px = -uy, py = ux;                       // perpendicular unit vector
        bool many = token.Contains('{') || token.Contains('}');
        bool zero = token.Contains('o');
        bool one = token.Contains('|');

        if (many)
        {
            AddCrowsFoot(d, ex, ey, ux, uy, theme);
            // Companion marker sits beyond the foot's convergence point: o{ → circle, |{ → bar.
            if (zero) AddErCircle(d, ex, ey, ux, uy, 25, theme);
            else if (one) AddErBar(d, ex, ey, ux, uy, px, py, 25, theme);
        }
        else if (zero && one)
        {
            AddErBar(d, ex, ey, ux, uy, px, py, 7, theme);      // |o / o|
            AddErCircle(d, ex, ey, ux, uy, 17, theme);
        }
        else if (one)
        {
            AddErBar(d, ex, ey, ux, uy, px, py, 7, theme);      // || — the double line
            AddErBar(d, ex, ey, ux, uy, px, py, 13, theme);
        }
        else if (zero)
        {
            AddErCircle(d, ex, ey, ux, uy, 10, theme);          // bare o
        }
    }

    // A short bar perpendicular to the line, centered at `dist` from the entity border.
    private static void AddErBar(MDiagram d, double ex, double ey, double ux, double uy,
        double px, double py, double dist, ThemeDefinition theme)
    {
        const double half = 6;
        double cx = ex + ux * dist, cy = ey + uy * dist;
        d.Connectors.Add(new MConnector
        {
            X1 = cx - px * half, Y1 = cy - py * half,
            X2 = cx + px * half, Y2 = cy + py * half,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None,
            Stroke = theme.Line, StrokeWidth = 1.5,
        });
    }

    // A small circle on the line, filled with the diagram background so it punches the line out.
    private static void AddErCircle(MDiagram d, double ex, double ey, double ux, double uy,
        double dist, ThemeDefinition theme)
    {
        const double r = 3.5;
        double cx = ex + ux * dist, cy = ey + uy * dist;
        d.Shapes.Add(new MShape
        {
            Kind = ShapeKind.Circle,
            X = cx - r, Y = cy - r, W = r * 2, H = r * 2,
            Fill = theme.Background, Stroke = theme.Line, StrokeWidth = 1.5,
        });
    }

    // The "many" foot: two side toes fanning from a convergence point on the line back toward the
    // entity border; the relationship line continues through as the middle toe.
    private static void AddCrowsFoot(MDiagram d, double ex, double ey, double ux, double uy,
        ThemeDefinition theme)
    {
        const double conv = 14;                         // convergence point distance from the border
        const double toe = 14;                          // toe length
        const double spread = 0.45;                     // radians ≈ 26°
        double cx = ex + ux * conv, cy = ey + uy * conv;
        double cos = Math.Cos(spread), sin = Math.Sin(spread);
        foreach (double sgn in new[] { 1.0, -1.0 })
        {
            // (-ux,-uy) = toward the entity, rotated by ±spread.
            double tx = -ux * cos + sgn * uy * sin;
            double ty = -uy * cos - sgn * ux * sin;
            d.Connectors.Add(new MConnector
            {
                X1 = cx, Y1 = cy,
                X2 = cx + tx * toe, Y2 = cy + ty * toe,
                StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                Stroke = theme.Line, StrokeWidth = 1.5,
            });
        }
    }

    // Small cardinality label near a connector end, nudged along + beside the line.
    private static void AddEndText(MDiagram d, string text, double x, double y,
        double towardX, double towardY, ThemeDefinition theme)
    {
        double dx = towardX - x, dy = towardY - y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
        double ux = dx / len, uy = dy / len;
        double w = Math.Max(12, text.Length * 5.0 + 4), h = 11;
        double cx = x + ux * 16 - uy * 9;
        double cy = y + uy * 16 + ux * 9;
        d.Shapes.Add(new MShape
        {
            Kind = ShapeKind.Text,
            X = cx - w / 2, Y = cy - h / 2, W = w, H = h,
            Text = text, FontSize = 8, TextColor = theme.Text,
        });
    }

    // Shift everything so the minimum coordinate sits at the 10pt margin, then size the canvas.
    private static void Normalize(MDiagram d)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Take(double x, double y) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }

        foreach (var s in d.Shapes) { Take(s.X, s.Y); Take(s.X + s.W, s.Y + s.H); }
        foreach (var c in d.Connectors)
        {
            Take(c.X1, c.Y1); Take(c.X2, c.Y2);
            if (c.Label != null) { Take(c.LabelX, c.LabelY); Take(c.LabelX + c.LabelW, c.LabelY + c.LabelH); }
        }

        double sx = Margin - minX, sy = Margin - minY;
        foreach (var s in d.Shapes) { s.X += sx; s.Y += sy; }
        foreach (var c in d.Connectors)
        {
            c.X1 += sx; c.Y1 += sy; c.X2 += sx; c.Y2 += sy;
            c.LabelX += sx; c.LabelY += sy;
        }

        d.Width = maxX + sx + Margin;
        d.Height = maxY + sy + Margin;
    }
}
