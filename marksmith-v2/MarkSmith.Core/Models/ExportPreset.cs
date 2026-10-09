using System.Text.Json.Serialization;

namespace MarkSmith.Models;

// A named snapshot of the output/style settings, so a look you like (theme + width + cleanup +
// formatting + diagrams + branding) can be re-applied in one click. Automation/API settings are
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
    // OversizedDiagramMode used to be saved and applied here. The Word writer forces one-page
    // shrink whatever it says, so applying a preset only left a stale value for automation to
    // trip over; old presets.json files still carry it and it is ignored.

    public bool BrandCoverPage { get; set; }
    public string BrandLogoPath { get; set; } = "";
    public string BrandFontFamily { get; set; } = "";

    // Added in 3.14. Nullable so a preset saved before then leaves these settings as they
    // are, instead of resetting them to defaults the user never chose.
    public bool? ThemeLightInfluence { get; set; }
    public bool? MermaidEnabled { get; set; }
    public bool? NormalizeLlm { get; set; }
    public bool? PageBorder { get; set; }
    public bool? SmartConnectors { get; set; }
    public string? ConnectorArrowhead { get; set; }
    public string? CustomFontPath { get; set; }
    public string? AuthorName { get; set; }

    public static ExportPreset Capture(string name, AppSettings s) => new()
    {
        Name = name,
        Theme = s.Theme, ContentWidth = s.ContentWidth, A4FixedWidth = s.A4FixedWidth, UnlimitedHeight = s.UnlimitedHeight,
        IncludeToc = s.IncludeToc, ShowAttribution = s.ShowAttribution, NoEmoji = s.NoEmoji,
        DashMode = s.DashMode, DashCustom = s.DashCustom, HeadingShift = s.HeadingShift, BoldMode = s.BoldMode, ItalicMode = s.ItalicMode,
        MermaidDocxMode = s.MermaidDocxMode,
        BrandCoverPage = s.BrandCoverPage, BrandLogoPath = s.BrandLogoPath, BrandFontFamily = s.BrandFontFamily,
        ThemeLightInfluence = s.ThemeLightInfluence, MermaidEnabled = s.MermaidEnabled, NormalizeLlm = s.NormalizeLlm,
        PageBorder = s.PageBorder, SmartConnectors = s.SmartConnectors, ConnectorArrowhead = s.ConnectorArrowhead,
        CustomFontPath = s.CustomFontPath, AuthorName = s.AuthorName,
    };

    /// <summary>
    /// Whether the settings look exactly as this preset would leave them, so the side panel can
    /// show the preset as the one in use, and stop showing it the moment any of its settings
    /// changes. Fields an older preset doesn't carry don't count.
    /// </summary>
    public bool Matches(AppSettings s) =>
        string.Equals(Theme, s.Theme, StringComparison.Ordinal)
        && ContentWidth == s.ContentWidth && A4FixedWidth == s.A4FixedWidth && UnlimitedHeight == s.UnlimitedHeight
        && IncludeToc == s.IncludeToc && ShowAttribution == s.ShowAttribution && NoEmoji == s.NoEmoji
        && DashMode == s.DashMode && (DashMode != 3 || DashCustom == s.DashCustom)
        && HeadingShift == s.HeadingShift && BoldMode == s.BoldMode && ItalicMode == s.ItalicMode
        && MermaidDocxMode == s.MermaidDocxMode
        && BrandCoverPage == s.BrandCoverPage && SamePath(BrandLogoPath, s.BrandLogoPath)
        && string.Equals(BrandFontFamily.Trim(), (s.BrandFontFamily ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
        && Same(ThemeLightInfluence, s.ThemeLightInfluence) && Same(MermaidEnabled, s.MermaidEnabled)
        && Same(NormalizeLlm, s.NormalizeLlm) && Same(PageBorder, s.PageBorder) && Same(SmartConnectors, s.SmartConnectors)
        && (ConnectorArrowhead is null || string.Equals(ConnectorArrowhead, s.ConnectorArrowhead, StringComparison.OrdinalIgnoreCase))
        && (CustomFontPath is null || SamePath(CustomFontPath, s.CustomFontPath))
        && (AuthorName is null || string.Equals(AuthorName.Trim(), (s.AuthorName ?? "").Trim(), StringComparison.Ordinal));

    private static bool Same(bool? saved, bool current) => saved is null || saved.Value == current;

    private static bool SamePath(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>One line for the preset list's tooltip: what applying it sets, in words.</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string> { Theme };
            parts.Add(A4FixedWidth ? "A4 width" : $"{ContentWidth} px wide");
            if (UnlimitedHeight) parts.Add("one continuous page");
            if (IncludeToc) parts.Add("table of contents");
            if (MermaidEnabled == false) parts.Add("diagrams as code");
            else parts.Add(MermaidDocxMode == 1 ? "editable Word diagrams" : "diagram pictures in Word");
            if (NoEmoji) parts.Add("no emoji");
            if (BrandCoverPage) parts.Add("cover page");
            if (!string.IsNullOrWhiteSpace(BrandFontFamily)) parts.Add(BrandFontFamily.Trim());
            return string.Join(" · ", parts);
        }
    }
}
