using MarkSmith.Mermaid.Ast;

namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>A participant as the sequence layout sees it: its header box on the canvas.</summary>
public readonly record struct SequenceParticipantBox(string Id, double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double Bottom => Y + Height;
}

/// <summary>A message in diagram order.</summary>
public readonly record struct SequenceMessageSpec(string FromId, string ToId, string? Label);

/// <summary>Where one message is drawn: its polyline, the centre of its label (which sits just
/// above the line, as Mermaid draws it, instead of on top of it), and whether it is a self-call.</summary>
public sealed record SequenceMessageRoute(IReadOnlyList<Point> Points, double LabelX, double LabelY, bool IsSelf)
{
    /// <summary>The autonumber shown at the message's start, when numbering is on.</summary>
    public int? Number { get; init; }
}

/// <summary>The whole drawing: one route per message (null when an end is missing), how far
/// each participant's lifeline runs below its header, and the notes, block frames and activation
/// bars drawn around the messages.</summary>
public sealed record SequenceDrawing(IReadOnlyList<SequenceMessageRoute?> Routes, IReadOnlyDictionary<string, double> LifelineLengths)
{
    public IReadOnlyList<SequenceNoteBox> Notes { get; init; } = Array.Empty<SequenceNoteBox>();
    /// <summary>Outermost first, so drawing in order puts nested frames on top.</summary>
    public IReadOnlyList<SequenceFrame> Frames { get; init; } = Array.Empty<SequenceFrame>();
    public IReadOnlyList<SequenceActivationBar> Activations { get; init; } = Array.Empty<SequenceActivationBar>();
}

/// <summary>A note: its box and text.</summary>
public sealed record SequenceNoteBox(double X, double Y, double Width, double Height, string Text)
{
    /// <summary>The text as drawn: Mermaid &lt;br/&gt; tags become line breaks.</summary>
    public string DisplayText => global::MarkSmith.Mermaid.Generator.MermaidCodeGenerator.FromBreakTags(Text);
}

/// <summary>A dashed divider across a frame (alt's else, par's and, critical's option).</summary>
public sealed record SequenceFrameDivider(double X, double Y, double Width, string Text)
{
    public double Right => X + Width;
    public string Caption => string.IsNullOrWhiteSpace(Text) ? string.Empty : $"[{Text}]";
}

/// <summary>A loop/alt/opt/par/critical/break block drawn as a labelled frame, or a rect block
/// drawn as a tinted band (<see cref="IsHighlight"/>, no label).</summary>
public sealed record SequenceFrame(double X, double Y, double Width, double Height, string Keyword, string Header,
    IReadOnlyList<SequenceFrameDivider> Dividers, int Depth, bool IsHighlight)
{
    public string Caption => string.IsNullOrWhiteSpace(Header) ? string.Empty : $"[{Header}]";
}

/// <summary>An activation bar on a lifeline; stacked activations step right by
/// <see cref="SequenceLayout.ActivationStep"/>.</summary>
public sealed record SequenceActivationBar(string ParticipantId, double X, double Y, double Width, double Height);

/// <summary>
/// Draws a sequence diagram the way Mermaid does: participants across the top, a dashed lifeline
/// under each, and every message on its own row in order, running straight from the sender's
/// lifeline to the receiver's. The Diagram Studio used to treat messages as box-to-box connectors,
/// so A→B and B→A landed on the same line and the order of the conversation was lost.
/// </summary>
public static class SequenceLayout
{
    /// <summary>Gap from the lowest header to the first message row.</summary>
    public const double FirstRowGap = 44;
    /// <summary>Row step for a one-line message.</summary>
    public const double RowStep = 46;
    /// <summary>Extra row height for each additional label line.</summary>
    public const double LabelLineHeight = 15;
    /// <summary>How far a self-call loops out to the right, and how tall the loop is.</summary>
    public const double SelfLoopWidth = 40, SelfLoopHeight = 22;
    /// <summary>Lifeline run-out below the last message.</summary>
    public const double Tail = 36;

    /// <summary>Height of a frame's header band (keyword tab and condition).</summary>
    public const double FrameHeader = 34;
    /// <summary>Frame padding around what it contains, per side.</summary>
    public const double FramePad = 22;
    /// <summary>Gap between a note and the lifeline it sits beside.</summary>
    public const double NoteGap = 10;
    /// <summary>Activation bar width, and how far each stacked activation steps right.</summary>
    public const double ActivationWidth = 10, ActivationStep = 5;

