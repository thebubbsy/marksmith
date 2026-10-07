namespace MarkSmith.Models;

/// <summary>
/// Everything the app says when a free user reaches a Pro feature: the status line, the upgrade
/// dialog, the banner, the "Pro" tags in the export menu and which export the main button runs.
/// Before this the same message existed in nine hand-written variants (two promised the free plan
/// covered only "Markdown, PDF and HTML", although EPUB and email are free too, and only Word ever
/// offered the trial, although the trial unlocks every Pro feature).
/// </summary>
public static class ProGate
{
    /// <summary>What a free user keeps. Kept in step with <see cref="FeatureClassifier.IsFree"/>.</summary>
    public const string FreePlanIncludes =
        "Free includes PDF, web page, EPUB, Markdown and email exports, the live preview, every theme and every studio.";

    /// <summary>The trial in one sentence. It is the whole of Pro, capped by Word exports, not by days.</summary>
    public const string TrialSummary =
        "The free trial unlocks everything in Pro until you've made 3 Word exports.";

    public const string StartTrialLabel = "Start free trial";
    public const string BuyLabel = "Buy Pro";
    public const string NotNowLabel = "Not now";

    /// <summary>The name people know a feature by ("Word export", not "DOCX export").</summary>
    public static string FeatureName(FeatureId id) => id switch
    {
        FeatureId.DocxExport => "Word export",
        FeatureId.PptxExport => "PowerPoint export",
        FeatureId.BatchConvert => "Batch conversion",
        FeatureId.WatchFolder => "Folder watching",
        FeatureId.AutoExportIngest => "Hands-free auto-export",
        FeatureId.ClipboardIngest => "Clipboard automation",
        FeatureId.AdvancedStyling => "Advanced formatting",
        FeatureId.EmailDraft => "Email drafts",
        _ => "PDF export",
    };

    /// <summary>What the feature gives you, said as a benefit. Shown in the upgrade dialog.</summary>
    public static string Pitch(FeatureId id) => id switch
    {
        FeatureId.DocxExport =>
            "Send editable Word documents with real headings, tables, editable equations and your diagrams.",
        FeatureId.PptxExport =>
            "Turn the document into a slide deck, one slide per section, in your theme.",
        FeatureId.BatchConvert =>
            "Convert a whole folder of Markdown files in one go.",
        FeatureId.WatchFolder =>
            "Drop a Markdown file into a folder and MarkSmith converts it for you.",
        FeatureId.AutoExportIngest =>
            "Anything the browser extension, clipboard or local API sends is exported straight away.",
        FeatureId.ClipboardIngest =>
            "Copy an answer from an AI chat and MarkSmith picks it up on its own.",
        _ => "",
    };

    /// <summary>Whether a gate should offer the trial. The trial is full Pro, so it unlocks every gated feature.</summary>
    public static bool OffersTrial(LicenseState state) => state.CanStartTrial;

    /// <summary>The status-bar line shown when a free user tries a Pro feature.</summary>
    public static string StatusLine(FeatureId id, LicenseState state) =>
        $"{FeatureName(id)} is a MarkSmith Pro feature. " +
        (OffersTrial(state) ? "Start the free trial to use it now." : "Upgrade to MarkSmith Pro to use it.");

    public static string DialogTitle(FeatureId id) => $"{FeatureName(id)} is part of MarkSmith Pro";

    /// <summary>The dialog's paragraphs, in order: the pitch, the offer, what stays free.</summary>
    public static IReadOnlyList<string> DialogParagraphs(FeatureId id, LicenseState state)
    {
        var paragraphs = new List<string>();
        var pitch = Pitch(id);
        if (pitch.Length > 0) paragraphs.Add(pitch);
        paragraphs.Add(OffersTrial(state)
            ? TrialSummary + " Nothing to pay and no card needed."
            : state.TrialUsed
                ? "Your free trial has been used. Buy Pro to keep using it; a license key unlocks it on this PC straight away."
                : "Buy Pro to unlock it; a license key unlocks it on this PC straight away.");
        paragraphs.Add(FreePlanIncludes);
        return paragraphs;
    }

    /// <summary>The main export button runs Word for anyone who can export Word, and PDF for everyone else.</summary>
    public static bool PrimaryExportIsWord(LicenseState state) => state.CanExportDocx;

    public static string PrimaryExportLabel(LicenseState state) =>
        PrimaryExportIsWord(state) ? "Generate Word (.docx)" : "Generate PDF (.pdf)";

    public static string PrimaryExportTip(LicenseState state) =>
        PrimaryExportIsWord(state)
            ? "Generate a Word document. Use the arrow for PDF, EPUB, email and the other formats."
            : "Generate a PDF. Use the arrow for web page, EPUB, email and the Pro formats.";

    /// <summary>
    /// The right-hand text of an export menu item: its shortcut, prefixed with "Pro" while the
    /// current license can't run it ("Pro · Ctrl+Shift+D").
    /// </summary>
    public static string MenuTag(FeatureId id, LicenseState state, string shortcut)
    {
        if (state.CanUse(id)) return shortcut;
        return shortcut.Length == 0 ? "Pro" : "Pro · " + shortcut;
    }

    /// <summary>The licence banner: title, message and action label (null = no action button).</summary>
    public static (string Title, string Message, string? Action) Banner(LicenseState state)
    {
        if (state.IsTrial)
        {
            var left = state.TrialExportsRemaining == 1 ? "1 Word export" : $"{state.TrialExportsRemaining} Word exports";
            return ($"Pro trial · {left} left",
                "Everything in Pro is unlocked. After the last Word export you're back on the free plan.",
                null);
        }
        return ("MarkSmith Free",
            "PDF, web page, EPUB and email exports are free. Word, PowerPoint and automation are Pro.",
            OffersTrial(state) ? StartTrialLabel : BuyLabel);
    }
}
