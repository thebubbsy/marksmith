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

    // Pro = an activated key. A TRIAL is NOT "pro": it is a free user with exactly ONE DOCX export
    // to try the killer feature, so the trial never unlocks PPTX or automation.
    public bool IsPro => Edition == Edition.Pro;

    // ---- entitlements (the paywall) ----
    public bool CanExportDocx => Edition is Edition.Pro or Edition.Trial; // Trial = its single export
    public bool CanExportPptx => Edition == Edition.Pro;                  // PPTX slide decks
    public bool CanAutomate => Edition == Edition.Pro;                    // hands-free auto-convert (clipboard / watch folder / extension)
    public bool ShowFooter => Edition != Edition.Pro;                     // "Made with Marksmith" footer on free exports
}

// What we persist to %LOCALAPPDATA%\MarkSmith\license.json.
public sealed class StoredLicense
{
    public string? Key { get; set; }
    public string? Email { get; set; }
    public string? InstanceId { get; set; }          // Lemon Squeezy activation instance, if used
    // The whole trial: exactly ONE DOCX export. > 0 = trial active with that many exports left
    // (only ever 1); 0 = not started or already used up. There is NO automatic trial — the user
    // starts it explicitly, and it is consumed by a single successful DOCX export.
    public int TrialExportsRemaining { get; set; }
}
