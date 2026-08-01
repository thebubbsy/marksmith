using System.Text.RegularExpressions;
using MdToPdf.Models;

namespace MdToPdf.Services.Mermaid;

// Renders mermaid `sequenceDiagram` sources into pure geometry per the MermaidModel contract:
// participant boxes (duplicated at the bottom, like mermaid), dashed lifelines, horizontal
// message rows, activation bars, notes, and alt/opt/loop/par frames. No OOXML here.
public sealed class MermaidSequenceRenderer : IMermaidRenderer
{
    private const double BoxHeight = 30;
    private const double RowHeight = 34;
    private const double FirstRowGap = 24;
    private const double Margin = 10;
    private const double MinCenterGap = 90;
    private const double NameCharWidth = 6.2;
    private const double LabelCharWidth = 5.2;
    private const double ActivationWidth = 8;
    private const double SelfLoopWidth = 30;
    private const double FramePad = 8;
    private const double FrameTitleBand = 20;

    public bool CanRender(string diagramType) =>
        string.Equals(diagramType, "sequencediagram", StringComparison.OrdinalIgnoreCase);

    public MDiagram Render(string source, ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(theme);
        var model = Parse(source);
        return Layout(model, theme);
    }

    // ---------------------------------------------------------------- parsing

    private sealed class Participant
    {
        public required string Id { get; init; }
        public string Label { get; set; } = "";
        public bool IsActor { get; set; }
    }

    private abstract record Ev;
    private sealed record MsgEv(string From, string To, string Label, bool Dashed, ArrowHead Head,
                                bool ActivateTarget, bool DeactivateSource) : Ev;
    private enum NotePos { LeftOf, RightOf, Over }
    private sealed record NoteEv(NotePos Pos, IReadOnlyList<string> Targets, string Text) : Ev;
    private sealed record ActivationEv(string Target, bool On) : Ev;
    private sealed record BlockStartEv(string Keyword, string Label) : Ev;
    private sealed record BlockElseEv(string Label) : Ev;
    private sealed record BlockEndEv : Ev;

    private sealed class ParsedModel
    {
        public List<Participant> Participants { get; } = new();
        public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
        public List<Ev> Events { get; } = new();
        public string? Title { get; set; }
    }

