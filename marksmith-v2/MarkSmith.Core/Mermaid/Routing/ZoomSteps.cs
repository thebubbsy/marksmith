namespace MarkSmith.Core.Mermaid.Routing;

/// <summary>
/// The zoom stops a canvas's zoom in / zoom out buttons step through, the same ones most editors
/// use. A zoom that sits between two stops (after Fit or Ctrl+wheel) moves to the next stop in
/// that direction rather than by a fixed amount.
/// </summary>
public static class ZoomSteps
{
    public static readonly IReadOnlyList<double> Stops =
        [0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0];

    private const double Tolerance = 0.005;

    /// <summary>The first stop above <paramref name="current"/>, capped at <paramref name="max"/>.</summary>
    public static double Next(double current, double max)
    {
        foreach (var stop in Stops)
            if (stop > current + Tolerance) return Math.Min(stop, max);
        return max;
    }

    /// <summary>The first stop below <paramref name="current"/>, floored at <paramref name="min"/>.</summary>
    public static double Previous(double current, double min)
    {
        for (int i = Stops.Count - 1; i >= 0; i--)
            if (Stops[i] < current - Tolerance) return Math.Max(Stops[i], min);
        return min;
    }
}
