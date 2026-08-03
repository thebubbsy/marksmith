namespace MarkSmith.Services;

// ISS-009: hard stop for the 30-day public beta. The pure date logic lives here (no UI — Core has
// no WinUI) so it is testable and reusable; the WinUI prompt + Environment.Exit enforcement lives
// in the app shell (MainWindow.CheckAndEnforceBetaExpirationAsync). Once the cutoff passes, the
// build refuses to keep converting documents and instead routes the user to the feedback page.
// Bump BetaCutoffDate for each new beta drop.
public static class BetaExpirationGuard
{
    // 2026-08-25 00:00 UTC — 30 days after the public beta build date.
    public static readonly DateTimeOffset BetaCutoffDate = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    // Where the "Submit Feedback & Get Update" dialog button takes the user.
    public const string FeedbackUrl = "https://github.com/thebubbsy/marksmith/issues";

    public static bool IsBetaExpired() => DateTimeOffset.UtcNow >= BetaCutoffDate;
}
