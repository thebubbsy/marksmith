namespace MdToPdf.Models;

public sealed class AppSettings
{
    public string TargetFormat { get; set; } = "pdf";
    public string Theme { get; set; } = "GitHub Light";
    public bool ThemeLightInfluence { get; set; }
    public int ContentWidth { get; set; } = 800;
    public bool MermaidEnabled { get; set; } = true;
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    // Template for export file names. Tokens: {title} (document title), {date} (yyyy-MM-dd),
    // {time} (HH-mm-ss), {format} (pdf/docx/pptx). Default "{title}" keeps the historic behavior.
    public string FileNameTemplate { get; set; } = "{title}";

    // Theme names the user has pinned; favorites are surfaced at the top of the theme dropdown.
    public List<string> FavoriteThemes { get; set; } = new();

    // Full paths of files the user has explicitly pinned; they always sit at the very top of the
    // Step-1 file picker (above the auto-surfaced recents) so a go-to document is one click away.
    public List<string> PinnedFiles { get; set; } = new();

    // Editor (Markdown source) font size in px. Zoomable at runtime (A+/A- buttons, Ctrl+wheel)
    // and persisted so the editor comes back at the size the user last chose. 13 matches the XAML default.
    public double EditorFontSize { get; set; } = 13;

    // Live-preview zoom factor (1.0 = 100%). Settable via the preview zoom buttons and the browser's
    // native Ctrl+wheel inside the WebView; persisted so the preview opens at the last zoom.
    public double PreviewZoom { get; set; } = 1.0;

    // Whether the Markdown editor soft-wraps long lines. When off, the editor scrolls horizontally
    // and shows a line-number gutter (wrapping would break line-number alignment). Persisted.
    public bool EditorWordWrap { get; set; } = true;

    // Looking Glass portal mode (ISS-004): fuses editor + preview into one canvas. The rendered
    // preview is the default surface; clicking it opens a "portal" aperture that reveals the
    // editable Markdown source behind the preview through a fog-of-war blur, with a glowing
    // cursor ring, iris animation and whir. Preview-only; never affects an export. Persisted.
    public bool LookingGlassMode { get; set; }

    // Portal reveal scope (ISS-004): how much of the Markdown source a portal reveals, 0..100.
    // 0 = tight focus (~3 clear lines with a blurred falloff back to the preview); 100 = the
    // full section/document is visible the moment the caret lands. Maps to the portal aperture
    // radius. Persisted.
    public int PortalRevealScope { get; set; } = 45;

    // Portal shape (ISS-004): "circle" spotlight, or full-width "focus" reading bands in three
    // heights ("focus1".."focus3" — skinny to tall). The reveal slider acts as the size dial for
    // whichever shape is active. Persisted.
    public string PortalShape { get; set; } = "circle";

    // Default export format across the app UI and the browser extension (ISS-019): "docx",
    // "pdf", "pptx" or "epub". Word is the default per user request.
    public string DefaultExportFormat { get; set; } = "docx";

    public bool UnlimitedHeight { get; set; } = true;
    public bool A4FixedWidth { get; set; } = true;

    public int AmbiguityMode { get; set; } = 1; // 0=AlwaysAsk, 1=UseDefault, 2=RememberChoices
    public List<AmbiguityPreference> AmbiguityPreferences { get; set; } = new();

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

    // What to do when a diagram is too big to fit a printed page in ShapeForge mode:
    //   0 = Ask — prompt at export time (the "call to user")
    //   1 = Exact — keep mermaid's exact layout/order; document opens in Word's Web Layout view
    //   2 = Reflow — re-wrap and re-order the diagram to fit the page width (fully printable)
    //   3 = MultiPageVertical — reflow to page width, split into page-height bands that span
    //       multiple pages downward in Print Layout (never Web Layout)
    //   4 = Grid — set document page size to N× normal (poster page); diagram at full scale,
    //       Web Layout view.  DiagramGridSize controls N (2 = 2×2, 3 = 3×3).
    //   5 = ShrinkToFit — scale uniformly below the 75% floor (down to 30% minimum) to squeeze
    //       the diagram onto one page in Print Layout.
    public int OversizedDiagramMode { get; set; } = 1;

    // Grid size for OversizedDiagramMode 4 (Grid).  2 = 2×2 poster (4 pages), 3 = 3×3 (9 pages).
    public int DiagramGridSize { get; set; } = 2;