    public static SequenceDrawing Layout(IReadOnlyList<SequenceParticipantBox> participants, IReadOnlyList<SequenceMessageSpec> messages) =>
        Layout(participants, messages.Select(m => SequenceStatement.ForMessage(new SequenceMessage { FromId = m.FromId, ToId = m.ToId, Text = m.Label ?? string.Empty })).ToList());

    /// <summary>
    /// Lays out a whole sequence script in order: every message on its row, notes on rows of their
    /// own, a frame around each block (with a header band, and a dashed divider per else/and/option),
    /// and activation bars from <c>activate</c>/<c>+</c> to <c>deactivate</c>/<c>-</c>. Mermaid's
    /// <c>-</c> shorthand (<c>B-->>-A</c>) ends the sender's activation.
    /// </summary>
    public static SequenceDrawing Layout(IReadOnlyList<SequenceParticipantBox> participants, IReadOnlyList<SequenceStatement> script, bool autoNumber = false)
    {
        var routes = new List<SequenceMessageRoute?>();
        var lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (participants.Count == 0)
        {
            foreach (var st in script) if (st.Kind == SequenceStatementKind.Message) routes.Add(null);
            return new SequenceDrawing(routes, lengths);
        }

        var byId = new Dictionary<string, SequenceParticipantBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in participants) byId.TryAdd(p.Id, p);

        var notes = new List<SequenceNoteBox>();
        var frames = new List<(SequenceFrame Frame, int Order)>();
        var bars = new List<SequenceActivationBar>();
        var openFrames = new Stack<OpenFrame>();
        var openBars = new Dictionary<string, Stack<double>>(StringComparer.OrdinalIgnoreCase);
        double minX = participants.Min(p => p.X), maxX = participants.Max(p => p.X + p.Width);

        double headerBottom = participants.Max(p => p.Bottom);
        double y = headerBottom + FirstRowGap;
        double lastRow = headerBottom;
        int frameOrder = 0;

        void Extend(double left, double right)
        {
            if (openFrames.Count == 0) return;
            var f = openFrames.Peek();
            f.Left = Math.Min(f.Left, left);
            f.Right = Math.Max(f.Right, right);
        }

        void Activate(string id, double at)
        {
            if (!byId.ContainsKey(id)) return;
            if (!openBars.TryGetValue(id, out var s)) openBars[id] = s = new Stack<double>();
            s.Push(at);
        }
        void Deactivate(string id, double at)
        {
            if (!byId.TryGetValue(id, out var p) || !openBars.TryGetValue(id, out var s) || s.Count == 0) return;
            double top = s.Pop();
            double x = p.CenterX - ActivationWidth / 2 + s.Count * ActivationStep;
            bars.Add(new SequenceActivationBar(p.Id, x, top, ActivationWidth, Math.Max(12, at - top)));
        }

        // Where a message leaves or meets a lifeline: its centre, or the outer edge of the
        // innermost open activation bar on the side facing the other end.
        double BarEdge(SequenceParticipantBox p, bool towardRight)
        {
            int d = openBars.TryGetValue(p.Id, out var s) ? s.Count : 0;
            if (d == 0) return p.CenterX;
            double barLeft = p.CenterX - ActivationWidth / 2 + (d - 1) * ActivationStep;
            return towardRight ? barLeft + ActivationWidth : barLeft;
        }

        // Mermaid numbering: a bare "autonumber" counts 1, 2, 3...; "autonumber 10 5" restarts at
        // 10 in steps of 5 from that point on; "autonumber off" stops it.
        bool numbering = autoNumber;
        int nextNumber = 1, numberStep = 1;

