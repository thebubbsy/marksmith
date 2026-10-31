namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>
/// The smallest scroll that brings an item into a viewport, along one axis. All values are in the
/// same units (a canvas's zoomed pixels). An item already in view, margin included, needs no
/// scroll; one too big to fit lines its start up with the viewport's start.
/// </summary>
public static class ViewportReveal
{
    /// <summary>The new offset that shows <c>[start, start + length]</c> with
    /// <paramref name="margin"/> around it, or null when it is already in view.</summary>
    public static double? OffsetToReveal(double offset, double viewport, double start, double length, double margin)
    {
        if (viewport <= 0 || length < 0) return null;
        double end = start + length;
        if (start - margin >= offset && end + margin <= offset + viewport) return null;
        if (length + 2 * margin >= viewport || start - margin < offset) return Math.Max(0, start - margin);
        return Math.Max(0, end + margin - viewport);
    }
}