    // Use native Word connection sites (Smart Connectors) instead of static lines.
    // When enabled, lines stay glued to shapes when dragged in Word.
    public bool SmartConnectors { get; set; } = true;

    // Fallback connector styles when not explicitly defined in the diagram
    public string ConnectorRouting { get; set; } = "default"; // "default", "straight", "elbow", "curved"
    public string ConnectorArrowhead { get; set; } = "default"; // "default", "triangle", "open", "diamond", "oval", "stealth", "none"

    // First-run guided tour: shown once automatically, replayable from the ⋯ menu.
    public bool HasSeenWelcome { get; set; }

    // Total app launches, for the one-time "enjoying it? there's a tip jar in the ⋯ menu" nudge
    // shown on the third launch (three launches = they're getting value from it).
    public int LaunchCount { get; set; }
    public bool HasSeenCoffeeReminder { get; set; }

    // Branding kit (Pro): make exports look like YOUR documents, not converted markdown.
    public bool BrandCoverPage { get; set; }          // title cover page (logo, title, date)
    public string BrandLogoPath { get; set; } = "";   // PNG/JPEG shown on the cover
    public string BrandFontFamily { get; set; } = ""; // "" = default (Calibri)

    // Typography (Task 16): preset id ("System", "Serif", "Sans-Serif", "Monospace",
    // "Dyslexic-friendly" — see FontManagerService) applied to rendered documents, plus an optional
    // custom TTF/OTF that is embedded into the output via @font-face.
    public string FontPreset { get; set; } = "System";
    public string CustomFontPath { get; set; } = "";

    // PDF security (Task 18): optional password protection + access control applied to the PDF after
    // export (see PdfSecurityService). The "allow" toggles default to true so an enabled-but-unconfigured
    // policy still permits everything until the user restricts it.
    public bool PdfEncrypt { get; set; }
    public string PdfUserPassword { get; set; } = "";
    public string PdfOwnerPassword { get; set; } = "";
    public bool PdfAllowPrinting { get; set; } = true;
    public bool PdfAllowCopying { get; set; } = true;
    public bool PdfAllowModifying { get; set; } = true;

    // Export extras
    public bool IncludeToc { get; set; }
    public bool ShowWordCount { get; set; } = true;
    public bool ShowAttribution { get; set; } = true;
    public bool NoEmoji { get; set; } // strip all emoji from preview + every export format
    public int DashMode { get; set; } // em-dash handling: 0 keep, 1 hyphen, 2 spaced, 3 custom
    public string DashCustom { get; set; } = ""; // replacement text when DashMode == 3

    // DOCX page chrome (both opt-in; a converted chat should look like a clean document, not a
    // framed certificate under revision tracking). Default off = no page border, no Track Changes.
    public bool PageBorder { get; set; }    // draw a full page frame in the theme border color
    public bool TrackChanges { get; set; }  // turn on Word revision tracking in the exported file

    // Formatting personalization (structure, not cleanup)
    public int HeadingShift { get; set; } // -5..+5: promote (-) or demote (+) every heading
    public int BoldMode { get; set; }     // 0 keep, 1 remove, 2 to italic
    public int ItalicMode { get; set; }   // 0 keep, 1 remove

    // Content language + text direction of the current document (BCP-47 tag + "ltr"/"rtl"), set from
    // the source page's metadata on ingest so RTL replies render right-to-left. Session-only in
    // practice (rewritten on each ingest, defaults reapplied for plain content); "" = the render
    // default (no explicit lang, ltr). Emitted onto the <html> element by MarkdownHtmlService.
    public string ContentLanguage { get; set; } = "";
    public string ContentDirection { get; set; } = "";

    public AppSettings()
    {
        ApiEnabled = true;
    }

    // Local REST API
    public bool ApiEnabled { get; set; }
    public int ApiPort { get; set; } = 47821;

    // Advanced mode reveals power-user styling options (cleanup + formatting) in the Style panel.
    public bool AdvancedMode { get; set; }

    // Pro mode skips the interactive pickers: Insert ▸ Image drops the raw markdown placeholder
    // directly (the classic one-keystroke behavior) instead of opening the drag & drop / URL modal.
    public bool ProMode { get; set; }

    // Security: Specific extension ID allowed to call the local API (resolves SAST warning)
    public string AllowedExtensionId { get; set; } = "";