    private static readonly Regex ParticipantRe =
        new(@"^(participant|actor)\s+(\w+)(?:\s+as\s+(.+?))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MessageRe =
        new(@"^(\w+)\s*(-->>|->>|--x|-x|--\)|-\)|-->|->)\s*([+-]?)\s*(\w+)\s*:\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex NoteRe =
        new(@"^note\s+(left of|right of|over)\s+([\w\s,]+?)\s*:\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ActivateRe =
        new(@"^(activate|deactivate)\s+(\w+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BlockRe =
        new(@"^(alt|opt|loop|par|critical|break|rect)\b\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ElseRe =
        new(@"^(else|and)\b\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex TitleRe =
        new(@"^title\s*:?\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static ParsedModel Parse(string source)
    {
        var model = new ParsedModel();
        bool headerSeen = false;
        bool autonumber = false;
        int msgNo = 0;
        int depth = 0;

        void Ensure(string id)
        {
            if (model.Index.ContainsKey(id)) return;
            model.Index[id] = model.Participants.Count;
            model.Participants.Add(new Participant { Id = id, Label = id });
        }

        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(';').Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;

            if (!headerSeen && line.Equals("sequenceDiagram", StringComparison.OrdinalIgnoreCase))
            {
                headerSeen = true;
                continue;
            }

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase))
            {
                if (depth == 0) throw new MermaidParseException("sequenceDiagram: 'end' without an open block.");
                depth--;
                model.Events.Add(new BlockEndEv());
                continue;
            }

            if (line.StartsWith("autonumber", StringComparison.OrdinalIgnoreCase))
            {
                autonumber = !line.Contains("off", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var mTitle = TitleRe.Match(line);
            if (mTitle.Success)
            {
                model.Title = mTitle.Groups[1].Value.Trim();
                continue;
            }

            var mPart = ParticipantRe.Match(line);
            if (mPart.Success)
            {
                var id = mPart.Groups[2].Value;
                Ensure(id);
                var p = model.Participants[model.Index[id]];
                p.IsActor = mPart.Groups[1].Value.Equals("actor", StringComparison.OrdinalIgnoreCase);
                if (mPart.Groups[3].Success) p.Label = mPart.Groups[3].Value.Trim().Trim('"');
                continue;
            }

            var mAct = ActivateRe.Match(line);
            if (mAct.Success)
            {
                var id = mAct.Groups[2].Value;
                Ensure(id);
                model.Events.Add(new ActivationEv(id, mAct.Groups[1].Value.Equals("activate", StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            var mNote = NoteRe.Match(line);
            if (mNote.Success)
            {
                var pos = mNote.Groups[1].Value.ToLowerInvariant() switch
                {
                    "left of" => NotePos.LeftOf,
                    "right of" => NotePos.RightOf,
                    _ => NotePos.Over,
                };
                var targets = mNote.Groups[2].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (targets.Length == 0)
                    throw new MermaidParseException($"sequenceDiagram: note without a target: '{line}'.");
                if (pos != NotePos.Over && targets.Length != 1)
                    throw new MermaidParseException($"sequenceDiagram: 'note {mNote.Groups[1].Value}' takes exactly one participant: '{line}'.");
                foreach (var t in targets) Ensure(t);
                model.Events.Add(new NoteEv(pos, targets, mNote.Groups[3].Value.Trim()));
                continue;
            }

            var mElse = ElseRe.Match(line);
            if (mElse.Success)
            {
                if (depth == 0) throw new MermaidParseException($"sequenceDiagram: '{mElse.Groups[1].Value}' outside a block: '{line}'.");
                model.Events.Add(new BlockElseEv(mElse.Groups[2].Value.Trim()));
                continue;
            }

            var mBlock = BlockRe.Match(line);
            if (mBlock.Success)
            {
                depth++;
                var keyword = mBlock.Groups[1].Value;
                var label = keyword == "rect" ? "" : mBlock.Groups[2].Value.Trim();
                model.Events.Add(new BlockStartEv(keyword, label));
                continue;
            }

            var mMsg = MessageRe.Match(line);
            if (mMsg.Success)
            {
                var from = mMsg.Groups[1].Value;
                var to = mMsg.Groups[4].Value;
                Ensure(from);
                Ensure(to);
                var arrow = mMsg.Groups[2].Value;
                bool dashed = arrow.StartsWith("--", StringComparison.Ordinal);
                var head = arrow.EndsWith(">>", StringComparison.Ordinal) ? ArrowHead.Triangle : ArrowHead.Open;
                var suffix = mMsg.Groups[3].Value;
                var text = mMsg.Groups[5].Value.Trim();
                if (autonumber)
                {
                    msgNo++;
                    text = text.Length > 0 ? $"{msgNo}. {text}" : $"{msgNo}.";
                }
                model.Events.Add(new MsgEv(from, to, text, dashed, head,
                    ActivateTarget: suffix == "+", DeactivateSource: suffix == "-"));
                continue;
            }

            throw new MermaidParseException($"sequenceDiagram: unrecognized line: '{line}'.");
        }

        if (depth != 0) throw new MermaidParseException("sequenceDiagram: block not closed with 'end'.");
        if (model.Participants.Count == 0)
            throw new MermaidParseException("sequenceDiagram: no participants or messages found.");
        return model;
    }

    // ---------------------------------------------------------------- layout

    private sealed class FrameCtx
    {
        public required string Keyword { get; init; }
        public required string Label { get; init; }
        public double Top { get; init; }
        public double MinX = double.PositiveInfinity;
        public double MaxX = double.NegativeInfinity;
        public double MaxY = double.NegativeInfinity;
        public List<(double Y, string Label)> Dividers { get; } = new();
        public double X;
        public double W;
        public double Bottom;
    }

    private static MDiagram Layout(ParsedModel model, ThemeDefinition theme)
    {
        int n = model.Participants.Count;
        var widths = new double[n];
        for (int i = 0; i < n; i++)
            widths[i] = Math.Max(70, model.Participants[i].Label.Length * NameCharWidth + 20);

        // Center-to-center gaps between adjacent participants, widened for labels/notes.
        var need = new double[Math.Max(0, n - 1)];
        for (int k = 0; k + 1 < n; k++)
            need[k] = Math.Max(MinCenterGap, widths[k] / 2 + widths[k + 1] / 2 + 20);

        foreach (var ev in model.Events)
        {
            switch (ev)
            {
                case MsgEv msg:
                {
                    int i = model.Index[msg.From], j = model.Index[msg.To];
                    double lw = msg.Label.Length * LabelCharWidth;
                    if (i == j)
                    {
                        if (i < n - 1 && lw > 0)
                            need[i] = Math.Max(need[i], SelfLoopWidth + 18 + lw);
                    }
                    else if (lw > 0)
                    {
                        int a = Math.Min(i, j), b = Math.Max(i, j);
                        double per = (lw + 20) / (b - a);
                        for (int k = a; k < b; k++) need[k] = Math.Max(need[k], per);
                    }
                    break;
                }
                case NoteEv note:
                {
                    double nw = Math.Max(50, note.Text.Length * LabelCharWidth + 20);
                    var idxs = note.Targets.Select(t => model.Index[t]).ToArray();
                    switch (note.Pos)
                    {
                        case NotePos.RightOf when idxs[0] < n - 1:
                            need[idxs[0]] = Math.Max(need[idxs[0]], nw + 20);
                            break;
                        case NotePos.LeftOf when idxs[0] > 0:
                            need[idxs[0] - 1] = Math.Max(need[idxs[0] - 1], nw + 20);
                            break;
                        case NotePos.Over:
                        {
                            int a = idxs.Min(), b = idxs.Max();
                            if (a == b)
                            {
                                if (a > 0) need[a - 1] = Math.Max(need[a - 1], nw / 2 + 10);
                                if (a < n - 1) need[a] = Math.Max(need[a], nw / 2 + 10);
                            }
                            else
                            {
                                double per = (nw + 10) / (b - a);
                                for (int k = a; k < b; k++) need[k] = Math.Max(need[k], per);
                            }
                            break;
                        }
                    }
                    break;
                }
            }
        }

        var cx = new double[n];
        cx[0] = Margin + widths[0] / 2;
        for (int k = 1; k < n; k++) cx[k] = cx[k - 1] + need[k - 1];

        double topY = model.Title != null ? 26 : 0;
        double boxBottom = topY + BoxHeight;

        // Row-by-row event processing.
        double cursor = boxBottom + FirstRowGap;   // Y of the next row line
        double anchor = double.NaN;                // Y of the most recent row line
        double extraMaxX = 0;                      // visual extent not captured by connector endpoints
        var actStacks = new Dictionary<string, Stack<double>>(StringComparer.Ordinal);
        var frameStack = new Stack<FrameCtx>();
        var doneFrames = new List<FrameCtx>();
        var messages = new List<MConnector>();
        var noteShapes = new List<MShape>();
        var actBars = new List<MShape>();

        void Content(double x1, double x2, double yBottom)
        {
            if (frameStack.Count == 0) return;
            var f = frameStack.Peek();
            f.MinX = Math.Min(f.MinX, Math.Min(x1, x2));
            f.MaxX = Math.Max(f.MaxX, Math.Max(x1, x2));
            f.MaxY = Math.Max(f.MaxY, yBottom);
        }

        void OpenActivation(string id, double y)
        {
            if (!actStacks.TryGetValue(id, out var stack))
                actStacks[id] = stack = new Stack<double>();
            stack.Push(y);
        }

        void EmitBar(string id, double y0, double y1)
        {
            actBars.Add(new MShape
            {
                Kind = ShapeKind.Rect,
                X = cx[model.Index[id]] - ActivationWidth / 2,
                Y = y0,
                W = ActivationWidth,
                H = Math.Max(10, y1 - y0),
                Fill = theme.Secondary,
                Stroke = theme.Line,
                StrokeWidth = 1,
            });
        }

        void CloseActivation(string id, double y)
        {
            if (!actStacks.TryGetValue(id, out var stack) || stack.Count == 0)
                throw new MermaidParseException($"sequenceDiagram: deactivation of '{id}' without a matching activation.");
            EmitBar(id, stack.Pop(), y);
        }

        foreach (var ev in model.Events)
        {
            switch (ev)
            {
                case MsgEv msg:
                {
                    double y = cursor;
                    int i = model.Index[msg.From], j = model.Index[msg.To];
                    if (i == j)
                    {
                        // Self message: elbow looping out to the right and back to the lifeline.
                        var c = new MConnector
                        {
                            X1 = cx[i], Y1 = y, X2 = cx[i], Y2 = y + 16,
                            Elbow = true, Dashed = msg.Dashed, EndHead = msg.Head, Stroke = theme.Line,
                        };
                        double reach = cx[i] + SelfLoopWidth + 8;
                        if (msg.Label.Length > 0)
                        {
                            c.Label = msg.Label;
                            c.LabelW = Math.Max(24, msg.Label.Length * LabelCharWidth);
                            c.LabelH = 14;
                            c.LabelX = cx[i] + SelfLoopWidth + 8;
                            c.LabelY = y;
                            reach = c.LabelX + c.LabelW;
                        }
                        messages.Add(c);
                        extraMaxX = Math.Max(extraMaxX, reach);
                        Content(cx[i], reach, y + 16);
                    }
                    else
                    {
                        var c = new MConnector
                        {
                            X1 = cx[i], Y1 = y, X2 = cx[j], Y2 = y,
                            Dashed = msg.Dashed, EndHead = msg.Head, Stroke = theme.Line,
                        };
                        Content(cx[i], cx[j], y);
                        if (msg.Label.Length > 0)
                        {
                            c.Label = msg.Label;
                            c.LabelW = Math.Max(24, msg.Label.Length * LabelCharWidth);
                            c.LabelH = 14;
                            c.LabelX = (cx[i] + cx[j]) / 2 - c.LabelW / 2;
                            c.LabelY = y - 15;
                            Content(c.LabelX, c.LabelX + c.LabelW, y);
                        }
                        messages.Add(c);
                    }
                    if (msg.DeactivateSource) CloseActivation(msg.From, y);
                    if (msg.ActivateTarget) OpenActivation(msg.To, y);
                    anchor = y;
                    cursor += RowHeight;
                    break;
                }
                case ActivationEv act:
                {
                    double y = double.IsNaN(anchor) ? cursor : anchor;
                    if (act.On) OpenActivation(act.Target, y);
                    else CloseActivation(act.Target, y);
                    break;
                }
                case NoteEv note:
                {
                    double w = Math.Max(50, note.Text.Length * LabelCharWidth + 20);
                    double y = cursor - 12; // vertical center on the row line
                    double x;
                    if (note.Pos == NotePos.LeftOf)
                    {
                        x = cx[model.Index[note.Targets[0]]] - 10 - w;
                    }
                    else if (note.Pos == NotePos.RightOf)
                    {
                        x = cx[model.Index[note.Targets[0]]] + 10;
                    }
                    else
                    {
                        var idxs = note.Targets.Select(t => model.Index[t]).ToArray();
                        double a = cx[idxs.Min()], b = cx[idxs.Max()];
                        w = Math.Max(w, b - a + 50);
                        x = (a + b) / 2 - w / 2;
                    }
                    noteShapes.Add(new MShape
                    {
                        Kind = ShapeKind.RoundRect,
                        X = x, Y = y, W = w, H = 24,
                        Text = note.Text,
                        Fill = theme.Background, Stroke = theme.Line, TextColor = theme.Primary,
                        FontSize = 9,
                    });
                    Content(x, x + w, y + 24);
                    anchor = cursor;
                    cursor += RowHeight;
                    break;
                }
                case BlockStartEv block:
                {
                    frameStack.Push(new FrameCtx { Keyword = block.Keyword, Label = block.Label, Top = cursor - 12 });
                    cursor += FrameTitleBand;
                    break;
                }
                case BlockElseEv els:
                {
                    frameStack.Peek().Dividers.Add((cursor - 12, els.Label));
                    cursor += FrameTitleBand;
                    break;
                }
                case BlockEndEv:
                {
                    var f = frameStack.Pop();
                    if (f.MinX > f.MaxX) { f.MinX = cx[0]; f.MaxX = cx[0] + 104; } // empty block
                    if (double.IsNegativeInfinity(f.MaxY)) f.MaxY = f.Top + 22;
                    f.X = f.MinX - FramePad;
                    double right = f.MaxX + FramePad;
                    string frameTitle = FrameTitle(f);
                    double titleW = frameTitle.Length * LabelCharWidth + 12;
                    if (right - f.X < titleW) right = f.X + titleW;
                    f.W = right - f.X;
                    f.Bottom = Math.Max(f.MaxY + FramePad, f.Top + 30);
                    doneFrames.Add(f);
                    if (frameStack.Count > 0)
                    {
                        // Include the child frame (shrunk 2pt) so the parent ends up 6pt wider each side.
                        var p = frameStack.Peek();
                        p.MinX = Math.Min(p.MinX, f.X + 2);
                        p.MaxX = Math.Max(p.MaxX, f.X + f.W - 2);
                        p.MaxY = Math.Max(p.MaxY, f.Bottom - 2);
                    }
                    break;
                }
            }
        }

        // Bottom of the body: everything drawn so far.
        double contentBottom = boxBottom + 10;
        foreach (var c in messages) contentBottom = Math.Max(contentBottom, Math.Max(c.Y1, c.Y2));
        foreach (var s in noteShapes) contentBottom = Math.Max(contentBottom, s.Y + s.H);
        foreach (var s in actBars) contentBottom = Math.Max(contentBottom, s.Y + s.H);
        foreach (var f in doneFrames) contentBottom = Math.Max(contentBottom, f.Bottom);
        double bottomBoxTop = contentBottom + 20;

        // Close activations that were never explicitly deactivated.
        foreach (var (id, stack) in actStacks)
            while (stack.Count > 0)
                EmitBar(id, stack.Pop(), bottomBoxTop - 4);

        var diagram = new MDiagram();

        if (model.Title != null)
        {
            double w = model.Title.Length * 6.5 + 10;
            diagram.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Text,
                X = (cx[0] + cx[n - 1]) / 2 - w / 2, Y = 2, W = w, H = 16,
                Text = model.Title, Bold = true, FontSize = 12, TextColor = theme.Heading,
            });
        }

        // Frames behind everything else.
        foreach (var f in doneFrames)
        {
            diagram.Shapes.Add(new MShape
            {
                Kind = ShapeKind.Frame,
                X = f.X, Y = f.Top, W = f.W, H = f.Bottom - f.Top,
                Stroke = theme.Border, StrokeWidth = 1.25,
            });
            string frameTitle = FrameTitle(f);
            if (frameTitle.Length > 0)
            {
                diagram.Shapes.Add(new MShape
                {
                    Kind = ShapeKind.Text,
                    X = f.X + 4, Y = f.Top + 3, W = frameTitle.Length * LabelCharWidth + 8, H = 12,
                    Text = frameTitle, Bold = true, FontSize = 9, TextColor = theme.Heading,
                });
            }
            foreach (var (dy, label) in f.Dividers)
            {
                diagram.Connectors.Add(new MConnector
                {
                    X1 = f.X, Y1 = dy, X2 = f.X + f.W, Y2 = dy,
                    Dashed = true, StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                    Stroke = theme.Border, StrokeWidth = 1,
                });
                if (label.Length > 0)
                {
                    diagram.Shapes.Add(new MShape
                    {
                        Kind = ShapeKind.Text,
                        X = f.X + 4, Y = dy + 3, W = label.Length * LabelCharWidth + 20, H = 12,
                        Text = $"[{label}]", Bold = true, FontSize = 9, TextColor = theme.Heading,
                    });
                }
            }
        }

        // Participant boxes (top + bottom) and lifelines.
        for (int i = 0; i < n; i++)
        {
            var p = model.Participants[i];
            foreach (var y in new[] { topY, bottomBoxTop })
            {
                diagram.Shapes.Add(new MShape
                {
                    Kind = p.IsActor ? ShapeKind.Ellipse : ShapeKind.RoundRect,
                    X = cx[i] - widths[i] / 2, Y = y, W = widths[i], H = BoxHeight,
                    Text = p.Label,
                    Fill = theme.Background, Stroke = theme.Line, TextColor = theme.Primary,
                    FontSize = 10,
                });
            }
            diagram.Connectors.Add(new MConnector
            {
                X1 = cx[i], Y1 = boxBottom, X2 = cx[i], Y2 = bottomBoxTop,
                Dashed = true, StartHead = ArrowHead.None, EndHead = ArrowHead.None,
                Stroke = theme.Line, StrokeWidth = 1,
            });
        }

        foreach (var s in actBars) diagram.Shapes.Add(s);
        foreach (var s in noteShapes) diagram.Shapes.Add(s);
        foreach (var c in messages) diagram.Connectors.Add(c);

        // Final bounds: shift so nothing is left of/above the margin, then size the canvas.
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = extraMaxX, maxY = 0;
        foreach (var s in diagram.Shapes)
        {
            minX = Math.Min(minX, s.X); minY = Math.Min(minY, s.Y);
            maxX = Math.Max(maxX, s.X + s.W); maxY = Math.Max(maxY, s.Y + s.H);
        }
        foreach (var c in diagram.Connectors)
        {
            minX = Math.Min(minX, Math.Min(c.X1, c.X2)); minY = Math.Min(minY, Math.Min(c.Y1, c.Y2));
            maxX = Math.Max(maxX, Math.Max(c.X1, c.X2)); maxY = Math.Max(maxY, Math.Max(c.Y1, c.Y2));
            if (c.Label != null)
            {
                minX = Math.Min(minX, c.LabelX); minY = Math.Min(minY, c.LabelY);
                maxX = Math.Max(maxX, c.LabelX + c.LabelW); maxY = Math.Max(maxY, c.LabelY + c.LabelH);
            }
        }

        double dx = Margin - minX, dy2 = Margin - minY;
        foreach (var s in diagram.Shapes) { s.X += dx; s.Y += dy2; }
        foreach (var c in diagram.Connectors)
        {
            c.X1 += dx; c.X2 += dx; c.Y1 += dy2; c.Y2 += dy2;
            if (c.Label != null) { c.LabelX += dx; c.LabelY += dy2; }
        }
        diagram.Width = maxX + dx + Margin;
        diagram.Height = maxY + dy2 + Margin;
        return diagram;
    }

    private static string FrameTitle(FrameCtx f) =>
        f.Keyword == "rect" ? "" : f.Label.Length > 0 ? $"{f.Keyword}: {f.Label}" : f.Keyword;
}
