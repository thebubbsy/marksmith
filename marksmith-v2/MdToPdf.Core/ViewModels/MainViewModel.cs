using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdToPdf.Models;
using MdToPdf.Services;

namespace MdToPdf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = AppServices.Settings;
    private readonly RecentFilesService _recentFilesService = AppServices.RecentFiles;
    private readonly MarkdownHtmlService _markdownHtml = AppServices.MarkdownHtml;
    private readonly ThemeCatalog _themes = AppServices.Themes;
    private readonly PdfExportService _pdfExport = new();
    private readonly DocxExportService _docxExport = new();
    private readonly PptxExportService _pptxExport = new();
    private readonly EpubExportService _epubExport = new();
    private readonly MermaidHarvestService _mermaidHarvest = new();

    private CancellationTokenSource? _conversionCts;

    // Set once by each UI project's main window at startup. Replaces the old WinUI-only
    // `App.MainAppWindow as MainWindow` downcast — the ViewModel now reaches the platform's web
    // renderer and native prompts only through these portable seams (see IWebRenderHost).
    public IWebRenderHost? Host { get; set; }
    public IUiPrompts? Prompts { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPdfFormat))]
    [NotifyPropertyChangedFor(nameof(IsDocxFormat))]
    private string _targetFormat = "pdf";
    public bool IsPdfFormat => TargetFormat == "pdf";
    public bool IsDocxFormat => TargetFormat == "docx";
    public int TargetFormatIndex
    {
        get => TargetFormat == "docx" ? 1 : 0;
        set { TargetFormat = value == 1 ? "docx" : "pdf"; OnPropertyChanged(); OnPropertyChanged(nameof(IsPdfFormat)); OnPropertyChanged(nameof(IsDocxFormat)); }
    }

    [RelayCommand]
    private void SetTargetFormatPdf() => TargetFormat = "pdf";

    [RelayCommand]
    private void SetTargetFormatDocx() => TargetFormat = "docx";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMarkdown))]
    private string _inputFilePath = string.Empty;

    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _fileNameTemplate = "{title}";
    [ObservableProperty] private string _selectedThemeName;
    [ObservableProperty] private bool _isCurrentThemeFavorite;
    [ObservableProperty] private bool _isCurrentFilePinned;
    [ObservableProperty] private bool _themeLightInfluence;
    [ObservableProperty] private int _contentWidth;
    [ObservableProperty] private bool _a4FixedWidth;
    [ObservableProperty] private bool _unlimitedHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMarkdown))]
    private bool _usePasteSource;

    [ObservableProperty]
    private bool _normalizeLlm;

    [ObservableProperty]
    private bool _autoClipboardIngest;

    [ObservableProperty]
    private bool _watchFolderEnabled;

    [ObservableProperty]
    private string _watchFolder = string.Empty;

    [ObservableProperty]
    private bool _watchFolderAutoConvert;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _autoConvertIngests;

    [ObservableProperty] private bool _appendToRunningDoc;
    [ObservableProperty] private string _runningDocPath = "";
    [ObservableProperty] private bool _showExtensionTip;
    [ObservableProperty] private bool _includeToc;
    [ObservableProperty] private bool _showWordCount;
    [ObservableProperty] private int _mermaidDocxMode = 1; // 0 Snapshot picture, 1 ShapeForge shapes
    [ObservableProperty] private int _oversizedDiagramMode; // 0 Ask, 1 Exact/WebLayout, 2 Reflow
    [ObservableProperty] private bool _brandCoverPage;
    [ObservableProperty] private string _brandLogoPath = "";
    [ObservableProperty] private string _brandFontFamily = "";
    [ObservableProperty] private bool _showAttribution;
    [ObservableProperty] private bool _noEmoji;
    [ObservableProperty] private int _dashMode;
    [ObservableProperty] private string _dashCustom;
    [ObservableProperty] private int _headingShift;
    [ObservableProperty] private int _boldMode;
    [ObservableProperty] private int _italicMode;
    [ObservableProperty] private bool _advancedMode;
    [ObservableProperty] private bool _proMode;
    [ObservableProperty] private bool _apiEnabled;
    [ObservableProperty] private int _apiPort;
    [ObservableProperty] private string _allowedExtensionId = string.Empty;
    [ObservableProperty] private string _detectedSourceText = string.Empty;

    // Cloud storage auto-publish (Task 9).
    [ObservableProperty] private bool _cloudAutoPublish;
    [ObservableProperty] private string _cloudProviderId = "";
    [ObservableProperty] private string _cloudSubfolder = "Marksmith";
    [ObservableProperty] private string _webDavEndpoint = "";
    [ObservableProperty] private string _webDavUser = "";
    [ObservableProperty] private string _webDavToken = "";

    // PDF header / footer engine (Task 10).
    [ObservableProperty] private string _pdfHeaderTemplate = "";
    [ObservableProperty] private string _pdfFooterTemplate = "";
    [ObservableProperty] private string _pdfPageNumberPosition = "None";

    // Typography preset (Task 16) — id from FontManagerService.Presets ("System" default).
    [ObservableProperty] private string _fontPreset = "System";

    // Cloud drives detected on this machine (Task 9); feeds the Settings ▸ Automation ▸ Cloud Sync
    // picker. Refreshed on startup and via RefreshCloudProviders() (the "Re-scan" button).
    public ObservableCollection<Models.CloudProviderInfo> CloudProviders { get; } = new();

    // The originating conversation's title (from source-page metadata on ingest), used as the
    // default export filename + document title. Empty for hand-typed / plain content.
    [ObservableProperty] private string _suggestedTitle = string.Empty;

    [ObservableProperty] private bool _hasMermaidDiagram;
    [ObservableProperty] private bool _hasOversizedDiagram;
    [ObservableProperty] private string _diagramHintText = string.Empty;
    [ObservableProperty] private StatusSeverity _diagramHintSeverity = StatusSeverity.Informational;

    // Classification of the last ingested document; feeds the preview badge and export attribution strip.
    public LlmClassification? LastClassification { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WordCountText))]
    [NotifyPropertyChangedFor(nameof(DocumentStats))]
    [NotifyPropertyChangedFor(nameof(DocumentStatsDetail))]
    [NotifyPropertyChangedFor(nameof(CurrentMarkdown))]
    private string _pastedMarkdown = string.Empty;

    private string _cachedFileMarkdown = string.Empty;
    private CancellationTokenSource? _fileReadCts;

    partial void OnInputFilePathChanged(string value)
    {
        _fileReadCts?.Cancel();
        _fileReadCts?.Dispose();
        _fileReadCts = new CancellationTokenSource();
        var token = _fileReadCts.Token;

        // Reflect whether the newly-selected file is one the user has pinned.
        IsCurrentFilePinned = !string.IsNullOrWhiteSpace(value)
            && _settingsService.Current.PinnedFiles.Contains(Path.GetFullPath(value), Services.PathEquality.Comparer);

        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            var syncContext = SynchronizationContext.Current;
            Task.Run(async () =>
            {
                try
                {
                    var text = await File.ReadAllTextAsync(value, token);
                    if (!token.IsCancellationRequested)
                    {
                        _cachedFileMarkdown = text;
                        if (syncContext != null)
                        {
                            syncContext.Post(_ =>
                            {
                                if (!token.IsCancellationRequested)
                                {
                                    PastedMarkdown = text;
                                    OnPropertyChanged(nameof(CurrentMarkdown));
                                }
                            }, null);
                        }
                        else
                        {
                            PastedMarkdown = text;
                            OnPropertyChanged(nameof(CurrentMarkdown));
                        }
                    }
                }
                catch
                {
                    if (!token.IsCancellationRequested)
                    {
                        _cachedFileMarkdown = string.Empty;
                        OnPropertyChanged(nameof(CurrentMarkdown));
                    }
                }
            });
        }
        else
        {
            _cachedFileMarkdown = string.Empty;
            OnPropertyChanged(nameof(CurrentMarkdown));
        }
    }

    public string CurrentMarkdown
    {
        get
        {
            if (UsePasteSource) return PastedMarkdown;
            return _cachedFileMarkdown;
        }
        set
        {
            PastedMarkdown = value;
            if (!UsePasteSource) UsePasteSource = true;
        }
    }

    public string WordCountText => DocumentStats.SummaryText;

    // Full breakdown (words, characters, reading time, headings/code/tables/images/links/diagrams)
    // for the status-bar tooltip. Recomputed lazily off the current markdown.
    public string DocumentStatsDetail => DocumentStats.DetailText;

    // Reading-time / structure metrics for the current document. Derived from PastedMarkdown, so it
    // refreshes with the same change notification the word count already fired on.
    public Services.DocumentStats DocumentStats => Services.DocumentStatsService.Analyze(PastedMarkdown);

    partial void OnPastedMarkdownChanged(string value)
    {
        HasMermaidDiagram = value?.Contains("```mermaid", StringComparison.Ordinal) == true;
        if (HasMermaidDiagram)
        {
            HasOversizedDiagram = Services.MermaidDocxRenderer.AnyWouldOverflow(value!);
            if (HasOversizedDiagram)
            {
                DiagramHintText = "Oversized Diagram Detected! A diagram is too large to fit on a standard page. Please review your layout strategy below.";
                DiagramHintSeverity = StatusSeverity.Warning;
            }
            else
            {
                DiagramHintText = "Your diagrams fit on a standard page. You may still apply a compression strategy below if you wish to shrink them further.";
                DiagramHintSeverity = StatusSeverity.Informational;
            }
        }
        else
        {
            HasOversizedDiagram = false;
        }
    }
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private StatusSeverity _statusSeverity = StatusSeverity.Informational;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;
    [ObservableProperty] private bool _isDebugModeEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _lastOutputPath;

    public bool IsNotBusy => !IsBusy;
    public bool HasOutput => !string.IsNullOrEmpty(LastOutputPath);

    // Licensing (drives the paywall UI). Backed by AppServices.License; kept in sync via its Changed event.
    public bool IsPro => AppServices.License.IsPro;
    public bool IsFree => !AppServices.License.IsPro;
    public string EditionStatus => AppServices.License.State.Status ?? "Free";
    public bool ShowProBadge => IsFree;

    private void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(IsPro));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(EditionStatus));
        OnPropertyChanged(nameof(ShowProBadge));
    }

    private readonly PresetsService _presetsService = new();
    public ObservableCollection<ExportPreset> Presets { get; } = new();

    public ObservableCollection<string> ThemeNames { get; }
    public ObservableCollection<string> RecentFiles { get; } = new();
    public ObservableCollection<Services.MarkdownFileEntry> MarkdownFiles { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();

    [ObservableProperty] private bool _isDiscoveringFiles;

    // Scan Downloads/Documents/Desktop/OneDrive for real .md files (newest first), pinned/opened
    // files kept on top. Called from the UI thread so the await resumes there to update the list.
    public async Task RefreshMarkdownFilesAsync()
    {
        if (IsDiscoveringFiles) return;
        IsDiscoveringFiles = true;
        try
        {
            // Explicitly pinned files lead, then the auto-tracked recents; DiscoverAsync keeps this
            // whole set above the disk-discovered files.
            var pinned = _settingsService.Current.PinnedFiles;
            var combined = pinned
                .Concat(_recentFilesService.Load().Where(r => !pinned.Contains(r, Services.PathEquality.Comparer)))
                .ToList();
            var entries = await Services.MarkdownDiscoveryService.DiscoverAsync(combined);
            MarkdownFiles.Clear();
            foreach (var e in entries)
            {
                // Re-mark explicit pins with a 📌 so they read as user-pinned, distinct from the ★
                // the discovery service stamps on plain recently-opened files.
                if (pinned.Contains(e.Path, Services.PathEquality.Comparer))
                    MarkdownFiles.Add(e with { Detail = "📌 " + e.Detail.TrimStart('★', ' ') });
                else
                    MarkdownFiles.Add(e);
            }
        }
        finally { IsDiscoveringFiles = false; }
    }

    // Pins/unpins the currently selected file. Pinned files are persisted and always floated to the
    // top of the Step-1 picker, so a go-to document stays one click away across sessions.
    public void TogglePinCurrentFile()
    {
        if (string.IsNullOrWhiteSpace(InputFilePath)) return;
        string full;
        try { full = Path.GetFullPath(InputFilePath); } catch { return; }

        var pinned = _settingsService.Current.PinnedFiles;
        if (pinned.Contains(full, Services.PathEquality.Comparer))
            pinned.RemoveAll(f => string.Equals(f, full, Services.PathEquality.Comparison));
        else
            pinned.Insert(0, full);
        IsCurrentFilePinned = pinned.Contains(full, Services.PathEquality.Comparer);
        SaveSettingsDebounced();

        _ = RefreshMarkdownFilesAsync();
    }

    public void RecordExport(string kind, string outputPath, string markdown, long durationMs = 0)
    {
        long sizeBytes = 0;
        try { if (File.Exists(outputPath)) sizeBytes = new FileInfo(outputPath).Length; } catch { /* best-effort */ }

        var entry = new HistoryEntry
        {
            Timestamp = DateTime.Now,
            SourceLabel = UsePasteSource ? "pasted" : Path.GetFileName(InputFilePath),
            Detected = LastClassification?.SourceName ?? "Markdown",
            Theme = SelectedThemeName,
            OutputPath = outputPath,
            Kind = kind,
            DocumentTitle = HistoryEntry.ExtractTitle(markdown),
            DurationMs = durationMs,
            OutputSizeBytes = sizeBytes,
        };
        History.Insert(0, entry);
        AppServices.History.Add(entry);
    }

    // Raised after a manual, user-initiated export finishes (kind + output path). The WinUI layer
    // subscribes to surface a Windows toast. Auto-ingest/watch-folder exports raise their own toast
    // through the ExportCoordinator's callback, and batch/API flows stay silent, so this is only
    // fired from the single-format ConvertTo*Async paths (suppressed while ExportAllAsync batches
    // them, which raises one combined notification instead).
    public event Action<string, string>? ExportCompleted;
    private bool _suppressExportToasts;
    private void RaiseExportCompleted(string kind, string path)
    {
        if (!_suppressExportToasts) ExportCompleted?.Invoke(kind, path);
    }

    public MainViewModel()
    {
        var settings = _settingsService.Current;
        _outputFolder = settings.OutputFolder;
        _fileNameTemplate = string.IsNullOrWhiteSpace(settings.FileNameTemplate) ? "{title}" : settings.FileNameTemplate;
        _selectedThemeName = settings.Theme;
        _themeLightInfluence = settings.ThemeLightInfluence;
        _contentWidth = settings.ContentWidth;
        _a4FixedWidth = settings.A4FixedWidth;
        _unlimitedHeight = settings.UnlimitedHeight;
        _normalizeLlm = settings.NormalizeLlm;
        _autoClipboardIngest = settings.AutoClipboardIngest;
        _watchFolderEnabled = settings.WatchFolderEnabled;
        _watchFolder = settings.WatchFolder;
        _watchFolderAutoConvert = settings.WatchFolderAutoConvert;
        _minimizeToTray = settings.MinimizeToTray;
        _autoConvertIngests = settings.AutoConvertIngests;
        _appendToRunningDoc = settings.AppendToRunningDoc;
        _runningDocPath = settings.RunningDocPath;
        _showExtensionTip = settings.ShowExtensionTip;
        foreach (var h in AppServices.History.All) History.Add(h);
        _includeToc = settings.IncludeToc;
        _mermaidDocxMode = settings.MermaidDocxMode;
        _oversizedDiagramMode = settings.OversizedDiagramMode;
        _brandCoverPage = settings.BrandCoverPage;
        _brandLogoPath = settings.BrandLogoPath;
        _brandFontFamily = settings.BrandFontFamily;
        _showAttribution = settings.ShowAttribution;
        _noEmoji = settings.NoEmoji;
        _dashMode = settings.DashMode;
        _dashCustom = settings.DashCustom;
        _headingShift = settings.HeadingShift;
        _boldMode = settings.BoldMode;
        _italicMode = settings.ItalicMode;
        _advancedMode = settings.AdvancedMode;
        _proMode = settings.ProMode;
        _apiEnabled = settings.ApiEnabled;
        _apiPort = settings.ApiPort;
        _allowedExtensionId = settings.AllowedExtensionId;
        _cloudAutoPublish = settings.CloudAutoPublish;
        _cloudProviderId = settings.CloudProviderId;
        _cloudSubfolder = settings.CloudSubfolder;
        _webDavEndpoint = settings.WebDavEndpoint;
        _webDavUser = settings.WebDavUser;
        _webDavToken = settings.WebDavToken;
        _pdfHeaderTemplate = settings.PdfHeaderTemplate;
        _pdfFooterTemplate = settings.PdfFooterTemplate;
        _pdfPageNumberPosition = settings.PdfPageNumberPosition;
        _fontPreset = settings.FontPreset;
        _targetFormat = settings.TargetFormat;

        RefreshCloudProviders();

        ThemeNames = new ObservableCollection<string>(BuildOrderedThemeNames(settings.FavoriteThemes));
        _isCurrentThemeFavorite = settings.FavoriteThemes.Contains(_selectedThemeName);
        foreach (var f in _recentFilesService.Load()) RecentFiles.Add(f);

        AppServices.License.Changed += OnLicenseChanged;
    }

    private CancellationTokenSource? _saveSettingsCts;

    private void SaveSettingsDebounced()
    {
        _saveSettingsCts?.Cancel();
        _saveSettingsCts?.Dispose();
        _saveSettingsCts = new CancellationTokenSource();
        var token = _saveSettingsCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                {
                    _settingsService.Save();
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    // Re-scans the machine for cloud-drive sync folders (Task 9) and refreshes the picker list.
    public void RefreshCloudProviders()
    {
        CloudProviders.Clear();
        foreach (var p in AppServices.CloudStorage.DetectProviders()) CloudProviders.Add(p);
    }

    partial void OnOutputFolderChanged(string value) { _settingsService.Current.OutputFolder = value; SaveSettingsDebounced(); }
    partial void OnFileNameTemplateChanged(string value) { _settingsService.Current.FileNameTemplate = value; SaveSettingsDebounced(); }
    partial void OnSelectedThemeNameChanged(string value) {
        _settingsService.Current.Theme = value;
        IsCurrentThemeFavorite = _settingsService.Current.FavoriteThemes.Contains(value);
        SaveSettingsDebounced();
    }
    partial void OnThemeLightInfluenceChanged(bool value) { _settingsService.Current.ThemeLightInfluence = value; SaveSettingsDebounced(); }
    partial void OnTargetFormatChanged(string value) { 
        _settingsService.Current.TargetFormat = value; 
        SaveSettingsDebounced(); 
        OnPropertyChanged(nameof(IsPdfFormat));
        OnPropertyChanged(nameof(IsDocxFormat));
        OnPropertyChanged(nameof(TargetFormatIndex));
    }
    partial void OnContentWidthChanged(int value) { _settingsService.Current.ContentWidth = value; SaveSettingsDebounced(); }
    partial void OnA4FixedWidthChanged(bool value) { 
        _settingsService.Current.A4FixedWidth = value; 
        SaveSettingsDebounced(); 
        if (value) ContentWidth = 794;
    }
    partial void OnUnlimitedHeightChanged(bool value) { _settingsService.Current.UnlimitedHeight = value; SaveSettingsDebounced(); }
    partial void OnNormalizeLlmChanged(bool value) { _settingsService.Current.NormalizeLlm = value; SaveSettingsDebounced(); }
    partial void OnAutoClipboardIngestChanged(bool value) { _settingsService.Current.AutoClipboardIngest = value; SaveSettingsDebounced(); }
    partial void OnWatchFolderEnabledChanged(bool value) { _settingsService.Current.WatchFolderEnabled = value; SaveSettingsDebounced(); }
    partial void OnWatchFolderChanged(string value) { _settingsService.Current.WatchFolder = value; SaveSettingsDebounced(); }
    partial void OnWatchFolderAutoConvertChanged(bool value) { _settingsService.Current.WatchFolderAutoConvert = value; SaveSettingsDebounced(); }
    partial void OnMinimizeToTrayChanged(bool value) { _settingsService.Current.MinimizeToTray = value; SaveSettingsDebounced(); }
    partial void OnAutoConvertIngestsChanged(bool value) { _settingsService.Current.AutoConvertIngests = value; SaveSettingsDebounced(); }
    partial void OnAppendToRunningDocChanged(bool value) { _settingsService.Current.AppendToRunningDoc = value; SaveSettingsDebounced(); }
    partial void OnRunningDocPathChanged(string value) { _settingsService.Current.RunningDocPath = value; SaveSettingsDebounced(); }
    partial void OnShowExtensionTipChanged(bool value) { _settingsService.Current.ShowExtensionTip = value; SaveSettingsDebounced(); }
    partial void OnIncludeTocChanged(bool value) { _settingsService.Current.IncludeToc = value; SaveSettingsDebounced(); }
    partial void OnMermaidDocxModeChanged(int value) { _settingsService.Current.MermaidDocxMode = value; SaveSettingsDebounced(); }
    partial void OnOversizedDiagramModeChanged(int value) { _settingsService.Current.OversizedDiagramMode = value; SaveSettingsDebounced(); }
    partial void OnBrandCoverPageChanged(bool value) { _settingsService.Current.BrandCoverPage = value; SaveSettingsDebounced(); }
    partial void OnBrandLogoPathChanged(string value) { _settingsService.Current.BrandLogoPath = value; SaveSettingsDebounced(); }
    partial void OnBrandFontFamilyChanged(string value) { _settingsService.Current.BrandFontFamily = value; SaveSettingsDebounced(); }
    partial void OnShowAttributionChanged(bool value) { _settingsService.Current.ShowAttribution = value; SaveSettingsDebounced(); }
    partial void OnNoEmojiChanged(bool value) { _settingsService.Current.NoEmoji = value; SaveSettingsDebounced(); }
    partial void OnDashModeChanged(int value) { _settingsService.Current.DashMode = value; SaveSettingsDebounced(); }
    partial void OnDashCustomChanged(string value) { _settingsService.Current.DashCustom = value; SaveSettingsDebounced(); }
    partial void OnHeadingShiftChanged(int value) { _settingsService.Current.HeadingShift = value; SaveSettingsDebounced(); }
    partial void OnBoldModeChanged(int value) { _settingsService.Current.BoldMode = value; SaveSettingsDebounced(); }
    partial void OnItalicModeChanged(int value) { _settingsService.Current.ItalicMode = value; SaveSettingsDebounced(); }
    partial void OnAdvancedModeChanged(bool value) { _settingsService.Current.AdvancedMode = value; SaveSettingsDebounced(); }
    partial void OnProModeChanged(bool value) { _settingsService.Current.ProMode = value; SaveSettingsDebounced(); }
    partial void OnApiEnabledChanged(bool value) { _settingsService.Current.ApiEnabled = value; SaveSettingsDebounced(); }
    partial void OnApiPortChanged(int value) { _settingsService.Current.ApiPort = value; SaveSettingsDebounced(); }
    partial void OnAllowedExtensionIdChanged(string value) { _settingsService.Current.AllowedExtensionId = value; SaveSettingsDebounced(); }
    partial void OnCloudAutoPublishChanged(bool value) { _settingsService.Current.CloudAutoPublish = value; SaveSettingsDebounced(); }
    partial void OnCloudProviderIdChanged(string value) { _settingsService.Current.CloudProviderId = value; SaveSettingsDebounced(); }
    partial void OnCloudSubfolderChanged(string value) { _settingsService.Current.CloudSubfolder = value; SaveSettingsDebounced(); }
    partial void OnWebDavEndpointChanged(string value) { _settingsService.Current.WebDavEndpoint = value; SaveSettingsDebounced(); }
    partial void OnWebDavUserChanged(string value) { _settingsService.Current.WebDavUser = value; SaveSettingsDebounced(); }
    partial void OnWebDavTokenChanged(string value) { _settingsService.Current.WebDavToken = value; SaveSettingsDebounced(); }
    partial void OnPdfHeaderTemplateChanged(string value) { _settingsService.Current.PdfHeaderTemplate = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnPdfFooterTemplateChanged(string value) { _settingsService.Current.PdfFooterTemplate = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnPdfPageNumberPositionChanged(string value) { _settingsService.Current.PdfPageNumberPosition = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnFontPresetChanged(string value) { _settingsService.Current.FontPreset = value; SaveSettingsDebounced(); }

    // Live preview of the page-number chrome with sample values (Task 10), so Settings shows what the
    // tokens expand to. Falls back to the default template when the matching band is empty.
    public string PdfFooterPreview
    {
        get
        {
            var pos = PdfPageNumberPosition ?? "None";
            var top = pos.StartsWith("Top", System.StringComparison.OrdinalIgnoreCase);
            var tpl = top ? PdfHeaderTemplate : PdfFooterTemplate;
            if (string.IsNullOrWhiteSpace(tpl) && !pos.Equals("None", System.StringComparison.OrdinalIgnoreCase))
                tpl = "Page {page} of {pages}";
            if (string.IsNullOrWhiteSpace(tpl)) return "(no header/footer)";
            return Services.PdfExportService.SubstituteTokens(tpl, "Document Title", 2, 10, System.DateTime.Now);
        }
    }

    public ThemeDefinition CurrentTheme => _themes.GetOrDefault(SelectedThemeName);

    // Pins/unpins the currently selected theme. Favorites are persisted and floated to the top of
    // the theme dropdown so a user's go-to palettes are always one glance away.
    public void ToggleFavoriteTheme()
    {
        if (string.IsNullOrWhiteSpace(SelectedThemeName)) return;
        var favorites = _settingsService.Current.FavoriteThemes;
        if (favorites.Contains(SelectedThemeName)) favorites.Remove(SelectedThemeName);
        else favorites.Add(SelectedThemeName);
        IsCurrentThemeFavorite = favorites.Contains(SelectedThemeName);

        // Rebuild the ordered list without disturbing the current selection.
        var ordered = BuildOrderedThemeNames(favorites);
        ThemeNames.Clear();
        foreach (var name in ordered) ThemeNames.Add(name);
        SaveSettingsDebounced();
    }

    private List<string> BuildOrderedThemeNames(IEnumerable<string> favorites)
    {
        var fav = new HashSet<string>(favorites, StringComparer.OrdinalIgnoreCase);
        return _themes.All.Select(t => t.Name)
            .OrderByDescending(n => fav.Contains(n))
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---- Export presets ----

    public void LoadPresets()
    {
        Presets.Clear();
        foreach (var p in _presetsService.Load()) Presets.Add(p);
    }

    public void SavePreset(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return;
        var preset = ExportPreset.Capture(name, _settingsService.Current);
        for (int i = Presets.Count - 1; i >= 0; i--)
            if (string.Equals(Presets[i].Name, name, StringComparison.OrdinalIgnoreCase)) Presets.RemoveAt(i);
        Presets.Insert(0, preset);
        _presetsService.Save(Presets);
    }

    public void DeletePreset(ExportPreset preset)
    {
        Presets.Remove(preset);
        _presetsService.Save(Presets);
    }

    // Apply through the VM's observable properties so the UI updates live, each change persists to
    // settings, and the preview refreshes — same as if the user set them by hand.
    public void ApplyPreset(ExportPreset p)
    {
        SelectedThemeName = p.Theme;
        ContentWidth = p.ContentWidth;
        A4FixedWidth = p.A4FixedWidth;
        UnlimitedHeight = p.UnlimitedHeight;
        IncludeToc = p.IncludeToc;
        ShowAttribution = p.ShowAttribution;
        NoEmoji = p.NoEmoji;
        DashMode = p.DashMode;
        DashCustom = p.DashCustom;
        HeadingShift = p.HeadingShift;
        BoldMode = p.BoldMode;
        ItalicMode = p.ItalicMode;
        MermaidDocxMode = p.MermaidDocxMode;
        OversizedDiagramMode = p.OversizedDiagramMode;
        BrandCoverPage = p.BrandCoverPage;
        BrandLogoPath = p.BrandLogoPath;
        BrandFontFamily = p.BrandFontFamily;
        StatusText = $"Applied preset: {p.Name}";
        StatusSeverity = StatusSeverity.Success;
    }

    // interactive: the live preview (enables the focused diagram viewer). PDF/export callers omit it.
    public string BuildPreviewHtml(string markdown, bool interactive = false) =>
        _markdownHtml.Render(markdown, _settingsService.Current, CurrentTheme, LastClassification, interactive);

    // Classification + normalization for content that did NOT arrive through IngestMarkdown —
    // manual paste and browsed/dropped files. Without this, the "Normalize AI formatting quirks"
    // toggle only worked for the automated ingest paths. Safe to call on already-ingested text:
    // normalization is idempotent, and the badge only updates when the new classification says
    // something stronger than what's already displayed (re-running on cleaned text scores lower
    // because most signals were just removed).
    public string PrepareMarkdown(string markdown)
    {
        var classification = AppServices.LlmSource.Classify(markdown);

        // Correctness repairs (copy-artifact removal, math rescue, matrix recovery) ALWAYS run —
        // they fix corruption, not style, so a broken matrix or leaked <thinking> tag is cleaned up
        // whether or not the "Normalize AI quirks" toggle is on. Neither is gated on recognizing a
        // specific vendor either: they apply to AI-ish text with no ChatGPT/Gemini/Claude tells at
        // all, and each is a no-op when its pattern doesn't match, so running on Generic text is safe.
        (markdown, _) = AppServices.LlmSource.RepairArtifacts(markdown, classification);
        if (NormalizeLlm)
            (markdown, _) = AppServices.LlmSource.NormalizeStyle(markdown, classification);

        // The source badge, though, only makes sense for a recognized vendor.
        if (classification.Source == LlmSource.Generic) return markdown;

        var better = LastClassification is null
            || classification.Source != LastClassification.Source
            || classification.Confidence > LastClassification.Confidence;
        if (better)
        {
            LastClassification = classification;
            DetectedSourceText = classification.AppliedFixes.Count > 0
                ? $"{classification.SourceName} · {classification.Confidence}% · {classification.AppliedFixes.Count} fixes"
                : $"{classification.SourceName} · {classification.Confidence}%";
        }
        return markdown;
    }

    // Entry point for all automated ingest paths (clipboard watcher, watched folder, REST API).
    // Classifies the text, optionally normalizes assistant-specific quirks, applies any source
    // metadata captured from the originating page (font, definitive source, model, title, language/
    // direction, brand accent -> theme), and loads it into the paste editor so the preview updates
    // immediately. `meta` is null for paths with no page context (a watched .md file).
    public void IngestMarkdown(string text, string origin, OutputOverride? meta = null)
    {
        // --- Apply page metadata BEFORE setting PastedMarkdown, so the single preview refresh that
        //     the PastedMarkdown setter triggers already reflects the font/theme/direction. ---

        // Font the reply was shown in -> live brand font (also drives the HTML preview/PDF now).
        if (!string.IsNullOrWhiteSpace(meta?.SourceFontFamily))
            BrandFontFamily = meta.SourceFontFamily;

        // Brand accent -> nearest built-in theme, so the export palette echoes the source.
        if (!string.IsNullOrWhiteSpace(meta?.SourceAccentColor) &&
            _themes.NearestByAccent(meta.SourceAccentColor) is { } themeName)
            SelectedThemeName = themeName;

        // Language + direction of the reply -> render as <html lang dir>. Session-only: rewritten on
        // every ingest (defaults reapplied for plain content) so RTL never sticks to a later doc.
        _settingsService.Current.ContentLanguage = meta?.SourceLanguage?.Trim() ?? "";
        _settingsService.Current.ContentDirection = meta?.SourceDirection?.Trim() ?? "";

        // Conversation title -> default export filename / document title.
        SuggestedTitle = meta?.SourceTitle?.Trim() ?? "";

        text ??= string.Empty;

        var classification = AppServices.LlmSource.Classify(text);

        // A definitive source id from the extension is ground truth — replace the content guess.
        if (LlmSourceService.ParseSourceId(meta?.SourceId) is { } reported)
        {
            classification = new LlmClassification
            {
                Source = reported,
                Confidence = 100,
                Signals = new List<string> { "reported by browser extension" },
                HasMath = classification.HasMath,
            };
        }
        if (!string.IsNullOrWhiteSpace(meta?.SourceModel))
            classification.Model = meta.SourceModel.Trim();

        // ISS-005: provider-specific dialect normalization, keyed off the definitive source id the
        // extension reports (DeepSeek escaped pipes, Perplexity [n] pips, quoted code fences, …).
        // Runs before the general artifact repair so its output is cleaned too. No-op for unknown ids.
        text = ProviderDialectNormalizer.Normalize(text, meta?.SourceId);

        // Correctness repairs always run; stylistic cleanup only when the toggle is on.
        (text, _) = AppServices.LlmSource.RepairArtifacts(text, classification);
        if (NormalizeLlm)
            (text, _) = AppServices.LlmSource.NormalizeStyle(text, classification);

        LastClassification = classification;
        DetectedSourceText = classification.Source == LlmSource.Generic
            ? $"Ingested from {origin}"
            : $"{classification.SourceDescription} · {classification.Confidence}% · {classification.AppliedFixes.Count} fixes";

        PastedMarkdown = text;
        UsePasteSource = true;
        StatusText = classification.Source == LlmSource.Generic
            ? $"Ingested Markdown from {origin}."
            : $"Ingested from {origin} — detected {classification.SourceDescription} formatting" +
              (classification.AppliedFixes.Count > 0 ? $", applied {classification.AppliedFixes.Count} fixes." : ".");
        StatusSeverity = StatusSeverity.Success;
    }

    public void IngestFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            IngestMarkdown(text, Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            StatusText = $"Could not ingest {path}: {ex.Message}";
            StatusSeverity = StatusSeverity.Error;
        }
    }

    [RelayCommand]
    private void LoadRecent(string path)
    {
        InputFilePath = path;
        UsePasteSource = false;
    }

    [RelayCommand]
    private async Task ExportDocumentAsync()
    {
        if (TargetFormat == "docx")
        {
            await ConvertToDocxAsync();
        }
        else
        {
            await ConvertToPdfAsync();
        }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        TargetFormat = "pdf";
        await ConvertToPdfAsync();
    }

    [RelayCommand]
    private async Task ExportDocxAsync()
    {
        TargetFormat = "docx";
        await ConvertToDocxAsync();
    }

    [RelayCommand]
    private void CancelConversion()
    {
        // Best-effort: WebView2's PrintToPdfAsync has no CancellationToken overload, so this
        // resets the UI immediately rather than truly aborting an in-flight render call — the
        // file may still be written a moment later. Good enough for "let me try something else
        // without waiting"; a hard-abort would need dropping to the CDP-level printing API.
        _conversionCts?.Cancel();
        StatusText = "Cancelled.";
        StatusSeverity = StatusSeverity.Warning;
        IsBusy = false;
    }

    public async Task ConvertToPdfAsync()
    {
        if (Host is null) { StatusText = "PDF export failed: preview engine not ready."; StatusSeverity = StatusSeverity.Error; return; }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("PDF", async ct =>
        {
            var html = BuildPreviewHtml(markdown);
            var outPath = ResolveOutputPath(sourceLabel, "pdf");
            await _pdfExport.ExportAsync(Host, html, outPath, _settingsService.Current);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("PDF", outPath, markdown);
            RaiseExportCompleted("PDF", outPath);
            StatusText = $"PDF export done: {outPath}";
        });
    }

    public async Task ConvertToDocxAsync()
    {
        if (!AppServices.License.CanExportDocx)
        {
            StatusText = "DOCX export is a Marksmith Pro feature — start your free trial or upgrade in Settings ⚙.";
            StatusSeverity = StatusSeverity.Warning;
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("DOCX", async ct =>
        {
            var outPath = ResolveOutputPath(sourceLabel, "docx");
            var settings = _settingsService.Current;
            var hasMermaid = markdown.Contains("```mermaid", StringComparison.Ordinal);

            // Large diagram? Ask (or honor the saved preference): keep mermaid's EXACT layout (Web
            // Layout view) or reflow to fit the printed page.
            List<Services.Mermaid.HarvestedDiagram?>? geometry = null;
            string? layoutNote = null;
            int? overrideMode = null;
            
            if (hasMermaid && settings.MermaidDocxMode == 1 && Host is not null)
            {
                // When the user has a saved preference (mode >= 1), always harvest — don't rely on
                // AnyWouldOverflow which uses the bespoke parser and can fail on complex diagrams.
                var mode = settings.OversizedDiagramMode;
                if (mode == 0 && Prompts is not null && Services.MermaidDocxRenderer.AnyWouldOverflow(markdown))
                    mode = await Prompts.AskOversizedDiagramModeAsync(); // 1 = exact, 2 = reflow
                overrideMode = mode;
                if (mode == 1 || (mode >= 3 && mode <= 8)) // any exact/multi-page/grid/shrink/compact mode
                {
                    geometry = await _mermaidHarvest.HarvestMermaidGeometryAsync(Host, markdown, settings, CurrentTheme);
                    var usable = geometry?.Any(g => g is { IsEmpty: false }) == true;
                    if (usable)
                    {
                        if (mode == 1 || mode == 4)
                            layoutNote = "  (large diagram: exact layout, opens in Web Layout)";
                        else if (mode is 6 or 7 or 8)
                            layoutNote = "  (large diagram: compact mode, fits on single page)";
                        else
                            layoutNote = "  (large diagram: fits on printed pages)";
                    }
                    else
                    {
                        geometry = null; // couldn't read exact geometry — reflow instead, and say so
                        layoutNote = "  (couldn't read exact layout — reflowed to fit the page)";
                    }
                }
                else if (mode == 2) layoutNote = "  (large diagram: reflowed to fit the page)";
            }

            // Generic harvest: always harvest as a universal fallback for any mermaid fence.
            // Even "bespoke" types (flowchart, sequence, etc.) can fail the bespoke parser on
            // complex inputs — the generic SVG-primitive path guarantees native shapes, never a picture.
            List<Services.Mermaid.GenericDiagram?>? genericGeom = null;
            if (hasMermaid && settings.MermaidDocxMode == 1 && Host is not null)
                genericGeom = await _mermaidHarvest.HarvestGenericGeometryAsync(Host, markdown, settings);

            // Rasterize mermaid diagrams (Snapshot mode, ShapeForge's fallback, and non-flowchart
            // families) — the renderer needs the platform's web host, which the caller wires up.
            List<byte[]?>? mermaidImgs = null;
            if (hasMermaid && Host is not null)
                mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, settings, CurrentTheme);
            // Disclose applied AI-cleanup fixes as a Word comment (paste source is already normalized).
            var fixes = NormalizeLlm && UsePasteSource ? LastClassification?.AppliedFixes : null;
            await _docxExport.ExportAsync(markdown, outPath, settings, mermaidImgs, fixes, geometry, genericGeom, overrideMode);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("DOCX", outPath, markdown);
            RaiseExportCompleted("DOCX", outPath);
            StatusText = $"DOCX export done: {outPath}{layoutNote}";
        });
    }

    public async Task ConvertToPptxAsync()
    {
        if (!AppServices.License.CanExportPptx)
        {
            StatusText = "PPTX export is a Marksmith Pro feature — start your free trial or upgrade in Settings ⚙.";
            StatusSeverity = StatusSeverity.Warning;
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("PPTX", async ct =>
        {
            var outPath = ResolveOutputPath(sourceLabel, PptxExportService.Extension);
            await _pptxExport.ExportAsync(markdown, outPath, _settingsService.Current);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("PPTX", outPath, markdown);
            RaiseExportCompleted("PPTX", outPath);
            StatusText = $"PPTX export done: {outPath}";
        });
    }

    // EPUB is a free (ungated) format — no license check, mirrors the PPTX path.
    public async Task ConvertToEpubAsync()
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("EPUB", async ct =>
        {
            var outPath = ResolveOutputPath(sourceLabel, EpubExportService.Extension);
            await _epubExport.ExportAsync(markdown, outPath, _settingsService.Current);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("EPUB", outPath, markdown);
            RaiseExportCompleted("EPUB", outPath);
            StatusText = $"EPUB export done: {outPath}";
        });
    }

    // One-click "Export all": produces every format the license allows (PDF always; DOCX/PPTX when
    // Pro) from the same resolved source, then reports a single combined summary. Each per-format
    // export runs through its own ConvertTo*Async (own progress/error handling), so a failure in one
    // format never blocks the others — success is detected by the export-history count growing.
    public async Task ExportAllAsync()
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        IsBusy = true;
        _suppressExportToasts = true; // one combined toast at the end, not one per format
        var done = new List<string>();
        var failed = new List<string>();
        var skipped = new List<string>();

        try
        {
            await RunOneFormatAsync("PDF", ConvertToPdfAsync, done, failed);
            if (AppServices.License.CanExportDocx) await RunOneFormatAsync("DOCX", ConvertToDocxAsync, done, failed);
            else skipped.Add("DOCX");
            if (AppServices.License.CanExportPptx) await RunOneFormatAsync("PPTX", ConvertToPptxAsync, done, failed);
            else skipped.Add("PPTX");
        }
        finally
        {
            _suppressExportToasts = false;
        }

        IsBusy = false;

        if (done.Count > 0) RaiseExportCompleted(string.Join(" + ", done), LastOutputPath ?? string.Empty);

        var parts = new List<string>();
        if (done.Count > 0) parts.Add($"Exported {string.Join(" + ", done)}");
        if (failed.Count > 0) parts.Add($"{string.Join(" + ", failed)} failed");
        if (skipped.Count > 0) parts.Add($"{string.Join(" + ", skipped)} skipped (Pro)");
        StatusText = string.Join(" · ", parts);
        StatusSeverity = failed.Count > 0 ? StatusSeverity.Warning : StatusSeverity.Success;
    }

    private async Task RunOneFormatAsync(string kind, Func<Task> export, List<string> done, List<string> failed)
    {
        var before = History.Count;
        try { await export(); }
        catch { /* per-format errors are already surfaced via the status bar */ }
        if (History.Count > before) done.Add(kind);
        else failed.Add(kind);
    }

    public async Task BatchConvertAsync(string sourceDir, string outputDir, string targetFormat)
    {
        await RunConversionAsync($"Batch {targetFormat.ToUpper()}", async ct =>
        {
            var settings = _settingsService.Current;
            await AppServices.BatchConvert.ConvertDirectoryAsync(Host, sourceDir, outputDir, targetFormat, settings, msg =>
            {
                StatusText = msg;
            });
            StatusText = $"Batch conversion to {targetFormat.ToUpper()} finished in {outputDir}";
        });
    }

    private async Task RunConversionAsync(string kind, Func<CancellationToken, Task> work)
    {
        _conversionCts?.Cancel();
        _conversionCts?.Dispose();
        _conversionCts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = $"Converting to {kind}...";
        StatusSeverity = StatusSeverity.Informational;
        try
        {
            await work(_conversionCts.Token);
            StatusSeverity = StatusSeverity.Success;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            StatusSeverity = StatusSeverity.Warning;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            StatusSeverity = StatusSeverity.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Turn an arbitrary conversation title into a safe filename base: strip characters illegal on
    // any OS, drop leftover Markdown emphasis markers, collapse whitespace, and cap the length at a
    // word boundary so a very long title can't blow up the path or produce an untypeable name that
    // ends mid-word (no trailing ellipsis).
    private static string SanitizeFileName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { ':', '?', '<', '|', '"' }).ToArray();
        var cleaned = new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        // Strip common Markdown emphasis/code markers that survive heading extraction.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[*_`#>|]", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.');
        const int max = 64;
        if (cleaned.Length > max)
        {
            var cut = cleaned.LastIndexOf(' ', max);
            cleaned = (cut > max / 2 ? cleaned[..cut] : cleaned[..max]).Trim().TrimEnd('.');
        }
        return cleaned;
    }

    private (string? Markdown, string SourceLabel) ResolveSource()
    {
        if (UsePasteSource)
        {
            if (string.IsNullOrWhiteSpace(PastedMarkdown))
            {
                StatusText = "Paste area is empty.";
                StatusSeverity = StatusSeverity.Warning;
                return (null, string.Empty);
            }
            // Prefer the document's own first heading as the filename base — it is parsed from the
            // actual Markdown and is therefore always a clean, faithful title. Fall back to the
            // captured conversation title (browser-extension metadata, which can be unreliable),
            // then to a random label if the content has no usable title either.
            var titleBase = SanitizeFileName(HistoryEntry.ExtractTitle(PastedMarkdown) ?? "");
            if (string.IsNullOrEmpty(titleBase))
                titleBase = SanitizeFileName(SuggestedTitle);
            var label = string.IsNullOrEmpty(titleBase)
                ? $"pasted_export_{Guid.NewGuid().ToString()[..8]}"
                : titleBase;
            return (PrepareMarkdown(PastedMarkdown), label);
        }

        if (string.IsNullOrWhiteSpace(InputFilePath) || !File.Exists(InputFilePath))
        {
            StatusText = "Please select a valid Markdown file first.";
            StatusSeverity = StatusSeverity.Warning;
            return (null, string.Empty);
        }

        return (PrepareMarkdown(File.ReadAllText(InputFilePath)), Path.GetFileNameWithoutExtension(InputFilePath));
    }

    public string ResolveOutputPath(string sourceLabel, string extension)
    {
        var inputDir = !string.IsNullOrWhiteSpace(InputFilePath) ? Path.GetDirectoryName(InputFilePath) : null;
        var folder = string.IsNullOrWhiteSpace(OutputFolder)
            ? (UsePasteSource || string.IsNullOrWhiteSpace(inputDir) ? _settingsService.Current.OutputFolder : inputDir)
            : OutputFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppContext.BaseDirectory;
        }
        Directory.CreateDirectory(folder);

        var baseName = ApplyFileNameTemplate(_settingsService.Current.FileNameTemplate, sourceLabel, extension);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = sourceLabel; // never lose the title entirely
        return Path.Combine(folder, $"{baseName}.{extension}");
    }

    // Expands the user's file-name template (Settings) into a safe base name. Supports {title},
    // {date}, {time} and {format}; anything the template yields is re-sanitized so a custom
    // template can never produce an invalid path.
    internal static string ApplyFileNameTemplate(string? template, string title, string extension)
    {
        if (string.IsNullOrWhiteSpace(template)) template = "{title}";
        var now = DateTime.Now;
        var name = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH-mm-ss"), StringComparison.OrdinalIgnoreCase)
            .Replace("{format}", extension, StringComparison.OrdinalIgnoreCase);
        return SanitizeFileName(name);
    }

    private void TrackRecent(string path)
    {
        var updated = _recentFilesService.AddToRecent(path);
        RecentFiles.Clear();
        foreach (var f in updated) RecentFiles.Add(f);

        // Promote the just-used file to the top of the discovered list, pinned, so it's easy to reopen.
        try
        {
            var full = Path.GetFullPath(path);
            for (int i = MarkdownFiles.Count - 1; i >= 0; i--)
                if (string.Equals(MarkdownFiles[i].Path, full, Services.PathEquality.Comparison))
                    MarkdownFiles.RemoveAt(i);
            var name = Path.GetFileName(full);
            var dirName = Path.GetDirectoryName(full);
            var folder = dirName is not null ? Path.GetFileName(dirName) : "";
            MarkdownFiles.Insert(0, new Services.MarkdownFileEntry(full, name, $"★ {folder} · just now", true));
        }
        catch { /* non-critical */ }
    }
}
