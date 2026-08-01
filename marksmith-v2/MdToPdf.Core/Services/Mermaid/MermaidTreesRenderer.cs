using System.Globalization;
using System.Text.RegularExpressions;
using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

// Renders the "tree/track" mermaid family — mindmap, timeline, journey, gitGraph — into pure
// geometry (MDiagram). No OOXML here; the DocxShapeEmitter turns the result into Word shapes.
public sealed class MermaidTreesRenderer : IMermaidRenderer
{
    public bool CanRender(string diagramType)
    {
        var t = diagramType.Trim().TrimEnd(':');
        return t is "mindmap" or "timeline" or "journey" or "gitgraph";
    }

    public MDiagram Render(string source, ThemeDefinition theme)
    {
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => !l.TrimStart().StartsWith("%%", StringComparison.Ordinal))
            .ToList();
        int hdr = lines.FindIndex(l => l.Trim().Length > 0);
        if (hdr < 0) throw new MermaidParseException("Empty mermaid source.");
        string kind = lines[hdr].Trim().Split(' ', '\t')[0].TrimEnd(':').ToLowerInvariant();
        var body = lines.Skip(hdr + 1).ToList();
        var diagram = kind switch
        {
            "mindmap" => RenderMindmap(body, theme),
            "timeline" => RenderTimeline(body, theme),
            "journey" => RenderJourney(body, theme),
            "gitgraph" => RenderGitGraph(body, theme),
            _ => throw new MermaidParseException($"MermaidTreesRenderer cannot render '{kind}'.")
        };
        Finalize(diagram);
        return diagram;
    }

    // ------------------------------------------------------------------ shared helpers

    private const double CharW = 6.2;

    private static double TextWidth(string text, double pad = 20, double min = 60)
    {
        double w = 0;
        foreach (var line in text.Split('\n')) w = Math.Max(w, line.Length * CharW);
        return Math.Max(min, w + pad);
    }

    private static string Wrap(string text, int maxChars)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var cur = "";
        foreach (var w in words)
        {
            if (cur.Length == 0) cur = w;
            else if (cur.Length + 1 + w.Length <= maxChars) cur += " " + w;
            else { lines.Add(cur); cur = w; }
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines.Count == 0 ? text : string.Join("\n", lines);
    }

    private static int LineCount(string s) => s.Count(c => c == '\n') + 1;

    private static bool TryParseHex(string? hex, out double r, out double g, out double b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var h = hex.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6 || !int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return false;
        r = ((v >> 16) & 0xFF) / 255.0;
        g = ((v >> 8) & 0xFF) / 255.0;
        b = (v & 0xFF) / 255.0;
        return true;
    }

    private static bool IsDark(string? hex)
        => TryParseHex(hex, out var r, out var g, out var b) && 0.299 * r + 0.587 * g + 0.114 * b < 0.5;

    private static (double H, double S, double L) RgbToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        double d = max - min;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static string HslToHex(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = ((int)(h / 60)) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static int B(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{B(r + m):X2}{B(g + m):X2}{B(b + m):X2}";
    }

    private static (double H, double S, double L) BaseAccent(ThemeDefinition theme)
    {
        if (TryParseHex(theme.Heading, out var r, out var g, out var b)) return RgbToHsl(r, g, b);
        if (TryParseHex(theme.Primary, out r, out g, out b)) return RgbToHsl(r, g, b);
        TryParseHex("#4472C4", out r, out g, out b);
        return RgbToHsl(r, g, b);
    }

    // Strong branch color i — hue rotated away from the theme accent.
    private static string BranchColor(ThemeDefinition theme, int i)
    {
        var (h, s, l) = BaseAccent(theme);
        return HslToHex(h + i * 47, Math.Max(s, 0.40), Math.Clamp(l, 0.32, 0.55));
    }

    // Pale fill matching branch color i (readable behind theme.Text).
    private static string FillColor(ThemeDefinition theme, int i)
    {
        var (h, s, _) = BaseAccent(theme);
        return HslToHex(h + i * 47, Math.Max(s * 0.6, 0.25), 0.86);
    }

    // Shift/scale everything so the whole drawing sits inside [0..Width]x[0..Height] with a margin.
    private static void Finalize(MDiagram d, double margin = 12)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Pt(double x, double y)
        {
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        foreach (var s in d.Shapes) { Pt(s.X, s.Y); Pt(s.X + s.W, s.Y + s.H); }
        foreach (var c in d.Connectors)
        {
            Pt(c.X1, c.Y1); Pt(c.X2, c.Y2);
            if (c.Label != null) { Pt(c.LabelX, c.LabelY); Pt(c.LabelX + c.LabelW, c.LabelY + c.LabelH); }
        }
        if (minX > maxX) { d.Width = 10; d.Height = 10; return; }
        double dx = margin - minX, dy = margin - minY;
        foreach (var s in d.Shapes) { s.X += dx; s.Y += dy; }
        foreach (var c in d.Connectors)
        {
            c.X1 += dx; c.Y1 += dy; c.X2 += dx; c.Y2 += dy;
            c.LabelX += dx; c.LabelY += dy;
        }
        d.Width = maxX + dx + margin;
        d.Height = maxY + dy + margin;
    }

    // ================================================================== MINDMAP

    private sealed class MindNode
    {
        public string Text = "";
        public ShapeKind Kind = ShapeKind.RoundRect;
        public List<MindNode> Children { get; } = new();
        public int Depth;
        public int Branch = -1;      // index of level-1 ancestor (palette slot)
        public double W, Y, SubH;
    }

    private static MDiagram RenderMindmap(List<string> body, ThemeDefinition theme)
    {
        const double NodeH = 26, VGap = 8, LevelGap = 70;

        MindNode? root = null;
        var stack = new List<(int Indent, MindNode Node)>();
        foreach (var raw in body)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string expanded = raw.Replace("\t", "    ");
            int indent = expanded.Length - expanded.TrimStart().Length;
            string text = Regex.Replace(expanded.Trim(), @"::icon\([^)]*\)", "").Trim();
            if (text.Length == 0) continue;
            var (label, kind) = ParseMindNodeText(text);
            if (label.Length == 0) continue;
            var node = new MindNode { Text = label, Kind = kind };
            if (root == null) { root = node; stack.Add((indent, node)); continue; }
            while (stack.Count > 1 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            stack[^1].Node.Children.Add(node);
            stack.Add((indent, node));
        }
        if (root == null) throw new MermaidParseException("Mindmap has no nodes.");

        // depth, width, branch color slot
        var maxW = new Dictionary<int, double>();
        int maxDepth = 0;
        void Measure(MindNode n, int depth, int branch)
        {
            n.Depth = depth;
            n.Branch = branch;
            n.W = TextWidth(n.Text);
            maxDepth = Math.Max(maxDepth, depth);
            maxW[depth] = Math.Max(maxW.GetValueOrDefault(depth), n.W);
            for (int i = 0; i < n.Children.Count; i++)
                Measure(n.Children[i], depth + 1, depth == 0 ? i : branch);
        }
        Measure(root, 0, -1);

        double SubH(MindNode n)
        {
            if (n.Children.Count == 0) return n.SubH = NodeH;
            double sum = 0;
            foreach (var c in n.Children) sum += SubH(c);
            sum += VGap * (n.Children.Count - 1);
            return n.SubH = Math.Max(NodeH, sum);
        }
        SubH(root);

        var xAt = new double[maxDepth + 1];
        for (int d2 = 1; d2 <= maxDepth; d2++) xAt[d2] = xAt[d2 - 1] + maxW[d2 - 1] + LevelGap;

        void Place(MindNode n, double top)
        {
            if (n.Children.Count == 0) { n.Y = top + (n.SubH - NodeH) / 2; return; }
            double childrenH = n.Children.Sum(c => c.SubH) + VGap * (n.Children.Count - 1);
            double cur = top + Math.Max(0, (n.SubH - childrenH) / 2);
            foreach (var c in n.Children) { Place(c, cur); cur += c.SubH + VGap; }
            double c1 = n.Children[0].Y + NodeH / 2, c2 = n.Children[^1].Y + NodeH / 2;
            n.Y = (c1 + c2) / 2 - NodeH / 2;
        }
        Place(root, 0);

        var d = new MDiagram();
        string rootText = IsDark(theme.Heading)
            ? (IsDark(theme.Background) ? "#FFFFFF" : theme.Background)
            : theme.Text;

        void Emit(MindNode n)
        {
            string fill, stroke, textColor;
            if (n.Depth == 0) { fill = theme.Heading; stroke = theme.Heading; textColor = rootText; }
            else if (n.Depth == 1) { fill = FillColor(theme, n.Branch); stroke = BranchColor(theme, n.Branch); textColor = theme.Text; }
            else { fill = theme.Secondary; stroke = theme.Border; textColor = IsDark(theme.Secondary) ? "#FFFFFF" : theme.Text; }
            d.Shapes.Add(new MShape
            {
                Kind = n.Kind, X = xAt[n.Depth], Y = n.Y, W = n.W, H = NodeH,
                Text = n.Text, Fill = fill, Stroke = stroke, TextColor = textColor,
                FontSize = n.Depth == 0 ? 11 : 10, Bold = n.Depth == 0,
            });
            foreach (var c in n.Children)
            {
                d.Connectors.Add(new MConnector
                {
                    X1 = xAt[n.Depth] + n.W, Y1 = n.Y + NodeH / 2,
                    X2 = xAt[c.Depth], Y2 = c.Y + NodeH / 2,
                    Elbow = true, StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                    Stroke = BranchColor(theme, Math.Max(c.Branch, 0)), StrokeWidth = 1.5,
                });
                Emit(c);
            }
        }
        Emit(root);
        return d;
    }

    private static (string Label, ShapeKind Kind) ParseMindNodeText(string text)
    {
        static string Clean(string s) => s.Trim().Trim('"', '`').Trim();
        Match m;
        if ((m = Regex.Match(text, @"^[\w\-]*\)\)(.*)\(\(\s*$")).Success)
            return (Clean(m.Groups[1].Value), ShapeKind.Ellipse);           // ))bang((
        if ((m = Regex.Match(text, @"^[\w\-]*\(\((.*)\)\)\s*$")).Success)
            return (Clean(m.Groups[1].Value), ShapeKind.Ellipse);           // ((circle)) incl. root((x))
        if ((m = Regex.Match(text, @"^[\w\-]*\((.*)\)\s*$")).Success)
            return (Clean(m.Groups[1].Value), ShapeKind.RoundRect);         // (rounded)
        if ((m = Regex.Match(text, @"^[\w\-]*\[(.*)\]\s*$")).Success)
            return (Clean(m.Groups[1].Value), ShapeKind.Rect);              // [square]
        return (Clean(text), ShapeKind.RoundRect);
    }

    // ================================================================== TIMELINE

    private sealed class TimePeriod
    {
        public string Label = "";
        public List<string> Events { get; } = new();
        public int Section = -1;
    }

    private static MDiagram RenderTimeline(List<string> body, ThemeDefinition theme)
    {
        string? title = null;
        var periods = new List<TimePeriod>();
        var sections = new List<(string Name, int Start)>();

        foreach (var raw in body)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("title ", StringComparison.OrdinalIgnoreCase)) { title = t[6..].Trim(); continue; }
            if (t.StartsWith("section ", StringComparison.OrdinalIgnoreCase))
            { sections.Add((t[8..].Trim(), periods.Count)); continue; }
            if (t.StartsWith(':'))
            {
                if (periods.Count == 0) throw new MermaidParseException("Timeline event continuation before any period.");
                periods[^1].Events.AddRange(t.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                continue;
            }
            var parts = t.Split(':', StringSplitOptions.TrimEntries);
            var p = new TimePeriod { Label = parts[0], Section = sections.Count - 1 };
            p.Events.AddRange(parts.Skip(1).Where(e => e.Length > 0));
            periods.Add(p);
        }
        if (periods.Count == 0) throw new MermaidParseException("Timeline has no periods.");

        double maxLabelW = periods.Max(p => TextWidth(p.Label, 12, 40));
        double step = Math.Max(110, maxLabelW + 16);
        int wrapChars = Math.Max(8, (int)((step - 26) / CharW));

        const double Margin = 16, EvGap = 5;
        var d = new MDiagram();

        double y = Margin;
        if (title != null)
        {
            double tw = TextWidth(title, 12, 60);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = Margin + periods.Count * step / 2 - tw / 2, Y = y, W = tw, H = 18,
                Text = title, TextColor = theme.Heading, FontSize = 13, Bold = true,
            });
            y += 26;
        }
        double sectionY = y;
        if (sections.Count > 0) y += 24;

        // wrap events, measure stack heights
        var evBoxes = new List<(int Period, string Text, double W, double H)>();
        double maxStack = 0;
        for (int i = 0; i < periods.Count; i++)
        {
            double stackH = 0;
            foreach (var ev in periods[i].Events)
            {
                string wrapped = Wrap(ev, wrapChars);
                double h = 8 + LineCount(wrapped) * 12;
                double w = Math.Min(TextWidth(wrapped, 14, 46), step - 10);
                evBoxes.Add((i, wrapped, w, h));
                stackH += h + EvGap;
            }
            maxStack = Math.Max(maxStack, stackH);
        }
        double axisY = y + maxStack + 14;

        double StationX(int i) => Margin + step / 2 + i * step;

        // axis line
        d.Connectors.Add(new MConnector
        {
            X1 = Margin, Y1 = axisY, X2 = Margin + periods.Count * step, Y2 = axisY,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1.75,
        });

        // section bands
        for (int s = 0; s < sections.Count; s++)
        {
            int start = sections[s].Start;
            int end = (s + 1 < sections.Count ? sections[s + 1].Start : periods.Count) - 1;
            if (end < start) continue;
            double cx = (StationX(start) + StationX(end)) / 2;
            double w = TextWidth(sections[s].Name, 12, 50);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = cx - w / 2, Y = sectionY, W = w, H = 16,
                Text = sections[s].Name, TextColor = BranchColor(theme, s), FontSize = 11, Bold = true,
            });
        }

        // period labels + station stems + event boxes
        var stackCursor = new double[periods.Count];
        for (int i = 0; i < periods.Count; i++) stackCursor[i] = axisY - 14;
        foreach (var (pi, text, w, h) in evBoxes)
        {
            int colorIdx = periods[pi].Section >= 0 ? periods[pi].Section : pi;
            double top = stackCursor[pi] - h;
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.RoundRect, X = StationX(pi) - w / 2, Y = top, W = w, H = h,
                Text = text, Fill = FillColor(theme, colorIdx), Stroke = BranchColor(theme, colorIdx),
                TextColor = theme.Text, FontSize = 9,
            });
            stackCursor[pi] = top - EvGap;
        }
        for (int i = 0; i < periods.Count; i++)
        {
            double x = StationX(i);
            if (periods[i].Events.Count > 0)
                d.Connectors.Add(new MConnector
                {
                    X1 = x, Y1 = axisY, X2 = x, Y2 = axisY - 14,
                    StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                    Stroke = theme.Line, StrokeWidth = 0.75,
                });
            double lw = TextWidth(periods[i].Label, 12, 40);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = x - lw / 2, Y = axisY + 8, W = lw, H = 16,
                Text = periods[i].Label, TextColor = theme.Text, FontSize = 10, Bold = true,
            });
        }
        return d;
    }

    // ================================================================== JOURNEY

    private sealed class JourneyTask
    {
        public string Name = "";
        public int Score = 3;
        public List<string> Actors { get; } = new();
        public int Section = -1;
    }

    private static MDiagram RenderJourney(List<string> body, ThemeDefinition theme)
    {
        string? title = null;
        var tasks = new List<JourneyTask>();
        var sections = new List<(string Name, int Start)>();

        foreach (var raw in body)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("title ", StringComparison.OrdinalIgnoreCase)) { title = t[6..].Trim(); continue; }
            if (t.StartsWith("section ", StringComparison.OrdinalIgnoreCase))
            { sections.Add((t[8..].Trim(), tasks.Count)); continue; }
            var parts = t.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            var task = new JourneyTask { Name = parts[0], Section = sections.Count - 1 };
            if (int.TryParse(parts[1], out int score)) task.Score = Math.Clamp(score, 1, 7);
            if (parts.Length > 2)
                task.Actors.AddRange(parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            tasks.Add(task);
        }
        if (tasks.Count == 0) throw new MermaidParseException("Journey has no tasks.");

        const double Margin = 16, Step = 105, R = 8; // circle 16pt
        var d = new MDiagram();

        double y = Margin;
        if (title != null)
        {
            double tw = TextWidth(title, 12, 60);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = Margin + tasks.Count * Step / 2 - tw / 2, Y = y, W = tw, H = 18,
                Text = title, TextColor = theme.Heading, FontSize = 13, Bold = true,
            });
            y += 26;
        }
        double sectionY = y;
        if (sections.Count > 0) y += 24;
        double actorY = y;
        double cy = actorY + 16 + R + 4;   // circle center
        double nameY = cy + R + 4;

        double StationX(int i) => Margin + Step / 2 + i * Step;

        for (int s = 0; s < sections.Count; s++)
        {
            int start = sections[s].Start;
            int end = (s + 1 < sections.Count ? sections[s + 1].Start : tasks.Count) - 1;
            if (end < start) continue;
            double cx = (StationX(start) + StationX(end)) / 2;
            double w = TextWidth(sections[s].Name, 12, 50);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = cx - w / 2, Y = sectionY, W = w, H = 16,
                Text = sections[s].Name, TextColor = BranchColor(theme, s), FontSize = 11, Bold = true,
            });
        }

        // baseline through all circle centers
        d.Connectors.Add(new MConnector
        {
            X1 = StationX(0), Y1 = cy, X2 = StationX(tasks.Count - 1), Y2 = cy,
            StartHead = ArrowHead.None, EndHead = ArrowHead.None, Stroke = theme.Line, StrokeWidth = 1.5,
        });

        static string Initials(string actor)
            => string.Concat(actor.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(w => char.ToUpperInvariant(w[0])));

        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            double x = StationX(i);
            string scoreColor = task.Score <= 2 ? "#D9534F" : task.Score <= 4 ? "#F0AD4E" : "#5CB85C";

            if (task.Actors.Count > 0)
            {
                string ini = string.Join(",", task.Actors.Select(Initials));
                double aw = TextWidth(ini, 8, 24);
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = x - aw / 2, Y = actorY, W = aw, H = 14,
                    Text = ini, TextColor = theme.Secondary, FontSize = 8,
                });
            }
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Circle, X = x - R, Y = cy - R, W = 2 * R, H = 2 * R,
                Text = task.Score.ToString(CultureInfo.InvariantCulture),
                Fill = scoreColor, Stroke = scoreColor, TextColor = "#FFFFFF", FontSize = 8, Bold = true,
            });
            string wrapped = Wrap(task.Name, 14);
            double nw = Math.Min(TextWidth(wrapped, 8, 40), Step - 8);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = x - nw / 2, Y = nameY, W = nw, H = 4 + LineCount(wrapped) * 12,
                Text = wrapped, TextColor = theme.Text, FontSize = 9,
            });
        }
        return d;
    }

    // ================================================================== GITGRAPH

    private sealed class GitCommit
    {
        public string? Id;
        public string? Tag;
        public int Lane;
        public int Seq;
        public bool IsMerge;
        public string Type = "NORMAL";
    }

    private static MDiagram RenderGitGraph(List<string> body, ThemeDefinition theme)
    {
        var lanes = new List<string> { "main" };
        var heads = new Dictionary<string, GitCommit?> { ["main"] = null };
        var pendingBranchFrom = new Dictionary<string, GitCommit?>();
        var commits = new List<GitCommit>();
        var laneLinks = new List<(GitCommit From, GitCommit To, bool Elbow)>();
        string current = "main";
        int seq = 0;

        int LaneOf(string name)
        {
            int idx = lanes.IndexOf(name);
            if (idx < 0) { lanes.Add(name); heads[name] = null; idx = lanes.Count - 1; }
            return idx;
        }

        GitCommit AddCommit(string? id, string? tag, string type, bool isMerge)
        {
            var c = new GitCommit { Id = id, Tag = tag, Lane = LaneOf(current), Seq = seq++, IsMerge = isMerge, Type = type };
            commits.Add(c);
            var prev = heads[current];
            if (prev != null) laneLinks.Add((prev, c, false));
            else if (pendingBranchFrom.TryGetValue(current, out var parent) && parent != null)
                laneLinks.Add((parent, c, true));       // branch fork connector
            heads[current] = c;
            return c;
        }

        foreach (var raw in body)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            string word = t.Split(' ', '\t')[0].ToLowerInvariant();
            string? id = Regex.Match(t, "id\\s*:\\s*\"([^\"]*)\"") is { Success: true } mi ? mi.Groups[1].Value : null;
            string? tag = Regex.Match(t, "tag\\s*:\\s*\"([^\"]*)\"") is { Success: true } mt ? mt.Groups[1].Value : null;
            string type = Regex.Match(t, @"type\s*:\s*(\w+)") is { Success: true } my ? my.Groups[1].Value.ToUpperInvariant() : "NORMAL";

            switch (word)
            {
                case "commit":
                case "cherry-pick":
                    AddCommit(id, tag, type, isMerge: false);
                    break;
                case "branch":
                {
                    string name = t.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)
                                   .Skip(1).FirstOrDefault()?.Trim('"') ?? throw new MermaidParseException("gitGraph: branch without a name.");
                    LaneOf(name);
                    pendingBranchFrom[name] = heads[current];
                    current = name;                      // mermaid: branch also checks out
                    break;
                }
                case "checkout":
                case "switch":
                {
                    string name = t.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)
                                   .Skip(1).FirstOrDefault()?.Trim('"') ?? throw new MermaidParseException("gitGraph: checkout without a name.");
                    LaneOf(name);
                    current = name;
                    break;
                }
                case "merge":
                {
                    string name = t.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries)
                                   .Skip(1).FirstOrDefault()?.Trim('"') ?? throw new MermaidParseException("gitGraph: merge without a name.");
                    var other = heads.GetValueOrDefault(name);
                    var mc = AddCommit(id, tag, type, isMerge: true);
                    if (other != null) laneLinks.Add((other, mc, true));    // merge connector
                    break;
                }
                // silently ignore unknown/config lines (options, accDescr, etc.)
            }
        }
        if (commits.Count == 0) throw new MermaidParseException("gitGraph has no commits.");

        const double Margin = 16, LaneGap = 44, XStep = 55;
        double labelW = lanes.Max(n => TextWidth(n, 10, 36));
        double x0 = Margin + labelW + 12;
        double LaneY(int lane) => Margin + 18 + lane * LaneGap;
        double CommitX(int s) => x0 + s * XStep;
        double Radius(GitCommit c) => c.IsMerge ? 7 : 6;

        var d = new MDiagram();

        // lane labels
        for (int i = 0; i < lanes.Count; i++)
        {
            double w = TextWidth(lanes[i], 10, 36);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text, X = Margin, Y = LaneY(i) - 7, W = w, H = 14,
                Text = lanes[i], TextColor = BranchColor(theme, i), FontSize = 9, Bold = true,
            });
        }

        // connectors first (under circles)
        foreach (var (from, to, elbow) in laneLinks)
        {
            d.Connectors.Add(new MConnector
            {
                X1 = CommitX(from.Seq), Y1 = LaneY(from.Lane),
                X2 = CommitX(to.Seq), Y2 = LaneY(to.Lane),
                Elbow = elbow, StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                Stroke = BranchColor(theme, to.Lane), StrokeWidth = 1.5,
            });
        }

        foreach (var c in commits)
        {
            double r = Radius(c), cx = CommitX(c.Seq), cy = LaneY(c.Lane);
            string color = BranchColor(theme, c.Lane);
            d.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Circle, X = cx - r, Y = cy - r, W = 2 * r, H = 2 * r,
                Fill = c.Type == "REVERSE" ? theme.Background : color,
                Stroke = c.Type == "HIGHLIGHT" ? theme.Text : color,
                StrokeWidth = c.Type == "HIGHLIGHT" ? 2.25 : 1.25,
                Dashed = c.Type == "REVERSE",
            });
            if (c.Id != null)
            {
                double w = TextWidth(c.Id, 4, 16);
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = cx - w / 2, Y = cy + r + 2, W = w, H = 11,
                    Text = c.Id, TextColor = theme.Secondary, FontSize = 7,
                });
            }
            if (c.Tag != null)
            {
                double w = TextWidth(c.Tag, 4, 16);
                d.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text, X = cx - w / 2, Y = cy - r - 14, W = w, H = 11,
                    Text = c.Tag, TextColor = theme.Heading, FontSize = 7, Bold = true,
                });
            }
        }
        return d;
    }
}
