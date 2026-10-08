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
public sealed record SequenceMessageRoute(IReadOnlyList<Point> Points, double LabelX, double LabelY, bool IsSelf);

/// <summary>The whole drawing: one route per message (null when an end is missing) and how far
/// each participant's lifeline runs below its header.</summary>
public sealed record SequenceDrawing(IReadOnlyList<SequenceMessageRoute?> Routes, IReadOnlyDictionary<string, double> LifelineLengths);

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

    public static SequenceDrawing Layout(IReadOnlyList<SequenceParticipantBox> participants, IReadOnlyList<SequenceMessageSpec> messages)
    {
        var routes = new List<SequenceMessageRoute?>(messages.Count);
        var lengths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (participants.Count == 0) return new SequenceDrawing(routes, lengths);

        var byId = new Dictionary<string, SequenceParticipantBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in participants) byId.TryAdd(p.Id, p);

        double headerBottom = participants.Max(p => p.Bottom);
        double y = headerBottom + FirstRowGap;
        double lastRow = headerBottom;

        foreach (var m in messages)
        {
            int lines = LabelLines(m.Label);
            int extraLines = lines - 1;
            double labelHalfHeight = (lines * LabelLineHeight + 8) / 2;
            y += extraLines * LabelLineHeight; // room above the line for a taller label

            if (!byId.TryGetValue(m.FromId, out var from) || !byId.TryGetValue(m.ToId, out var to))
            {
                routes.Add(null);
                continue;
            }

            double x1 = from.CenterX, x2 = to.CenterX;
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
                routes.Add(new SequenceMessageRoute(pts, x1 + SelfLoopWidth + 6 + LabelWidth(m.Label) / 2, y + SelfLoopHeight / 2, IsSelf: true));
                lastRow = y + SelfLoopHeight;
                y += RowStep + SelfLoopHeight;
            }
            else
            {
                routes.Add(new SequenceMessageRoute(new List<Point> { new(x1, y), new(x2, y) }, (x1 + x2) / 2, y - 3 - labelHalfHeight, IsSelf: false));
                lastRow = y;
                y += RowStep;
            }
        }

        foreach (var p in participants)
            lengths[p.Id] = Math.Max(Tail, lastRow + Tail - p.Bottom);

        return new SequenceDrawing(routes, lengths);
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