    // Cloud storage auto-publish (Task 9): mirror every export into a cloud drive. The provider id
    // selects a detected local sync folder ("onedrive"/"googledrive"/"dropbox"/"box"/"icloud") that
    // the provider's own desktop client then syncs, or "webdav" for an endpoint-driven HTTP PUT.
    public bool CloudAutoPublish { get; set; }
    public string CloudProviderId { get; set; } = "";
    public string CloudSubfolder { get; set; } = "Marksmith";
    public string WebDavEndpoint { get; set; } = "";
    public string WebDavUser { get; set; } = "";
    public string WebDavToken { get; set; } = "";

    // PDF header / footer engine (Task 10): per-page header and footer template strings. Tokens:
    // {title} (document title), {page} (current page no.), {pages} (total pages), {date} (print date).
    // Chromium fills {page}/{pages}/{date} per page at print time; {title} is embedded literally.
    // Both default to empty so existing edge-to-edge zero-margin PDFs are unchanged until opted in.
    public string PdfHeaderTemplate { get; set; } = "";
    public string PdfFooterTemplate { get; set; } = "";
    // Where the page-number chrome sits: "None" (off), "BottomRight", "BottomCenter" or "TopRight".
    // When not "None" and the matching template is empty, a default "Page {page} of {pages}" is used.
    public string PdfPageNumberPosition { get; set; } = "None";

    // Auto-updater (Task 13): poll the GitHub Releases feed for a newer version once at startup and
    // surface an in-app notification when one is found. The manual "Check for updates" button in
    // Settings -> About always works regardless of this toggle. Default on.
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    // Returns a copy with any non-null override fields applied — used so an API/extension caller can
    // export with its own output profile without mutating the app's persistent settings.
    public AppSettings CloneWith(OutputOverride? o)
    {
        var s = (AppSettings)MemberwiseClone();
        if (o is null) return s;
        if (o.Theme is not null) s.Theme = o.Theme;
        if (o.ThemeLightInfluence is { } tli) s.ThemeLightInfluence = tli;
        if (o.ContentWidth is { } cw) s.ContentWidth = cw;
        if (o.A4FixedWidth is { } a4) s.A4FixedWidth = a4;
        if (o.UnlimitedHeight is { } uh) s.UnlimitedHeight = uh;
        if (o.IncludeToc is { } toc) s.IncludeToc = toc;
        if (o.ShowWordCount is { } swc) s.ShowWordCount = swc;
        if (o.ShowAttribution is { } sa) s.ShowAttribution = sa;
        if (o.NoEmoji is { } ne) s.NoEmoji = ne;
        if (o.DashMode is { } dm) s.DashMode = dm;
        if (o.DashCustom is not null) s.DashCustom = o.DashCustom;
        if (o.HeadingShift is { } hs) s.HeadingShift = hs;
        if (o.BoldMode is { } bm) s.BoldMode = bm;
        if (o.ItalicMode is { } im) s.ItalicMode = im;
        if (o.NormalizeLlm is { } nl) s.NormalizeLlm = nl;
        if (o.MermaidDocxMode is { } mm) s.MermaidDocxMode = mm;
        if (o.OversizedDiagramMode is { } odm) s.OversizedDiagramMode = odm;
        if (o.DiagramGridSize is { } dgs) s.DiagramGridSize = dgs;
        if (o.SmartConnectors is { } sc) s.SmartConnectors = sc;
        if (o.ConnectorRouting is not null) s.ConnectorRouting = o.ConnectorRouting;
        if (o.ConnectorArrowhead is not null) s.ConnectorArrowhead = o.ConnectorArrowhead;
        if (o.BrandCoverPage is { } bcp) s.BrandCoverPage = bcp;
        if (!string.IsNullOrWhiteSpace(o.OutputFolder)) s.OutputFolder = o.OutputFolder;
        if (!string.IsNullOrWhiteSpace(o.SourceFontFamily)) s.BrandFontFamily = o.SourceFontFamily;
        if (!string.IsNullOrWhiteSpace(o.SourceLanguage)) s.ContentLanguage = o.SourceLanguage;
        if (!string.IsNullOrWhiteSpace(o.SourceDirection)) s.ContentDirection = o.SourceDirection;
        s.AmbiguityPreferences = new List<AmbiguityPreference>(AmbiguityPreferences);
        return s;
    }

