namespace MarkSmith.Models;

/// <summary>
/// The formats an unattended export can produce (the "Default output format" setting, the
/// clipboard / extension / API auto-export, the watch folder, batch convert and
/// <c>/api/convert</c>) and what each is called. Before this, each of those paths kept its own
/// list: the watch folder knew PDF and Word and wrote a PDF for anything else, the dropped-files
/// batch knew PDF and Word and threw for PowerPoint/EPUB, and the folder batch reported a success
/// for a format it had no case for.
/// </summary>
public static class OutputFormats
{
    public const string Pdf = "pdf", Docx = "docx", Pptx = "pptx", Epub = "epub", Eml = "eml", Msg = "msg";

    /// <summary>Every format automation can write, in the order the pickers list them.</summary>
    public static IReadOnlyList<string> All { get; } = new[] { Pdf, Docx, Pptx, Epub, Eml, Msg };

    /// <summary>The stored or requested name in canonical form ("email" → "eml", "DOCX" → "docx"),
    /// or null for something no exporter writes.</summary>
    public static string? Normalize(string? format)
    {
        var f = format?.Trim().TrimStart('.').ToLowerInvariant();
        if (f is "email") f = Eml;
        return f is not null && All.Contains(f) ? f : null;
    }

    public static bool IsEmail(string? format) => Normalize(format) is Eml or Msg;

    /// <summary>Only the PDF needs the preview engine to draw it; the rest are written in-process
    /// (diagrams are drawn with the engine when it is available and fall back without it).</summary>
    public static bool NeedsRenderHost(string format) => Normalize(format) == Pdf;

    /// <summary>What the format is called in a sentence ("exported as a Word document").</summary>
    public static string Label(string? format) => Normalize(format) switch
    {
        Docx => "Word document",
        Pptx => "PowerPoint deck",
        Epub => "EPUB e-book",
        Eml => "email draft (.eml)",
        Msg => "Outlook message (.msg)",
        _ => "PDF",
    };

    /// <summary>The short name used for a history row and the "… ready" notification.</summary>
    public static string Kind(string? format) => Normalize(format) switch
    {
        Docx => "DOCX",
        Pptx => "PPTX",
        Epub => "EPUB",
        Eml => "Email",
        Msg => "Outlook message",
        _ => "PDF",
    };

    /// <summary>The same short name worked out from an output file's extension.</summary>
    public static string KindForPath(string path) =>
        Normalize(System.IO.Path.GetExtension(path)) is { } f ? Kind(f)
        : System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();

    /// <summary>The Pro feature behind a format, or null when it is free on every plan.</summary>
    public static FeatureId? ProFeature(string? format) => Normalize(format) switch
    {
        Docx => FeatureId.DocxExport,
        Pptx => FeatureId.PptxExport,
        _ => null,
    };
}

/// <summary>
/// Who may run unattended exports. Automation is Pro, with one exception the owner asked for:
/// anything that only ever writes email drafts is free on every plan ("everything to help people
/// not have to deal with work emails should be free"). So a free user whose default output format
/// is an email draft can use the clipboard watcher, the watch folder and auto-export.
/// </summary>
public static class AutomationPolicy
{
    /// <summary>True when automation that writes <paramref name="formats"/> may run.</summary>
    public static bool Allows(LicenseState state, IEnumerable<string?> formats)
    {
        if (state.CanAutomate) return true;
        var any = false;
        foreach (var f in formats)
        {
            if (!OutputFormats.IsEmail(f)) return false;
            any = true;
        }
        return any;
    }

    public static bool Allows(LicenseState state, string? targetFormat) => Allows(state, new[] { targetFormat });

    /// <summary>The extra sentence a free user sees on an automation gate: the free way in.</summary>
    public const string EmailIsFreeHint =
        "Automation that writes email drafts is free: set Default output format to an email draft in Settings.";
}
