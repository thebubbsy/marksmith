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
}
