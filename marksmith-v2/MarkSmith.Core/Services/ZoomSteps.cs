namespace MarkSmith.Services;

/// <summary>
/// The preview's zoom stops, the same ladder browsers and Office use (… 90%, 100%, 110%, 125%,
/// 150% …). Zoom in/out and Ctrl+wheel move to the next stop, so a scale that came from fitting
/// the page to the pane (say 137%) snaps onto the ladder at the first step instead of drifting by
/// a fixed amount from an odd number forever.
/// </summary>
public static class ZoomSteps
{
    public static IReadOnlyList<double> Stops { get; } = new[]
    {
        0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0,
    };

    public static double Min => Stops[0];
    public static double Max => Stops[^1];

    /// <summary>
    /// The largest scale "fit page width" will pick. Beyond about 125% a fitted page in a wide
    /// pane turns into giant text (Preview-only view used to open at 171%); the reader can still
    /// zoom past it by hand.
    /// </summary>
    public const double FitMax = 1.25;

    // Scales within half a percent of a stop count as that stop, so 0.999 from a rounding
    // somewhere doesn't make "zoom in" land on 100%.
    private const double Tolerance = 0.005;

    /// <summary>The first stop above <paramref name="current"/>, or <see cref="Max"/>.</summary>
    public static double Next(double current)
    {
        foreach (var stop in Stops)
            if (stop > current + Tolerance) return stop;
        return Max;
    }

    /// <summary>The first stop below <paramref name="current"/>, or <see cref="Min"/>.</summary>
    public static double Previous(double current)
    {
        for (var i = Stops.Count - 1; i >= 0; i--)
            if (Stops[i] < current - Tolerance) return Stops[i];
        return Min;
    }

    public static bool CanZoomIn(double current) => current < Max - Tolerance;
    public static bool CanZoomOut(double current) => current > Min + Tolerance;
}
