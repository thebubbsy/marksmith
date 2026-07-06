namespace MdToPdf.Models;

public sealed class AppSettings
{
    public string Theme { get; set; } = "GitHub Light";
    public int ContentWidth { get; set; } = 800;
    public bool MermaidEnabled { get; set; } = true;
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    public bool UnlimitedHeight { get; set; } = true;
    public bool A4FixedWidth { get; set; } = true;

    // AI ingest + normalization
    public bool NormalizeLlm { get; set; } = true;
    public bool AutoClipboardIngest { get; set; }
    public bool WatchFolderEnabled { get; set; }
    public string WatchFolder { get; set; } = "";
    public bool WatchFolderAutoConvert { get; set; }
    public bool MinimizeToTray { get; set; }
    // Auto-generate a PDF whenever an AI chat is ingested (clipboard / API / extension).
    public bool AutoConvertIngests { get; set; }
    // Append each DOCX ingest as a dated section to one growing document instead of a new file.
    public bool AppendToRunningDoc { get; set; }
    public string RunningDocPath { get; set; } = "";
    // One-time in-app tip about the browser extension.
    public bool ShowExtensionTip { get; set; } = true;

    // How Mermaid diagrams are written into DOCX exports:
    //   0 = Snapshot — embed a picture of the rendered diagram
    //   1 = ShapeForge — rebuild the diagram as native, editable Word shapes (default)
    public int MermaidDocxMode { get; set; } = 1;

    // Branding kit (Pro): make exports look like YOUR documents, not converted markdown.
    public bool BrandCoverPage { get; set; }          // title cover page (logo, title, date)
    public string BrandLogoPath { get; set; } = "";   // PNG/JPEG shown on the cover
    public string BrandFontFamily { get; set; } = ""; // "" = default (Calibri)

    // Export extras
    public bool IncludeToc { get; set; }
    public bool ShowAttribution { get; set; } = true;
    public bool NoEmoji { get; set; } // strip all emoji from preview + every export format
    public int DashMode { get; set; } // em-dash handling: 0 keep, 1 hyphen, 2 spaced, 3 custom
    public string DashCustom { get; set; } = ""; // replacement text when DashMode == 3

    // Formatting personalization (structure, not cleanup)
    public int HeadingShift { get; set; } // -5..+5: promote (-) or demote (+) every heading
    public int BoldMode { get; set; }     // 0 keep, 1 remove, 2 to italic
    public int ItalicMode { get; set; }   // 0 keep, 1 remove

    // Local REST API
    public bool ApiEnabled { get; set; }
    public int ApiPort { get; set; } = 47821;

    // Advanced mode reveals power-user styling options (cleanup + formatting) in the Style panel.
    public bool AdvancedMode { get; set; }

    // Returns a copy with any non-null override fields applied — used so an API/extension caller can
    // export with its own output profile without mutating the app's persistent settings.
    public AppSettings CloneWith(OutputOverride? o)
    {
        var s = (AppSettings)MemberwiseClone();
        if (o is null) return s;
        if (o.Theme is not null) s.Theme = o.Theme;
        if (o.ContentWidth is { } cw) s.ContentWidth = cw;
        if (o.A4FixedWidth is { } a4) s.A4FixedWidth = a4;
        if (o.UnlimitedHeight is { } uh) s.UnlimitedHeight = uh;
        if (o.IncludeToc is { } toc) s.IncludeToc = toc;
        if (o.ShowAttribution is { } sa) s.ShowAttribution = sa;
        if (o.NoEmoji is { } ne) s.NoEmoji = ne;
        if (o.DashMode is { } dm) s.DashMode = dm;
        if (o.DashCustom is not null) s.DashCustom = o.DashCustom;
        if (o.HeadingShift is { } hs) s.HeadingShift = hs;
        if (o.BoldMode is { } bm) s.BoldMode = bm;
        if (o.ItalicMode is { } im) s.ItalicMode = im;
        if (o.NormalizeLlm is { } nl) s.NormalizeLlm = nl;
        if (o.MermaidDocxMode is { } mm) s.MermaidDocxMode = mm;
        if (!string.IsNullOrWhiteSpace(o.OutputFolder)) s.OutputFolder = o.OutputFolder;
        return s;
    }
}
