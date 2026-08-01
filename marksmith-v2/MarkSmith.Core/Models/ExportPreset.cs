namespace MdToPdf.Models;

// A named snapshot of the output/style settings, so a look you like (theme + width + cleanup +
// formatting + diagram mode + branding) can be re-applied in one click. Automation/API settings are
// deliberately excluded — a preset is about how the document looks, not how the app runs.
public sealed class ExportPreset
{
    public string Name { get; set; } = "";

    public string Theme { get; set; } = "GitHub Light";
    public int ContentWidth { get; set; } = 820;
    public bool A4FixedWidth { get; set; }
    public bool UnlimitedHeight { get; set; }

    public bool IncludeToc { get; set; }
    public bool ShowAttribution { get; set; }
    public bool NoEmoji { get; set; }

    public int DashMode { get; set; }
    public string DashCustom { get; set; } = "";
    public int HeadingShift { get; set; }
    public int BoldMode { get; set; }
    public int ItalicMode { get; set; }

    public int MermaidDocxMode { get; set; } = 1;
    public int OversizedDiagramMode { get; set; }

    public bool BrandCoverPage { get; set; }
    public string BrandLogoPath { get; set; } = "";
    public string BrandFontFamily { get; set; } = "";

    public static ExportPreset Capture(string name, AppSettings s) => new()
    {
        Name = name,
        Theme = s.Theme, ContentWidth = s.ContentWidth, A4FixedWidth = s.A4FixedWidth, UnlimitedHeight = s.UnlimitedHeight,
        IncludeToc = s.IncludeToc, ShowAttribution = s.ShowAttribution, NoEmoji = s.NoEmoji,
        DashMode = s.DashMode, DashCustom = s.DashCustom, HeadingShift = s.HeadingShift, BoldMode = s.BoldMode, ItalicMode = s.ItalicMode,
        MermaidDocxMode = s.MermaidDocxMode, OversizedDiagramMode = s.OversizedDiagramMode,
        BrandCoverPage = s.BrandCoverPage, BrandLogoPath = s.BrandLogoPath, BrandFontFamily = s.BrandFontFamily,
    };
}
