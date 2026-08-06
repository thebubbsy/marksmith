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

    // Classifier entry point: "can the CURRENT license run this feature?" Free features always
    // return true; pro features consult the entitlements above.
    public bool CanUse(FeatureId id) => FeatureClassifier.LicenseAllows(id, this);
}

// What we persist to %LOCALAPPDATA%\MarkSmith\license.json.
public sealed class StoredLicense
{
    public string? Key { get; set; }
    public string? Email { get; set; }
    public string? InstanceId { get; set; }          // Lemon Squeezy activation instance, if used
    // The whole trial: exactly ONE DOCX export. > 0 = trial active with that many exports left
    // (only ever 1). There is NO automatic trial — the user starts it explicitly, and it is
    // consumed by a single successful DOCX export.
    public int TrialExportsRemaining { get; set; }

    // True once the trial's one export has been used. This is what makes a USED trial
    // distinguishable from a never-started one — a used trial can never be restarted (the user
    // cannot re-grant themselves the export by toggling state).
    public bool TrialUsed { get; set; }

    // When the trial export was consumed (tracked so the state is verifiable).
    public DateTimeOffset? TrialExportUsedUtc { get; set; }
}

// ===== Top-down feature classification =====
//
// Every gateable surface of the app is classified here, once, so "what is free vs what is paid"
// lives in exactly one place. The classifier is the single source of truth every gate reads —
// the VM gates, the automation manager, the batch pipeline and the pro-gate dialog.

public enum FeatureId
{
    // ---- Free tier (works for everyone) ----
    MarkdownToPdf,     // PDF/HTML/Markdown export, editing, live preview, themes, studios

    // ---- Pro tier (checked against the license) ----
    DocxExport,        // Word export (+ editable equations) — the one-export trial unlocks this once
    PptxExport,        // PowerPoint export
    BatchConvert,      // folder → multi-format batch conversion
    WatchFolder,       // watch a folder for new .md files → auto-convert
    AutoExportIngest,  // auto-export a PDF after clipboard/API/extension ingests
    ClipboardIngest,   // clipboard watcher for AI-chat ingests
    AdvancedStyling,   // advanced formatting personalization section
}

public static class FeatureClassifier
{
    public static bool IsFree(FeatureId id) => id switch
    {
        FeatureId.MarkdownToPdf => true,
        _ => false,
    };

    public static string DisplayName(FeatureId id) => id switch
    {
        FeatureId.MarkdownToPdf => "Markdown → PDF export",
        FeatureId.DocxExport => "DOCX export",
        FeatureId.PptxExport => "PPTX export",
        FeatureId.BatchConvert => "Batch conversion",
        FeatureId.WatchFolder => "Folder-watch automation",
        FeatureId.AutoExportIngest => "Hands-free auto-export",
        FeatureId.ClipboardIngest => "Clipboard automation",
        FeatureId.AdvancedStyling => "Advanced formatting",
        _ => id.ToString(),
    };

    /// <summary>Whether the given license state permits a feature (free features are always true).</summary>
    public static bool LicenseAllows(FeatureId id, LicenseState state) => id switch
    {
        FeatureId.MarkdownToPdf => true,
        FeatureId.DocxExport => state.CanExportDocx,
        FeatureId.PptxExport => state.CanExportPptx,
        FeatureId.BatchConvert or FeatureId.WatchFolder
            or FeatureId.AutoExportIngest or FeatureId.ClipboardIngest => state.CanAutomate,
        FeatureId.AdvancedStyling => true, // advanced styling is free — the toggle simply reveals it
        _ => true,
    };
}