    public void UpdateFrom(AppSettings? other)
    {
        if (other is null) return;
        TargetFormat = other.TargetFormat;
        Theme = other.Theme;
        ThemeLightInfluence = other.ThemeLightInfluence;
        ContentWidth = other.ContentWidth;
        MermaidEnabled = other.MermaidEnabled;
        OutputFolder = other.OutputFolder;
        FileNameTemplate = other.FileNameTemplate;
        FavoriteThemes = new List<string>(other.FavoriteThemes);
        PinnedFiles = new List<string>(other.PinnedFiles);
        EditorFontSize = other.EditorFontSize;
        PreviewZoom = other.PreviewZoom;
        EditorWordWrap = other.EditorWordWrap;
        LookingGlassMode = other.LookingGlassMode;
        PortalRevealScope = other.PortalRevealScope;
        PortalShape = other.PortalShape;
        DefaultExportFormat = other.DefaultExportFormat;
        UnlimitedHeight = other.UnlimitedHeight;
        A4FixedWidth = other.A4FixedWidth;
        NormalizeLlm = other.NormalizeLlm;
        AutoClipboardIngest = other.AutoClipboardIngest;
        WatchFolderEnabled = other.WatchFolderEnabled;
        WatchFolder = other.WatchFolder;
        WatchFolderAutoConvert = other.WatchFolderAutoConvert;
        MinimizeToTray = other.MinimizeToTray;
        AutoConvertIngests = other.AutoConvertIngests;
        AppendToRunningDoc = other.AppendToRunningDoc;
        RunningDocPath = other.RunningDocPath;
        ShowExtensionTip = other.ShowExtensionTip;
        MermaidDocxMode = other.MermaidDocxMode;
        OversizedDiagramMode = other.OversizedDiagramMode;
        DiagramGridSize = other.DiagramGridSize;
        SmartConnectors = other.SmartConnectors;
        ConnectorRouting = other.ConnectorRouting;
        ConnectorArrowhead = other.ConnectorArrowhead;
        HasSeenWelcome = other.HasSeenWelcome;
        LaunchCount = other.LaunchCount;
        HasSeenCoffeeReminder = other.HasSeenCoffeeReminder;
        BrandCoverPage = other.BrandCoverPage;
        BrandLogoPath = other.BrandLogoPath;
        BrandFontFamily = other.BrandFontFamily;
        FontPreset = other.FontPreset;
        CustomFontPath = other.CustomFontPath;
        PdfEncrypt = other.PdfEncrypt;
        PdfUserPassword = other.PdfUserPassword;
        PdfOwnerPassword = other.PdfOwnerPassword;
        PdfAllowPrinting = other.PdfAllowPrinting;
        PdfAllowCopying = other.PdfAllowCopying;
        PdfAllowModifying = other.PdfAllowModifying;
        IncludeToc = other.IncludeToc;
        ShowWordCount = other.ShowWordCount;
        ShowAttribution = other.ShowAttribution;
        PageBorder = other.PageBorder;
        TrackChanges = other.TrackChanges;
        NoEmoji = other.NoEmoji;
        DashMode = other.DashMode;
        DashCustom = other.DashCustom;
        HeadingShift = other.HeadingShift;
        BoldMode = other.BoldMode;
        ItalicMode = other.ItalicMode;
        ContentLanguage = other.ContentLanguage;
        ContentDirection = other.ContentDirection;
        ApiEnabled = other.ApiEnabled;
        ApiPort = other.ApiPort;
        AdvancedMode = other.AdvancedMode;
        ProMode = other.ProMode;
        AllowedExtensionId = other.AllowedExtensionId;
        CloudAutoPublish = other.CloudAutoPublish;
        CloudProviderId = other.CloudProviderId;
        CloudSubfolder = other.CloudSubfolder;
        WebDavEndpoint = other.WebDavEndpoint;
        WebDavUser = other.WebDavUser;
        WebDavToken = other.WebDavToken;
        PdfHeaderTemplate = other.PdfHeaderTemplate;
        PdfFooterTemplate = other.PdfFooterTemplate;
        PdfPageNumberPosition = other.PdfPageNumberPosition;
        CheckForUpdatesOnStartup = other.CheckForUpdatesOnStartup;
        AmbiguityMode = other.AmbiguityMode;
        AmbiguityPreferences = new List<AmbiguityPreference>(other.AmbiguityPreferences);
    }
}
