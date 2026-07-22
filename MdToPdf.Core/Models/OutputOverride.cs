namespace MdToPdf.Models;

// A partial set of output settings the browser extension (or any API caller) can supply so that
// automated exports use the extension's own profile instead of the app's live UI settings. Every
// field is optional; nulls fall back to whatever the app currently has.
public sealed class OutputOverride
{
    public string? Theme { get; set; }
    public bool? ThemeLightInfluence { get; set; }
    public int? ContentWidth { get; set; }
    public bool? A4FixedWidth { get; set; }
    public bool? UnlimitedHeight { get; set; }
    public bool? IncludeToc { get; set; }
    public bool? ShowAttribution { get; set; }
    public bool? NoEmoji { get; set; }
    public int? DashMode { get; set; }
    public string? DashCustom { get; set; }
    public int? HeadingShift { get; set; }
    public int? BoldMode { get; set; }
    public int? ItalicMode { get; set; }
    public bool? NormalizeLlm { get; set; }

    // Mermaid-in-DOCX method: 0 = Snapshot (embedded picture), 1 = ShapeForge (native Word shapes).
    public int? MermaidDocxMode { get; set; }
    
    // ShapeForge and general diagram settings
    public int? OversizedDiagramMode { get; set; }
    public int? DiagramGridSize { get; set; }
    public bool? SmartConnectors { get; set; }
    public string? ConnectorRouting { get; set; }
    public string? ConnectorArrowhead { get; set; }

    // Cover Page setting
    public bool? BrandCoverPage { get; set; }

    // Where automated exports are written (folder path). Blank = leave the app's setting.
    public string? OutputFolder { get; set; }

    // Which file(s) to produce: any of "pdf", "docx", "pptx", "epub", comma-separated, or "both"
    // (= pdf,docx). Null/blank = pdf.
    public string? Format { get; set; }

    // ---- Source metadata captured from the originating AI-chat page ----------------------------
    // All optional. Carried two ways, both landing here: the browser extension's API sends them as
    // JSON fields inside `output`; the "Copy as Markdown" button embeds the same fields in a hidden
    // clipboard-HTML marker that ClipboardSourceMeta parses back into this object. The point is to
    // preserve the context an AI reply loses the moment it becomes plain Markdown — so the export
    // reflects where it actually came from instead of a generic guess.

    // Font family the reply was rendered in on the source page (getComputedStyle). -> BrandFontFamily.
    public string? SourceFontFamily { get; set; }

    // Canonical originating site, known for certain by the extension from the page's hostname:
    // "chatgpt" | "gemini" | "claude" | "copilot". Replaces LlmSourceService's text-pattern GUESS
    // with ground truth (100% confidence) when present.
    public string? SourceId { get; set; }

    // Model label if the page exposes one (e.g. "GPT-4o", "Claude Opus 4.1"). Best-effort — sites
    // don't always surface it in stable markup — so it enriches attribution but never gates it.
    public string? SourceModel { get; set; }

    // The conversation's title (page/tab title or sidebar heading), used as the default export
    // filename + document title instead of a throwaway "pasted_export_ab12cd34".
    public string? SourceTitle { get; set; }

    // BCP-47 language tag (document.documentElement.lang) and text direction ("ltr" | "rtl") of the
    // reply. -> AppSettings.ContentLanguage/ContentDirection, so RTL content lays out correctly in
    // the preview and PDF instead of being forced left-to-right.
    public string? SourceLanguage { get; set; }
    public string? SourceDirection { get; set; }

    // The source site's brand accent (hex). -> nearest built-in theme (ThemeCatalog.NearestByAccent),
    // so the exported document's palette echoes where the content came from.
    public string? SourceAccentColor { get; set; }
}
