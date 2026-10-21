namespace MarkSmith.Services;

/// <summary>
/// Sizing rules for the main window's status bar. The status message sits on the left and a row
/// of copy pills on the right; at the default 1220px launch width the full pill row is wider
/// than the window, so it ran underneath the message ("y LinksReady."). These rules decide when
/// the secondary pills fold into a single "More" menu and how wide the message may grow.
/// </summary>
public static class StatusBarLayout
{
    /// <summary>Room always kept for the busy ring and a readable slice of the status message.</summary>
    public const double MinimumLeftWidth = 300;

    /// <summary>The message never grows past this, even on very wide windows.</summary>
    public const double MaximumMessageWidth = 560;

    /// <summary>Below this the message is unreadable, so it stops shrinking (the tooltip has it all).</summary>
    public const double MinimumMessageWidth = 80;

    /// <summary>
    /// True when the right-hand row, with every pill shown, would leave less than
    /// <see cref="MinimumLeftWidth"/> for the left-hand side.
    /// </summary>
    public static bool ShouldFoldSecondaryPills(double availableWidth, double fullRightWidth)
    {
        if (availableWidth <= 0 || fullRightWidth <= 0) return false;
        return fullRightWidth + MinimumLeftWidth > availableWidth;
    }

    /// <summary>
    /// The widest the status message can be without reaching the right-hand row, given the
    /// width of everything else on the left (margins, busy ring, badges and links).
    /// </summary>
    public static double MessageMaxWidth(double availableWidth, double rightWidth, double otherLeftWidth)
    {
        const double gap = 12; // breathing room between the message and the first right-hand pill
        var room = availableWidth - rightWidth - otherLeftWidth - gap;
        if (double.IsNaN(room)) return MaximumMessageWidth;
        return Math.Clamp(room, MinimumMessageWidth, MaximumMessageWidth);
    }
}
