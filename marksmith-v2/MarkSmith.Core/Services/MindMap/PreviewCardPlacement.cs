namespace MarkSmith.Services.MindMap;

/// <summary>
/// Where the Galaxy's floating preview card goes. It used to sit in the bottom-right corner
/// whatever was under it: hovering a node down there (run #47, with a real mouse) put the card
/// over the very node it describes, and over the connection legend. Now it takes the first
/// corner that leaves the previewed node clear, staying inside the band between the top
/// overlays (tour banner, tag pills) and the legend.
/// </summary>
public static class PreviewCardPlacement
{
    public enum Corner { BottomRight, TopRight, TopLeft, BottomLeft }

    /// <summary>A rectangle in the canvas container's coordinates.</summary>
    public readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public double OverlapArea(Box other)
        {
            var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
            var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        public Box Inflate(double by) => new(X - by, Y - by, Width + 2 * by, Height + 2 * by);
    }

    /// <summary>Distance from the container's edges.</summary>
    public const double Edge = 24;

    /// <summary>Space kept between the card and the previewed node.</summary>
    public const double NodeClearance = 8;

    /// <summary>Preference order: bottom-right (where people expect it), then the top corners.
    /// Bottom-left comes last because the minimap lives there.</summary>
    private static readonly Corner[] Order = { Corner.BottomRight, Corner.TopRight, Corner.TopLeft, Corner.BottomLeft };

    /// <summary>
    /// The card's rectangle at <paramref name="corner"/>. <paramref name="topReserved"/> and
    /// <paramref name="bottomReserved"/> are the bands the overlays use at the top and bottom.
    /// </summary>
    public static Box At(Corner corner, double containerWidth, double containerHeight,
        double cardWidth, double cardHeight, double topReserved, double bottomReserved)
    {
        var top = Math.Max(Edge, topReserved);
        var bottom = Math.Max(Edge, bottomReserved);
        // A short window: keep the card on screen, even if that means touching an overlay.
        var y = corner is Corner.TopRight or Corner.TopLeft
            ? Math.Min(top, containerHeight - Edge - cardHeight)
            : Math.Max(Edge, containerHeight - bottom - cardHeight);
        var x = corner is Corner.BottomRight or Corner.TopRight
            ? Math.Max(Edge, containerWidth - Edge - cardWidth)
            : Edge;
        return new Box(x, Math.Max(0, y), cardWidth, cardHeight);
    }

    /// <summary>
    /// The first corner whose card leaves <paramref name="node"/> clear, else the one covering least of it.
    /// The card is re-placed on every pan and zoom step (run #48: it was placed once, so dragging the
    /// map slid the node under it). With <paramref name="current"/> given, the card stays where it is
    /// while that corner is still clear, rather than snapping back to bottom-right mid-drag.
    /// </summary>
    public static Corner Choose(Box node, double containerWidth, double containerHeight,
        double cardWidth, double cardHeight, double topReserved, double bottomReserved, Corner? current = null)
    {
        var keepClear = node.Inflate(NodeClearance);
        if (current is { } stay &&
            At(stay, containerWidth, containerHeight, cardWidth, cardHeight, topReserved, bottomReserved).OverlapArea(keepClear) == 0)
            return stay;
        var best = Corner.BottomRight;
        var bestOverlap = double.MaxValue;
        foreach (var corner in Order)
        {
            var card = At(corner, containerWidth, containerHeight, cardWidth, cardHeight, topReserved, bottomReserved);
            var overlap = card.OverlapArea(keepClear);
            if (overlap == 0) return corner;
            if (overlap < bestOverlap)
            {
                best = corner;
                bestOverlap = overlap;
            }
        }
        return best;
    }
}