        foreach (var st in script)
        {
            if (st.Kind == SequenceStatementKind.Raw && TryReadAutoNumber(st.Text, out bool on, out int? start, out int? step))
            {
                numbering = on;
                if (start is { } s0) nextNumber = s0;
                if (step is { } s1) numberStep = s1;
                continue;
            }

            switch (st.Kind)
            {
                case SequenceStatementKind.Message:
                {
                    var m = st.Message!;
                    int? number = null;
                    if (numbering) { number = nextNumber; nextNumber += numberStep; }
                    int lines = LabelLines(m.Text);
                    double labelHalfHeight = (lines * LabelLineHeight + 8) / 2;
                    y += (lines - 1) * LabelLineHeight; // room above the line for a taller label

                    if (!byId.TryGetValue(m.FromId, out var from) || !byId.TryGetValue(m.ToId, out var to))
                    {
                        routes.Add(null);
                        continue;
                    }

                    double rowY = y;
                    // "+" starts the receiver's bar on this row and "-" ends the sender's after it,
                    // so both arrows meet the bar's edge, as Mermaid draws them.
                    if (m.ActivateTarget) Activate(to.Id, rowY);
                    double x1 = from.CenterX, x2 = to.CenterX;
                    if (!string.Equals(from.Id, to.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        x1 = BarEdge(from, towardRight: x2 > x1);
                        x2 = BarEdge(to, towardRight: x1 > x2);
                    }
                    if (string.Equals(from.Id, to.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        var pts = new List<Point>
                        {
                            new(x1, y),
                            new(x1 + SelfLoopWidth, y),
                            new(x1 + SelfLoopWidth, y + SelfLoopHeight),
                            new(x1, y + SelfLoopHeight),
                        };
                        // The label reads to the right of the loop, clear of the lifeline.
                        double labelX = x1 + SelfLoopWidth + 6 + LabelWidth(m.Text) / 2;
                        routes.Add(new SequenceMessageRoute(pts, labelX, y + SelfLoopHeight / 2, IsSelf: true) { Number = number });
                        Extend(x1 - 10, labelX + LabelWidth(m.Text) / 2);
                        lastRow = y + SelfLoopHeight;
                        y += RowStep + SelfLoopHeight;
                    }
                    else
                    {
                        routes.Add(new SequenceMessageRoute(new List<Point> { new(x1, y), new(x2, y) }, (x1 + x2) / 2, y - 3 - labelHalfHeight, IsSelf: false) { Number = number });
                        double half = LabelWidth(m.Text) / 2;
                        Extend(Math.Min(Math.Min(x1, x2), (x1 + x2) / 2 - half), Math.Max(Math.Max(x1, x2), (x1 + x2) / 2 + half));
                        lastRow = y;
                        y += RowStep;
                    }

                    if (m.DeactivateTarget) Deactivate(from.Id, rowY);
                    break;
                }

                case SequenceStatementKind.Note:
                {
                    var n = st.Note!;
                    var targets = n.TargetParticipantIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                    if (targets.Count == 0) break;
                    string noteText = global::MarkSmith.Mermaid.Generator.MermaidCodeGenerator.FromBreakTags(n.Text);
                    int lines = LabelLines(noteText);
                    double height = lines * LabelLineHeight + 14;
                    double width = Math.Max(90, LabelWidth(noteText));
                    double left;
                    switch (n.Placement)
                    {
                        case NotePlacement.LeftOf:
                            left = targets.Min(t => t.CenterX) - NoteGap - ActivationWidth / 2 - width;
                            break;
                        case NotePlacement.RightOf:
                            left = targets.Max(t => t.CenterX) + NoteGap + ActivationWidth / 2;
                            break;
                        default:
                            double l = targets.Min(t => t.CenterX), r = targets.Max(t => t.CenterX);
                            if (targets.Count > 1) width = Math.Max(width, r - l + 50);
                            left = (l + r) / 2 - width / 2;
                            break;
                    }
                    double top = y - LabelLineHeight;
                    notes.Add(new SequenceNoteBox(left, top, width, height, n.Text));
                    Extend(left, left + width);
                    lastRow = top + height;
                    y = top + height + FirstRowGap - 6;
                    break;
                }

                case SequenceStatementKind.Activate:
                    Activate(st.ParticipantId, lastRow > headerBottom ? lastRow : y - RowStep / 2);
                    break;

                case SequenceStatementKind.Deactivate:
                    Deactivate(st.ParticipantId, lastRow > headerBottom ? lastRow : y - RowStep / 2);
                    break;

                case SequenceStatementKind.BlockStart:
                {
                    bool highlight = st.BlockType == SequenceBlockType.Rect;
                    var f = new OpenFrame
                    {
                        Top = y - FirstRowGap / 2,
                        Keyword = string.IsNullOrEmpty(st.Keyword) ? st.BlockType.ToString().ToLowerInvariant() : st.Keyword,
                        Header = highlight ? string.Empty : st.Text,
                        IsHighlight = highlight,
                        Depth = openFrames.Count,
                        Order = frameOrder++,
                        // An empty frame still spans its header text.
                        Left = double.MaxValue,
                        Right = double.MinValue,
                        HeaderWidth = highlight ? 0 : LabelWidth(st.Keyword) + LabelWidth($"[{st.Text}]") + 20,
                    };
                    openFrames.Push(f);
                    y += highlight ? 10 : FrameHeader;
                    break;
                }

                case SequenceStatementKind.BlockDivider:
                    if (openFrames.Count == 0) break;
                    openFrames.Peek().Dividers.Add((y - FirstRowGap / 2 + 4, st.Text));
                    openFrames.Peek().HeaderWidth = Math.Max(openFrames.Peek().HeaderWidth, LabelWidth($"[{st.Text}]") + 20);
                    y += FrameHeader - 4;
                    break;

                case SequenceStatementKind.BlockEnd:
                {
                    if (openFrames.Count == 0) break;
                    var f = openFrames.Pop();
                    double bottom = Math.Max(lastRow, f.Top + FrameHeader) + 16;
                    if (f.Left > f.Right) { f.Left = minX; f.Right = maxX; } // nothing inside: span the diagram
                    double left = f.Left - FramePad, right = Math.Max(f.Right + FramePad, left + f.HeaderWidth);
                    frames.Add((new SequenceFrame(left, f.Top, right - left, bottom - f.Top, f.Keyword, f.Header,
                        f.Dividers.Select(d => new SequenceFrameDivider(left, d.Y, right - left, d.Text)).ToList(), f.Depth, f.IsHighlight), f.Order));
                    lastRow = bottom;
                    y = bottom + FirstRowGap - 4;
                    // The parent frame encloses this one.
                    Extend(left - 4, right + 4);
                    break;
                }
            }
        }

        // Activations still open at the end run to the last row.
        foreach (var (id, stack) in openBars.ToList())
            while (stack.Count > 0) Deactivate(id, lastRow + 12);

        foreach (var p in participants)
            lengths[p.Id] = Math.Max(Tail, lastRow + Tail - p.Bottom);

        return new SequenceDrawing(routes, lengths)
        {
            Notes = notes,
            Frames = frames.OrderBy(f => f.Order).Select(f => f.Frame).ToList(),
            Activations = bars,
        };
    }

    /// <summary>Reads an "autonumber" line: bare, with a start and optional step, or "off".</summary>
    public static bool TryReadAutoNumber(string? line, out bool on, out int? start, out int? step)
    {
        on = false; start = null; step = null;
        var parts = (line ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("autonumber", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length > 1 && parts[1].Equals("off", StringComparison.OrdinalIgnoreCase)) return true;
        on = true;
        if (parts.Length > 1 && int.TryParse(parts[1], out int a)) start = a;
        if (parts.Length > 2 && int.TryParse(parts[2], out int b)) step = b;
        return true;
    }

    private sealed class OpenFrame
    {
        public double Top, Left, Right, HeaderWidth;
        public string Keyword = string.Empty, Header = string.Empty;
        public bool IsHighlight;
        public int Depth, Order;
        public List<(double Y, string Text)> Dividers { get; } = new();
    }

    /// <summary>Horizontal space a message label needs (11 px UI font, padded).</summary>
    public static double LabelWidth(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        return label.Replace("\r\n", "\n").Split('\n', '\r').Max(l => l.Length) * 6.6 + 26;
    }

    public static int LabelLines(string? label) =>
        string.IsNullOrEmpty(label) ? 1 : label.Replace("\r\n", "\n").Split('\n', '\r').Length;

    /// <summary>
    /// Centre-to-centre distance between neighbouring participants <paramref name="left"/> and
    /// <paramref name="right"/>: wide enough for both header boxes and for the longest label of any
    /// message passing between them (Mermaid widens the gap the same way).
    /// </summary>
    public static double ColumnSpacing(double leftWidth, double rightWidth, double widestLabelBetween) =>
        Math.Max(220, Math.Max((leftWidth + rightWidth) / 2 + 60, widestLabelBetween + 40));
}
