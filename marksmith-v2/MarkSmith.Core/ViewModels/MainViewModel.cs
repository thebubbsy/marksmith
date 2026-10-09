using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MarkSmith.Models;
using MarkSmith.Services;

namespace MarkSmith.ViewModels;

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
private readonly MarkdownExportService _mdExport = new();
    private readonly MermaidHarvestService _mermaidHarvest = new();
    private static readonly HttpClient _imageClient = new();

    private CancellationTokenSource? _conversionCts;

    // Set once by each UI project's main window at startup. Replaces the old WinUI-only
    // `App.MainAppWindow as MainWindow` downcast — the ViewModel now reaches the platform's web
    // renderer and native prompts only through these portable seams (see IWebRenderHost).
    public IWebRenderHost? Host { get; set; }
    public IUiPrompts? Prompts { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPdfFormat))]
    [NotifyPropertyChangedFor(nameof(IsDocxFormat))]
    [NotifyPropertyChangedFor(nameof(TargetFormatLabel))]
    [NotifyPropertyChangedFor(nameof(AutomationFormatNote))]
    private string _targetFormat = "pdf";

    /// <summary>
    /// What the default output format is called in sentences ("Auto-export ingests as a PDF"). The
    /// automation options used to say "PDF" whatever the setting, while the watch folder, clipboard
    /// ingest and batch convert all export in <see cref="TargetFormat"/>.
    /// </summary>
    public string TargetFormatLabel => OutputFormats.Label(TargetFormat);

    public string AutomationFormatNote =>
        AppServices.License.CanAutomate || !OutputFormats.IsEmail(TargetFormat)
            ? $"Automatic exports use your default format ({TargetFormatLabel}), set in Settings ▸ General."
            : $"Automatic exports use your default format ({TargetFormatLabel}), set in Settings ▸ General. Email automation is free on every plan.";

    /// <summary>Whether unattended exports may run for this license and default format
    /// (<see cref="AutomationPolicy"/>: Pro, or anything that only writes email drafts).</summary>
    public bool AutomationAllowed => AutomationPolicy.Allows(AppServices.License.State, TargetFormat);

    /// <summary>
    /// The local API server's state in words, set by the shell after every start/stop attempt:
    /// "Listening on http://127.0.0.1:47821", "Off", or why it failed to start. Settings and the
    /// side panel's Automation section both show it, so the toggle is never the only clue.
    /// </summary>
    [ObservableProperty] private string _apiStatusText = "Off";
    [ObservableProperty] private bool _apiStatusIsError;
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

    // Google Docs export (Settings → Google): the user's own Google Cloud OAuth client + the
    // live state of the device sign-in flow.
    [ObservableProperty] private string _googleClientId = "";
    [ObservableProperty] private string _googleClientSecret = "";
    [ObservableProperty] private string _googleRefreshToken = "";
    [ObservableProperty] private string _googleAccountEmail = "";
    [ObservableProperty] private string _googleAuthStatus = "Not connected";
    [ObservableProperty] private string _googleDeviceCode = "";
    [ObservableProperty] private string _googleVerifyUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentMarkdown))]
    private bool _usePasteSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NormalizationRulesPaused))]
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
    [ObservableProperty] private string _brandTemplatePath = "";
    [ObservableProperty] private bool _showAttribution;
    [ObservableProperty] private bool _noEmoji;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashCustom))]
    private int _dashMode;

    /// <summary>The em-dash "Custom…" choice is selected, so its replacement box applies.</summary>
    public bool IsDashCustom => DashMode == 3;
    [ObservableProperty] private string _dashCustom;
    [ObservableProperty] private int _headingShift;
    [ObservableProperty] private int _boldMode;
    [ObservableProperty] private int _italicMode;
    [ObservableProperty] private bool _proMode;
    [ObservableProperty] private bool _hardwareAcceleration = true;
    [ObservableProperty] private bool _apiEnabled;
    [ObservableProperty] private int _apiPort;
    [ObservableProperty] private bool _enableStreamingApi;
    [ObservableProperty] private bool _skipLaunchVideo;
    [ObservableProperty] private string _allowedExtensionId = string.Empty;
    [ObservableProperty] private string _detectedSourceText = string.Empty;

    /// <summary>Persistent per-document undo/redo stacks for the markdown editor (survives app
    /// restarts via undo_history.json). Records every change; see <see cref="UndoStep"/>.</summary>
    private readonly Services.EditorUndoHistory _editorUndo = new();

    /// <summary>Current caret position in the editor, kept fresh by the UI so undo snapshots can
    /// restore the caret exactly.</summary>
    public int EditorCaret { get; set; }

    public bool CanUndo => _editorUndo.CanUndo;
    public bool CanRedo => _editorUndo.CanRedo;

    /// <summary>Applies one undo step (the UI sets the returned text + caret).</summary>
    public Services.UndoSnapshot? UndoStep()
    {
        var snap = _editorUndo.Undo();
        if (snap is not null)
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
        return snap;
    }

    public Services.UndoSnapshot? RedoStep()
    {
        var snap = _editorUndo.Redo();
        if (snap is not null)
        {
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
        }
        return snap;
    }

    /// <summary>Forces the next editor change to open a fresh undo step (used before programmatic
    /// content injections so they undo as a unit).</summary>
    public void BreakUndoBurst() => _editorUndo.BreakBurst();

    /// <summary>Persists all documents' undo/redo stacks to disk (called on app exit).</summary>
    public void SaveUndoHistory() => _editorUndo.Flush();

    /// <summary>True when the browser extension has polled within the last 90 seconds.</summary>
    public bool ExtensionConnected =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ApiServer.LastExtensionPollTs < 90_000;

    // ---- House-style template pipeline (extension round-trip) ---------------------------------
    // The .dotx import (Settings ▸ Automation card) parses the template locally, then hands a
    // prompt to the browser extension via the reverse command channel (ApiServer.EnqueueCommand).
    // The extension feeds the prompt to the user's OWN web AI — Marksmith never calls an LLM —
    // and posts the reply back to POST /api/commands/result, which PollThemeJobResult consumes.

    /// <summary>The generated AI prompt, surfaced in Settings as a copyable manual fallback.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHouseStylePrompt))]
    private string _houseStylePrompt = "";

    /// <summary>Status line under the house-style import button in Settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHouseStyleStatus))]
    private string _houseStyleStatus = "";

    /// <summary>Manual fallback for the AI's JSON reply (pasted when the extension round-trip
    /// isn't available). Bound to the Settings text box.</summary>
    [ObservableProperty]
    private string _houseStyleJsonResult = "";

    public bool HasHouseStylePrompt => !string.IsNullOrWhiteSpace(HouseStylePrompt);
    public bool HasHouseStyleStatus => !string.IsNullOrWhiteSpace(HouseStyleStatus);

    private string? _pendingThemeJobId;

    /// <summary>Parses a .dotx/.docx template, builds the style prompt and — when the browser
    /// extension is connected — enqueues it for the zero-click round-trip. The prompt is ALWAYS
    /// surfaced in Settings so the import works without the extension: copy the prompt into your
    /// web AI, then paste its JSON reply via <see cref="ApplyHouseStyleThemeJson"/>.
    /// Throws when the template cannot be parsed (the caller surfaces the error dialog).</summary>
    public string BeginHouseStyleImport(string dotxPath)
    {
        var summary = TemplateThemeService.ParseDotx(dotxPath);
        // Advanced house style: also inherit the template's page geometry, margins, columns and
        // header/footer — extracted locally (no AI round-trip) and replayed on every export.
        var layout = TemplateThemeService.ParseLayout(dotxPath);
        _settingsService.Current.BrandLayout = layout.IsEmpty ? null : layout;
        SaveSettingsDebounced();

        var prompt = TemplateThemeService.BuildPrompt(summary, layout.IsEmpty ? null : layout);
        HouseStylePrompt = prompt;
        BrandTemplatePath = dotxPath;

        // Always enqueue on the reverse command channel — harmless when the extension is away
        // (the poll simply finds nothing), and the prompt + JSON paste box below remain the
        // manual fallback. The status line tells the user which path to take.
        var jobId = ApiServer.EnqueueCommand("theme-prompt", prompt);
        _pendingThemeJobId = jobId;
        HouseStyleStatus = ExtensionConnected
            ? layout.IsEmpty
                ? "Prompt sent to the browser extension — waiting for your web AI's reply…"
                : "Prompt sent to the browser extension — waiting for your web AI's reply… (page setup, margins, columns and header/footer inherited from the template)"
            : layout.IsEmpty
                ? "Extension not connected — copy the prompt below into your web AI, then paste its JSON reply into the box below."
                : "Extension not connected — copy the prompt below into your web AI, then paste its JSON reply into the box below. (page setup, margins, columns and header/footer inherited from the template)";
        return jobId;
    }

    /// <summary>Manual fallback: apply the web AI's JSON reply directly — the same parse/save/select
    /// path the extension result takes. Supersedes any still-pending extension result.</summary>
    public bool ApplyHouseStyleThemeJson(string json)
    {
        _pendingThemeJobId = null;
        bool ok = ApplyThemeResult(json);
        if (ok) HouseStyleJsonResult = ""; // clear the box on success so a stale paste can't re-apply
        return ok;
    }

    /// <summary>Parses the AI reply, saves the custom theme and selects it (selecting triggers the
    /// preview refresh). Shared by the extension round-trip and the manual JSON paste. Any page
    /// geometry the JSON carried is merged over the template's locally-extracted layout so the
    /// JSON is the COMPLETE house-style spec (colour, fonts, page size, margins, columns;
    /// header/footer content stays inherited from the template).</summary>
    private bool ApplyThemeResult(string replyMarkdown)
    {
        var theme = TemplateThemeService.ParseAiResponse(replyMarkdown);
        if (theme is null)
        {
            HouseStyleStatus = "The AI reply wasn't valid theme JSON — copy the prompt below into your web chat and try again.";
            StatusText = "House-style import failed: the AI reply wasn't a valid theme JSON.";
            StatusSeverity = StatusSeverity.Error;
            return false;
        }

        if (theme.Layout is not null)
        {
            var merged = Models.HouseLayout.Merge(_settingsService.Current.BrandLayout, theme.Layout);
            _settingsService.Current.BrandLayout = merged;
            SaveSettingsDebounced();
        }

        TemplateThemeService.SaveTheme(theme);
        var ordered = BuildOrderedThemeNames(_settingsService.Current.FavoriteThemes);
        ThemeNames.Clear();
        foreach (var name in ordered) ThemeNames.Add(name);
        SelectedThemeName = theme.Name;
        HouseStyleStatus = $"Theme “{theme.Name}” created and selected.";
        StatusText = $"House-style theme “{theme.Name}” applied.";
        StatusSeverity = StatusSeverity.Success;
        return true;
    }

    /// <summary>Checks the reverse command channel for the pending house-style result and applies
    /// it. Returns true when a result was CONSUMED (even if it failed to parse — the error is
    /// surfaced in HouseStyleStatus), false when nothing was pending yet.</summary>
    public bool PollThemeJobResult()
    {
        if (_pendingThemeJobId is null) return false;
        var result = ApiServer.GetResult(_pendingThemeJobId);
        if (result is null) return false;
        _pendingThemeJobId = null;
        ApplyThemeResult(result.ReplyMarkdown);
        return true;
    }

    /// <summary>Heartbeat tick driven by the UI-layer timer: refreshes the extension-connected
    /// flag and polls for a pending house-style theme result on the dispatcher thread.</summary>
    public void TickExtensionChannel()
    {
        OnPropertyChanged(nameof(ExtensionConnected));
        PollThemeJobResult();
    }

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

    // PDF security (Task 18): password protection + access-control permissions.
    [ObservableProperty] private bool _pdfEncrypt;
    [ObservableProperty] private string _pdfUserPassword = "";
    [ObservableProperty] private string _pdfOwnerPassword = "";
    [ObservableProperty] private bool _pdfAllowPrinting = true;
    [ObservableProperty] private bool _pdfAllowCopying = true;
    [ObservableProperty] private bool _pdfAllowModifying = true;

    // Diagram + document chrome settings that previously had no UI surface.
    [ObservableProperty] private bool _mermaidEnabled = true;
    [ObservableProperty] private bool _smartConnectors = true;
    [ObservableProperty] private string _connectorArrowhead = "default";
    [ObservableProperty] private bool _pageBorder;
    [ObservableProperty] private bool _trackChanges;
    [ObservableProperty] private string _authorName = "";
    [ObservableProperty] private string _customFontPath = "";

    // General preferences.
    [ObservableProperty] private int _ambiguityMode = 1;
    [ObservableProperty] private bool _checkForUpdatesOnStartup = true;
    [ObservableProperty] private bool _autoInstallUpdatesOnLaunch = true;
    [ObservableProperty] private bool _autoRestartAfterUpdate = true;
    [ObservableProperty] private bool _autoFocusOnSplit;
    [ObservableProperty] private bool _showLineNumbers;

    [ObservableProperty] private bool _isUpdateAvailable;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private bool _isUpdateReady;
    [ObservableProperty] private double _updateDownloadProgress;
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private string _latestUpdateTag = "";
    [ObservableProperty] private string _updateDownloadUrl = "";
    [ObservableProperty] private bool _portalFocusBlur = true;
    [ObservableProperty] private double _portalSurroundBlurRadius = 6.0;
    [ObservableProperty] private bool _portalInsideBlur = true;
    [ObservableProperty] private double _portalInsideBlurRadius = 5.0;

    // Typography preset (Task 16) — id from FontManagerService.Presets ("System" default).
    [ObservableProperty] private string _fontPreset = "System";

    // Cloud drives detected on this machine (Task 9); feeds the Settings ▸ Automation ▸ Cloud Sync
    // picker. Refreshed on startup and via RefreshCloudProviders() (the "Re-scan" button).
    public ObservableCollection<Models.CloudProviderInfo> CloudProviders { get; } = new();

    // True when the selected cloud provider is WebDAV (shows the endpoint/credentials fields).
    public bool IsWebDavProvider => CloudProviderId == "webdav";

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

    public bool HasInputFile => !string.IsNullOrWhiteSpace(InputFilePath) && File.Exists(InputFilePath);

    /// <summary>
    /// True while the editor holds the open file's own text (opened, then edited). Pasted,
    /// ingested and imported text replaces the editor without clearing <see cref="InputFilePath"/>,
    /// and editing flips <see cref="UsePasteSource"/> too, so neither says whose text this is.
    /// Without it Ctrl+S wrote a chat sent from the browser extension over the last file opened,
    /// and relative images resolved next to a file the text never came from.
    /// </summary>
    [ObservableProperty]
    private bool _isEditingOpenFile;

    /// <summary>Call whenever the editor is given text that didn't come from the open file.</summary>
    public void DetachFromOpenFile() => IsEditingOpenFile = false;

    // Switching back to the file source (picking the same file again after a paste) shows the
    // file's own text, which is the open file's document again.
    partial void OnUsePasteSourceChanged(bool value)
    {
        if (!value && HasInputFile && _openFileStamp is not null) IsEditingOpenFile = true;
    }

    // Last-write time and length of the open file when it was read or saved, so a save can tell
    // that another program (a second editor, a sync client) changed it in the meantime.
    private (DateTime WriteUtc, long Length)? _openFileStamp;

    private static (DateTime, long)? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch { return null; }
    }

    /// <summary>True when the open file changed on disk since MarkSmith read or last saved it.</summary>
    public bool OpenFileChangedOnDisk() =>
        _openFileStamp is { } known && !string.IsNullOrWhiteSpace(InputFilePath)
        && StampOf(InputFilePath) is { } now && now != known;

    /// <summary>Records the open file's state right after MarkSmith wrote it.</summary>
    public void MarkOpenFileSaved() =>
        _openFileStamp = string.IsNullOrWhiteSpace(InputFilePath) ? null : StampOf(InputFilePath);

    partial void OnInputFilePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasInputFile));
        // Persistent undo: the active document switches when the new content lands (Seed in the
        // read callback) so keystrokes typed during the file read cannot pollute the new file's
        // undo stack. Failure/empty branches switch explicitly so the key never goes stale.
        _fileReadCts?.Cancel();
        _fileReadCts?.Dispose();
        _fileReadCts = new CancellationTokenSource();
        var token = _fileReadCts.Token;

        // Reflect whether the newly-selected file is one the user has pinned.
        IsCurrentFilePinned = !string.IsNullOrWhiteSpace(value)
            && _settingsService.Current.PinnedFiles.Contains(Path.GetFullPath(value), Services.PathEquality.Comparer);

        _openFileStamp = null;
        if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
        {
            var syncContext = SynchronizationContext.Current;
            _ = ReadInputFileAsync(value, token, syncContext);
        }
        else
        {
            _editorUndo.SetDocument(value);
            _cachedFileMarkdown = string.Empty;
            IsEditingOpenFile = false;
            OnPropertyChanged(nameof(CurrentMarkdown));
        }
    }

    // What the open file was converted from ("Word", "PDF", "HTML", "Email", an importer plugin's
    // name), or null when it is Markdown/text. A converted source is never written back to:
    // Ctrl+S saves a Markdown copy beside it instead (see MainWindow.SaveDocumentToFileAsync).
    [ObservableProperty]
    private string? _sourceImportKind;

    private async Task ReadInputFileAsync(string value, CancellationToken token, SynchronizationContext? syncContext)
    {
        try
        {
            // Through the importers, not a raw read: a .docx, .pdf, .html or .eml opens as the
            // Markdown it converts to, never as bytes in the editor.
            var stamp = StampOf(value);
            var imported = await Plugins.PluginFileReader.ImportAsync(value);
            token.ThrowIfCancellationRequested();
            var text = imported.Markdown;
            if (!token.IsCancellationRequested)
            {
                CaptureVersionSafe(value, text, "opened");
                _cachedFileMarkdown = text;
                _editorUndo.Seed(value, text);
                void Apply()
                {
                    if (token.IsCancellationRequested) return;
                    SourceImportKind = imported.Kind;
                    _openFileStamp = stamp;
                    IsEditingOpenFile = true;
                    PastedMarkdown = text;
                    OnPropertyChanged(nameof(CurrentMarkdown));
                    if (imported.IsConverted)
                    {
                        var lead = imported.Summary ?? $"Opened {Path.GetFileName(value)} as Markdown (converted from {imported.Kind})";
                        StatusText = lead + " · Ctrl+S saves a Markdown copy; the original file is never changed.";
                        StatusSeverity = StatusSeverity.Success;
                    }
                }
                if (syncContext != null) syncContext.Post(_ => Apply(), null);
                else Apply();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                _editorUndo.SetDocument(value);
                _cachedFileMarkdown = string.Empty;
                void Fail()
                {
                    SourceImportKind = null;
                    IsEditingOpenFile = false;
                    OnPropertyChanged(nameof(CurrentMarkdown));
                    StatusText = $"Couldn't open {Path.GetFileName(value)}: {ex.Message}";
                    StatusSeverity = StatusSeverity.Error;
                }
                if (syncContext != null) syncContext.Post(_ => Fail(), null);
                else Fail();
            }
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
    // refreshes with the same change notification the word count already fired on. The result is
    // cached per source string: WordCountText and DocumentStatsDetail both read this property, so
    // without the cache a single status-bar refresh ran the full Analyze scan twice.
    private Services.DocumentStats _cachedStats;
    private string? _cachedStatsSource;
    public Services.DocumentStats DocumentStats
    {
        get
        {
            var src = PastedMarkdown;
            if (!ReferenceEquals(src, _cachedStatsSource))
            {
                _cachedStats = Services.DocumentStatsService.Analyze(src);
                _cachedStatsSource = src;
            }
            return _cachedStats;
        }
    }

    partial void OnPastedMarkdownChanged(string value)
    {
        // Persistent undo: every editor change flows through this setter (two-way binding). The
        // history service coalesces a typing burst into one step and dedupes the binding
        // round-trip that follows an undo/redo, so no guard flag is needed here.
        _editorUndo.RecordChange(value ?? "", EditorCaret);
        ScheduleAutoSnapshot(value);

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

    // The file the status bar's "Open · Show in folder" links act on. Set only while the status
    // line is announcing that export; any later status message hides the links again, so they never
    // sit beside an unrelated message (the Windows toast is easy to miss or switched off).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusOutput))]
    private string? _statusOutputPath;
    public bool HasStatusOutput => !string.IsNullOrEmpty(StatusOutputPath);
    private bool _settingExportStatus;

    partial void OnStatusTextChanged(string value)
    {
        if (!_settingExportStatus) StatusOutputPath = null;
    }

    internal void AnnounceExport(string message, string? outputPath)
    {
        _settingExportStatus = true;
        try
        {
            StatusText = message;
            StatusOutputPath = outputPath;
        }
        finally { _settingExportStatus = false; }
    }

    // Licensing (drives the paywall UI). Backed by AppServices.License; kept in sync via its Changed event.
    public bool IsPro => AppServices.License.IsPro;
    public bool IsFree => AppServices.License.State.Edition == Models.Edition.Free;
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

    // ---- AI normalization custom rules (Settings pane) ----
    public ObservableCollection<Models.TextCleanupRuleItem> NormalizationRules { get; } = new();

    [RelayCommand]
    private void AddNormalizationRule()
    {
        NormalizationRules.Add(new Models.TextCleanupRuleItem(SaveNormalizationRules));
        SaveNormalizationRules();
    }

    [RelayCommand]
    private void RemoveNormalizationRule(Models.TextCleanupRuleItem rule)
    {
        NormalizationRules.Remove(rule);
        SaveNormalizationRules();
    }

    private void SaveNormalizationRules()
    {
        _settingsService.Current.CustomNormalizationRules =
            NormalizationRules.Select(r => r.ToRule()).ToList();
        SaveSettingsDebounced();
        // Raised on every add, remove and edit: the side panel hides the empty list box by it, and
        // the shell re-renders the preview so a rule's effect shows as you type it.
        OnPropertyChanged(nameof(HasNormalizationRules));
        OnPropertyChanged(nameof(NormalizationRulesPaused));
    }

    public bool HasNormalizationRules => NormalizationRules.Count > 0;

    /// <summary>The rules only run as part of the AI-quirks pass, so say so while that's off rather
    /// than showing match counts that no longer apply.</summary>
    public bool NormalizationRulesPaused => !NormalizeLlm && HasNormalizationRules;

    // Hands each rule row what the latest preview pass did with it (match count or a run-time error).
    private void ShowNormalizationRuleOutcomes(IReadOnlyList<CleanupRuleOutcome>? outcomes)
    {
        for (var i = 0; i < NormalizationRules.Count; i++)
            NormalizationRules[i].ShowOutcome(outcomes is not null && i < outcomes.Count ? outcomes[i] : null);
    }

    // Document outline (Task 17): H1–H6 entries extracted from CurrentMarkdown. The anchors are the
    // exact Markdig AutoIdentifier ids the preview renders, so the outline flyout can click-to-scroll.
    public ObservableCollection<TocEntry> TocEntries { get; } = new();

    /// <summary>Re-extracts the document outline from <see cref="CurrentMarkdown"/> (Task 17).</summary>
    public void RefreshToc()
    {
        var entries = TocExtractorService.Extract(CurrentMarkdown);
        TocEntries.Clear();
        foreach (var e in entries) TocEntries.Add(e);
    }

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

    /// <param name="sourcePath">The file the export was made from when it isn't the document in the
    /// editor (a watched or batch-converted file): the history row names it and the version is
    /// filed under it, instead of under whatever happens to be open.</param>
    public void RecordExport(string kind, string outputPath, string markdown, long durationMs = 0, string? sourcePath = null)
    {
        long sizeBytes = 0;
        try { if (File.Exists(outputPath)) sizeBytes = new FileInfo(outputPath).Length; } catch { /* best-effort */ }

        var entry = new HistoryEntry
        {
            Timestamp = DateTime.Now,
            SourceLabel = sourcePath is not null ? Path.GetFileName(sourcePath) : UsePasteSource ? "pasted" : Path.GetFileName(InputFilePath),
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

        // Version history: every successful export is a version of the working document.
        var effectivePath = sourcePath ?? (!string.IsNullOrWhiteSpace(InputFilePath) ? InputFilePath : "scratch://workspace-session.md");
        CaptureVersionSafe(effectivePath, markdown, "export:" + kind.ToLowerInvariant());
    }

    private CancellationTokenSource? _autoSnapshotCts;

    private void ScheduleAutoSnapshot(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        _autoSnapshotCts?.Cancel();
        _autoSnapshotCts = new CancellationTokenSource();
        var token = _autoSnapshotCts.Token;

        _ = Task.Delay(20000, token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !string.IsNullOrWhiteSpace(markdown))
            {
                var effectivePath = !string.IsNullOrWhiteSpace(InputFilePath) ? InputFilePath : "scratch://workspace-session.md";
                CaptureVersionSafe(effectivePath, markdown, "autosave");
            }
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    public async Task CreateManualSnapshotAsync(string? label = null)
    {
        var text = CurrentMarkdown;
        if (string.IsNullOrWhiteSpace(text)) return;
        var effectivePath = !string.IsNullOrWhiteSpace(InputFilePath) ? InputFilePath : "scratch://workspace-session.md";
        var captured = await AppServices.VersionHistory.CaptureAsync(effectivePath, text, "snapshot", label ?? "Manual Checkpoint", isStarred: true);
        if (captured)
        {
            StatusText = $"Saved checkpoint: {label ?? "Manual Checkpoint"}";
            StatusSeverity = StatusSeverity.Success;
        }
    }

    /// <summary>Best-effort version-history capture — never throws, never blocks the UI.</summary>
    private async void CaptureVersionSafe(string filePath, string content, string source, string? label = null, bool isStarred = false)
    {
        try { await AppServices.VersionHistory.CaptureAsync(filePath, content, source, label, isStarred); }
        catch { /* history is best-effort; a store failure must never break the app */ }
    }

    /// <summary>Restores a stored version's content into the editor (preview refreshes via the
    /// normal binding). The next save/export becomes a new version — history is never rewritten.</summary>
    public async Task<bool> RestoreVersionAsync(string id)
    {
        try
        {
            var content = await AppServices.VersionHistory.GetContentAsync(id);
            if (content is null) return false;
            // Keep the text being replaced: undo only lasts for this session, history doesn't.
            if (!string.IsNullOrWhiteSpace(PastedMarkdown) && PastedMarkdown != content)
            {
                var key = !string.IsNullOrWhiteSpace(InputFilePath) ? InputFilePath : "scratch://workspace-session.md";
                try { await AppServices.VersionHistory.CaptureAsync(key, PastedMarkdown, "snapshot", "Before restore"); }
                catch { /* best effort, never block the restore */ }
            }
            _editorUndo.BreakBurst(); // a version restore must undo as its own step
            PastedMarkdown = content;
            OnPropertyChanged(nameof(CurrentMarkdown));
            StatusText = "Restored a previous version from history.";
            StatusSeverity = StatusSeverity.Success;
            return true;
        }
        catch
        {
            StatusText = "Could not restore that version.";
            StatusSeverity = StatusSeverity.Error;
            return false;
        }
    }

    // Raised after a manual, user-initiated export finishes (kind + output path). The WinUI layer
    // subscribes to surface a Windows toast. Auto-ingest/watch-folder exports raise their own toast
    // through the ExportCoordinator's callback, and batch/API flows stay silent, so this is only
    // fired from the single-format ConvertTo*Async paths (suppressed while ExportAllAsync batches
    // them, which raises one combined notification instead).
    public event Action<string, string>? ExportCompleted;

    // Raised when a FREE user attempts a PRO feature. The UI shell shows the standardized
    // pro-gate dialog; non-UI hosts (tests, CLI) can ignore it - the StatusText fallback still carries the message.
    public event Action<FeatureId>? ProFeatureAttempted;

    /// <summary>Shell-side gate notifications (MainWindow) route through here so the event
    /// stays invocable only from the owning class.</summary>
    public void NotifyProFeatureAttempted(FeatureId id, Func<Task>? resume = null) => ReportProGate(id, resume);

    // The one way a gate reports a Pro feature: the shared status line (ProGate), then the shell's
    // upgrade dialog. `resume` is the action the user was trying to run; if they start the trial
    // from that dialog the shell calls ResumeAfterUnlockAsync and it simply happens, instead of
    // leaving them to find the button again.
    private Func<Task>? _resumeAfterUnlock;

    private void ReportProGate(FeatureId id, Func<Task>? resume)
    {
        _resumeAfterUnlock = resume;
        StatusText = ProGate.StatusLine(id, AppServices.License.State);
        StatusSeverity = StatusSeverity.Warning;
        ProFeatureAttempted?.Invoke(id);
    }

    /// <summary>Runs the action that hit the last Pro gate, once, if the license now allows it.</summary>
    public Task ResumeAfterUnlockAsync()
    {
        var resume = _resumeAfterUnlock;
        _resumeAfterUnlock = null;
        return resume is null ? Task.CompletedTask : resume();
    }

    // A free user must never START with automation switched on (a persisted Pro-era setting would
    // otherwise leave the toggles looking active while AutomationManager refuses to run them).
    private void SanitizeAutomationForLicense()
    {
        if (AutomationAllowed) return;
        bool changed = false;
        if (AutoClipboardIngest) { AutoClipboardIngest = false; changed = true; }
        if (WatchFolderEnabled) { WatchFolderEnabled = false; changed = true; }
        if (AutoConvertIngests) { AutoConvertIngests = false; changed = true; }
        if (changed)
        {
            _settingsService.Current.AutoClipboardIngest = false;
            _settingsService.Current.WatchFolderEnabled = false;
            _settingsService.Current.AutoConvertIngests = false;
            SaveSettingsDebounced();
        }
    }
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
        // The free plan's email-only automation depends on the default format, so read it first:
        // it's loaded further down, and checking before that switched a free user's email
        // watch folder off at every start.
        _targetFormat = settings.TargetFormat;
        SanitizeAutomationForLicense();
        foreach (var rule in _settingsService.Current.CustomNormalizationRules ?? new List<TextCleanupRule>())
            NormalizationRules.Add(new Models.TextCleanupRuleItem(SaveNormalizationRules, rule.Find, rule.Replace, rule.IsRegex));
        foreach (var h in AppServices.History.All) History.Add(h);
        _includeToc = settings.IncludeToc;
        _mermaidDocxMode = settings.MermaidDocxMode;
        _oversizedDiagramMode = settings.OversizedDiagramMode;
        _brandCoverPage = settings.BrandCoverPage;
        _brandLogoPath = settings.BrandLogoPath;
        _brandFontFamily = settings.BrandFontFamily;
        _brandTemplatePath = settings.BrandTemplatePath;
        _showAttribution = settings.ShowAttribution;
        _noEmoji = settings.NoEmoji;
        _dashMode = settings.DashMode;
        _dashCustom = settings.DashCustom;
        _headingShift = settings.HeadingShift;
        _boldMode = settings.BoldMode;
        _italicMode = settings.ItalicMode;
        _proMode = settings.ProMode;
        _hardwareAcceleration = settings.HardwareAcceleration;
        _apiEnabled = settings.ApiEnabled;
        _apiPort = settings.ApiPort;
        _skipLaunchVideo = settings.SkipLaunchVideo;
        _enableStreamingApi = settings.EnableStreamingApi;
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
        _pdfEncrypt = settings.PdfEncrypt;
        _pdfUserPassword = settings.PdfUserPassword;
        _pdfOwnerPassword = settings.PdfOwnerPassword;
        _pdfAllowPrinting = settings.PdfAllowPrinting;
        _pdfAllowCopying = settings.PdfAllowCopying;
        _pdfAllowModifying = settings.PdfAllowModifying;
        _mermaidEnabled = settings.MermaidEnabled;
        _smartConnectors = settings.SmartConnectors;
        _connectorArrowhead = settings.ConnectorArrowhead;
        _pageBorder = settings.PageBorder;
        _trackChanges = settings.TrackChanges;
        LoadEmailSettings(settings);
        LoadOcrSettings(settings);
        _authorName = settings.AuthorName;
        _customFontPath = settings.CustomFontPath;
        _ambiguityMode = settings.AmbiguityMode;
        _checkForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _autoInstallUpdatesOnLaunch = settings.AutoInstallUpdatesOnLaunch;
        _autoRestartAfterUpdate = settings.AutoRestartAfterUpdate;
        _autoFocusOnSplit = settings.AutoFocusOnSplit;
        _showLineNumbers = settings.ShowLineNumbers;
        _showWordCount = settings.ShowWordCount;
        _portalFocusBlur = settings.PortalFocusBlur;
        _portalSurroundBlurRadius = settings.PortalSurroundBlurRadius;
        _portalInsideBlur = settings.PortalInsideBlur;
        _portalInsideBlurRadius = settings.PortalInsideBlurRadius;

        RefreshCloudProviders();

        if (_checkForUpdatesOnStartup)
        {
            _ = CheckForUpdatesOnStartupAsync();
        }

        ThemeNames = new ObservableCollection<string>(BuildOrderedThemeNames(settings.FavoriteThemes));
        _isCurrentThemeFavorite = settings.FavoriteThemes.Contains(_selectedThemeName);
        foreach (var f in _recentFilesService.Load()) RecentFiles.Add(f);

        AppServices.License.Changed += OnLicenseChanged;
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var res = await AppServices.Updates.CheckAsync();
            if (res.UpdateAvailable)
            {
                IsUpdateAvailable = true;
                LatestUpdateTag = res.LatestTag;
                UpdateDownloadUrl = res.DownloadUrl;
                UpdateStatusText = res.Message;

                if (AutoInstallUpdatesOnLaunch && !string.IsNullOrEmpty(UpdateDownloadUrl))
                {
                    await DownloadAndApplyUpdateAsync();
                }
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task DownloadAndApplyUpdateAsync()
    {
        if (IsDownloadingUpdate || string.IsNullOrEmpty(UpdateDownloadUrl)) return;

        IsDownloadingUpdate = true;
        IsUpdateReady = false;
        UpdateStatusText = $"Downloading {LatestUpdateTag}... 0%";

        var progress = new Progress<double>(p =>
        {
            UpdateDownloadProgress = p;
            UpdateStatusText = $"Downloading {LatestUpdateTag}... {p:F0}%";
        });

        try
        {
            var success = await AppServices.Updates.DownloadAndInstallAsync(UpdateDownloadUrl, progress);
            IsDownloadingUpdate = false;
            if (success)
            {
                IsUpdateReady = true;
                UpdateStatusText = $"Update {LatestUpdateTag} downloaded and ready!";
                if (AutoRestartAfterUpdate)
                {
                    MarkSmith.Services.UpdateService.RelaunchApplication();
                }
            }
            else
            {
                UpdateStatusText = AppServices.Updates.LastFailureReason ?? "The update didn't install.";
            }
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            UpdateStatusText = $"Update failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public void RelaunchNow()
    {
        MarkSmith.Services.UpdateService.RelaunchApplication();
    }

    [RelayCommand]
    public void DismissUpdateBanner()
    {
        IsUpdateAvailable = false;
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
        OnPropertyChanged(nameof(AutomationAllowed));
        if ((AutoClipboardIngest || WatchFolderEnabled || AutoConvertIngests) && !AutomationAllowed)
        {
            SanitizeAutomationForLicense();
            StatusText = $"Automation is off: on the free plan it only runs for email drafts, and the default format is now {OutputFormats.Label(value)}.";
            StatusSeverity = StatusSeverity.Warning;
        }
        OnPropertyChanged(nameof(IsPdfFormat));
        OnPropertyChanged(nameof(IsDocxFormat));
        OnPropertyChanged(nameof(TargetFormatIndex));
    }
    partial void OnContentWidthChanged(int value) {
        // A4 lock is authoritative: a manual width edit while locked reverts to the A4 width so the
        // canvas/PDF/DOCX/Google page models never desync (review-fix pin: A4_Lock_Is_Authoritative).
        if (A4FixedWidth && value != 794) { ContentWidth = 794; return; }
        _settingsService.Current.ContentWidth = value; SaveSettingsDebounced();
    }
    partial void OnA4FixedWidthChanged(bool value) { 
        _settingsService.Current.A4FixedWidth = value; 
        SaveSettingsDebounced(); 
        if (value) ContentWidth = 794;
    }
    partial void OnUnlimitedHeightChanged(bool value) { _settingsService.Current.UnlimitedHeight = value; SaveSettingsDebounced(); }
    partial void OnNormalizeLlmChanged(bool value) { _settingsService.Current.NormalizeLlm = value; SaveSettingsDebounced(); }
    partial void OnAutoClipboardIngestChanged(bool value)
    {
        if (value && !AutomationAllowed)
        {
            // Gate at the SOURCE: a free user cannot even switch automation on, so the feature
            // never half-runs (the old bug: watchers started and only the export step complained).
#pragma warning disable MVVMTK0034
            _autoClipboardIngest = false;
#pragma warning restore MVVMTK0034
            OnPropertyChanged();
            ReportProGate(FeatureId.ClipboardIngest, () => { AutoClipboardIngest = true; return Task.CompletedTask; });
            StatusText += " " + AutomationPolicy.EmailIsFreeHint;
            return;
        }
        _settingsService.Current.AutoClipboardIngest = value; SaveSettingsDebounced();
    }
    partial void OnWatchFolderEnabledChanged(bool value)
    {
        if (value && !AutomationAllowed)
        {
#pragma warning disable MVVMTK0034
            _watchFolderEnabled = false;
#pragma warning restore MVVMTK0034
            OnPropertyChanged();
            ReportProGate(FeatureId.WatchFolder, () => { WatchFolderEnabled = true; return Task.CompletedTask; });
            StatusText += " " + AutomationPolicy.EmailIsFreeHint;
            return;
        }
        _settingsService.Current.WatchFolderEnabled = value; SaveSettingsDebounced();
    }
    partial void OnAutoConvertIngestsChanged(bool value)
    {
        if (value && !AutomationAllowed)
        {
#pragma warning disable MVVMTK0034
            _autoConvertIngests = false;
#pragma warning restore MVVMTK0034
            OnPropertyChanged();
            ReportProGate(FeatureId.AutoExportIngest, () => { AutoConvertIngests = true; return Task.CompletedTask; });
            StatusText += " " + AutomationPolicy.EmailIsFreeHint;
            return;
        }
        _settingsService.Current.AutoConvertIngests = value; SaveSettingsDebounced();
    }
    partial void OnWatchFolderChanged(string value) { _settingsService.Current.WatchFolder = value; SaveSettingsDebounced(); }
    partial void OnWatchFolderAutoConvertChanged(bool value) { _settingsService.Current.WatchFolderAutoConvert = value; SaveSettingsDebounced(); }
    partial void OnMinimizeToTrayChanged(bool value) { _settingsService.Current.MinimizeToTray = value; SaveSettingsDebounced(); }
    partial void OnAppendToRunningDocChanged(bool value) { _settingsService.Current.AppendToRunningDoc = value; SaveSettingsDebounced(); }
    partial void OnRunningDocPathChanged(string value) { _settingsService.Current.RunningDocPath = value; SaveSettingsDebounced(); }
    partial void OnShowExtensionTipChanged(bool value) { _settingsService.Current.ShowExtensionTip = value; SaveSettingsDebounced(); }
    partial void OnIncludeTocChanged(bool value) { _settingsService.Current.IncludeToc = value; SaveSettingsDebounced(); }
    partial void OnMermaidDocxModeChanged(int value) { _settingsService.Current.MermaidDocxMode = value; SaveSettingsDebounced(); }
    partial void OnOversizedDiagramModeChanged(int value) { _settingsService.Current.OversizedDiagramMode = value; SaveSettingsDebounced(); }
    partial void OnBrandCoverPageChanged(bool value) { _settingsService.Current.BrandCoverPage = value; SaveSettingsDebounced(); }
    partial void OnBrandLogoPathChanged(string value) { _settingsService.Current.BrandLogoPath = value; SaveSettingsDebounced(); }
    partial void OnBrandFontFamilyChanged(string value) { _settingsService.Current.BrandFontFamily = value; SaveSettingsDebounced(); }
    partial void OnBrandTemplatePathChanged(string value) { _settingsService.Current.BrandTemplatePath = value; SaveSettingsDebounced(); }
    partial void OnShowAttributionChanged(bool value) { _settingsService.Current.ShowAttribution = value; SaveSettingsDebounced(); }
    partial void OnNoEmojiChanged(bool value) { _settingsService.Current.NoEmoji = value; SaveSettingsDebounced(); }
    partial void OnDashModeChanged(int value) { _settingsService.Current.DashMode = value; SaveSettingsDebounced(); }
    partial void OnDashCustomChanged(string value) { _settingsService.Current.DashCustom = value; SaveSettingsDebounced(); }
    partial void OnHeadingShiftChanged(int value) { _settingsService.Current.HeadingShift = value; SaveSettingsDebounced(); }
    partial void OnBoldModeChanged(int value) { _settingsService.Current.BoldMode = value; SaveSettingsDebounced(); }
    partial void OnItalicModeChanged(int value) { _settingsService.Current.ItalicMode = value; SaveSettingsDebounced(); }
    partial void OnProModeChanged(bool value) { _settingsService.Current.ProMode = value; SaveSettingsDebounced(); }
    partial void OnHardwareAccelerationChanged(bool value) { _settingsService.Current.HardwareAcceleration = value; SaveSettingsDebounced(); }
    partial void OnApiEnabledChanged(bool value) { _settingsService.Current.ApiEnabled = value; SaveSettingsDebounced(); }
    partial void OnApiPortChanged(int value) { _settingsService.Current.ApiPort = value; SaveSettingsDebounced(); }
    partial void OnEnableStreamingApiChanged(bool value) { _settingsService.Current.EnableStreamingApi = value; SaveSettingsDebounced(); }
    partial void OnSkipLaunchVideoChanged(bool value) { _settingsService.Current.SkipLaunchVideo = value; SaveSettingsDebounced(); }
    partial void OnAllowedExtensionIdChanged(string value) { _settingsService.Current.AllowedExtensionId = value; SaveSettingsDebounced(); }
    partial void OnCloudAutoPublishChanged(bool value) { _settingsService.Current.CloudAutoPublish = value; SaveSettingsDebounced(); }
    partial void OnCloudProviderIdChanged(string value) { _settingsService.Current.CloudProviderId = value; OnPropertyChanged(nameof(IsWebDavProvider)); SaveSettingsDebounced(); }
    partial void OnCloudSubfolderChanged(string value) { _settingsService.Current.CloudSubfolder = value; SaveSettingsDebounced(); }
    partial void OnWebDavEndpointChanged(string value) { _settingsService.Current.WebDavEndpoint = value; SaveSettingsDebounced(); }
    partial void OnWebDavUserChanged(string value) { _settingsService.Current.WebDavUser = value; SaveSettingsDebounced(); }
    partial void OnWebDavTokenChanged(string value) { _settingsService.Current.WebDavToken = value; SaveSettingsDebounced(); }
    partial void OnPdfHeaderTemplateChanged(string value) { _settingsService.Current.PdfHeaderTemplate = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnPdfFooterTemplateChanged(string value) { _settingsService.Current.PdfFooterTemplate = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnPdfPageNumberPositionChanged(string value) { _settingsService.Current.PdfPageNumberPosition = value; SaveSettingsDebounced(); OnPropertyChanged(nameof(PdfFooterPreview)); }
    partial void OnFontPresetChanged(string value) { _settingsService.Current.FontPreset = value; SaveSettingsDebounced(); }
    partial void OnPdfEncryptChanged(bool value) { _settingsService.Current.PdfEncrypt = value; SaveSettingsDebounced(); }
    partial void OnPdfUserPasswordChanged(string value) { _settingsService.Current.PdfUserPassword = value; SaveSettingsDebounced(); }
    partial void OnPdfOwnerPasswordChanged(string value) { _settingsService.Current.PdfOwnerPassword = value; SaveSettingsDebounced(); }
    partial void OnPdfAllowPrintingChanged(bool value) { _settingsService.Current.PdfAllowPrinting = value; SaveSettingsDebounced(); }
    partial void OnPdfAllowCopyingChanged(bool value) { _settingsService.Current.PdfAllowCopying = value; SaveSettingsDebounced(); }
    partial void OnPdfAllowModifyingChanged(bool value) { _settingsService.Current.PdfAllowModifying = value; SaveSettingsDebounced(); }
    partial void OnMermaidEnabledChanged(bool value) { _settingsService.Current.MermaidEnabled = value; SaveSettingsDebounced(); }
    partial void OnSmartConnectorsChanged(bool value) { _settingsService.Current.SmartConnectors = value; SaveSettingsDebounced(); }
    partial void OnConnectorArrowheadChanged(string value) { _settingsService.Current.ConnectorArrowhead = value; SaveSettingsDebounced(); }
    partial void OnPageBorderChanged(bool value) { _settingsService.Current.PageBorder = value; SaveSettingsDebounced(); }
    partial void OnTrackChangesChanged(bool value) { _settingsService.Current.TrackChanges = value; SaveSettingsDebounced(); }
    partial void OnAuthorNameChanged(string value) { _settingsService.Current.AuthorName = value; SaveSettingsDebounced(); }
    partial void OnCustomFontPathChanged(string value) { _settingsService.Current.CustomFontPath = value; SaveSettingsDebounced(); }
    partial void OnAmbiguityModeChanged(int value) { _settingsService.Current.AmbiguityMode = value; SaveSettingsDebounced(); }
    partial void OnCheckForUpdatesOnStartupChanged(bool value) { _settingsService.Current.CheckForUpdatesOnStartup = value; SaveSettingsDebounced(); }
    partial void OnAutoInstallUpdatesOnLaunchChanged(bool value) { _settingsService.Current.AutoInstallUpdatesOnLaunch = value; SaveSettingsDebounced(); }
    partial void OnAutoRestartAfterUpdateChanged(bool value) { _settingsService.Current.AutoRestartAfterUpdate = value; SaveSettingsDebounced(); }
    partial void OnAutoFocusOnSplitChanged(bool value) { _settingsService.Current.AutoFocusOnSplit = value; SaveSettingsDebounced(); }
    partial void OnShowLineNumbersChanged(bool value) { _settingsService.Current.ShowLineNumbers = value; SaveSettingsDebounced(); }
    partial void OnShowWordCountChanged(bool value) { _settingsService.Current.ShowWordCount = value; SaveSettingsDebounced(); }
    partial void OnPortalFocusBlurChanged(bool value) { _settingsService.Current.PortalFocusBlur = value; SaveSettingsDebounced(); }
    partial void OnPortalSurroundBlurRadiusChanged(double value) { _settingsService.Current.PortalSurroundBlurRadius = value; SaveSettingsDebounced(); }
    partial void OnPortalInsideBlurChanged(bool value) { _settingsService.Current.PortalInsideBlur = value; SaveSettingsDebounced(); }
    partial void OnPortalInsideBlurRadiusChanged(double value) { _settingsService.Current.PortalInsideBlurRadius = value; SaveSettingsDebounced(); }

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
    public string BuildPreviewHtml(string markdown, bool interactive = false)
    {
        // Relative image paths ("images/chart.png") resolve against the open file's folder.
        using var images = DocumentImages.UseFolder(DocumentFolder);
        return _markdownHtml.Render(markdown, _settingsService.Current, CurrentTheme, LastClassification, interactive);
    }

    /// <summary>The folder of the open file, which relative image paths resolve against; null
    /// for pasted text.</summary>
    // The file source (!UsePasteSource) is the file itself; the paste source is the file only
    // while it is the open file's text being edited.
    public string? DocumentFolder => (!UsePasteSource || IsEditingOpenFile) && HasInputFile ? DocumentImages.FolderOf(InputFilePath) : null;

    /// <summary>Canvas-only render for the live in-place swap path — skips the HTML shell.</summary>
    public string? BuildPreviewCanvasHtml(string markdown) =>
        _markdownHtml.RenderCanvasOnly(markdown, _settingsService.Current, CurrentTheme, LastClassification);

    // Classification + normalization for content that did NOT arrive through IngestMarkdown —
    // manual paste and browsed/dropped files. Without this, the "Normalize AI formatting quirks"
    // toggle only worked for the automated ingest paths. Safe to call on already-ingested text:
    // normalization is idempotent, and the badge only updates when the new classification says
    // something stronger than what's already displayed (re-running on cleaned text scores lower
    // because most signals were just removed).
    /// <param name="forPreview">True from the live preview (UI thread): the cleanup-rule rows then show
    /// how many matches each rule made in this document.</param>
    public string PrepareMarkdown(string markdown, bool forPreview = false)
    {
        var classification = AppServices.LlmSource.Classify(markdown);

        // Correctness repairs (copy-artifact removal, math rescue, matrix recovery) ALWAYS run —
        // they fix corruption, not style, so a broken matrix or leaked <thinking> tag is cleaned up
        // whether or not the "Normalize AI quirks" toggle is on. Neither is gated on recognizing a
        // specific vendor either: they apply to AI-ish text with no ChatGPT/Gemini/Claude tells at
        // all, and each is a no-op when its pattern doesn't match, so running on Generic text is safe.
        (markdown, _) = AppServices.LlmSource.RepairArtifacts(markdown, classification);
        var ruleOutcomes = forPreview && NormalizeLlm ? new List<CleanupRuleOutcome>() : null;
        if (NormalizeLlm)
            (markdown, _) = AppServices.LlmSource.NormalizeStyle(markdown, classification, _settingsService.Current.CustomNormalizationRules, ruleOutcomes);
        if (forPreview) ShowNormalizationRuleOutcomes(ruleOutcomes);

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
            (text, _) = AppServices.LlmSource.NormalizeStyle(text, classification, _settingsService.Current.CustomNormalizationRules);

        LastClassification = classification;
        DetectedSourceText = classification.Source == LlmSource.Generic
            ? $"Ingested from {origin}"
            : $"{classification.SourceDescription} · {classification.Confidence}% · {classification.AppliedFixes.Count} fixes";

        _editorUndo.BreakBurst(); // an ingest must undo as its own step
        DetachFromOpenFile();
        PastedMarkdown = text;
        UsePasteSource = true;
        StatusText = classification.Source == LlmSource.Generic
            ? $"Ingested Markdown from {origin}."
            : $"Ingested from {origin} — detected {classification.SourceDescription} formatting" +
              (classification.AppliedFixes.Count > 0 ? $", applied {classification.AppliedFixes.Count} fixes." : ".");
        StatusSeverity = StatusSeverity.Success;
    }

    /// <summary>True when <paramref name="path"/> is the file open in the editor.</summary>
    public bool IsOpenDocument(string path) => !UsePasteSource && IsSameFile(path, InputFilePath);

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
        // resets the UI immediately rather than truly aborting an in-flight render call.
        // Handles asynchronous file write buffer latency during rapid source switching.
        _conversionCts?.Cancel();
        _exportAllCancelled = true;
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
            var outPath = PrepareOutputPath(sourceLabel, "pdf");
            var html = BuildPreviewHtml(markdown);
            // Pass the source so the PDF carries it like batch and auto-ingest PDFs do — without it
            // a PDF made with the main export button couldn't be reopened as Markdown.
            await _pdfExport.ExportAsync(Host, html, outPath, _settingsService.Current, markdown);
            CompleteExport("PDF", outPath, markdown, ct);
        });
    }

    public async Task ConvertToDocxAsync()
    {
        if (!AppServices.License.CanExportDocx)
        {
            ReportProGate(FeatureId.DocxExport, ConvertToDocxAsync);
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("DOCX", async ct =>
        {
            var outPath = PrepareOutputPath(sourceLabel, "docx");
            var settings = _settingsService.Current;
            var hasMermaid = markdown.Contains("```mermaid", StringComparison.Ordinal);

            // Large diagram? Ask (or honor the saved preference): keep mermaid's EXACT layout (Web
            // Layout view) or reflow to fit the printed page.
            List<Services.Mermaid.HarvestedDiagram?>? geometry = null;
            string? layoutNote = null;
            int? overrideMode = null;

            // Oversized-diagram handling is product-mandated to Aggressive Shrink (mode 4) always —
            // DocxExportService hard-forces it, so there is no Ask prompt and no mode-specific
            // messaging. Harvest exact geometry so the ShapeForge native-shape path can run.
            if (hasMermaid && settings.MermaidDocxMode == 1 && Host is not null)
            {
                geometry = await _mermaidHarvest.HarvestMermaidGeometryAsync(Host, markdown, settings, CurrentTheme);
                var usable = geometry?.Any(g => g is { IsEmpty: false }) == true;
                layoutNote = usable
                    ? "  (large diagram: aggressive shrink, fits on one page)"
                    : "  (couldn't read exact layout — reflowed to fit the page)";
                if (!usable) geometry = null; // couldn't read exact geometry — ShapeForge reflows instead
            }

            // Generic harvest: always harvest as a universal fallback for any mermaid fence.
            // Even "bespoke" types (flowchart, sequence, etc.) can fail the bespoke parser on
            // complex inputs — the generic SVG-primitive path guarantees native shapes, never a picture.
            List<Services.Mermaid.GenericDiagram?>? genericGeom = null;
            if (hasMermaid && settings.MermaidDocxMode == 1 && Host is not null)
                genericGeom = await _mermaidHarvest.HarvestGenericGeometryAsync(Host, markdown, settings, CurrentTheme);

            // Rasterize mermaid diagrams (Snapshot mode, ShapeForge's fallback, and non-flowchart
            // families) — the renderer needs the platform's web host, which the caller wires up.
            List<byte[]?>? mermaidImgs = null;
            if (hasMermaid && Host is not null)
                mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, settings, CurrentTheme);
            // Disclose applied AI-cleanup fixes as a Word comment (paste source is already normalized).
            var fixes = NormalizeLlm && UsePasteSource ? LastClassification?.AppliedFixes : null;
            var wasTrialBefore = AppServices.License.State.Edition == Models.Edition.Trial;
            await _docxExport.ExportAsync(markdown, outPath, settings, mermaidImgs, fixes, geometry, genericGeom, overrideMode);
            // The trial cap is enforced inside DocxExportService (the one chokepoint for every DOCX
            // path). Here we only detect the moment the trial was just SPENT so the status line can
            // say so — the note must never appear while the trial is still active (1st/2nd export).
            var trialSpentNow = wasTrialBefore && AppServices.License.State.Edition == Models.Edition.Free;
            var trialNote = trialSpentNow ? " (that was the last Word export of your trial; Word export now needs Pro)" : "";
            CompleteExport("DOCX", outPath, markdown, ct, layoutNote + trialNote);
        });
    }

    public async Task ConvertToPptxAsync()
    {
        if (!AppServices.License.CanExportPptx)
        {
            ReportProGate(FeatureId.PptxExport, ConvertToPptxAsync);
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("PPTX", async ct =>
        {
            var outPath = PrepareOutputPath(sourceLabel, PptxExportService.Extension);
            // Diagrams go on the slides as pictures; the renderer needs the preview engine.
            List<byte[]?>? diagrams = null;
            if (markdown.Contains("```mermaid", StringComparison.Ordinal) && Host is not null)
            {
                StatusText = "Drawing diagrams for the slides…";
                diagrams = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, _settingsService.Current, CurrentTheme);
                ct.ThrowIfCancellationRequested();
            }
            await _pptxExport.ExportAsync(markdown, outPath, _settingsService.Current, diagrams);
            CompleteExport("PPTX", outPath, markdown, ct);
        });
    }

    // EPUB is a free (ungated) format — no license check, mirrors the PPTX path.
    public async Task ConvertToEpubAsync()
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("EPUB", async ct =>
        {
            var outPath = PrepareOutputPath(sourceLabel, EpubExportService.Extension);
            // E-readers run no JavaScript: draw the diagrams here and ship them as images.
            List<byte[]?>? diagrams = null;
            if (markdown.Contains("```mermaid", StringComparison.Ordinal) && Host is not null)
            {
                StatusText = "Drawing diagrams for the e-book…";
                diagrams = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, _settingsService.Current, CurrentTheme);
                ct.ThrowIfCancellationRequested();
            }
            await _epubExport.ExportAsync(markdown, outPath, _settingsService.Current, null, diagrams);
            CompleteExport("EPUB", outPath, markdown, ct);
        });
    }

    // HTML is a free (ungated) format — renders the markdown through the full HTML pipeline and saves to disk.
    public async Task ConvertToHtmlAsync()
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("HTML", async ct =>
        {
            // "html", not ".html": ResolveOutputPath adds the dot (this used to write "Report..html").
            var outPath = PrepareOutputPath(sourceLabel, "html");
            // Embed the bundled mermaid/KaTeX/highlight.js: the preview loads them from an in-app
            // virtual host, which a browser opening the saved file can't reach.
            var html = StandaloneHtml.Inline(BuildPreviewHtml(markdown, interactive: false));
            await File.WriteAllTextAsync(outPath, html, ct);
            CompleteExport("HTML", outPath, markdown, ct);
        });
    }

    // Google Docs export: connect + export. Gated like DOCX (Pro/trial); needs the user's own
    // Google Cloud OAuth client (Settings → Google) and a completed device sign-in.
    public event Action<string>? GoogleDocCreated;

    public bool IsGoogleConfigured => AppServices.GoogleAuth.IsConfigured(_settingsService.Current);
    public bool IsGoogleConnected => IsGoogleConfigured && !string.IsNullOrWhiteSpace(_settingsService.Current.GoogleRefreshToken);

    [RelayCommand]
    private async Task ConnectGoogleAsync()
    {
        var s = _settingsService.Current;
        if (!AppServices.GoogleAuth.IsConfigured(s))
        {
            GoogleAuthStatus = "Google sign-in isn't configured yet (missing client credentials).";
            return;
        }
        GoogleAuthStatus = "Starting sign-in…";
        GoogleDeviceCode = "";
        GoogleVerifyUrl = "";
        try
        {
            var dc = await AppServices.GoogleAuth.StartDeviceCodeAsync(s);
            GoogleDeviceCode = dc.UserCode;
            GoogleVerifyUrl = dc.VerificationUrl;
            GoogleAuthStatus = $"1) Open {dc.VerificationUrl} · 2) enter code  {dc.UserCode}  · 3) allow access";

            var tok = await AppServices.GoogleAuth.PollForTokenAsync(s, dc.DeviceCode, dc.Interval, dc.ExpiresIn);
            GoogleRefreshToken = tok.RefreshToken;
            GoogleAccountEmail = await AppServices.GoogleAuth.FetchAccountEmailAsync(tok.AccessToken);
            SaveSettingsDebounced();
            GoogleAuthStatus = string.IsNullOrEmpty(GoogleAccountEmail)
                ? "Connected to Google — ready to export to Google Docs."
                : $"Connected as {GoogleAccountEmail} — ready to export to Google Docs.";
        }
        catch (GoogleAuthException ex) { GoogleAuthStatus = ex.Message; }
        catch (Exception ex) { GoogleAuthStatus = $"Sign-in failed: {ex.Message}"; }
    }

    [RelayCommand]
    private void SignOutGoogle()
    {
        GoogleRefreshToken = "";
        GoogleAccountEmail = "";
        GoogleAuthStatus = "Not connected";
        GoogleDeviceCode = "";
        GoogleVerifyUrl = "";
        SaveSettingsDebounced();
    }

    public async Task ConvertToGoogleDocsAsync()
    {
        if (!AppServices.License.CanExportDocx)
        {
            ReportProGate(FeatureId.DocxExport, ConvertToGoogleDocsAsync);
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        var s = _settingsService.Current;
        if (!AppServices.GoogleAuth.IsConfigured(s))
        {
            StatusText = "Google Docs export isn't configured — see Settings → Google.";
            StatusSeverity = StatusSeverity.Warning;
            return;
        }
        if (string.IsNullOrWhiteSpace(s.GoogleRefreshToken))
        {
            StatusText = "Connect your Google account first: Settings → Google → Connect.";
            StatusSeverity = StatusSeverity.Warning;
            return;
        }

        await RunConversionAsync("Google Docs", async ct =>
        {
            var token = await AppServices.GoogleAuth.RefreshAccessTokenAsync(s, s.GoogleRefreshToken, ct);

            // Native images for mermaid diagrams (like the Word path) — the web host renders them.
            List<byte[]?>? mermaidImgs = null;
            if (markdown.Contains("```mermaid", StringComparison.Ordinal) && Host is not null)
                mermaidImgs = await _mermaidHarvest.RenderMermaidPngsAsync(Host, markdown, s, CurrentTheme);

            var result = await AppServices.GoogleDocs.ExportAsync(
                markdown, s, CurrentTheme, token.AccessToken, mermaidImgs, sourceLabel,
                fetchRemoteImage: url => FetchRemoteImageAsync(url, ct), ct);

            GoogleDocCreated?.Invoke(result.Url);
            StatusText = $"Google Docs created: {result.Url}";
        });
    }

    private static async Task<byte[]?> FetchRemoteImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _imageClient.GetAsync(url, ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch { return null; }
    }

    partial void OnGoogleClientIdChanged(string value) { _settingsService.Current.GoogleClientId = value.Trim(); SaveSettingsDebounced(); OnPropertyChanged(nameof(IsGoogleConfigured)); OnPropertyChanged(nameof(IsGoogleConnected)); }
    partial void OnGoogleClientSecretChanged(string value) { _settingsService.Current.GoogleClientSecret = value; SaveSettingsDebounced(); }
    partial void OnGoogleRefreshTokenChanged(string value) { _settingsService.Current.GoogleRefreshToken = value; OnPropertyChanged(nameof(IsGoogleConnected)); }
    partial void OnGoogleAccountEmailChanged(string value) { _settingsService.Current.GoogleAccountEmail = value; }

    // Markdown is a free (ungated) format — the counterpart to the DOCX -> MD reverse pipeline:
    // import a Word file (or paste AI output), then save the recovered/cleaned source back out as a
    // canonical .md. Mirrors the EPUB path (no license check, same resolve/record/toast flow).
    public async Task ConvertToMarkdownAsync()
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("Markdown", async ct =>
        {
            var outPath = PrepareOutputPath(sourceLabel, MarkdownExportService.Extension);
            await _mdExport.ExportAsync(markdown, outPath, _settingsService.Current);
            CompleteExport("MD", outPath, markdown, ct);
        });
    }

    // One-click "Export all": produces every format the license allows (PDF always; DOCX/PPTX when
    // Pro) from the same resolved source, then reports a single combined summary. Each per-format
    // export runs through its own ConvertTo*Async (own error handling), so a failure in one format
    // never blocks the others — success is detected by the export-history count growing. The busy
    // state holds for the whole run (it used to drop between formats, re-enabling Export mid-run),
    // the status line counts "2 of 3", Cancel stops the remaining formats, and the summary keeps
    // each failure's reason instead of a bare "DOCX failed".
    public async Task ExportAllAsync()
    {
        var (markdown, _) = ResolveSource();
        if (markdown is null) return;

        var formats = new List<(string Kind, Func<Task> Export)> { ("PDF", ConvertToPdfAsync) };
        var skipped = new List<string>();
        if (AppServices.License.CanExportDocx) formats.Add(("DOCX", ConvertToDocxAsync)); else skipped.Add("DOCX");
        if (AppServices.License.CanExportPptx) formats.Add(("PPTX", ConvertToPptxAsync)); else skipped.Add("PPTX");

        IsBusy = true;
        _exportAllRunning = true;
        _exportAllCancelled = false;
        _suppressExportToasts = true; // one combined toast at the end, not one per format
        var done = new List<string>();
        var failures = new List<string>();
        string? firstOutput = null;

        try
        {
            for (var i = 0; i < formats.Count; i++)
            {
                if (_exportAllCancelled) break;
                _exportAllStep = $"Export all ({i + 1} of {formats.Count}): ";
                var (kind, export) = formats[i];
                var before = History.Count;
                try { await export(); }
                catch { /* per-format errors are already surfaced via the status bar */ }
                if (History.Count > before)
                {
                    done.Add(kind);
                    firstOutput ??= LastOutputPath;
                }
                else if (!_exportAllCancelled)
                {
                    failures.Add(StatusText.StartsWith(kind, StringComparison.Ordinal) ? StatusText : $"{kind} export failed.");
                }
            }
        }
        finally
        {
            _suppressExportToasts = false;
            _exportAllRunning = false;
            _exportAllStep = "";
            IsBusy = false;
        }

        if (done.Count > 0) RaiseExportCompleted(string.Join(" + ", done), LastOutputPath ?? string.Empty);

        var parts = new List<string>();
        if (done.Count > 0) parts.Add($"Exported {string.Join(" + ", done)}");
        if (_exportAllCancelled) parts.Add(done.Count > 0 ? "cancelled before the rest" : "Export all cancelled");
        parts.AddRange(failures);
        if (skipped.Count > 0) parts.Add($"{string.Join(" + ", skipped)} skipped (Pro)");
        var folder = firstOutput is null ? null : Path.GetDirectoryName(firstOutput);
        if (folder is not null && failures.Count == 0 && !_exportAllCancelled) parts.Add($"in {folder}");
        AnnounceExport(string.Join(" · ", parts), done.Count > 0 ? LastOutputPath : null);
        StatusSeverity = _exportAllCancelled ? StatusSeverity.Warning
            : failures.Count == 0 ? StatusSeverity.Success
            : done.Count > 0 ? StatusSeverity.Warning
            : StatusSeverity.Error;
    }

    private bool _exportAllRunning;
    private bool _exportAllCancelled;
    private string _exportAllStep = "";

    // The output path of the export in flight, so a failure message can name the file.
    private string? _pendingOutputPath;

    // ResolveOutputPath plus two up-front checks:
    //  - never write over the document being exported: with the default "{title}" template and an
    //    output folder next to the input, "Export as Markdown" resolved to the open .md itself and
    //    replaced the user's source with the normalized copy. Those exports get " (exported)".
    //  - fail fast when the target is open in another program (Word, Acrobat), rather than after a
    //    long render that then can't be written.
    internal string PrepareOutputPath(string sourceLabel, string extension)
    {
        var outPath = ResolveOutputPath(sourceLabel, extension);
        if (!UsePasteSource && IsSameFile(outPath, InputFilePath))
        {
            var dir = Path.GetDirectoryName(outPath) ?? "";
            outPath = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(outPath)} (exported).{extension}");
        }
        _pendingOutputPath = outPath;
        ExportFailureMessage.ThrowIfLocked(outPath);
        return outPath;
    }

    private static bool IsSameFile(string a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Services.PathEquality.Comparison); }
        catch { return false; }
    }

    // Shared tail of every single-format export. Checking the token first means an export the user
    // cancelled doesn't come back seconds later claiming "done", raising a toast and adding a history row.
    private void CompleteExport(string kind, string outPath, string markdown, CancellationToken ct, string note = "")
    {
        ct.ThrowIfCancellationRequested();
        LastOutputPath = outPath;
        if (!UsePasteSource) TrackRecent(InputFilePath);
        RecordExport(kind, outPath, markdown);
        RaiseExportCompleted(kind, outPath);
        // File name first: the status bar trims long text from the end, and the old
        // "PDF export done: C:/Users/.../a/long/folder/Report.pdf" lost the one part that mattered.
        // People say "Word document", not "DOCX".
        var label = kind switch { "MD" => "Markdown", "DOCX" => "Word document", "PPTX" => "PowerPoint deck", _ => kind };
        AnnounceExport($"{label} saved: {Path.GetFileName(outPath)}{note} · in {Path.GetDirectoryName(outPath)}", outPath);
    }

    /// <summary>Converts every document in <paramref name="sourceDir"/> (and its subfolders).</summary>
    public Task BatchConvertAsync(string sourceDir, string outputDir, string targetFormat) =>
        RunBatchAsync(() => AutomationExportService.FindBatchSources(sourceDir, recursive: true, outputDir), sourceDir, outputDir, targetFormat,
            () => BatchConvertAsync(sourceDir, outputDir, targetFormat));

    /// <summary>Converts the given files (several dropped on the window) into <paramref name="outputDir"/>.</summary>
    public Task BatchConvertFilesAsync(IReadOnlyList<string> files, string outputDir, string targetFormat) =>
        RunBatchAsync(() => files, null, outputDir, targetFormat, () => BatchConvertFilesAsync(files, outputDir, targetFormat));

    private async Task RunBatchAsync(Func<IReadOnlyList<string>> files, string? baseFolder, string outputDir, string targetFormat, Func<Task> resume)
    {
        var fmt = OutputFormats.Normalize(targetFormat);
        if (fmt is null)
        {
            StatusText = $"Batch convert can't write \"{targetFormat}\". Pick PDF, Word, PowerPoint, EPUB or an email draft as the default output format.";
            StatusSeverity = StatusSeverity.Error;
            return;
        }
        if (fmt == OutputFormats.Docx && !AppServices.License.CanExportDocx)
        {
            ReportProGate(FeatureId.DocxExport, resume);
            return;
        }
        if (!AutomationPolicy.Allows(AppServices.License.State, fmt))
        {
            ReportProGate(FeatureId.BatchConvert, resume);
            StatusText += " " + AutomationPolicy.EmailIsFreeHint;
            return;
        }

        var list = files();
        if (list.Count == 0)
        {
            StatusText = "There's nothing here MarkSmith can convert: no Markdown, text, Word, web page or email files.";
            StatusSeverity = StatusSeverity.Warning;
            return;
        }

        BatchConvertResult? result = null;
        await RunConversionAsync($"{list.Count} {(list.Count == 1 ? "file" : "files")}", async ct =>
        {
            result = await AppServices.BatchConvert.ConvertFilesAsync(Host, list, baseFolder, outputDir, fmt, _settingsService.Current,
                progressCallback: msg => { if (msg.StartsWith("Converting ", StringComparison.Ordinal)) StatusText = "Batch: " + msg; },
                recorded: (written, md, source) => RecordExport(OutputFormats.Kind(fmt), written, md, sourcePath: source),
                ct: ct);
            if (result.Cancelled) ct.ThrowIfCancellationRequested();
            if (result.Done > 0) LastOutputPath = result.Produced[^1];
        });
        if (result is { Cancelled: false } r) AnnounceBatch(r);
    }

    /// <summary>The finished batch on the status line: how many, the first failure and where the
    /// files went, with the Open button on the last file written.</summary>
    public void AnnounceBatch(BatchConvertResult result)
    {
        AnnounceExport(result.Summary(), result.Done > 0 ? result.Produced[^1] : null);
        StatusSeverity = result.Total == 0 ? StatusSeverity.Warning
            : result.Failed == 0 ? StatusSeverity.Success
            : result.Done > 0 ? StatusSeverity.Warning : StatusSeverity.Error;
    }

    private async Task RunConversionAsync(string kind, Func<CancellationToken, Task> work)
    {
        _conversionCts?.Cancel();
        _conversionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _conversionCts = cts;
        _pendingOutputPath = null;
        IsBusy = true;
        StatusText = $"{_exportAllStep}Converting to {kind}…";
        StatusSeverity = StatusSeverity.Informational;
        // Every exporter finds the document's relative images through this (DocumentImages).
        using var images = DocumentImages.UseFolder(DocumentFolder);
        try
        {
            await work(cts.Token);
            StatusSeverity = StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            // Cancel already reset the UI. If this run was cancelled (or replaced by a newer export
            // after Cancel re-enabled the buttons), its late result must not overwrite that state.
            if (cts.IsCancellationRequested || !ReferenceEquals(_conversionCts, cts))
            {
                if (ReferenceEquals(_conversionCts, cts)) { StatusText = "Cancelled."; StatusSeverity = StatusSeverity.Warning; }
            }
            else
            {
                StatusText = ex is OperationCanceledException ? "Cancelled." : ExportFailureMessage.Describe(kind, ex, _pendingOutputPath);
                StatusSeverity = ex is OperationCanceledException ? StatusSeverity.Warning : StatusSeverity.Error;
            }
        }
        finally
        {
            if (ReferenceEquals(_conversionCts, cts) && !_exportAllRunning) IsBusy = false;
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

        // A converted source (Word, PDF, HTML, email) exports the Markdown it opened as, not its bytes.
        var source = Plugins.PluginFileReader.IsMarkdownFile(InputFilePath) || string.IsNullOrEmpty(_cachedFileMarkdown)
            ? File.ReadAllText(InputFilePath)
            : _cachedFileMarkdown;
        return (PrepareMarkdown(source), Path.GetFileNameWithoutExtension(InputFilePath));
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
