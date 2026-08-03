namespace MarkSmith.Models;

public enum Edition { Free, Trial, Pro }

// The resolved licensing state the rest of the app reads. Entitlements live here so the paywall is
// defined in exactly one place.
public sealed class LicenseState
{
    public Edition Edition { get; init; } = Edition.Free;
    public string? Key { get; init; }
    public string? Email { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public string? Status { get; init; }

    public bool IsPro => Edition is Edition.Pro or Edition.Trial;

    // ---- entitlements (the paywall) ----
    public bool CanExportDocx => IsPro;   // DOCX + editable math
    public bool CanExportPptx => IsPro;   // PPTX slide decks
    public bool CanAutomate => IsPro;     // hands-free auto-convert (clipboard / watch folder / extension)
    public bool ShowFooter => !IsPro;     // "Made with Marksmith" footer on free exports
}

// What we persist to %LOCALAPPDATA%\MarkSmith\license.json.
public sealed class StoredLicense
{
    public string? Key { get; set; }
    public string? Email { get; set; }
    public string? InstanceId { get; set; }          // Lemon Squeezy activation instance, if used
    public DateTimeOffset? TrialStartUtc { get; set; }
    // Highest wall-clock time ever observed (monotonic; never decreases). Lets the trial detect a
    // clock rollback — if the current time is before this, the system clock was moved back and the
    // trial is evaluated against LastSeenUtc instead, so winding the clock back can't revive it.
    public DateTimeOffset? LastSeenUtc { get; set; }
}
