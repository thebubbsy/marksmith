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
    public int TrialExportsRemaining { get; init; }

    // Pro = an activated key. A TRIAL is the FULL Pro experience — every feature unlocked, no
    // paywall, no footer — capped at exactly 3 DOCX exports; after the 3rd the user drops back to
    // Free and the restrictions apply. The trial NEVER shows a "this is a Pro feature" message.
    public bool IsPro => Edition == Edition.Pro;
    public bool IsTrial => Edition == Edition.Trial;

    // ---- entitlements (the paywall) ----
    public bool CanExportDocx => Edition is Edition.Pro or Edition.Trial; // Trial = its 3 exports
    public bool CanExportPptx => Edition is Edition.Pro or Edition.Trial; // trial is full pro
    public bool CanAutomate => Edition is Edition.Pro or Edition.Trial;   // trial is full pro
    public bool ShowFooter => Edition == Edition.Free;                    // no footer while trial/pro

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
    // The trial: FULL Pro with a 3-DOCX-export cap. > 0 = trial active with that many exports
    // left (starts at 3, never auto-starts — the user triggers it). After the 3rd successful DOCX
    // export the user drops back to Free and the paywall returns.
    public int TrialExportsRemaining { get; set; }

    // True once the trial's 3 exports are spent. A USED trial can never be restarted (the user
    // cannot re-grant themselves the exports by toggling state).
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
    DocxExport,        // Word export (+ editable equations) — the 3-export trial unlocks this (then Free)
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
