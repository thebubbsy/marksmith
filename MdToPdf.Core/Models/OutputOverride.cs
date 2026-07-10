namespace MdToPdf.Models;

// A partial set of output settings the browser extension (or any API caller) can supply so that
// automated exports use the extension's own profile instead of the app's live UI settings. Every
// field is optional; nulls fall back to whatever the app currently has.
public sealed class OutputOverride
{
    public string? Theme { get; set; }
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

    // Where automated exports are written (folder path). Blank = leave the app's setting.
    public string? OutputFolder { get; set; }

    // Which file(s) to produce: any of "pdf", "docx", "pptx", "epub", comma-separated, or "both"
    // (= pdf,docx). Null/blank = pdf.
    public string? Format { get; set; }

    // Font family detected from the source AI-chat page at copy/send time (the "Copy as Markdown"
    // button reads getComputedStyle on the reply and carries it via the HTML clipboard format, or
    // the extension can send it directly). Maps onto BrandFontFamily so the preview/export use the
    // same font the reply was actually shown in, instead of the app's default.
    public string? SourceFontFamily { get; set; }
}
