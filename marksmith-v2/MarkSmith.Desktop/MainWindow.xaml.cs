using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;
using MarkSmith.Mermaid.Sync;
using Shortcuts = MarkSmith.Services.KeyboardShortcuts;

namespace MarkSmith;

public sealed partial class MainWindow : Window, Services.IWebRenderHost, Services.IUiPrompts
{
    private static readonly HashSet<string> PreviewAffectingProperties = new()
    {
        nameof(ViewModels.MainViewModel.PastedMarkdown),
        nameof(ViewModels.MainViewModel.InputFilePath),
        nameof(ViewModels.MainViewModel.UsePasteSource),
        nameof(ViewModels.MainViewModel.SelectedThemeName),
        nameof(ViewModels.MainViewModel.ThemeLightInfluence),
        // Preview as email: the toggle, and the Email options its header and body show.
        nameof(ViewModels.MainViewModel.PreviewAsEmail),
        nameof(ViewModels.MainViewModel.EmailTo),
        nameof(ViewModels.MainViewModel.EmailCc),
        nameof(ViewModels.MainViewModel.EmailSubjectTemplate),
        nameof(ViewModels.MainViewModel.EmailRepeatTitleInBody),
        nameof(ViewModels.MainViewModel.EmailAttachPdf),
        nameof(ViewModels.MainViewModel.EmailAttachDocx),
        nameof(ViewModels.MainViewModel.ContentWidth),
        nameof(ViewModels.MainViewModel.A4FixedWidth),
        nameof(ViewModels.MainViewModel.UnlimitedHeight),
        nameof(ViewModels.MainViewModel.IncludeToc),
        nameof(ViewModels.MainViewModel.ShowAttribution),
        nameof(ViewModels.MainViewModel.NoEmoji),
        nameof(ViewModels.MainViewModel.DashMode),
        nameof(ViewModels.MainViewModel.DashCustom),
        nameof(ViewModels.MainViewModel.HeadingShift),
        nameof(ViewModels.MainViewModel.BoldMode),
        nameof(ViewModels.MainViewModel.ItalicMode),
        nameof(ViewModels.MainViewModel.BrandFontFamily),
        // These change the rendered page too; without them the preview kept the old look until
        // the next keystroke (the reading-time pill, the fallback font, Mermaid on/off, the AI
        // cleanup and its custom rules, an embedded font file).
        nameof(ViewModels.MainViewModel.ShowWordCount),
        nameof(ViewModels.MainViewModel.FontPreset),
        nameof(ViewModels.MainViewModel.MermaidEnabled),
        nameof(ViewModels.MainViewModel.NormalizeLlm),
        nameof(ViewModels.MainViewModel.HasNormalizationRules),
        nameof(ViewModels.MainViewModel.CustomFontPath),
    };

    private static readonly HashSet<string> AutomationProperties = new()
    {
        nameof(ViewModels.MainViewModel.AutoClipboardIngest),
        nameof(ViewModels.MainViewModel.WatchFolderEnabled),
        nameof(ViewModels.MainViewModel.WatchFolder),
        nameof(ViewModels.MainViewModel.ApiEnabled),
        nameof(ViewModels.MainViewModel.ApiPort),
        // On the free plan automation only runs for email drafts, so the format decides it too.
        nameof(ViewModels.MainViewModel.TargetFormat),
    };

    private readonly DispatcherQueueTimer _previewDebounce;

    // Preview refresh intensity. Historically typing did a "light" refresh and paste/style changes a
    // "heavy" one (loading sprite over a blur); the sprite/blur visuals are retired — every refresh
    // now renders in plain sight — but the classification is kept as a future intensity signal.
    // PropertyChanged fires per keystroke, so a single edit's delta is our typing signal.
    private int _lastMarkdownLen;
    private bool _nextRefreshHeavy;
    // True while the mermaid snapshot renderer owns the WebView — preview refreshes (e.g. the ingest
    // debounce firing mid-harvest) must not navigate away from the render page.
    private bool _mermaidHarvestActive;
    // Diagram Studio node positions live as %% {"id":...} comment lines inside mermaid fences.
    // They're noise in the raw editor, so the editor shows stripped markdown and the removed
    // lines are stashed here (per mermaid block index) to be re-injected on save / studio open.
    private Dictionary<int, List<string>> _mermaidSpatialStash = new();
    // Single-instance studios (kept alive for the window's lifetime)
    private Views.SmartArtStudio.SmartArtDesignStudioWindow? _smartArtDesignStudio;
    private Views.ShapeStudio.ShapeDesignStudioWindow? _shapeDesignStudio;
    private Views.MindMap.MindMapGalaxyWindow? _mindMapGalaxyWindow;
    private const int HeavyChangeThreshold = 32; // chars changed in one edit above which it's a paste, not typing
    private readonly Services.ClipboardIngestService _clipboardIngest;
    private readonly Services.FolderIngestService _folderIngest;
    private readonly Services.AutomationManager _automationManager;
    private readonly Services.ExportCoordinator _exportCoordinator = new();
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private bool _exitRequested;
    private bool _showingLogsDialog;
    private List<string> _sessionLogFiles = new();

    // Preview loading spinner state. The spinner ticks at ~60fps and stays up for at least SpinMinSec
    // so a fast render never looks like a white flash. It hides only once BOTH the navigation has
    // completed and the minimum time passed. Two styles alternate each time (not random): mode 0 spins
    // the logo about its centre, mode 1 traces an upright figure-eight.
    private const double SpinMinSec = 0.65;
    private const double SpinDt = 1.0 / 60.0;
    private DispatcherQueueTimer? _spinTimer;
    private int _spinMode;         // 0 spin, 1 figure-eight — alternates on each show
    private double _spinPhase;     // seconds the spinner has been visible
    private bool _spinNavDone;
    private bool _spinActive;

    // Editor<->preview sync-scroll. The Code and Preview panes are alternating tabs, so "sync"
    // means carrying the scroll position across a tab switch instead of always landing at the top.
    // Forward (Code->Preview): we stash the editor's scroll fraction before re-navigating, then
    // apply it once NavigationCompleted fires. Reverse (Preview->Code): we read the WebView's
    // scroll fraction and mirror it onto the editor's internal ScrollViewer.
    private sealed record PreviewScrollState(double FractionX, double FractionY, double PixelX, double PixelY);
    private PreviewScrollState? _pendingPreviewScroll;

    // Find bar (Ctrl+F): the current query's match offsets into the editor text, and which match is
    // highlighted. Recomputed on every keystroke of the query so the "n/m" count stays live.
    private readonly List<int> _findMatches = new();
    private int _findMatchIndex = -1;

    // Preview zoom: WinUI 3's WebView2 exposes no ZoomFactor, so the page's own script scales the
    // sheet (fit-to-width, or an absolute zoom handed over via window.__msSetZoom). A
    // document-created listener turns Ctrl+wheel into a "preview-zoom" message so the buttons and
    // the wheel share one code path. _lastPreviewZoom is the scale currently on screen (the page
    // reports it back), so +/− always step from what the user is looking at.
    private double _lastPreviewZoom = 1.0;

    // Auto-recovery: the paste buffer is debounced-written to a recovery file so an unexpected exit
    // (crash, power loss, forced close) never loses an unsaved document; it's offered back on launch.
    // Batch 11 (#59): flows through AppPaths.ConfigDir like every other persisted state, so the
    // MARKSMITH_CONFIG_DIR redirect (test isolation / portable installs) is honored here too.
    private static readonly string RecoveryDir = Services.AppPaths.ConfigDir;
    private static readonly string RecoveryPath = Path.Combine(RecoveryDir, "autosave_recovery.md");
    private DispatcherQueueTimer? _autosaveTimer;

    // Extension channel heartbeat: refreshes the "extension connected" flag (the house-style .dotx
    // import in Settings is gated on it) and polls the reverse command channel for an AI-generated
    // theme posted back by the extension. Runs on the dispatcher so ViewModel mutations are safe.
    private DispatcherQueueTimer? _extensionHeartbeat;

    // Centre "Looking Glass" view mode: Code / Split (editor + preview side by side) / Preview.
    private enum ViewMode { Code, Split, Preview }
    private ViewMode _viewMode = ViewMode.Code;
    private bool _initializingCenterView;

    // Left-pane hover-drawer: after a document is selected the Source/Files pane collapses to a
    // 28px tab; hovering the tab re-expands it while the mouse is on it, and it tucks away the
    // moment the pointer leaves. The pre-collapse width is stashed so a custom splitter size is
    // preserved across expand/collapse cycles.
    private bool _leftPaneCollapsed;
    private bool _pointerOverLeftPane;
    private double _leftPaneExpandedWidth = 320;

    // Focus mode (F11): hides the left and right panes so the editor/preview takes the full width.
    // The pane widths (and MinWidths, which otherwise clamp the collapsed columns open) the user
    // sized with the splitters are stashed so they can be restored.
    private GridLength _savedLeftPaneWidth;
    private GridLength _savedRightPaneWidth;
    private double _savedLeftPaneMinWidth;
    private double _savedRightPaneMinWidth;

    // Markdown lint: issues found in the current document (refreshed on every edit).
    private List<Services.MarkdownLintService.LintIssue> _lintIssues = new();

    // Guards the word-wrap ToggleButton's Checked event from firing during start-up wiring.
    private bool _initializingWordWrap;

    // Guards the Looking Glass portal ToggleButton's Checked event during start-up wiring (ISS-004).
    private bool _initializingLookingGlass;

    // Guards the portal reveal-scope Slider's ValueChanged event during start-up wiring (ISS-004).
    private bool _initializingPortalReveal;

    // Looking Glass portal (ISS-004): true while a source-reveal portal is open in the preview.
    // Suppresses the debounced typing refresh so re-navigating the WebView doesn't destroy the
    // open portal mid-edit; the preview is refreshed once when the portal closes. _portalDirty
    // tracks whether the portal actually edited the source so we only re-render when needed.
    private bool _portalOpen;
    private bool _portalDirty;
    // Last markdown the preview canvas rendered (via navigation OR in-place swap) — lets the live
    // path skip no-op re-swaps, e.g. when a portal edit's own debounce echo comes back around.
    private string? _lastLiveCanvasMd;
    private string? _lastRenderedHtml;

    public IRelayCommand ShowWindowCommand { get; }
    public IRelayCommand ExitApplicationCommand { get; }

    private ViewModels.MainViewModel ViewModel => App.ViewModel;

    public MainWindow()
    {
        ShowWindowCommand = new RelayCommand(() =>
        {
            AppWindow.Show();
            Activate();
        });
        ExitApplicationCommand = new RelayCommand(() =>
        {
            _exitRequested = true;
            _trayIcon?.Dispose();
            Close();
        });

        InitializeComponent();

        Title = "MarkSmith";
        // Unpackaged app: the exe icon covers Explorer/taskbar, but the title bar needs an
        // explicit runtime assignment (relative paths resolve against the CWD, so anchor to base).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        SizeWindowForDisplay();
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootGrid.DataContext = ViewModel;
        ViewModel.Host = new BackgroundExportHostImpl(this);
        ViewModel.PutEmailOnClipboard = content =>
        {
            var package = new DataPackage();
            package.SetText(content.Text);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(content.Html));
            Clipboard.SetContent(package);
            Clipboard.Flush(); // stays pasteable after the app closes
        };

        // App-wide hover/press "lift" animation for every button already declared in XAML —
        // see Services/HoverPolish.cs. Flyout and ContentDialog content isn't in the tree yet
        // at this point, so those get polished individually where they're opened.
        Services.HoverPolish.Track(RootGrid);
        // The ⋯ menu tip is light-dismiss, which only a click triggers; a dialog opened from the
        // keyboard (Ctrl+,) left it floating above the dialog. Every dialog closes it first.
        Services.HoverPolish.DialogOpening += dialog =>
        {
            if (dialog.XamlRoot == Content.XamlRoot) MoreMenuTip.IsOpen = false;
        };

        // Ctrl+, opens Settings (the gear button's tooltip has always advertised it). Added in code
        // because VirtualKey has no named member for the comma key, so XAML can't spell it.
        var settingsAccelerator = new KeyboardAccelerator
        {
            Key = (Windows.System.VirtualKey)188, // VK_OEM_COMMA
            Modifiers = Windows.System.VirtualKeyModifiers.Control,
        };
        settingsAccelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            OnSettingsClick(this, new RoutedEventArgs());
        };
        RootGrid.KeyboardAccelerators.Add(settingsAccelerator);

        // Style-panel expanders auto-scroll their newly-revealed fields into view. Wired in
        // code-behind because the XAML Expanded="…" attribute crashes XamlCompiler (it exits 1 with
        // no output.json), whereas subscribing here is equivalent and build-safe. Note the control
        // exposes Expanding/Collapsed (there is no Expanded event in this Windows App SDK).
        ExportBrandingExpander.Expanding += OnStyleExpanderExpanded;
        // The subject preview follows the document; refresh it when the section comes into view
        // instead of on every keystroke.
        StyleEmailExpander.Expanding += (_, _) => ViewModel.RefreshEmailSubjectPreview();
        var subjectDebounce = DispatcherQueue.CreateTimer();
        subjectDebounce.Interval = TimeSpan.FromMilliseconds(400);
        subjectDebounce.IsRepeating = false;
        subjectDebounce.Tick += (_, _) => ViewModel.RefreshEmailSubjectPreview();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.CurrentMarkdown) && StyleEmailExpander.IsExpanded)
            {
                subjectDebounce.Stop();
                subjectDebounce.Start();
            }
        };
        WireStyleSectionMemory();

        // The editor and the document: the TextBox shows a view (folded regions collapse to one
        // line), the view model holds the whole document. Subscribed before every other TextChanged
        // handler so they all see the view model already updated, as they did with the binding.
        PasteTextBox.Text = ViewModel.CurrentMarkdown ?? string.Empty;
        PasteTextBox.TextChanged += (_, _) => SyncDocumentFromEditor();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.CurrentMarkdown)) SyncEditorFromDocument();
        };

        // Persistent undo/redo: the editor owns its undo stack (native TextBox undo is disabled in
        // XAML). Keep the caret in the ViewModel so undo snapshots can restore it exactly.
        PasteTextBox.SelectionChanged += (_, _) => ViewModel.EditorCaret = PasteTextBox.SelectionStart;

        // Ctrl+, opens Settings. This can't be a XAML KeyboardAccelerator: WinUI can't represent the
        // comma key in an accelerator (a raw "188" fails XAML parsing, and VirtualKey.OemComma crashes
        // the framework's accelerator-string builder — see microsoft-ui-xaml#708), so it's handled here.
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;

        // Live preview-width ruler (the hairline under the preview): report the pane's width in CSS
        // pixels as the user resizes. WebView2 maps 1 CSS px to 1 DIP at zoom 1, so ActualWidth is
        // the same number the rendered document sees for its own px-based page width.
        PreviewWebView.SizeChanged += (_, e) =>
            PreviewWidthText.Text = $"{(int)Math.Round(e.NewSize.Width)} px";

        // Editor cursor position readout in the status bar (Ln/Col + selection size).
        PasteTextBox.SelectionChanged += (_, _) => UpdateCursorPosition();
        // The gutter marks the caret's line, and re-wraps with the editor's width.
        PasteTextBox.SelectionChanged += (_, _) => QueueGutterLayout();
        PasteTextBox.SizeChanged += (_, _) => QueueGutterLayout();

        // Editor font-size zoom: apply the persisted size and let Ctrl+wheel adjust it live.
        ApplyEditorFontSize(App.Settings.Current.EditorFontSize, persist: false);
        PasteTextBox.PointerWheelChanged += OnEditorPointerWheel;

        // Centre view mode: restore the user's Code/Split/Preview choice so the code section can
        // stay hidden — the old behaviour forced the Code view open on every launch.
        var savedView = App.Settings.Current.EditorViewMode;
        var savedTab = savedView == "Preview" ? ViewPreviewTab : savedView == "Split" ? ViewSplitTab : ViewCodeTab;
        if (savedTab != ViewCodeTab)
        {
            _initializingCenterView = true;
            savedTab.IsSelected = true;
            _initializingCenterView = false;
        }

        // Word wrap + line numbers: apply the persisted wrap and line-number settings.
        _initializingWordWrap = true;
        WordWrapToggle.IsChecked = App.Settings.Current.EditorWordWrap;
        _initializingWordWrap = false;
        ApplyWordWrap(App.Settings.Current.EditorWordWrap, persist: false);

        _initializingLineNumbers = true;
        if (LineNumbersToggle != null) LineNumbersToggle.IsChecked = App.Settings.Current.ShowLineNumbers;
        if (LineGutter != null) LineGutter.Visibility = App.Settings.Current.ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
        _initializingLineNumbers = false;
        if (App.Settings.Current.ShowLineNumbers) UpdateLineNumbers();

        PasteTextBox.Loaded += (_, _) =>
        {
            if (FindEditorScrollViewer() is { } sv)
            {
                sv.ViewChanged += (_, _) => SyncLineGutterScroll();
            }
        };

        // ISS-004: reflect the persisted Looking Glass portal mode without firing a preview refresh.
        _initializingLookingGlass = true;
        LookingGlassToggle.IsChecked = App.Settings.Current.LookingGlassMode;
        _initializingLookingGlass = false;

        // ISS-004: reflect the persisted portal reveal scope + shape without firing the change handlers.
        _initializingPortalReveal = true;
        PortalRevealSlider.Value = App.Settings.Current.PortalRevealScope;
        foreach (var item in PortalShapeCombo.Items)
            if (item is ComboBoxItem ci && (ci.Tag as string) == App.Settings.Current.PortalShape)
            {
                PortalShapeCombo.SelectedItem = ci;
                break;
            }
        if (PortalShapeCombo.SelectedItem is null) PortalShapeCombo.SelectedIndex = 0; // unknown value -> Circle
        _initializingPortalReveal = false;

        _initializingPortalBlur = true;
        if (PortalInsideBlurSlider != null) PortalInsideBlurSlider.Value = App.Settings.Current.PortalInsideBlurRadius;
        if (PortalSurroundBlurSlider != null) PortalSurroundBlurSlider.Value = App.Settings.Current.PortalSurroundBlurRadius;
        _initializingPortalBlur = false;

        // Centre pane bottom bar: sync the portal row + editing clusters with the persisted
        // portal/view state now that both toggles are initialized.
        UpdateCenterBottomBar();

        // Find keeps focus in its own box, so the editor must still show the current match while
        // unfocused (the default not-focused highlight is invisible).
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accentObj) && accentObj is Windows.UI.Color accent)
            PasteTextBox.SelectionHighlightColorWhenNotFocused = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, accent.R, accent.G, accent.B));

        // Markdown lint refresh on every edit + the non-invasive SmartArt offer (debounced so a
        // paste of a long ChatGPT answer is scanned once, not per keystroke).
        PasteTextBox.TextChanged += (_, _) =>
        {
            UpdateLintIndicator();
            UpdateLineNumbers();
            UpdateFoldStatus();
            SyncLineGutterScroll();
            // An open find bar's "3 of 12" must follow edits, or Next jumps to stale offsets.
            if (FindBar.Visibility == Visibility.Visible) RecomputeFindMatches(keepPosition: true);
            // RULE: blank editor -> the left Source/Files pane is forcibly expanded again.
            if (string.IsNullOrWhiteSpace(PasteTextBox?.Text))
            {
                ExpandLeftPane();
            }
            else
            {
                // RULE: the moment the editor has content — even just typing it in — the left
                // Source/Files pane tucks away to regain that screen real estate for the
                // preview/code (paste and file-pick already do this; typing should too). Never
                // collapse while the pointer is over the drawer — the user may be mid-pick.
                if (!_leftPaneCollapsed && !_pointerOverLeftPane) AutoCollapseLeftPane();
            }
        };
        UpdateLintIndicator();

        // Export-completion toast: manual exports raise ExportCompleted from the ViewModel.
        ViewModel.ExportCompleted += (kind, path) => ShowExportToast(kind, path);

        // Google Docs exports create a doc in the cloud — open it in the default browser.
        ViewModel.GoogleDocCreated += url =>
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* browser open is best-effort */ }
        };

        // Typing in the paste editor fires PropertyChanged per keystroke; coalesce preview
        // reloads so WebView2 isn't re-navigated on every character.
        _previewDebounce = DispatcherQueue.CreateTimer();
        _previewDebounce.Interval = TimeSpan.FromMilliseconds(180);
        _previewDebounce.IsRepeating = false;

        _previewDebounce.Tick += async (_, _) =>
        {
            // Outline (Task 17): the TOC depends only on the markdown, so refresh it on the same
            // debounce as the preview — cheap, and keeps the flyout in step with what's rendered.
            ViewModel.RefreshToc();
            // ISS-004: while a portal is open a re-navigation would destroy it mid-edit, so the
            // update goes through the live path instead — push the editor's text into the portal's
            // textarea (split + portal: typed text appears inside the shape) and swap the preview
            // canvas in place behind it. The page-script rebuild happens when the portal closes.
            if (_portalOpen)
            {
                var md = ViewModel.CurrentMarkdown ?? "";
                if (PreviewWebView.CoreWebView2 is { } pcore)
                {
                    var js = "if (window.__portalUpdateSource) { window.__portalUpdateSource(" +
                             System.Text.Json.JsonSerializer.Serialize(md) + "); }";
                    try { await pcore.ExecuteScriptAsync(js); } catch { }
                }
                _portalDirty = true;
                await UpdatePreviewCanvasLiveAsync(md);
                return;
            }
            var heavy = _nextRefreshHeavy;
            _nextRefreshHeavy = false;
            // Typing-sized changes render in place — same live path the portal uses — so the page
            // never re-navigates under the reader; it falls back to a real refresh when the page
            // can't host the new content (e.g. first math/code block since the last navigation).
            // Heavy changes (paste / theme / layout) still rebuild the whole page.
            if (!heavy && await UpdatePreviewCanvasLiveAsync()) return;
            await RefreshPreviewAsync(heavy);
        };

        _spinTimer = DispatcherQueue.CreateTimer();
        _spinTimer.Interval = TimeSpan.FromMilliseconds(16);
        _spinTimer.IsRepeating = true;
        _spinTimer.Tick += (_, _) => OnSpinTick();

        _extensionHeartbeat = DispatcherQueue.CreateTimer();
        _extensionHeartbeat.Interval = TimeSpan.FromSeconds(5);
        _extensionHeartbeat.IsRepeating = true;
        _extensionHeartbeat.Tick += (_, _) => ViewModel.TickExtensionChannel();
        _extensionHeartbeat.Start();

        _clipboardIngest = new Services.ClipboardIngestService(DispatcherQueue, (text, origin, output) => IngestFromSource(text, origin, output), () => ViewModel.PastedMarkdown);
        _folderIngest = new Services.FolderIngestService(DispatcherQueue, path => _ = OnWatchedFileAsync(path));
        // ISS-011: surface the auto-detected AI-agent export folders as one-click watch presets.
        WatchFolderPresets.ItemsSource = Services.AiAgentFolderPresets.GetAvailablePresets();
        _automationManager = new Services.AutomationManager(
            App.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin, ovr) => DispatcherQueue.TryEnqueue(() => IngestFromSource(md, origin, ovr)),
            ConvertForApiAsync,
            App.Governance,
            () => App.Settings.Current.AllowedExtensionId,
            () => App.Settings.Current,
            settings => { App.Settings.Current.UpdateFrom(settings); App.Settings.Save(); },
            BatchConvertForApiAsync);
        // The browser extension's "Live app settings" page reads and changes these through the
        // view model, on this thread, so the panels update the moment the extension saves.
        _automationManager.ApiServer.ExtensionSettings = new Services.ExtensionSettingsBridge(
            ViewModel, () => ViewModel.ThemeNames.ToList(), RunOnUiAsync);
        _automationManager.ApiServer.OpenEmailDraft = OpenEmailDraftForApiAsync;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        WireStreamingApi();

        // Expanded editing bar: when the bottom bar has room, common actions become direct buttons
        // instead of hiding under the cluster dropdowns (split view, full code mode, fullscreen).
        BuildEditingExpandedButtons();
        // The centre column also changes width when a splitter is dragged, not just on window
        // resize — so follow the bar itself.
        CenterBottomBar.SizeChanged += (_, e) => { if (e.PreviousSize.Width != e.NewSize.Width) UpdateCenterBottomBar(); };
        RootGrid.SizeChanged += (_, e) => { if (e.PreviousSize.Width != e.NewSize.Width) FitRightPane(); };
        UpdateCenterBottomBar();
        App.License.Changed += () => DispatcherQueue.TryEnqueue(UpdateLicenseBanner);
        // Standardized pro-gate: any PRO feature a free user attempts raises this; the shell shows
        // the modal with trial/upgrade actions (non-UI hosts get only the StatusText fallback).
        ViewModel.ProFeatureAttempted += feature => DispatcherQueue.TryEnqueue(() => _ = ShowProGateAsync(feature));
        SyncSourcePanels();
        ApplyAutomationSettings();
        UpdateLicenseBanner();
        ExtensionTip.IsOpen = ViewModel.ShowExtensionTip;
        HistoryList.ItemsSource = ViewModel.History; // Flyout popups don't inherit DataContext
        TocList.ItemsSource = ViewModel.TocEntries; // Outline flyout (Task 17) — same reason
        ViewModel.TocEntries.CollectionChanged += (_, _) => UpdateOutlineEmptyState();
        UpdateOutlineEmptyState();
        ViewModel.RefreshToc();
        InitTrayIcon();

        AppWindow.Closing += (sender, e) =>
        {
            if (ViewModel.MinimizeToTray && !_exitRequested)
            {
                e.Cancel = true;
                AppWindow.Hide(); // watchers + API keep running; tray icon brings it back
                return;
            }

            if (_sessionLogFiles.Count > 0 && !_showingLogsDialog)
            {
                e.Cancel = true;
                _showingLogsDialog = true;
                _ = ShowDebugLogsDialogAndExitAsync();
            }
        };

        Closed += (_, _) =>
        {
            // Persistent undo: write every document's undo/redo stacks so Ctrl+Z keeps working
            // after the app is closed and re-opened.
            ViewModel.SaveUndoHistory();
            _clipboardIngest.Dispose();
            _folderIngest.Dispose();
            _automationManager.Dispose();
            _trayIcon?.Dispose();
        };

        ViewModel.LoadPresets();
        _ = InitializePreviewAsync();
        _ = LoadMarkdownFilesAsync(); // scan for real .md files in the background

        // First-run: show the guided tour once the visual tree is ready (XamlRoot available).
        if (!App.Settings.Current.HasSeenWelcome)
            RootGrid.Loaded += OnFirstRunLoaded;

        // Launch counter for the ⋯-menu tip jar nudge: by the third launch they're clearly getting
        // value, so point out — once — that Buy Me a Coffee (and everything else) lives in that menu.
        // Skipped on a first run, where the post-tour tip already introduces the menu.
        App.Settings.Current.LaunchCount++;
        App.Settings.Save();
        if (App.Settings.Current.HasSeenWelcome &&
            App.Settings.Current.LaunchCount >= 3 &&
            !App.Settings.Current.HasSeenCoffeeReminder)
            RootGrid.Loaded += OnCoffeeReminderLoaded;

        // Start in the editor. Without this WinUI focuses the first tab stop — the licence banner's
        // "Start 3-export trial" button, drawn with a focus rectangle on every launch — so typing
        // or Ctrl+V right after opening the app went nowhere. Registered before the recovery and
        // tour handlers so their dialogs open after it and hand focus back to the editor on close.
        RootGrid.Loaded += OnInitialFocusLoaded;

        // Auto-recovery: offer back any unsaved document that survived the previous session.
        RootGrid.Loaded += OnRecoveryCheckLoaded;

        // ISS-009: public beta time-bomb — once the cutoff passes, block the app with the
        // feedback prompt instead of letting a stale build keep converting documents.
        RootGrid.Loaded += OnBetaExpirationCheckLoaded;
    }

    private void OnBetaExpirationCheckLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnBetaExpirationCheckLoaded; // one-shot
        DispatcherQueue.TryEnqueue(() => _ = CheckAndEnforceBetaExpirationAsync());
    }

    // ISS-009: the pure cutoff check lives in Core (Services.BetaExpirationGuard); here we surface
    // the WinUI prompt and hard-stop the app either way — Primary opens the feedback page first.
    private async Task CheckAndEnforceBetaExpirationAsync()
    {
        if (!Services.BetaExpirationGuard.IsBetaExpired()) return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "⏰ Public Beta Period Completed",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Thank you for testing the MarkSmith Public Beta! The 30-day feedback period has ended. Please submit your feedback and download the latest build to continue converting documents."
            },
            PrimaryButtonText = "Submit Feedback & Get Update",
            CloseButtonText = "Close App",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.BetaExpirationGuard.FeedbackUrl));
        }

        Environment.Exit(0);
    }

    private void OnInitialFocusLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnInitialFocusLoaded; // one-shot
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (PasteTextBox is { Visibility: Visibility.Visible, ActualWidth: > 0 })
                PasteTextBox.Focus(FocusState.Programmatic);
        });
    }

    private void OnRecoveryCheckLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRecoveryCheckLoaded; // one-shot
        DispatcherQueue.TryEnqueue(() => _ = CheckRecoveryAsync());
    }

    private void OnFirstRunLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnFirstRunLoaded; // one-shot
        DispatcherQueue.TryEnqueue(() => _ = ShowWelcomeTourAsync());
    }

    // Third-launch tip jar nudge (see constructor). One-shot and once ever, gated on the
    // persisted HasSeenCoffeeReminder flag set here before showing.
    private void OnCoffeeReminderLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnCoffeeReminderLoaded; // one-shot
        App.Settings.Current.HasSeenCoffeeReminder = true;
        App.Settings.Save();
        DispatcherQueue.TryEnqueue(() => ShowMoreMenuTip(
            "Enjoying MarkSmith?",
            "Third launch already — glad it's earning its keep! If it saves you time, there's a ☕ Buy Me a Coffee in this menu. Tour, shortcuts and settings live here too."));
    }

    // Points the TeachingTip at the ⋯ menu with the given copy. Used by the first-run intro
    // (post-tour) and the third-launch tip jar reminder.
    private void ShowMoreMenuTip(string title, string subtitle)
    {
        MoreMenuTip.Title = title;
        MoreMenuTip.Subtitle = subtitle;
        MoreMenuTip.IsOpen = true;
    }

    // AppWindow sizes are physical pixels, so the old fixed Resize(1220, 800) opened at ~813×533
    // DIPs on a 150%-scaled laptop — narrower than the three panes need, so the Style & Export
    // pane ran off the edge on first launch. Size in DIPs scaled by the window's DPI, clamp to the
    // work area, and set a matching minimum so the layout can never be squeezed past the point
    // where it breaks (320 Source + 400 editor + 290 Style & Export + splitters and padding).
    private void SizeWindowForDisplay()
    {
        const int designWidth = 1220, designHeight = 800, minWidth = 1120, minHeight = 640;
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;

        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = System.Math.Min((int)(designWidth * scale), work.Width);
        var height = System.Math.Min((int)(designHeight * scale), work.Height);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = System.Math.Min((int)(minWidth * scale), work.Width);
            presenter.PreferredMinimumHeight = System.Math.Min((int)(minHeight * scale), work.Height);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnAppTitleBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Logo + name + dot + tagline need ~330px; below that, drop the dot and tagline whole.
        var show = e.NewSize.Width >= 340;
        TitleTagline.Visibility = TitleTaglineDot.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // Once the tagline has gone and space is still short, the palette button drops its label
        // (icon + Ctrl+K keycap stay) so the drag region never shrinks to nothing.
        // Hiding the label gives the drag region its ~110px back, so it only returns once there's
        // room for it again; one threshold would flip the label on and off forever.
        if (CommandPaletteLabel is not null)
        {
            var labelShown = CommandPaletteLabel.Visibility == Visibility.Visible;
            if (labelShown && e.NewSize.Width < 200) CommandPaletteLabel.Visibility = Visibility.Collapsed;
            else if (!labelShown && e.NewSize.Width >= 330) CommandPaletteLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnCommandPaletteClick(object sender, RoutedEventArgs e) => _ = ShowCommandPaletteAsync();

    private void OnEditorFindClick(object sender, RoutedEventArgs e) => ShowFindBar();

    private void OnEditorReplaceClick(object sender, RoutedEventArgs e) => ShowFindBar(replace: true);

    // "Export history" moved into the ⋯ menu but kept its rich ListView flyout: the menu item
    // re-opens it as the button's attached flyout (enqueued so the closing menu doesn't eat it).
    private void OnExportHistoryMenuClick(object sender, RoutedEventArgs e)
    {
        // An empty ListView opened as a bare title with a blank box under it.
        var empty = ViewModel.History.Count == 0;
        HistoryEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        DispatcherQueue.TryEnqueue(() =>
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(MoreMenuButton));
    }

    // WebSocket streaming for the local REST API (opt-in via Settings > Local REST API > Enable
    // WebSocket streaming, OFF by default). While connected, clients receive live status/busy
    // events and can pull a preview snapshot or stream text into the editor.
    private void WireStreamingApi()
    {
        var api = _automationManager.ApiServer;
        api.PreviewHtmlProvider = () => ViewModel.BuildPreviewHtml(ViewModel.PastedMarkdown ?? "");
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ViewModel.StatusText) or nameof(ViewModel.IsBusy))
                api.PublishStreamEvent(new { type = "status", text = ViewModel.StatusText, busy = ViewModel.IsBusy });
        };
    }

    // The expanded editing bar: one icon button per common inline action (the same handlers the
    // cluster dropdowns use), visible whenever the bottom bar is wide enough to fit them.
    private void BuildEditingExpandedButtons()
    {
        // Visual cleanup: the 15 actions read as one noisy wall when expanded. They are now laid
        // out in four logical bands — text styles, headings, lists, inserts — separated by the
        // same subtle 1px divider the cluster dropdowns use, with uniform 30x32 buttons.
        // Headings keep their H1–H4 letterforms; everything else uses the same Fluent icon as its
        // entry in the cluster menus (Bold / Italic / Strikethrough included — a 12px italic "I"
        // read as a slash) instead of the old "Img" / "Tbl" / "<>" text stand-ins.
        const string numberedListPath = "M1,1 h1 v1 h-1 Z M0,2 h2 v1 h-2 Z M1,3 h1 v1 h-1 Z M1,4 h1 v1 h-1 Z M0,5 h3 v1 h-3 Z M0,9 h2 v1 h-2 Z M2,10 h1 v1 h-1 Z M1,11 h1 v1 h-1 Z M0,12 h1 v1 h-1 Z M0,13 h3 v1 h-3 Z M5,3 h11 v1.5 h-11 Z M5,11 h11 v1.5 h-11 Z";
        (Func<UIElement> Content, string Tip, RoutedEventHandler Click)[] actions =
        {
            (() => Glyph("\uE8DD"), Shortcuts.Tip("Bold", "format.bold"), OnBoldClick),
            (() => Glyph("\uE8DB"), Shortcuts.Tip("Italic", "format.italic"), OnItalicClick),
            (() => Glyph("\uEDE0"), "Strikethrough", OnStrikethroughClick),
            (() => Letter("H1"), Shortcuts.Tip("Heading 1", "format.h1"), OnH1Click),
            (() => Letter("H2"), Shortcuts.Tip("Heading 2", "format.h2"), OnH2Click),
            (() => Letter("H3"), Shortcuts.Tip("Heading 3", "format.h3"), OnH3Click),
            (() => Letter("H4"), Shortcuts.Tip("Heading 4", "format.h4"), OnH4Click),
            (() => Glyph("\uE8FD"), "Bullet list", OnBulletListClick),
            (() => new PathIcon { Data = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Microsoft.UI.Xaml.Media.Geometry), numberedListPath) }, "Numbered list", OnNumberedListClick),
            (() => Glyph("\uE73A"), "Task list", OnTaskListClick),
            (() => Glyph("\uE9B1"), "Blockquote", OnBlockquoteClick),
            (() => Glyph("\uE71B"), "Insert link", OnLinkClick),
            (() => Glyph("\uEB9F"), "Insert image", OnImageClick),
            (() => Glyph("\uE80A"), "Insert table", OnTableClick),
            (() => Glyph("\uE943"), "Code block", OnCodeBlockClick),
        };

        static UIElement Letter(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        static UIElement Glyph(string glyph) => new FontIcon { Glyph = glyph, FontSize = 14 };

        for (int i = 0; i < actions.Length; i++)
        {
            var (content, tip, click) = actions[i];
            var button = new Button
            {
                Content = content(),
                Width = 30,
                Height = 32,
                Padding = new Thickness(0),
            };
            // Name from the tooltip's words (minus the shortcut), not the glyph/letterform.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip.Split(" (")[0]);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tip);
            button.Click += click;
            EditingExpandedPanel.Children.Add(button);
            if (i is 2 or 6 or 10) // band ends: text styles, headings, lists
            {
                EditingExpandedPanel.Children.Add(Divider());
            }
        }

        // Everything else the Insert and Tools clusters hold — rich components, Diagram & Galaxy
        // (Shape Studio, SmartArt, Diagram Studio), version history, references, and the case /
        // sort / clean-up tools — was unreachable while the bar was expanded, which is the default
        // at normal width in Code mode. The same flyouts hang off two compact dropdowns instead.
        EditingExpandedPanel.Children.Add(Divider());
        EditingExpandedPanel.Children.Add(MenuButton("", "More to insert",
            "More to insert — rich components, diagrams, Shape Studio, references", InsertClusterButton.Flyout));
        EditingExpandedPanel.Children.Add(MenuButton("", "Tools",
            "Tools — transform selected text, sort lines, clean up the document", ToolsClusterButton.Flyout));

        Border Divider() => new()
        {
            Width = 1,
            Height = 16,
            Background = ResolveDividerBrush(EditingExpandedPanel.ActualTheme),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        static DropDownButton MenuButton(string glyph, string name, string tip, Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
        {
            var button = new DropDownButton
            {
                Content = Glyph(glyph),
                Height = 32,
                Padding = new Thickness(7, 0, 5, 0),
                Flyout = flyout,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tip);
            return button;
        }
    }

    // The clusters' divider is a ThemeResource in XAML; this mirrors it from code so the expanded
    // bar's bands stay visually identical to the collapsed layout's separators. CardStrokeColor
    // lives in the WinUI theme dictionaries, so resolve via the active theme dictionary first
    // (a plain TryGetValue on Resources can miss theme-dictionary-only keys), then fall back.
    // The clusters' divider is a ThemeResource in XAML; this mirrors it from code so the expanded
    // bar's bands stay visually identical to the collapsed layout's separators. The key lives in the
    // WinUI theme dictionaries, so resolve via the ACTUAL theme of the bar (RequestedTheme can be
    // Default even when the effective theme is dark) with a neutral fallback.
    private static Microsoft.UI.Xaml.Media.Brush ResolveDividerBrush(ElementTheme actualTheme)
    {
        var app = Microsoft.UI.Xaml.Application.Current;
        if (app is not null)
        {
            var theme = actualTheme == ElementTheme.Dark ? "Dark" : "Light";
            if (app.Resources.ThemeDictionaries.TryGetValue(theme, out var dictObj) &&
                dictObj is Microsoft.UI.Xaml.ResourceDictionary dict &&
                dict.TryGetValue("CardStrokeColorDefaultBrush", out var value) &&
                value is Microsoft.UI.Xaml.Media.Brush brush)
            {
                return brush;
            }
            if (app.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var direct) &&
                direct is Microsoft.UI.Xaml.Media.Brush directBrush)
            {
                return directBrush;
            }
        }
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(90, 128, 138, 158));
    }


    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.UsePasteSource))
        {
            SyncSourcePanels();
        }

        // Auto-recovery: any edit to the paste buffer (or a switch into/out of paste mode) re-arms
        // the debounced autosave that mirrors the buffer to the recovery file.
        if (e.PropertyName == nameof(ViewModels.MainViewModel.PastedMarkdown) ||
            e.PropertyName == nameof(ViewModels.MainViewModel.UsePasteSource))
        {
            ScheduleAutosave();
        }

        if (e.PropertyName == nameof(ViewModels.MainViewModel.DetectedSourceText))
        {
            DetectedBadge.Visibility = string.IsNullOrEmpty(ViewModel.DetectedSourceText)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        // PageBorder is a preview-affecting setting: toggling it must refresh the preview
        // immediately (it renders the page frame) without waiting for a manual re-render.
        if (e.PropertyName == nameof(ViewModels.MainViewModel.PageBorder))
        {
            _ = RefreshPreviewAsync();
        }

        if (e.PropertyName is not null && AutomationProperties.Contains(e.PropertyName))
        {
            ApplyAutomationSettings();
        }

        if (e.PropertyName is not null && PreviewAffectingProperties.Contains(e.PropertyName))
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.PastedMarkdown))
            {
                var len = ViewModel.PastedMarkdown?.Length ?? 0;
                if (Math.Abs(len - _lastMarkdownLen) > HeavyChangeThreshold)
                {
                    _nextRefreshHeavy = true;
                    MaybeShowPlainPasteHint(ViewModel.PastedMarkdown ?? "");
                }
                _lastMarkdownLen = len;
            }
            else
            {
                _nextRefreshHeavy = true; // theme / width / TOC / formatting — a visible re-render
            }
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }
    }

    private bool _plainPasteHintShown;

    // A sizeable paste with no Markdown structure at all almost certainly came from plain
    // copy-paste out of an AI chat — the formatting is already lost. Nudge once per session toward
    // the extension's "Copy as Markdown" button, and bump /api/attention so that button pulses in
    // the browser right now.
    private void MaybeShowPlainPasteHint(string text)
    {
        if (_plainPasteHintShown || text.Length < 250) return;
        if (!LooksLikePlainText(text)) return;
        _plainPasteHintShown = true;
        ExtensionHintBar.IsOpen = true;
        Services.ApiServer.AttentionTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool LooksLikePlainText(string t)
    {
        if (t.Contains("```") || t.Contains("](") || t.Contains("**") || t.Contains("【")
            || t.Contains(":::") || t.Contains("$$") || t.Contains(@"\(") || t.Contains(@"\[")
            || t.Contains("=== \"") || t.Contains("[^") || t.Contains("~~") || t.Contains("==")
            || t.Contains(@"\begin{") || t.Contains("<table") || t.Contains("<div") || t.Contains("<span"))
            return false;

        foreach (var raw in t.Split('\n', '\r'))
        {
            var s = raw.TrimStart();
            if (s.StartsWith('#') || s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("> ")
                || s.StartsWith('|') || s.StartsWith(":::") || s.StartsWith("- [") || s.StartsWith("* [")
                || s.StartsWith("+ ") || s.StartsWith(": ")
                || (s.Length > 2 && char.IsDigit(s[0]) && s[1] == '.' && s[2] == ' '))
                return false; // any structural markdown or special MarkSmith syntax → not a plain paste
        }
        return true;
    }



    // The licence banner and everything else that depends on the edition: hidden for Pro, a quiet
    // export counter during the trial, and on Free one informational line with the trial (or Buy)
    // action. The copy comes from Core ProGate so it matches the upgrade dialog and status bar.
    private void UpdateLicenseBanner()
    {
        var st = App.License.State;
        UpdateExportButtonForLicense(st);
        if (st.Edition == Models.Edition.Pro) { LicenseBanner.IsOpen = false; return; }

        var (title, message, action) = Models.ProGate.Banner(st);
        // Informational, not Warning: being on the free plan isn't something that went wrong, and
        // a yellow bar on every launch read like an error.
        LicenseBanner.Severity = InfoBarSeverity.Informational;
        LicenseBanner.Title = title;
        LicenseBanner.Message = message;
        if (LicenseActionButton is not null)
        {
            LicenseActionButton.Click -= OnStartTrialClick;
            LicenseActionButton.Click -= OnUpgradeClick;
            if (action is null)
            {
                LicenseActionButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                var trial = action == Models.ProGate.StartTrialLabel;
                LicenseActionButton.Content = action;
                LicenseActionButton.Click += trial ? OnStartTrialClick : OnUpgradeClick;
                ToolTipService.SetToolTip(LicenseActionButton, trial
                    ? Models.ProGate.TrialSummary
                    : "Open the MarkSmith Pro purchase page in your browser");
                LicenseActionButton.Visibility = Visibility.Visible;
            }
        }
        LicenseBanner.IsOpen = true;
    }

    // The main export button runs what the current license can actually do: Word for Pro and the
    // trial, PDF on Free. The flyout's Pro-only items say "Pro" beside their shortcut, and "Export
    // all" is hidden on Free, where it could only ever produce the same PDF as the item above it.
    private void UpdateExportButtonForLicense(Models.LicenseState st)
    {
        var word = Models.ProGate.PrimaryExportIsWord(st);
        PrimaryExportText.Text = Models.ProGate.PrimaryExportLabel(st);
        PrimaryExportIcon.Glyph = word ? "\uE74E" : "\uE749";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExportSplitButton, word ? "Generate Word document" : "Generate PDF");
        ToolTipService.SetToolTip(ExportSplitButton, Models.ProGate.PrimaryExportTip(st));

        ExportWordItem.KeyboardAcceleratorTextOverride =
            Models.ProGate.MenuTag(Models.FeatureId.DocxExport, st, Shortcuts.KeysFor("export.docx"));
        ExportPdfItem.KeyboardAcceleratorTextOverride = Shortcuts.KeysFor("export.pdf");
        ExportPptxItem.KeyboardAcceleratorTextOverride =
            Models.ProGate.MenuTag(Models.FeatureId.PptxExport, st, Shortcuts.KeysFor("export.pptx"));
        ExportGoogleDocsItem.KeyboardAcceleratorTextOverride = Models.ProGate.MenuTag(Models.FeatureId.DocxExport, st, "");
        ExportAllItem.Visibility = st.CanExportDocx || st.CanExportPptx ? Visibility.Visible : Visibility.Collapsed;
    }

    // Start the 3-export trial straight from the banner (also the trigger point for testing the
    // free -> trial transition without digging into Settings).
    private void OnStartTrialClick(object sender, RoutedEventArgs e) => StartTrialFromShell();

    private bool StartTrialFromShell()
    {
        var (ok, message) = App.License.StartTrial();
        ViewModel.StatusText = message;
        ViewModel.StatusSeverity = ok ? Models.StatusSeverity.Success : Models.StatusSeverity.Warning;
        return ok;
    }

    private async void OnUpgradeClick(object sender, RoutedEventArgs e)
    {
        if (!Services.LicenseService.IsStoreConfigured)
        {
            ViewModel.StatusText = "The online store link isn't configured yet.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
            return;
        }
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.CheckoutUrl(App.License.State.Email))); }
        catch { /* no browser / bad uri */ }
    }

    // ------------------------------------------------------------------ persistent undo/redo
    // Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z while the editor is focused. The accelerators shadow the
    // TextBox's native undo (Handled=true) — native undo is in-memory only and dies on restart,
    // whereas the app-owned stack survives close/reopen and mode switches (Code/Split/Preview/
    // Looking Glass share one TextBox).
    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // ALWAYS mark handled: even with an empty app stack, Ctrl+Z must never fall through to the
        // TextBox's native undo — it has its own (never-populated) stack, so falling through would
        // pop a "ghost" step that moves the text the wrong way.
        args.Handled = true;
        var snap = ViewModel.UndoStep();
        if (snap is null) return;
        ApplyUndoSnapshot(snap);
    }

    private void OnRedoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        var snap = ViewModel.RedoStep();
        if (snap is null) return;
        ApplyUndoSnapshot(snap);
    }

    private void ApplyUndoSnapshot(Services.UndoSnapshot snap)
    {
        PasteTextBox.Text = snap.Text; // binding round-trip is deduped by the history service
        PasteTextBox.SelectionStart = Math.Clamp(snap.Caret, 0, PasteTextBox.Text.Length);
        PasteTextBox.SelectionLength = 0;
        ViewModel.EditorCaret = PasteTextBox.SelectionStart;
        _ = RefreshPreviewAsync(); // undo/redo changes the source — keep the preview honest
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Models.ExportPreset preset })
            ViewModel.ApplyPreset(preset);
    }

    private async void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "e.g. Client report — dark, branded", Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Save preset",
            Content = new StackPanel { Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Save the current theme, width, cleanup, formatting, diagram mode and branding as a named preset." }, box } },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            ViewModel.SavePreset(box.Text);
            ViewModel.StatusText = $"Preset saved: {box.Text.Trim()}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetsCombo.SelectedItem is Models.ExportPreset preset)
        {
            ViewModel.DeletePreset(preset);
            PresetsCombo.SelectedItem = null;
        }
    }

    private void OnToggleThemeFavoriteClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ToggleFavoriteTheme();
    }

    // "Create a theme": a color picker per theme element, prefilled from the selected theme so users
    // nudge a coherent starting point rather than eight black swatches. Selecting an existing CUSTOM
    // theme turns this into its editor (Delete offered). Mirrors the Avalonia build's OnCreateThemeClick;
    // themes persist via CustomThemeStore (shared engine) and work everywhere a built-in does.
    private async void OnCreateThemeClick(object sender, RoutedEventArgs e)
    {
        var baseTheme = App.Themes.GetOrDefault(ViewModel.SelectedThemeName);
        var editingCustom = !App.Themes.IsBuiltin(baseTheme.Name);

        var nameBox = new TextBox
        {
            PlaceholderText = "Theme name",
            Text = editingCustom ? baseTheme.Name : $"My {baseTheme.Name}",
        };

        (string Label, string Hint, string Hex)[] elements =
        {
            ("Background", "the page itself", baseTheme.Background),
            ("Text", "body copy", baseTheme.Text),
            ("Headings", "titles + accent", baseTheme.Heading),
            ("Code blocks", "code + callout fill", baseTheme.Code),
            ("Borders", "tables, rules, boxes", baseTheme.Border),
            ("Primary", "labels inside diagrams", baseTheme.Primary),
            ("Panels", "quote/alert backgrounds", baseTheme.Secondary),
            ("Lines", "diagram connectors", baseTheme.Line),
        };

        static Windows.UI.Color Parse(string hex)
        {
            var s = hex.TrimStart('#');
            if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
        }
        static string Hex(Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

        var pickers = new ColorPicker[elements.Length];
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(nameBox);
        for (int i = 0; i < elements.Length; i++)
        {
            // A full ColorPicker × 8 is a wall of color wheels — use a compact swatch that opens the
            // picker in a flyout, so the dialog stays scannable.
            var picker = new ColorPicker
            {
                Color = Parse(elements[i].Hex),
                IsAlphaEnabled = false,
                IsColorChannelTextInputVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Ring,
            };
            pickers[i] = picker;

            var swatch = new Button
            {
                Width = 92, Height = 30,
                Background = new SolidColorBrush(picker.Color),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)),
                BorderThickness = new Thickness(1),
            };
            picker.ColorChanged += (_, args) => swatch.Background = new SolidColorBrush(args.NewColor);
            swatch.Flyout = new Flyout { Content = picker };

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = elements[i].Label });
            label.Children.Add(new TextBlock { Text = elements[i].Hint, FontSize = 11, Opacity = 0.6 });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(swatch, 1);
            grid.Children.Add(label);
            grid.Children.Add(swatch);
            rows.Children.Add(grid);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = editingCustom ? $"Edit “{baseTheme.Name}”" : "Create a theme",
            Content = new ScrollViewer { Content = rows, MaxHeight = 480 },
            PrimaryButtonText = "Save theme",
            SecondaryButtonText = editingCustom ? "Delete" : null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);

        if (result == ContentDialogResult.Secondary && editingCustom)
        {
            Services.CustomThemeStore.Remove(baseTheme.Name);
            RefreshThemeNames(select: App.Themes.All[0].Name);
            return;
        }
        if (result != ContentDialogResult.Primary) return;

        var name = (nameBox.Text ?? "").Trim();
        if (name.Length == 0) name = "My theme";
        if (App.Themes.IsBuiltin(name)) name += " (custom)"; // never shadow a stock theme name

        Services.CustomThemeStore.AddOrUpdate(new Models.ThemeDefinition(
            name,
            Hex(pickers[0].Color), Hex(pickers[1].Color), Hex(pickers[2].Color), Hex(pickers[3].Color),
            Hex(pickers[4].Color), Hex(pickers[5].Color), Hex(pickers[6].Color), Hex(pickers[7].Color)));
        RefreshThemeNames(select: name);
    }

    private void RefreshThemeNames(string select)
    {
        ViewModel.ThemeNames.Clear();
        foreach (var t in App.Themes.All) ViewModel.ThemeNames.Add(t.Name);
        ViewModel.SelectedThemeName = select; // triggers preview refresh
    }

    private void OnTakeTourClick(object sender, RoutedEventArgs e) => _ = ShowWelcomeTourAsync();

    // The first-run guided tour. Auto-shown once (gated on settings.HasSeenWelcome); replayable
    // from the ⋯ menu. A borderless dialog hosting the WelcomeTour control, closed when the
    // tour raises Completed. Finishing (not skipping) can then load a sample document and hand off
    // to the TeachingTip walkthrough of the real controls, per the checkboxes on the final page.
    private async Task ShowWelcomeTourAsync()
    {
        Views.WelcomeTour? tour = null;
        try
        {
            tour = new Views.WelcomeTour();
            var dialog = new ContentDialog
            {
                Content = tour,
                XamlRoot = Content.XamlRoot,
                Padding = new Thickness(0),
            };
            // The tour card is 540 wide; the ContentDialog's default ContentDialogMaxWidth (~548)
            // leaves no room for the dialog's own chrome, so the right edge — the Next / Get started
            // button — was being clipped. Give it generous headroom in both dimensions so nothing
            // clips and no scrollbar appears to overlap the buttons.
            dialog.Resources["ContentDialogMaxWidth"] = 760.0;
            dialog.Resources["ContentDialogMinWidth"] = 560.0;
            dialog.Resources["ContentDialogMaxHeight"] = 940.0;
            tour.Completed += (_, _) => dialog.Hide();
            await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
        }
        catch (Exception ex)
        {
            // Never let the tour crash the app — but never hide the failure either. A silent
            // catch here shipped a dead "?" button once already.
            ViewModel.StatusText = $"Tour failed to open: {ex.GetType().Name}: {ex.Message}";
        }

        if (!App.Settings.Current.HasSeenWelcome)
        {
            App.Settings.Current.HasSeenWelcome = true;
            App.Settings.Save();
            // First run only: after the tour, point out the ⋯ menu so nobody hunts for the
            // relocated tour / shortcuts / settings / tip jar.
            ShowMoreMenuTip(
                "Everything else lives here",
                "Version history, recent exports, the tour, keyboard shortcuts, Settings — and a ☕ tip jar if MarkSmith saves your day.");
        }

        if (tour?.LoadSampleRequested == true) LoadSampleDocument();
    }

    // A showcase document for the tour: something in every direction the app is good at —
    // detection-worthy prose, a table, KaTeX math, a Mermaid diagram (built-in), and a PlantUML
    // fence. If the PlantUML plugin isn't installed the preview shows its "install this plugin"
    // affordance instead, which is itself worth discovering. Only offered from the tour, and only
    // replaces the editor when the user hasn't typed anything (never clobber real work).
    private const string SampleMarkdown = """
        # Quarterly Review — Sample Document

        This is a **sample** so you can try MarkSmith without hunting for a Markdown file.
        Restyle it on the right, then hit **Generate PDF** below.

        > [!TIP]
        > Everything here survives export: the table, the math, and the diagrams.
        
        ---
        
        # Table of Contents
        
        - [Data Tables](#data-tables)
        - [Math](#math)
        - [Diagrams](#diagrams)
        - [Formatting & Code](#formatting--code)
        - [Definition Lists](#definition-lists)
        - [Task Lists](#task-lists)
        - [Admonitions](#admonitions)

        ---

        ## Data Tables

        | Region | Revenue | Change |
        |--------|---------|--------|
        | APAC   | $4.2M   | +12%   |
        | EU     | $3.1M   | +5%    |
        | US     | $5.5M   | +9%    |

        ---
        
        ## Math

        Reserves follow $R = \sum_{i=1}^{n} p_i \cdot L_i$ — and in Word export this becomes a
        real, editable equation, not a picture.
        
        Block equations work too:
        $$
        \begin{bmatrix}
        1 & 2 & 3 \\
        4 & 5 & 6 \\
        7 & 8 & 9
        \end{bmatrix}
        $$

        ---
        
        ## Diagrams

        ### Mermaid Flowchart

        ```mermaid
        flowchart LR
          A[Paste a chat] --> B{MarkSmith}
          B --> C[Polished PDF]
          B --> D[Editable Word]
        ```

        ### PlantUML Sequence

        ```plantuml
        @startuml
        You -> MarkSmith: paste markdown
        MarkSmith --> You: finished document
        @enduml
        ```
        
        ### Graphviz
        
        ```graphviz
        digraph G {
            A -> B;
            A -> C;
            B -> D;
            C -> D;
        }
        ```
        
        Six diagram languages render from plain code fences — Mermaid is built in, and PlantUML,
        Graphviz, D2, Typst and Vega-Lite are one-click installs in **Settings → Plugins**.
        
        ---
        
        ## Formatting & Code
        
        *Italic*, **Bold**, ***Bold Italic***, ~~Strikethrough~~, ==Highlight==, and `Inline code`.
        Subscript: H~2~O | Superscript: X^2^
        
        ```python
        def hello_world():
            print("Syntax highlighting works!")
        ```
        
        ---
        
        ## Definition Lists
        
        MarkSmith
        : The tool you are using right now.
        
        Markdown
        : A lightweight markup language.
        
        ---
        
        ## Task Lists
        
        - [x] Completed task
        - [ ] Incomplete task
        
        ---
        
        ## Admonitions
        
        > [!WARNING]
        > This is a warning admonition.
        
        !!! note
            Python Markdown style admonitions work too!
            
        ---
        
        ## Try it yourself!
        
        Try editing this markdown in the textbox on the left to see the live preview instantly update.
        """;

    private void LoadSampleDocument()
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.PastedMarkdown)) return;
        ViewModel.DetachFromOpenFile();
        ViewModel.UsePasteSource = true;
        ViewModel.PastedMarkdown = SampleMarkdown;
    }

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settingsView = new Views.SettingsView();
        // Installing/removing a diagram engine changes what the open document can render, so re-run
        // the live preview (heavy path re-invokes the plugin renderers) as soon as it happens —
        // even while the Settings dialog is still open, so the change is visible the moment it closes.
        settingsView.PluginsChanged += () => DispatcherQueue.TryEnqueue(async () =>
        {
            await RefreshPreviewAsync(heavy: true);
        });
        settingsView.FitTo(Content.XamlRoot.Size);
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = settingsView,
            CloseButtonText = "Close",
            // No DefaultButton: settings apply as you change them, so Close isn't a commit action,
            // and an accent Close competed with each page's own primary (Sign in, Start trial).
            XamlRoot = Content.XamlRoot,
        };
        // Left nav + pages needs up to 820 of content; the stock ContentDialogMaxWidth (~548) would
        // clip it (the trap the tour and Suite Hub hit too).
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;
        dialog.Resources["ContentDialogMaxHeight"] = 900.0;
        await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
    }

    private async void OnSuiteHubClick(object sender, RoutedEventArgs e)
    {
        var suiteHubView = new Views.SuiteHubView(_automationManager.IsApiRunning,
            _automationManager.IsApiRunning ? _automationManager.ApiPort : ViewModel.ApiPort,
            turnOnApi: async () =>
            {
                // Same setting as Settings › Automation › Local API; the property hook re-applies
                // automation synchronously.
                ViewModel.ApiEnabled = true;
                await System.Threading.Tasks.Task.Delay(150);
                bool running = _automationManager.IsApiRunning;
                return (running, running ? _automationManager.ApiPort : ViewModel.ApiPort,
                        running ? null : $"Couldn't turn on the connection: port {ViewModel.ApiPort} may be in use by another program. Pick a different port in Settings › Automation.");
            });
        ContentDialog? dialog = null;

        suiteHubView.OpenMermaidStudioRequested += () =>
        {
            dialog?.Hide();
            OnOpenMermaidStudioClick(this, new RoutedEventArgs());
        };

        suiteHubView.OpenShapeStudioRequested += () =>
        {
            dialog?.Hide();
            OnOpenShapeDesignStudioClick(this, new RoutedEventArgs());
        };

        suiteHubView.OpenGalaxyRequested += () =>
        {
            dialog?.Hide();
            OnOpenMindMapGalaxyClick(this, new RoutedEventArgs());
        };

        dialog = new ContentDialog
        {
            Title = "Suite Hub",
            Content = suiteHubView,
            CloseButtonText = "Done",
            // Done is the hub's one accent action; the cards' own buttons are all standard.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        // SuiteHubView asks for MinWidth 680, but ContentDialog's default ContentDialogMaxWidth is
        // ~548 — so the dialog clipped its own content: the right-hand column of cards lost the end
        // of every description and, worse, its buttons (Copy MCP Config, Copy CLI Syntax, Launch
        // Galaxy) were cut off the edge and unreachable. Same trap the welcome tour hit above.
        // 680 content + the dialog's ~48 of horizontal padding needs ~730, so give it headroom.
        dialog.Resources["ContentDialogMaxWidth"] = 820.0;
        dialog.Resources["ContentDialogMinWidth"] = 740.0;
        dialog.Resources["ContentDialogMaxHeight"] = 900.0;
        await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
    }

    // ---- Automation (clipboard watcher / folder watcher / REST API) ----

    private void ApplyAutomationSettings()
    {
        _automationManager.ApplyAutomationSettings(
            ViewModel,
            () => _clipboardIngest.Start(),
            () => _clipboardIngest.Stop(),
            _clipboardIngest.IsRunning,
            folder => _folderIngest.Start(folder),
            () => _folderIngest.Stop(),
            _folderIngest.IsRunning,
            OnApiStatusChanged);
    }

    // AutomationManager reports the health URL while listening, "" when stopped, and
    // "API failed to start: …" on failure. Settings and the side panel both show the VM's words.
    private void OnApiStatusChanged(string status)
    {
        const string failed = "API failed to start: ";
        ViewModel.ApiStatusIsError = status.StartsWith(failed, StringComparison.Ordinal);
        ViewModel.ApiStatusText =
            ViewModel.ApiStatusIsError ? "Couldn't start: " + status[failed.Length..]
            : string.IsNullOrEmpty(status) ? "Off"
            : "Listening on " + status.Replace("/api/health", "", StringComparison.Ordinal);
    }

    // Tray icon is created in code, not markup — the WASDK 1.6 XAML compiler crashes on
    // H.NotifyIcon's XAML types, but consuming them from C# works fine.
    private void InitTrayIcon()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Text = "Open MarkSmith", Command = ShowWindowCommand });
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = ExitApplicationCommand });

            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "MarkSmith",
                // Icon (System.Drawing, real .ico) — NOT IconSource: H.NotifyIcon 2.3.0's
                // IconSource->ToIconAsync path decodes the image to pixels then re-wraps the
                // stream as System.Drawing.Icon, which only accepts ICO container bytes and
                // throws ArgumentException on the async continuation (unhandled -> app crash).
                Icon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico")),
                ContextMenuMode = H.NotifyIcon.ContextMenuMode.SecondWindow,
                NoLeftClickDelay = true,
                LeftClickCommand = ShowWindowCommand,
                ContextFlyout = menu,
            };
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            _trayIcon = null; // tray is a convenience — never block startup on it
            System.Diagnostics.Debug.WriteLine($"Tray icon unavailable: {ex.Message}");
        }
    }

    private void OnExtensionTipClosed(object sender, object args) => ViewModel.ShowExtensionTip = false;

    // Clipboard / API / extension ingests land here. Always update the UI; when "auto-generate PDF
    // from AI-chat ingests" is on, also export a PDF — so the extension sending a conversation at
    // its end produces a finished document with no clicks.
    private void IngestFromSource(string text, string origin, Models.OutputOverride? output = null)
    {
        // Source-page metadata (font, definitive source, model, title, language/direction, brand
        // accent) is applied inside IngestMarkdown so the live preview reflects it immediately —
        // see MainViewModel.IngestMarkdown.
        ViewModel.IngestMarkdown(text, origin, output);
        if (!ViewModel.AutoConvertIngests) return;
        var formats = Services.ExportCoordinator.ParseFormats(output?.Format, App.Settings.Current.TargetFormat);
        if (Models.AutomationPolicy.Allows(App.License.State, formats)) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = Models.ProGate.FeatureName(Models.FeatureId.AutoExportIngest) + " is a MarkSmith Pro feature. The content is in the editor, ready to export by hand. " + Models.AutomationPolicy.EmailIsFreeHint;
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    private static string[] ParseFormats(string? format) => Services.ExportCoordinator.ParseFormats(format);

    private async Task AutoExportIngestAsync(Models.OutputOverride? output)
    {
        await _exportCoordinator.AutoExportIngestAsync(
            ViewModel,
            output,
            this,
            () => new OffscreenScope(this),
            ShowAutomationToast,
            () => RefreshPreviewAsync());
    }

    private async Task OnWatchedFileAsync(string path)
    {
        await _exportCoordinator.OnWatchedFileAsync(
            ViewModel,
            path,
            this,
            () => new OffscreenScope(this),
            ShowAutomationToast,
            () => RefreshPreviewAsync());
    }

    // Automation can write any format; the toast used to say "PDF ready" for a Word file or an email.
    private static void ShowAutomationToast(string path) => ShowExportToast(Models.OutputFormats.KindForPath(path), path);

    // Windows toast on export completion. kind is "PDF"/"DOCX"/"PPTX" (or a combined "PDF + DOCX"
    // label from Export-all). Best-effort: notifications can be disabled system-wide, and the
    // in-app status bar always reports regardless.
    private static void ShowExportToast(string kind, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var toast = new AppNotificationBuilder()
                .AddText($"{kind} ready")
                .AddText(Path.GetFileName(path))
                .AddArgument("action", "open")
                .AddArgument("path", path)
                .AddButton(new AppNotificationButton("Open file")
                    .AddArgument("action", "open").AddArgument("path", path))
                .AddButton(new AppNotificationButton("Show in folder")
                    .AddArgument("action", "folder").AddArgument("path", path))
                .BuildNotification();
            AppNotificationManager.Default.Show(toast);
        }
        catch
        {
            // Toasts are best-effort (notifications can be disabled system-wide); the InfoBar still reports.
        }
    }

    private void OnHistoryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Models.HistoryEntry entry) return;
        OpenExportedFile(entry.OutputPath);
    }

    // Every format MarkSmith itself exports. Anything else in a history row is refused rather than
    // shell-executed. ".html" was missing, so the Open output button silently did nothing after an
    // HTML export and its history row reported "Blocked opening untrusted file type". The same
    // happened to every saved email and Outlook message until .eml/.msg joined the list.
    private static bool IsExportedFileType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".docx" or ".pptx" or ".epub" or ".md" or ".html" or ".eml" or ".msg";

    private void OpenExportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            ViewModel.StatusText = $"{Path.GetFileName(path)} is no longer there. It was moved or deleted after export.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        if (!IsExportedFileType(path))
        {
            ViewModel.StatusText = $"Blocked opening untrusted file type: {Path.GetExtension(path)}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Typically "no app is associated with .epub" — say so instead of failing silently.
            ViewModel.StatusText = $"Couldn't open {Path.GetFileName(path)}: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    private void ShowExportedFileInFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            ViewModel.StatusText = $"{Path.GetFileName(path)} is no longer there. It was moved or deleted after export.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch { /* Explorer unavailable — nothing useful to report */ }
    }

    private void OnStatusOpenOutputClick(object sender, RoutedEventArgs e) => OpenExportedFile(ViewModel.StatusOutputPath);

    private void OnStatusShowOutputFolderClick(object sender, RoutedEventArgs e) => ShowExportedFileInFolder(ViewModel.StatusOutputPath);

    // Document outline (Task 17): scroll the preview to the clicked heading. The anchor is the exact
    // id Markdig rendered on the heading element, so getElementById + scrollIntoView lands on it.
    private async void OnTocItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Services.TocEntry entry) return;
        OutlineButton.Flyout?.Hide();
        // The outline used to scroll only the preview, so in Code view a click did nothing at all.
        // Wherever the editor is showing, the caret goes to the heading too.
        if (_viewMode != ViewMode.Preview && entry.Line > 0) GoToDocumentLine(entry.Line);
        if (string.IsNullOrEmpty(entry.Anchor)) return;
        if (PreviewWebView.CoreWebView2 is not { } core) return;
        var js = "(function(){var el=document.getElementById(" +
                 System.Text.Json.JsonSerializer.Serialize(entry.Anchor) +
                 ");if(el){el.scrollIntoView({behavior:'smooth',block:'start'});}})();";
        try { await core.ExecuteScriptAsync(js); } catch { /* best-effort */ }
    }

    private async void OnBrowseWatchFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await Services.NativeFilePicker.PickFolderAsync(
            this, "Choose the folder to watch", Services.NativeFilePicker.Purpose.AutomationFolders,
            okLabel: "Watch this folder", folder: Directory.Exists(ViewModel.WatchFolder) ? ViewModel.WatchFolder : null);
        if (!string.IsNullOrEmpty(folder)) ViewModel.WatchFolder = folder;
    }


    // Preset directory selector for standard AI pipeline output locations.
    private void OnWatchFolderPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WatchFolderPresets.SelectedItem is Services.AiAgentFolderPresets.FolderPreset preset)
            ViewModel.WatchFolder = preset.Path;
    }

    // ISS-008: open the tip jar — a no-strings donation page for the project.
    private async void OnBuyCoffeeClick(object sender, RoutedEventArgs e)
    {
        var coffeeUrl = new Uri("https://buymeacoffee.com/mbubbtechz");
        await Windows.System.Launcher.LaunchUriAsync(coffeeUrl);
    }

    // Batch: convert every .md in a chosen folder (optionally its subfolders too) to a chosen
    // format — PDF/DOCX/PPTX/EPUB — one by one through the same classify → normalize → render
    // pipeline the watched folder uses. Pro (automation) feature.
    // Standardized output for a free user attempting a paid feature: one modal, one set of words
    // (Core ProGate). The trial is full Pro, so it's offered for every gated feature while it's
    // unused, and starting it from here carries straight on with what the user was doing.
    private bool _proGateOpen;

    private async Task ShowProGateAsync(Models.FeatureId feature)
    {
        // Two gates can fire for one gesture (a toggle that bounces back, then a retry); one dialog
        // is enough, and a second ShowAsync while one is open would throw.
        if (_proGateOpen) return;
        _proGateOpen = true;
        try
        {
            var st = App.License.State;
            var trial = Models.ProGate.OffersTrial(st);
            var store = Services.LicenseService.IsStoreConfigured;

            var body = new StackPanel { Spacing = 12, MaxWidth = 440 };
            var paragraphs = Models.ProGate.DialogParagraphs(feature, st);
            for (var i = 0; i < paragraphs.Count; i++)
            {
                var last = i == paragraphs.Count - 1;
                body.Children.Add(new TextBlock
                {
                    Text = paragraphs[i],
                    TextWrapping = TextWrapping.Wrap,
                    // The free-plan line is reassurance, not the offer: quieter, below the rest.
                    Style = last ? (Style)Application.Current.Resources["CaptionTextBlockStyle"] : null,
                    Foreground = last ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : null,
                });
            }

            var dialog = new ContentDialog
            {
                Title = Models.ProGate.DialogTitle(feature),
                Content = body,
                PrimaryButtonText = trial ? Models.ProGate.StartTrialLabel : store ? Models.ProGate.BuyLabel : string.Empty,
                SecondaryButtonText = trial && store ? Models.ProGate.BuyLabel : string.Empty,
                CloseButtonText = Models.ProGate.NotNowLabel,
                DefaultButton = trial || store ? ContentDialogButton.Primary : ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot,
            };
            var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
            if (result == ContentDialogResult.Primary && trial)
            {
                if (StartTrialFromShell()) await ViewModel.ResumeAfterUnlockAsync();
            }
            else if (result == ContentDialogResult.Primary || result == ContentDialogResult.Secondary)
            {
                await OpenStoreAsync();
            }
        }
        finally
        {
            _proGateOpen = false;
        }
    }

    // Opens the checkout link; while StoreUrl still carries the placeholder the user gets a status
    // line instead of a broken browser launch.
    private async Task OpenStoreAsync()
    {
        if (Services.LicenseService.IsStoreConfigured)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.CheckoutUrl(App.License.State.Email))); }
            catch { /* no browser / bad uri — ignore */ }
        }
        else
        {
            ViewModel.StatusText = "The online store link isn't configured yet.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        }
    }

    // Hidden Ctrl+Shift+Alt+L: full license reset to Free (key + trial + used flag) — the
    // developer/test path for exercising the free-vs-pro gates on demand.
    private void OnDevProToggleInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        var (pro, message) = App.License.ToggleDevPro();
        ViewModel.StatusText = message;
        ViewModel.StatusSeverity = pro ? Models.StatusSeverity.Success : Models.StatusSeverity.Informational;
        UpdateLicenseBanner();
    }

    private void OnHiddenResetLicenseInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
#if !DEBUG
        // Go-live: the hidden reset (which refunds the trial) is a developer affordance — dead in
        // shipped Release builds, matching the DevProKey treatment in LicenseValidator.
        return;
#else
        App.License.ResetToFree();
        ViewModel.StatusText = "License reset to Free (hidden command) — free-tier limits are active. Start 3-export trial from Settings to re-test.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        UpdateLicenseBanner();
#endif
    }

    private async void OnBatchConvertClick(object sender, RoutedEventArgs e)
    {
        // Batch is Pro, except a batch that only writes email drafts (AutomationPolicy). The format
        // is picked in the dialog, so a free user still gets the dialog with the email formats.
        var folderPath = await Services.NativeFilePicker.PickFolderAsync(
            this, "Choose a folder to convert", Services.NativeFilePicker.Purpose.AutomationFolders, okLabel: "Convert this folder");
        if (string.IsNullOrEmpty(folderPath)) return;

        var folderName = System.IO.Path.GetFileName(folderPath);
        if (string.IsNullOrEmpty(folderName)) folderName = folderPath;

        var outFolder = App.Settings.Current.OutputFolder;
        var all = Services.AutomationExportService.FindBatchSources(folderPath, recursive: true, outFolder);
        var top = Services.AutomationExportService.FindBatchSources(folderPath, recursive: false, outFolder);
        if (all.Length == 0)
        {
            ViewModel.StatusText = $"Nothing to convert in {folderName}: no Markdown, text, Word, web page or email files there or in its subfolders.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        var (fmt, recursive) = await AskBatchFormatAsync(folderName, top.Length, all.Length);
        if (fmt is null) return;
        if (fmt == Models.OutputFormats.Docx && !App.License.CanExportDocx)
        {
            ViewModel.NotifyProFeatureAttempted(Models.FeatureId.DocxExport, () => { OnBatchConvertClick(sender, e); return Task.CompletedTask; });
            return;
        }
        if (!Models.AutomationPolicy.Allows(App.License.State, fmt))
        {
            ViewModel.NotifyProFeatureAttempted(Models.FeatureId.BatchConvert, () => { OnBatchConvertClick(sender, e); return Task.CompletedTask; });
            return;
        }

        if (Models.OutputFormats.NeedsRenderHost(fmt) && !await EnsurePreviewWebViewAsync())
        {
            ViewModel.StatusText = "Batch convert couldn't start: the preview engine didn't load, and a PDF needs it. Try again.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }

        ViewModel.IsBusy = true;
        try
        {
            var result = await _exportCoordinator.BatchConvertForApiAsync(
                ViewModel,
                folderPath,
                fmt,
                null,
                this,
                () => new OffscreenScope(this),
                () => RefreshPreviewAsync(false),
                recursive);

            if (result.Done > 0) ViewModel.LastOutputPath = result.Produced[^1];
            ViewModel.AnnounceBatch(result);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = "Batch convert failed: " + Services.ExportFailureMessage.Describe(Models.OutputFormats.Kind(fmt), ex, null);
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
        finally
        {
            ViewModel.IsBusy = false;
        }
    }

    // Which format to batch-convert to, and whether to include subfolders. Starts on the default
    // output format; on the free plan the Pro formats say so (email drafts are free). Returns
    // (null, false) if cancelled.
    private async Task<(string? Format, bool Recursive)> AskBatchFormatAsync(string folderName, int topCount, int allCount)
    {
        var state = App.License.State;
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, "Convert to");
        foreach (var f in Models.OutputFormats.All)
        {
            var label = char.ToUpperInvariant(Models.OutputFormats.Label(f)[0]) + Models.OutputFormats.Label(f)[1..];
            if (!Models.AutomationPolicy.Allows(state, f)) label += "  ·  Pro";
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = f });
        }
        var preferred = Models.OutputFormats.Normalize(App.Settings.Current.TargetFormat) ?? Models.OutputFormats.Pdf;
        combo.SelectedIndex = Math.Max(0, Models.OutputFormats.All.ToList().IndexOf(preferred));

        static string Files(int n) => n == 1 ? "1 document" : $"{n} documents";
        var hasNested = allCount > topCount;
        var recurse = new CheckBox
        {
            Content = $"Include subfolders ({Files(allCount)} in all)",
            IsChecked = topCount == 0,
            Visibility = hasNested ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateSummary() => summary.Text = recurse.IsChecked == true
            ? $"{Files(allCount)} in {folderName} and its subfolders will be converted with the current Style settings. The folder structure is recreated in your output folder."
            : $"{Files(topCount)} in {folderName} will be converted with the current Style settings, into your output folder.";
        recurse.Checked += (_, _) => UpdateSummary();
        recurse.Unchecked += (_, _) => UpdateSummary();
        UpdateSummary();

        var where = new TextBlock
        {
            Text = "Output folder: " + App.Settings.Current.OutputFolder,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Batch convert " + folderName,
            Content = new StackPanel { Children = { summary, combo, recurse, where } },
            PrimaryButtonText = "Convert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) != ContentDialogResult.Primary) return (null, false);
        var fmt = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? Models.OutputFormats.Pdf;
        return (fmt, recurse.IsChecked == true);
    }

    private async void OnBrowseBrandLogoClick(object sender, RoutedEventArgs e)
    {
        var file = await PickLogoAsync();
        if (!string.IsNullOrEmpty(file)) ViewModel.BrandLogoPath = file;
    }

    private async void OnBrowseRunningDocClick(object sender, RoutedEventArgs e)
    {
        var file = await Services.NativeFilePicker.PickSaveFileAsync(
            this, "Choose where to keep the AI notebook", Services.NativeFilePicker.Purpose.Exports,
            "AI notebook.docx", new[] { Models.FileType.Of("Word document", ".docx") }, okLabel: "Use this file");
        if (!string.IsNullOrEmpty(file)) ViewModel.RunningDocPath = file;
    }

    private async Task<byte[]> ConvertForApiAsync(string markdown, Models.OutputOverride? output)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var bytes = await _exportCoordinator.ConvertForApiAsync(
                    ViewModel,
                    markdown,
                    output,
                    this,
                    () => new OffscreenScope(this),
                    () => RefreshPreviewAsync());
                tcs.SetResult(bytes);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task;
    }

    private Task RunOnUiAsync(Func<Task> work)
    {
        if (DispatcherQueue.HasThreadAccess) return work();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { await work(); tcs.SetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            }))
            tcs.TrySetException(new InvalidOperationException("MarkSmith is closing."));
        return tcs.Task;
    }

    // POST /api/email with open: true (the extension's "Open in Outlook"): the same draft the
    // export writes (.eml or .msg, per the Email format setting), saved to the outbox and handed
    // to the default mail app. Free on every plan.
    private async Task<Services.ApiServer.EmailDraftResult> OpenEmailDraftForApiAsync(string markdown, Models.OutputOverride? output)
    {
        output ??= new Models.OutputOverride();
        var format = Services.Email.MailApps.Resolve(output.Format is "eml" or "msg" ? output.Format : ViewModel.EmailFormat);
        output.Format = format;
        var bytes = await ConvertForApiAsync(markdown, output);
        var subject = format == Services.Email.MailApps.Msg
            ? Services.Email.MsgImporter.SubjectOf(bytes)
            : MimeKit.MimeMessage.Load(new MemoryStream(bytes)).Subject ?? "";
        var label = !string.IsNullOrWhiteSpace(subject) ? subject : output.SourceTitle ?? "Email draft";
        Services.Email.EmailOutbox.Clean();
        var path = Services.Email.EmailOutbox.PathFor(label, format);
        await File.WriteAllBytesAsync(path, bytes);
        var opened = Services.Email.EmailOutbox.Open(path);
        await RunOnUiAsync(() =>
        {
            ViewModel.AnnounceEmailFromApi(subject, path, opened);
            return Task.CompletedTask;
        });
        var notes = opened ? Array.Empty<string>()
            : new[] { $"Windows has no app set to open .{format} files. Pick Outlook under Settings > Apps > Default apps." };
        return new Services.ApiServer.EmailDraftResult(opened, path, subject, notes);
    }

    private async Task<object> BatchConvertForApiAsync(string folderPath, string format, Models.OutputOverride? ovr)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var result = await _exportCoordinator.BatchConvertForApiAsync(
                    ViewModel,
                    folderPath,
                    format,
                    ovr,
                    this,
                    () => new OffscreenScope(this),
                    () => RefreshPreviewAsync(false));
                if (result.Total > 0) ViewModel.AnnounceBatch(result);
                tcs.SetResult(result.ToApi());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return await tcs.Task;
    }

    // ---- Source panel (File | Paste) ----

    private void OnSourceSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // ViewModel.UsePasteSource = sender.SelectedItem == PasteTab;
    }

    private void SyncSourcePanels()
    {
        // var paste = ViewModel.UsePasteSource;
        // SourceSelector.SelectedItem = paste ? PasteTab : FileTab;
        // FilePanel.Visibility = paste ? Visibility.Collapsed : Visibility.Visible;
        // PastePanel.Visibility = paste ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSourceDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Drop to open";
            }
        }
    }

    private async void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        // Markdown, plus everything the importers turn into Markdown (Word, PDF, HTML, email and
        // any importer plugin's formats) — the same list the Open picker offers.
        var docs = items.OfType<StorageFile>().Where(f => Plugins.PluginFileReader.CanOpen(f.Path)).ToList();
        if (docs.Count == 0) return;

        // Single file: load it into the editor exactly as before.
        if (docs.Count == 1)
        {
            ViewModel.InputFilePath = docs[0].Path;
            ViewModel.UsePasteSource = false;
            return;
        }

        // Multi-file drop (Task 11): one batch job in the default output format, written to the
        // output folder, with progress and a summary on the status line.
        await RunMultiFileBatchAsync(docs);
    }

    // Straight from where the files are (no temp copy): every dropped document is read in place,
    // and an output that would land on one of them gets " (converted)" instead of replacing it.
    private Task RunMultiFileBatchAsync(List<StorageFile> docs) =>
        ViewModel.BatchConvertFilesAsync(docs.Select(f => f.Path).ToList(), App.Settings.Current.OutputFolder, App.Settings.Current.TargetFormat);

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Drop to open";
            }
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var docs = items.OfType<StorageFile>().Where(f => Plugins.PluginFileReader.CanOpen(f.Path)).ToList();
        if (docs.Count == 0) return;

        if (docs.Count == 1)
        {
            ViewModel.InputFilePath = docs[0].Path;
            AutoCollapseLeftPane();
            ViewModel.UsePasteSource = false;
            return;
        }

        await RunMultiFileBatchAsync(docs);
    }

    // Drag-and-drop support on editor: accepts Markdown files (loads document) and image files (embeds reference).
    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
                e.DragUIOverride.Caption = "Drop to open document or embed image";
        }
    }

    private async void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();

        // 1. Check for documents first (Markdown or supported text formats)
        var docs = items.OfType<StorageFile>().Where(f => Plugins.PluginFileReader.CanOpen(f.Path)).ToList();
        if (docs.Count == 1)
        {
            ViewModel.InputFilePath = docs[0].Path;
            AutoCollapseLeftPane();
            ViewModel.UsePasteSource = false;
            return;
        }
        if (docs.Count > 1)
        {
            await RunMultiFileBatchAsync(docs);
            return;
        }

        // 2. Check for image files
        string[] imageExts = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg" };
        var images = items.OfType<StorageFile>()
            .Where(f => imageExts.Contains(f.FileType.ToLowerInvariant()))
            .ToList();
        if (images.Count == 0) return;

        var targetDir = await ResolveImageDropFolderAsync();
        if (targetDir is null)
        {
            ViewModel.StatusText = "Couldn't find a folder to store the dropped image(s).";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }

        // Images already in (or below) the document's folder are referenced where they are; others
        // are copied in first. Destinations are relative to a saved document and survive spaces
        // (InsertSnippetBuilder.Image), so a drop from "My Pictures" no longer writes broken Markdown.
        var docFolder = ViewModel.DocumentFolder;
        var refs = new List<string>();
        var copied = 0;
        foreach (var img in images)
        {
            var path = img.Path;
            if (!IsInsideFolder(path, docFolder ?? targetDir.Path))
            {
                var dest = await img.CopyAsync(targetDir, img.Name, NameCollisionOption.GenerateUniqueName);
                path = dest.Path;
                copied++;
            }
            refs.Add(Services.InsertSnippetBuilder.Image("", path, docFolder ?? "").Trim('\n'));
        }

        InsertMarkdown("\n" + string.Join("\n\n", refs) + "\n");
        var noun = refs.Count == 1 ? "image" : $"{refs.Count} images";
        ViewModel.StatusText = copied == 0
            ? $"Added {noun} to the document."
            : docFolder is null
                ? $"Added {noun}. Pasted text has no folder, so {(copied == 1 ? "it was" : "they were")} copied to {targetDir.Path}."
                : $"Added {noun}, copied into the document's folder so {(refs.Count == 1 ? "it travels" : "they travel")} with it.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    private static bool IsInsideFolder(string path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var rel = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(folder), System.IO.Path.GetFullPath(path));
            return !rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel);
        }
        catch { return false; }
    }

    private async Task<StorageFolder?> ResolveImageDropFolderAsync()
    {
        string? dir = null;
        if (!ViewModel.UsePasteSource && !string.IsNullOrWhiteSpace(ViewModel.InputFilePath))
            dir = System.IO.Path.GetDirectoryName(ViewModel.InputFilePath);
        if (string.IsNullOrWhiteSpace(dir)) dir = ViewModel.OutputFolder;
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            return await StorageFolder.GetFolderFromPathAsync(dir);
        }
        catch { return null; }
    }

    private async void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var allExts = Plugins.PluginFileReader.NativeExtensions
            .Concat(App.Plugins.AllImporterExtensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var file = await Services.NativeFilePicker.PickOpenFileAsync(
            this, "Open a document", Services.NativeFilePicker.Purpose.Documents,
            new[]
            {
                Models.FileType.Of("All supported documents", allExts),
                Models.FileType.Of("Markdown", ".md", ".markdown"),
                Models.FileType.AllFiles,
            },
            okLabel: "Open", folder: OpenDocumentFolder);
        if (!string.IsNullOrEmpty(file))
        {
            ViewModel.InputFilePath = file;
            AutoCollapseLeftPane();
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseLogoClick(object sender, RoutedEventArgs e)
    {
        var file = await PickLogoAsync();
        if (!string.IsNullOrEmpty(file))
        {
            ViewModel.BrandLogoPath = file;
        }
    }

    // Logo pickers in Style & Export and in the branding expander share one dialog.
    private Task<string?> PickLogoAsync() => Services.NativeFilePicker.PickOpenFileAsync(
        this, "Choose a logo", Services.NativeFilePicker.Purpose.Images,
        new[]
        {
            Models.FileType.Of("Images", ".png", ".jpg", ".jpeg"),
            Models.FileType.Of("PNG image", ".png"),
            Models.FileType.Of("JPEG image", ".jpg", ".jpeg"),
        },
        okLabel: "Use this logo", start: Services.StartFolder.Pictures,
        folder: Services.NativeFilePicker.FolderOf(ViewModel.BrandLogoPath));

    // Save dialogs for something taken from the open document start beside it, named after it.
    private string? OpenDocumentFolder
        => !ViewModel.UsePasteSource && ViewModel.HasInputFile ? Services.NativeFilePicker.FolderOf(ViewModel.InputFilePath) : null;

    private string SuggestedNameFromDocument(string what)
        => !ViewModel.UsePasteSource && ViewModel.HasInputFile
            ? Models.FileDialogRules.SafeFileName($"{Path.GetFileNameWithoutExtension(ViewModel.InputFilePath)} {what}", what)
            : what;

    private async void OnBrowseFontClick(object sender, RoutedEventArgs e)
    {
        var file = await Services.NativeFilePicker.PickOpenFileAsync(
            this, "Choose a font to embed", Services.NativeFilePicker.Purpose.Fonts,
            new[]
            {
                Models.FileType.Of("Fonts", ".ttf", ".otf"),
                Models.FileType.Of("TrueType font", ".ttf"),
                Models.FileType.Of("OpenType font", ".otf"),
            },
            okLabel: "Embed",
            folder: Services.NativeFilePicker.FolderOf(ViewModel.CustomFontPath));
        if (!string.IsNullOrEmpty(file))
        {
            ViewModel.CustomFontPath = file;
        }
    }

    // The Style-panel expanders (Export Branding / Advanced Options) reveal content that usually
    // lands below the panel ScrollViewer's fold. After the expand animation settles, bring the last
    // revealed element into view so the new fields are immediately visible instead of requiring a
    // manual scroll.
    private void OnStyleExpanderExpanded(object sender, ExpanderExpandingEventArgs e)
    {
        if (sender is not Expander exp) return;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(350);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (exp.Content is StackPanel sp && sp.Children.Count > 0)
                sp.Children[sp.Children.Count - 1].StartBringIntoView();
        };
        timer.Start();
    }

    // Style & Export sections remember whether they are open. With every section expanded on
    // first launch the pane was a ~2,700 px scroll; now a fresh install opens Appearance only and
    // afterwards each section stays the way the user left it.
    private Expander[] StyleSectionExpanders => new[]
    {
        StyleAppearanceExpander, StyleLayoutExpander, StyleWordExpander, StyleEmailExpander, StyleDiagramsExpander,
        StyleContentExpander, StyleFormattingExpander, ExportBrandingExpander,
    };

    private void WireStyleSectionMemory()
    {
        var saved = App.Settings.Current.ExpandedStyleSections;
        foreach (var exp in StyleSectionExpanders)
        {
            var key = exp.Tag as string ?? "";
            exp.IsExpanded = saved is null ? key == "Appearance" : saved.Contains(key);
            exp.Expanding += (_, _) => RememberStyleSection(key, open: true);
            exp.Collapsed += (_, _) => RememberStyleSection(key, open: false);
        }
    }

    private void RememberStyleSection(string key, bool open)
    {
        var settings = App.Settings.Current;
        var list = settings.ExpandedStyleSections
                   ?? StyleSectionExpanders.Where(x => x.IsExpanded && !Equals(x.Tag, key)).Select(x => x.Tag as string ?? "").ToList();
        list.Remove(key);
        if (open) list.Add(key);
        settings.ExpandedStyleSections = list;
        try { App.Settings.Save(); } catch { /* not worth interrupting a click for */ }
    }

    private void OnMarkdownFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Services.MarkdownFileEntry entry })
        {
            ViewModel.LoadRecentCommand.Execute(entry.Path);
            AutoCollapseLeftPane(); // the panel did its job — tuck it away
        }
    }

    private async void OnRescanMarkdownClick(object sender, RoutedEventArgs e)
    {
        await LoadMarkdownFilesAsync();
    }

    // Discover .md files across the user's common folders; drives the Step 1 picker. Toggles the
    // inline "Scanning…" hint and disables Rescan while it runs.
    private async Task LoadMarkdownFilesAsync()
    {
        RescanButton.IsEnabled = false;
        ScanningLabel.Visibility = Visibility.Visible;
        try { await ViewModel.RefreshMarkdownFilesAsync(); }
        finally
        {
            ScanningLabel.Visibility = Visibility.Collapsed;
            RescanButton.IsEnabled = true;
        }
    }

    // ---- Export ----

    private async void OnBrowseFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = await Services.NativeFilePicker.PickFolderAsync(
            this, "Choose where exports are saved", Services.NativeFilePicker.Purpose.AutomationFolders,
            okLabel: "Save exports here", folder: Directory.Exists(ViewModel.OutputFolder) ? ViewModel.OutputFolder : null);
        if (!string.IsNullOrEmpty(folder)) ViewModel.OutputFolder = folder;
    }

    // The "call to user" for a page-dominating diagram: keep mermaid's exact layout (Web Layout view)
    // or reflow it to fit the printed page. Returns 1 = exact, 2 = reflow. Persists if "remember".
    public async Task<int> AskOversizedDiagramModeAsync()
    {
        var remember = new CheckBox { Content = "Remember my choice (change later in Settings)", Margin = new Thickness(0, 14, 0, 0) };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This document has a large diagram that won't fit a printed page. How should MarkSmith put it into Word?"
        });

        var rbGroup = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var rbExact = new RadioButton { Content = "Keep exact layout (Opens in Web Layout view)", IsChecked = true, Tag = 1 };
        var rbReflow = new RadioButton { Content = "Reflow to fit page (Uniform scale)", Tag = 2 };
        var rbCompactSpace = new RadioButton { Content = "Compact spacing (Shrink gaps first)", Tag = 5 };
        var rbCompactShapes = new RadioButton { Content = "Compact shapes (Shrink shapes first)", Tag = 6 };
        var rbUltraCompact = new RadioButton { Content = "Ultra compact (Shrink both equally)", Tag = 7 };

        rbGroup.Children.Add(rbExact);
        rbGroup.Children.Add(rbReflow);
        rbGroup.Children.Add(rbCompactSpace);
        rbGroup.Children.Add(rbCompactShapes);
        rbGroup.Children.Add(rbUltraCompact);

        body.Children.Add(rbGroup);
        body.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Large diagram",
            Content = body,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
        if (result != ContentDialogResult.Primary) return 1; // default to exact on cancel

        int mode = 1;
        if (rbReflow.IsChecked == true) mode = 2;
        else if (rbCompactSpace.IsChecked == true) mode = 5;
        else if (rbCompactShapes.IsChecked == true) mode = 6;
        else if (rbUltraCompact.IsChecked == true) mode = 7;

        if (remember.IsChecked == true)
        {
            App.Settings.Current.OversizedDiagramMode = mode;
            App.Settings.Save();
            ViewModel.OversizedDiagramMode = mode; // keep the Settings UI in sync
        }
        return mode;
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e) => OpenExportedFile(ViewModel.LastOutputPath);

    // ---- Preview ----

    private async Task InitializePreviewAsync()
    {
        await PreviewWebView.EnsureCoreWebView2Async(await Services.WebView2EnvironmentFactory.CreateAsync());
        MapAssetHost(PreviewWebView.CoreWebView2);
        var core = PreviewWebView.CoreWebView2;

        // ISS-007: the preview is a document viewer, not a browser. Suppress the default right-click
        // context menu, and intercept link clicks so external URLs open in the system browser instead
        // of navigating the preview away from the document. Internal schemes (the marksmith.assets
        // virtual host, data: and about: used by NavigateToString) are allowed through.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.NavigationStarting += (s, e) =>
        {
            var uri = e.Uri ?? string.Empty;
            if (uri.Length == 0 ||
                uri.StartsWith("https://marksmith.assets", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Cancel = true;

            if (Uri.TryCreate(uri, UriKind.Absolute, out var external) &&
                (external.Scheme == Uri.UriSchemeHttp || external.Scheme == Uri.UriSchemeHttps))
            {
                _ = Windows.System.Launcher.LaunchUriAsync(external);
            }
        };
        core.NewWindowRequested += (s, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var external))
            {
                _ = Windows.System.Launcher.LaunchUriAsync(external);
            }
        };

        // The preview auto-refreshes on every change (debounced). Navigation completing satisfies
        // one of the two conditions to hide the spinner; the minimum-time gate satisfies the other.
        PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => _spinNavDone = true;
        PreviewWebView.CoreWebView2.WebMessageReceived += OnPreviewWebMessage;
        // Sync-scroll: once a fresh preview page finishes loading, restore the scroll position the
        // user had in the editor (stashed just before we re-navigated on the tab switch).
        PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => ApplyPendingPreviewScroll();

        // Preview zoom: the page's own script owns the scale (fit-to-width or an absolute zoom);
        // RefreshPreviewAsync seeds the persisted choice into every render, so there is nothing to
        // re-apply after navigation. Here: restore the persisted state and install the
        // Ctrl+wheel bridge.
        _lastPreviewZoom = Math.Clamp(App.Settings.Current.PreviewZoom, PreviewZoomMin, PreviewZoomMax);
        SetPreviewFit(App.Settings.Current.PreviewZoomFit);
        UpdatePreviewZoomReadout(_lastPreviewZoom);
        await SetupPreviewZoomAsync();

        await RefreshPreviewAsync();
    }

    // The focused diagram viewer posts {type:"save-diagram", format, data} when the user clicks
    // PNG/SVG; show a save picker and write the file. Best-effort — a bad message is ignored.
    private async void OnPreviewWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            // Ctrl+wheel over the preview (bridged from the document-created listener): zoom one step.
            // The cursor is over the document here, so the zoom may anchor to it on overflow axes.
            if (type == "preview-zoom")
            {
                var delta = root.TryGetProperty("delta", out var dProp) ? dProp.GetDouble() : 0;
                if (delta != 0) ApplyPreviewZoom(delta < 0 ? Services.ZoomSteps.Next(_lastPreviewZoom) : Services.ZoomSteps.Previous(_lastPreviewZoom), cursor: true);
                return;
            }

            // The page reports the scale it actually applied (fit changes with every resize), so
            // the readout shows the truth and the next +/− steps from what the user is looking at.
            if (type == "preview-scale")
            {
                if (root.TryGetProperty("scale", out var scProp) && scProp.GetDouble() is var applied and > 0)
                {
                    _lastPreviewZoom = applied;
                    UpdatePreviewZoomReadout(applied);
                }
                return;
            }

            // Looking Glass portal (ISS-004): a portal just opened in the preview — hand it the
            // editor's current Markdown so it can reveal (and edit) the source behind the preview.
            if (type == "portal-open")
            {
                _portalOpen = true;
                _portalDirty = false;
                var source = ViewModel.CurrentMarkdown ?? "";
                var js = "if (window.__portalSetSource) { window.__portalSetSource(" +
                         System.Text.Json.JsonSerializer.Serialize(source) + "); }";
                _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
                return;
            }
            if (type == "portal-edit")
            {
                var text = root.TryGetProperty("text", out var tProp) ? tProp.GetString() : null;
                if (text != null && text != (ViewModel.CurrentMarkdown ?? ""))
                {
                    ViewModel.BreakUndoBurst(); // portal typing must undo as its own step
                    ViewModel.CurrentMarkdown = text; // flows to the editor via binding
                    _portalDirty = true;
                    // Live render: swap the preview's canvas in place (no navigation, so the open
                    // portal survives) — the markdown generates right in front of the user's eyes
                    // while they type through the shape.
                    _ = UpdatePreviewCanvasLiveAsync(text);
                }
                return;
            }
            if (type == "portal-closed")
            {
                var wasDirty = _portalDirty;
                _portalOpen = false;
                _portalDirty = false;
                // Content is already live via the in-place swaps; this light re-nav just rebuilds
                // the page scripts (TOC anchors, scroll-spy) without any blur or spinner.
                if (wasDirty) _ = RefreshPreviewAsync(heavy: false);
                return;
            }

            if (type == "mermaid-error")
            {
                var error = root.GetProperty("error").GetString() ?? "Unknown parse error";
                System.Diagnostics.Debug.WriteLine($"[Mermaid Error] {error}");
                ViewModel.StatusText = $"Mermaid Syntax Error: {error}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            if (type == "launch-mermaid-studio" || type == "edit-mermaid-code")
            {
                var code = root.TryGetProperty("code", out var cProp) ? cProp.GetString() : "";
                var idx = root.TryGetProperty("index", out var iProp) ? iProp.GetInt32() : 0;
                DispatcherQueue.TryEnqueue(() => _ = ShowMermaidDiagramStudioWindowAsync(code ?? "", idx));
                return;
            }
            if (type == "mermaid-node-edit")
            {
                var oldText = root.GetProperty("oldText").GetString();
                var newText = root.GetProperty("newText").GetString();
                if (!string.IsNullOrEmpty(oldText) && !string.IsNullOrEmpty(newText) && oldText != newText)
                {
                    var currentMd = ViewModel.CurrentMarkdown ?? "";
                    if (currentMd.Contains(oldText))
                    {
                        ViewModel.BreakUndoBurst(); // label edit must undo as its own step
                        ViewModel.CurrentMarkdown = currentMd.Replace(oldText, newText);
                        ViewModel.StatusText = $"Diagram label updated: '{oldText}' → '{newText}'";
                        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
                    }
                }
                return;
            }
            if (type == "page-overflow")
            {
                var elements = root.GetProperty("elements");
                var desc = string.Join(", ", elements.EnumerateArray().Select(el => el.GetProperty("element").GetString()));
                System.Diagnostics.Debug.WriteLine($"[Page Overflow] Elements overflow: {desc}");
                ViewModel.StatusText = $"Page Overflow: {desc} exceed page width.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            if (type != "save-diagram") return;

            var format = root.GetProperty("format").GetString() ?? "png";
            var data = root.GetProperty("data").GetString() ?? "";

            var ext = format.ToLowerInvariant();
            var file = await Services.NativeFilePicker.PickSaveFileAsync(
                this, "Save the diagram", Services.NativeFilePicker.Purpose.Exports,
                $"{SuggestedNameFromDocument("diagram")}.{ext}",
                new[] { Models.FileType.Of($"{format.ToUpperInvariant()} image", ext) },
                okLabel: "Save", folder: OpenDocumentFolder);
            if (string.IsNullOrEmpty(file)) return;

            if (format == "svg")
                await File.WriteAllTextAsync(file, data);
            else
            {
                var b64 = data.Contains(',') ? data[(data.IndexOf(',') + 1)..] : data;
                await File.WriteAllBytesAsync(file, Convert.FromBase64String(b64));
            }
            ViewModel.StatusText = $"Diagram saved: {file}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Error processing message: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    private async Task ShowMermaidDiagramStudioWindowAsync(string sampleCode, int targetIndex)
    {
        // The editor holds stripped markdown — restore the stashed %% position lines so the
        // studio lays nodes out exactly where the user left them.
        var currentMd = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
            ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);

        var studioWindow = new Views.Mermaid.MermaidDiagramStudioWindow(currentMd, targetIndex);
        studioWindow.SyncToMarkdownRequested += async (s, markdown) =>
        {
            var fullMd = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
                ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);
            var synced = studioWindow.ViewModel.SyncToMarkdown(fullMd);
            // Editor stays clean: strip the fresh position lines back out into the stash.
            ViewModel.BreakUndoBurst(); // studio sync-back must undo as its own step
            ViewModel.CurrentMarkdown = Mermaid.Sync.MermaidSpatialMetadataService.Strip(synced, out _mermaidSpatialStash);
            ViewModel.StatusText = "Mermaid diagram code updated via Diagram Studio.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            await RefreshPreviewAsync(heavy: true);
        };
        studioWindow.Activate();
        await Task.CompletedTask;
    }

    // Serve the bundled web assets (mermaid, KaTeX, highlight.js) from a real https origin so
    // NavigateToString pages can load them — no CDN, works offline. Referenced as
    // https://{Services.WebAssets.Host}/mermaid.min.js etc.
    internal static void MapAssetHost(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
        if (!Directory.Exists(dir)) return;
        try
        {
            core.SetVirtualHostNameToFolderMapping(
                Services.WebAssets.Host, dir,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { /* mapping already set or unavailable — CDN fallback in the HTML still works */ }
        MapImageHost(core);
    }

    // Local images too big to inline into a NavigateToString page are served from this host (Core
    // DocumentImages registers each one as it renders). Only registered files are answered, so a
    // page can't use it to read anything else on disk.
    private const string ImageHost = "marksmith.images";

    private static void MapImageHost(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        try
        {
            Services.DocumentImages.ServedHost = ImageHost;
            core.AddWebResourceRequestedFilter($"https://{ImageHost}/*",
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = e.Request.Uri;
                if (!uri.StartsWith($"https://{ImageHost}/", StringComparison.OrdinalIgnoreCase)) return;
                var path = Services.DocumentImages.ServedPath(uri);
                try
                {
                    if (path is not null)
                    {
                        var stream = File.OpenRead(path).AsRandomAccessStream();
                        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK",
                            $"Content-Type: {Services.DocumentImages.MimeFor(path)}\r\nCache-Control: no-cache");
                        return;
                    }
                }
                catch { /* locked or vanished since the render: answer 404 below */ }
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            };
        }
        catch { /* older runtime: big images keep their file path, as before */ }
    }

    // The WebView initializes asynchronously after launch, but headless work (auto-generate on
    // ingest, watched files, API convert, batch) can arrive first — "WebView2 is not initialized".
    // Every headless caller awaits this before rendering.
    private async Task<bool> EnsurePreviewWebViewAsync()
    {
        if (PreviewWebView.CoreWebView2 is not null) return true;
        try { await PreviewWebView.EnsureCoreWebView2Async(await Services.WebView2EnvironmentFactory.CreateAsync()); } catch { return false; }
        return PreviewWebView.CoreWebView2 is not null;
    }

    // WebView2 doesn't reliably paint while the window is hidden (tray mode), which can yield blank
    // PDFs. For the duration of a headless render, present the window off-screen, unactivated, and
    // hidden from the taskbar/Alt-Tab — the user never sees a thing — then hide it again.
    private (bool Hidden, Windows.Graphics.PointInt32 Pos) BeginOffscreenRender()
    {
        if (AppWindow.IsVisible) return (false, default);
        var pos = AppWindow.Position;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        AppWindow.Show(false); // no activation — focus stays wherever the user has it
        return (true, pos);
    }

    private void EndOffscreenRender((bool Hidden, Windows.Graphics.PointInt32 Pos) state)
    {
        if (!state.Hidden) return;
        AppWindow.Hide();
        AppWindow.Move(state.Pos);
        AppWindow.IsShownInSwitchers = true;
    }

    private sealed class OffscreenScope : IDisposable
    {
        private readonly MainWindow _window;
        private readonly (bool Hidden, Windows.Graphics.PointInt32 Pos) _state;

        public OffscreenScope(MainWindow window)
        {
            _window = window;
            _state = window.BeginOffscreenRender();
        }

        public void Dispose()
        {
            _window.EndOffscreenRender(_state);
        }
    }

    // Called on every refresh. Starts the animated spinner if it isn't already running; otherwise
    // just marks the new navigation in-flight so it stays up until the latest render finishes.
    private void StartSpinner()
    {
        if (_spinActive) { _spinNavDone = false; return; }

        _spinActive = true;
        _spinNavDone = false;
        _spinPhase = 0;
        _spinMode = 1 - _spinMode; // alternate: spin, then figure-eight, then spin…

        SpinnerLogoTransform.TranslateX = 0;
        SpinnerLogoTransform.TranslateY = 0;
        SpinnerLogoTransform.Rotation = 0;

        PreviewSpinner.Visibility = Visibility.Visible;
        _spinTimer!.Start();
    }

    private void OnSpinTick()
    {
        _spinPhase += SpinDt;

        if (_spinMode == 0)
        {
            // Spin the logo about its centre (RenderTransformOrigin 0.5,0.5).
            SpinnerLogoTransform.Rotation = (_spinPhase * 300) % 360;
        }
        else
        {
            // Upright logo tracing a figure-eight (Gerono lemniscate: x = A sin t, y = (A/2) sin 2t).
            var t = _spinPhase * 2.4;
            const double a = 46;
            SpinnerLogoTransform.TranslateX = a * Math.Sin(t);
            SpinnerLogoTransform.TranslateY = a * 0.55 * Math.Sin(2 * t);
            SpinnerLogoTransform.Rotation = 0; // stays upright
        }

        if (_spinNavDone && _spinPhase >= SpinMinSec) HideSpinner();
    }

    private void HideSpinner()
    {
        _spinTimer!.Stop();
        _spinActive = false;
        PreviewSpinner.Visibility = Visibility.Collapsed;
        // Reveal the freshly-rendered content: the blur clears over a smooth transition as the sprite goes.
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(
            "document.body && document.body.classList.remove('ms-loading')");
    }

    // ---- IWebRenderHost / IUiPrompts implementation ----

    public Task<bool> EnsureReadyAsync() => EnsurePreviewWebViewAsync();

    public async Task NavigateToStringAsync(string html)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var tcs = new TaskCompletionSource();
        void OnNavigationCompleted(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= OnNavigationCompleted;
            tcs.TrySetResult();
        }
        core.NavigationCompleted += OnNavigationCompleted;
        core.NavigateToString(html);
        await tcs.Task;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        return await core.ExecuteScriptAsync(javaScript);
    }

    public async Task<bool> PrintToPdfAsync(string outputPath, Services.PdfPageSetup setup)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        return await PrintExportToPdfInternalAsync(core, outputPath, setup);
    }

    private bool _exportWebViewInitialized;
    private async Task<Microsoft.Web.WebView2.Core.CoreWebView2> EnsureExportCoreWebView2Async()
    {
        await ExportWebView.EnsureCoreWebView2Async();
        if (!_exportWebViewInitialized && ExportWebView.CoreWebView2 is { } core)
        {
            MapAssetHost(core);
            _exportWebViewInitialized = true;
        }
        return ExportWebView.CoreWebView2 ?? throw new InvalidOperationException("ExportWebView failed to initialize.");
    }

    private async Task<bool> EnsureExportWebViewAsync()
    {
        try
        {
            await EnsureExportCoreWebView2Async();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExportWebView Init Error] {ex}");
            return false;
        }
    }

    private async Task<bool> PrintExportToPdfInternalAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, string outputPath, Services.PdfPageSetup setup)
    {
        var hasHeaderFooter = !string.IsNullOrEmpty(setup.HeaderTemplate) || !string.IsNullOrEmpty(setup.FooterTemplate);
        if (!hasHeaderFooter)
        {
            var printSettings = core.Environment.CreatePrintSettings();
            printSettings.ShouldPrintBackgrounds = setup.PrintBackgrounds;
            printSettings.ShouldPrintHeaderAndFooter = false;
            printSettings.PageWidth = setup.PageWidthIn;
            printSettings.PageHeight = setup.PageHeightIn;
            printSettings.MarginTop = setup.MarginTopIn;
            printSettings.MarginBottom = setup.MarginBottomIn;
            printSettings.MarginLeft = setup.MarginLeftIn;
            printSettings.MarginRight = setup.MarginRightIn;
            return await core.PrintToPdfAsync(outputPath, printSettings);
        }

        var args = new Dictionary<string, object?>
        {
            ["paperWidth"] = setup.PageWidthIn,
            ["paperHeight"] = setup.PageHeightIn,
            ["marginTop"] = setup.MarginTopIn,
            ["marginBottom"] = setup.MarginBottomIn,
            ["marginLeft"] = setup.MarginLeftIn,
            ["marginRight"] = setup.MarginRightIn,
            ["printBackground"] = setup.PrintBackgrounds,
            ["displayHeaderFooter"] = true,
            ["headerTemplate"] = setup.HeaderTemplate,
            ["footerTemplate"] = setup.FooterTemplate,
            ["preferCSSPageSize"] = false,
        };
        var resultJson = await core.CallDevToolsProtocolMethodAsync(
            "Page.printToPDF", System.Text.Json.JsonSerializer.Serialize(args));
        using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
        var b64 = doc.RootElement.GetProperty("data").GetString();
        if (string.IsNullOrEmpty(b64)) return false;
        await File.WriteAllBytesAsync(outputPath, Convert.FromBase64String(b64));
        return true;
    }

    private sealed class BackgroundExportHostImpl : Services.IWebRenderHost
    {
        private readonly MainWindow _window;
        public BackgroundExportHostImpl(MainWindow window) => _window = window;

        public Task<bool> EnsureReadyAsync() => _window.EnsureExportWebViewAsync();

        public async Task NavigateToStringAsync(string html)
        {
            var core = await _window.EnsureExportCoreWebView2Async();
            var tcs = new TaskCompletionSource();
            void OnCompleted(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnCompleted;
                tcs.TrySetResult();
            }
            core.NavigationCompleted += OnCompleted;
            core.NavigateToString(html);
            await tcs.Task;
        }

        public async Task<string?> ExecuteScriptAsync(string javaScript)
        {
            var core = await _window.EnsureExportCoreWebView2Async();
            return await core.ExecuteScriptAsync(javaScript);
        }

        public async Task<bool> PrintToPdfAsync(string outputPath, Services.PdfPageSetup setup)
        {
            var core = await _window.EnsureExportCoreWebView2Async();
            return await _window.PrintExportToPdfInternalAsync(core, outputPath, setup);
        }

        public Task BeginHarvestAsync() => Task.CompletedTask;
        public Task EndHarvestAsync() => Task.CompletedTask;
    }

    public Task BeginHarvestAsync() => Task.CompletedTask;
    public Task EndHarvestAsync()
    {
        _mermaidHarvestActive = false;
        return RefreshPreviewAsync(false);
    }

    private async Task RefreshPreviewAsync(bool heavy = true)
    {
        if (PreviewWebView.CoreWebView2 is null) return;
        if (_mermaidHarvestActive) return; // snapshot renderer owns the WebView right now

        // ISS-004: any refresh re-navigates the preview, which tears down any open portal's DOM —
        // clear the portal flags so they don't go stale and block future typing refreshes.
        _portalOpen = false;
        _portalDirty = false;

        // The loading sprite + blur treatment is retired: the user wants to literally watch the
        // preview render — no blur, no loading symbols. The pre-paint scroll restore below keeps
        // the re-render from visibly jumping, so a bare refresh reads as an in-place repaint.
        // (`heavy` is kept for call-site compatibility / future intensity signalling.)
        _ = heavy;

        var vm = ViewModel;
        var markdown = await ResolvePreviewMarkdownAsync();

        var prepared = vm.PrepareMarkdown(markdown, forPreview: true);
        var html = vm.PreviewAsEmail ? vm.BuildEmailPreviewHtml(prepared) : vm.BuildPreviewHtml(prepared, interactive: true);

        // Essential refresh check: skip re-navigating WebView2 if content is identical and not heavy
        if (!heavy && markdown == _lastLiveCanvasMd && _lastRenderedHtml != null && html == _lastRenderedHtml)
            return;

        _lastLiveCanvasMd = markdown;
        _lastRenderedHtml = html;

        if (vm.IsDebugModeEnabled)
        {
            try
            {
                var logsDir = Path.Combine(Services.AppPaths.ConfigDir, "DebugLogs");
                Directory.CreateDirectory(logsDir);
                var logFile = Path.Combine(logsDir, $"Preview_Session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("n").Substring(0, 4)}.log");
                
                var prompt = "Tell me everything wrong with the way we displayed the MD format in this HTML and how to resolve it:\n\n";
                await File.WriteAllTextAsync(logFile, prompt + html);
                _sessionLogFiles.Add(logFile);
            }
            catch { }
        }

        if (_pendingPreviewScroll is null)
            _pendingPreviewScroll = await CapturePreviewScrollPositionAsync();

        if (_pendingPreviewScroll is { } sc)
        {
            var fxStr = sc.FractionX.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var fyStr = sc.FractionY.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var pxStr = sc.PixelX.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            var pyStr = sc.PixelY.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

            html = html.Replace("</body>",
                "<script>(function(){" +
                "var maxX=document.documentElement.scrollWidth-window.innerWidth;" +
                "var maxY=document.documentElement.scrollHeight-window.innerHeight;" +
                $"var tx=maxX>0?Math.round({fxStr}*maxX):{pxStr};" +
                $"var ty=maxY>0?Math.round({fyStr}*maxY):{pyStr};" +
                "if(tx>0||ty>0){window.scrollTo(tx,ty);}" +
                "})();</script></body>");
        }

        // Seed the zoom before the page's zoom script first lays out (it waits for
        // DOMContentLoaded), so a re-render never flashes at the wrong scale.
        html = html.Replace("</body>", "<script>window.__msZoom=" + PreviewZoomArg() + ";</script></body>");

        try
        {
            if (PreviewWebView?.CoreWebView2 != null && !string.IsNullOrEmpty(html))
            {
                PreviewWebView.CoreWebView2.NavigateToString(html);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WebView2] NavigateToString skipped: {ex.Message}");
        }
    }

    private async Task<string> ResolvePreviewMarkdownAsync()
    {
        var vm = ViewModel;
        if (vm.UsePasteSource) return vm.PastedMarkdown ?? "";
        if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            return await Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath);
        if (!string.IsNullOrWhiteSpace(vm.PastedMarkdown))
            return vm.PastedMarkdown;
        // "Paste" was the old name of the editor tab; it's "Code" now.
        return "# MarkSmith\n\nDrop a Markdown file on **1 · Source**, or open the **Code** tab and paste or start typing.";
    }

    private async Task<bool> UpdatePreviewCanvasLiveAsync(string? markdown = null)
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null || _mermaidHarvestActive) return false;

        markdown ??= await ResolvePreviewMarkdownAsync();
        if (markdown == _lastLiveCanvasMd) return true; // canvas already shows exactly this source

        var vm = ViewModel;
        var inner = vm.BuildPreviewCanvasHtml(vm.PrepareMarkdown(markdown, forPreview: true));
        if (inner is null) return false;

        var js = "(function(){var c=document.getElementById('canvas');if(!c)return 'nav';" +
                 "var h=" + System.Text.Json.JsonSerializer.Serialize(inner) + ";" +
                 "if(h.indexOf('class=\"math\"')>=0&&!window.__msRenderMath)return 'nav';" +
                 "if(h.indexOf('language-')>=0&&!window.hljs)return 'nav';" +
                 "var sx=window.scrollX||0, sy=window.scrollY||0;" +
                 "c.innerHTML=h;" +
                 "try{if(window.mermaid&&window.mermaid.run){window.mermaid.run();}}catch(x){}" +
                 "try{if(window.__msRenderMath){window.__msRenderMath(c);}}catch(x){}" +
                 "try{if(window.hljs){c.querySelectorAll('pre code').forEach(function(el){window.hljs.highlightElement(el);});}}catch(x){}" +
                 "window.scrollTo(sx, sy);" +
                 "return 'ok';})();";
        string result;
        try { result = await core.ExecuteScriptAsync(js); } catch { return false; }
        if (result != "\"ok\"") return false;
        _lastLiveCanvasMd = markdown;
        return true;
    }

    private async Task<PreviewScrollState?> CapturePreviewScrollPositionAsync()
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return null;
        string result;
        try
        {
            result = await core.ExecuteScriptAsync(
                "(function(){" +
                "var maxX=document.documentElement.scrollWidth-window.innerWidth;" +
                "var maxY=document.documentElement.scrollHeight-window.innerHeight;" +
                "var fx=maxX>0?(window.scrollX/maxX):0;" +
                "var fy=maxY>0?(window.scrollY/maxY):0;" +
                "return JSON.stringify({fx:fx,fy:fy,px:window.scrollX||0,py:window.scrollY||0});" +
                "})();");
        }
        catch { return null; }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result);
            var root = doc.RootElement;
            var fx = Math.Clamp(root.GetProperty("fx").GetDouble(), 0.0, 1.0);
            var fy = Math.Clamp(root.GetProperty("fy").GetDouble(), 0.0, 1.0);
            var px = Math.Max(0.0, root.GetProperty("px").GetDouble());
            var py = Math.Max(0.0, root.GetProperty("py").GetDouble());
            return new PreviewScrollState(fx, fy, px, py);
        }
        catch { return null; }
    }

    private void OnToggleDebugModeInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.IsDebugModeEnabled = !ViewModel.IsDebugModeEnabled;
        ViewModel.StatusText = ViewModel.IsDebugModeEnabled ? "Debug Mode Enabled (HTML logging active)" : "Debug Mode Disabled";
        ViewModel.StatusSeverity = ViewModel.IsDebugModeEnabled ? Models.StatusSeverity.Warning : Models.StatusSeverity.Informational;
        args.Handled = true;
    }

    // ---- Global keyboard shortcuts (documented in the F1 cheatsheet) ----

    private void OnOpenFileAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        OnBrowseFileClick(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnGeneratePdfAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsNotBusy) _ = ViewModel.ConvertToPdfAsync();
        args.Handled = true;
    }

    private void OnExportDocxAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsNotBusy) _ = ViewModel.ConvertToDocxAsync();
        args.Handled = true;
    }

    private void OnExportPptxAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsNotBusy) _ = ViewModel.ConvertToPptxAsync();
        args.Handled = true;
    }

    private void OnMermaidStudioAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        OnOpenMermaidStudioClick(this, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnRootPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Ctrl+, opens Settings. The comma key is VirtualKey 188 (VK_OEM_COMMA); WinUI's VirtualKey
        // enum has no named member for it and its accelerator-string builder crashes on it, so this
        // shortcut is handled here rather than as a XAML KeyboardAccelerator (see constructor note).
        if (e.Key == (Windows.System.VirtualKey)188)
        {
            var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (ctrl && !shift && !alt)
            {
                OnSettingsClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private void OnShortcutsCheatsheetInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = ShowShortcutsCheatsheetAsync();
        args.Handled = true;
    }

    private void OnCommandPaletteInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = ShowCommandPaletteAsync();
        args.Handled = true;
    }

    private void OnSaveDocumentAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        _ = SaveDocumentToFileAsync();
        args.Handled = true;
    }

    // Ctrl+S writes the editor's current content back to the source .md file. Editing a file flips the
    // buffer into paste mode, so without this the only way to keep edits was to re-create the file by
    // hand — now the editor is a real round-trip editor for file-based documents.
    private async Task SaveDocumentToFileAsync()
    {
        var path = ViewModel.InputFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ViewModel.StatusText = "Nothing to save to: this text wasn't opened from a file. Keep it with Export as Markdown (.md), or open a file (Ctrl+O).";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        // Pasted, sent-in or imported text replaced the file's text in the editor. Writing it over
        // the file it never came from would destroy that file.
        if (!ViewModel.IsEditingOpenFile)
        {
            ViewModel.StatusText = $"Not saved: this text didn't come from {Path.GetFileName(path)}, so Ctrl+S won't write over it. Keep it with Export as Markdown (.md).";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        if (!Plugins.PluginFileReader.IsMarkdownFile(path))
        {
            await SaveConvertedSourceAsMarkdownAsync(path);
            return;
        }
        try
        {
            // Restore any stashed %% position metadata so the saved file keeps studio layouts.
            if (ViewModel.OpenFileChangedOnDisk() && !await ConfirmOverwriteExternalChangeAsync(path)) return;
            var toSave = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
                ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);
            await File.WriteAllTextAsync(path, toSave);
            ViewModel.MarkOpenFileSaved();
            ViewModel.StatusText = $"Saved changes to {path}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Save failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Another program (a second editor, OneDrive, git) wrote the file after MarkSmith read it.
    // Saving would silently throw those changes away, so ask first.
    private async Task<bool> ConfirmOverwriteExternalChangeAsync(string path)
    {
        var root = RootGrid?.XamlRoot ?? Content?.XamlRoot;
        if (root is null) return false;
        var dialog = new ContentDialog
        {
            Title = "File changed outside MarkSmith",
            Content = new TextBlock
            {
                Text = $"{Path.GetFileName(path)} was changed by another program after MarkSmith opened it. " +
                       "Saving replaces those changes with what's in the editor.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
            },
            PrimaryButtonText = "Replace their changes",
            CloseButtonText = "Don't save",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        var replace = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) == ContentDialogResult.Primary;
        if (!replace)
        {
            ViewModel.StatusText = $"Not saved. {Path.GetFileName(path)} keeps the other program's changes; your edits are still in the editor.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
        return replace;
    }

    // The open file is a Word/PDF/HTML/email document shown as Markdown. Writing Markdown over it
    // would destroy it, so Ctrl+S writes "<name>.md" beside it (Services.Import.MarkdownCopy) and
    // carries on editing that file.
    private async Task SaveConvertedSourceAsMarkdownAsync(string sourcePath)
    {
        try
        {
            var fallbackDir = !string.IsNullOrWhiteSpace(ViewModel.OutputFolder) ? ViewModel.OutputFolder
                : !string.IsNullOrWhiteSpace(App.Settings.Current.OutputFolder) ? App.Settings.Current.OutputFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var toSave = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
                ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);
            var target = await Task.Run(() => Services.Import.MarkdownCopy.Save(sourcePath, toSave, fallbackDir));

            ViewModel.InputFilePath = target;
            ViewModel.UsePasteSource = false;
            ViewModel.StatusText = $"Saved as Markdown: {Path.GetFileName(target)} · {Path.GetFileName(sourcePath)} is unchanged. Ctrl+S now saves to the .md.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            ViewModel.StatusOutputPath = target;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Couldn't save a Markdown copy: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    private void OnFindAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ShowFindBar();
        args.Handled = true;
    }

    private async void OnShortcutsButtonClick(object sender, RoutedEventArgs e)
    {
        await ShowShortcutsCheatsheetAsync();
    }

    // Generated from Core's KeyboardShortcuts, the same list the palette and tooltips read and a
    // test checks against the XAML accelerators, so every row is a shortcut that works. Each key
    // is drawn as its own keycap; alternative chords stack in the keys column.
    private async Task ShowShortcutsCheatsheetAsync()
    {
        var res = Application.Current.Resources;
        var secondary = (Brush)res["TextFillColorSecondaryBrush"];

        var rows = new StackPanel { Spacing = 6 };
        foreach (var (section, sectionRows) in Shortcuts.Sheet())
        {
            if (sectionRows.Count == 0) continue;
            rows.Children.Add(new TextBlock
            {
                Text = section,
                Style = (Style)res["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, rows.Children.Count == 0 ? 0 : 14, 0, 2),
            });
            if (section == Shortcuts.FormattingSection)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "These work while the Markdown editor has focus.",
                    Style = (Style)res["CaptionTextBlockStyle"],
                    Foreground = secondary,
                    Margin = new Thickness(0, -2, 0, 2),
                });
            }

            foreach (var shortcut in sectionRows)
            {
                var row = new Grid { ColumnSpacing = 16, MinHeight = 30 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Alternative chords stack one per line, so three-key pairs never overflow the column.
                var keys = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                var chordKeyLists = shortcut.Chords.Count > 0
                    ? shortcut.Chords.Select(c => c.Keys).ToList()
                    : new List<IReadOnlyList<string>> { (shortcut.Gesture ?? "").Split('+') };
                foreach (var chordKeys in chordKeyLists)
                {
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    AddChord(line, chordKeys);
                    keys.Children.Add(line);
                }
                Grid.SetColumn(keys, 0);

                var desc = new TextBlock { Text = shortcut.Action, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(desc, 1);

                row.Children.Add(keys);
                row.Children.Add(desc);
                rows.Children.Add(row);
            }
        }

        void AddChord(StackPanel host, IEnumerable<string> chordKeys)
        {
            var first = true;
            foreach (var key in chordKeys)
            {
                if (!first) host.Children.Add(new TextBlock { Text = "+", FontSize = 11, Foreground = secondary, VerticalAlignment = VerticalAlignment.Center });
                first = false;
                host.Children.Add(new Border
                {
                    Background = (Brush)res["ControlFillColorDefaultBrush"],
                    BorderBrush = (Brush)res["ControlStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1, 1, 1, 2),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 1, 7, 2),
                    MinWidth = 24,
                    Child = new TextBlock { Text = key, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                });
            }
        }

        var footer = new TextBlock
        {
            Text = $"Can't remember a shortcut? Press {Shortcuts.KeysFor("app.palette")} and type what you want to do.",
            Style = (Style)res["CaptionTextBlockStyle"],
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        };
        rows.Children.Add(footer);

        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            // Scrolls rather than growing past the window on smaller screens.
            Content = new ScrollViewer { Content = rows, Padding = new Thickness(0, 0, 14, 0), MaxHeight = 540, Width = 500 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
    }

    // ---- Command palette (Ctrl+K): fuzzy search across actions, themes, and recent files ----

    // Shortcut is display-only, read from Core's KeyboardShortcuts (the accelerators live in XAML).
    private sealed record PaletteCommand(string Label, string Category, Func<Task> Run, string Shortcut = "", string Keywords = "")
    {
        public Visibility ShortcutVisibility => Shortcut.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // What a screen reader announces for the row (it read the record's debug text,
        // "PaletteCommand { Label = …, Run = System.Func`1[…] }").
        public override string ToString() => Shortcut.Length > 0 ? $"{Label}, {Category}, {Shortcut}" : $"{Label}, {Category}";
    }

    // Every action a user might go looking for: run #21b found the palette had no Find, Save,
    // Import, view switching, inserting or clean-up. Names match the toolbar and menus (one name
    // per studio: Diagram Studio, Shape Studio, SmartArt Studio, Document Galaxy, Suite Hub).
    private List<PaletteCommand> BuildPaletteCommands()
    {
        PaletteCommand Do(string label, string category, Action run, string? shortcutId = null, string keywords = "") =>
            new(label, category, () => { run(); return Task.CompletedTask; }, shortcutId is null ? "" : Shortcuts.KeysFor(shortcutId), keywords);
        PaletteCommand DoAsync(string label, string category, Func<Task> run, string? shortcutId = null, string keywords = "") =>
            new(label, category, run, shortcutId is null ? "" : Shortcuts.KeysFor(shortcutId), keywords);
        // Editing commands act on the editor, so bring it into view from Preview first.
        PaletteCommand Edit(string label, RoutedEventHandler handler, string? shortcutId = null, string keywords = "") =>
            Do(label, "Edit", () => { EnsureEditorVisible(); handler(this, new RoutedEventArgs()); }, shortcutId, keywords);
        PaletteCommand Insert(string label, RoutedEventHandler handler) =>
            Do(label, "Insert", () => { EnsureEditorVisible(); handler(this, new RoutedEventArgs()); });
        var click = new RoutedEventArgs();

        var cmds = new List<PaletteCommand>
        {
            DoAsync("Export PDF", "Export", () => ViewModel.ConvertToPdfAsync(), "export.pdf", "save as pdf acrobat"),
            DoAsync("Export Word (.docx)", "Export", () => ViewModel.ConvertToDocxAsync(), "export.docx", "docx microsoft office"),
            DoAsync("Export PowerPoint (.pptx)", "Export", () => ViewModel.ConvertToPptxAsync(), "export.pptx", "pptx slides deck presentation"),
            DoAsync("Export EPUB", "Export", () => ViewModel.ConvertToEpubAsync(), keywords: "ebook kindle book"),
            DoAsync("Export HTML", "Export", () => ViewModel.ConvertToHtmlAsync()),
            DoAsync("Email draft (open in Outlook)", "Export", () => ViewModel.CreateEmailDraftAsync(), "export.email", "mail send message"),
            DoAsync("Save as email (.eml)", "Export", () => ViewModel.SaveEmailAsync(), keywords: "eml mail message"),
            DoAsync("Save as Outlook message (.msg)", "Export", () => ViewModel.SaveOutlookMessageAsync(), keywords: "msg outlook mail message"),
            DoAsync("Copy as email", "Export", () => ViewModel.CopyAsEmailAsync(), keywords: "clipboard paste mail message reply gmail outlook"),
            DoAsync("Export all formats", "Export", () => ViewModel.ExportAllAsync()),
            Do("Copy the rendered HTML", "Export", () => OnCopyHtmlClick(this, click)),
            Do("Print the rendered document", "Export", PrintDocument, "file.print"),
            Do("Recent exports", "Export", () => OnExportHistoryMenuClick(this, click)),

            Do("Open a document", "File", () => OnBrowseFileClick(this, click), "file.open"),
            Do("Open an email (.eml or .msg)", "File", () => OnBrowseFileClick(this, click), keywords: "outlook message msg eml mail"),
            Do("Import Word, PDF, HTML or email as a new document", "File", () => OnImportDocumentClick(this, click)),
            DoAsync("Save (converted files save as a Markdown copy)", "File", SaveDocumentToFileAsync, "file.save"),
            Do("Version history", "File", () => OnOpenHistoryClick(this, click), keywords: "undo restore backup checkpoint"),

            Do("Find", "Edit", () => ShowFindBar(), "edit.find", "search look up"),
            Do("Find and replace", "Edit", () => ShowFindBar(replace: true), "edit.replace", "search substitute swap"),
            Edit("Bold", OnBoldClick, "format.bold"),
            Edit("Italic", OnItalicClick, "format.italic"),
            Edit("Strikethrough", OnStrikethroughClick),
            Edit("Heading 1", OnH1Click, "format.h1"),
            Edit("Heading 2", OnH2Click, "format.h2"),
            Edit("Heading 3", OnH3Click, "format.h3"),
            Edit("Heading 4", OnH4Click, "format.h4"),
            Edit("Bullet list", OnBulletListClick),
            Edit("Numbered list", OnNumberedListClick),
            Edit("Task list", OnTaskListClick),
            Edit("Blockquote", OnBlockquoteClick),
            Edit("Make selection UPPERCASE", OnTransformUpperClick),
            Edit("Make selection lowercase", OnTransformLowerClick),
            Edit("Make selection Title Case", OnTransformTitleClick),
            Edit("Sort lines A to Z", OnSortLinesAscClick),
            Edit("Sort lines Z to A", OnSortLinesDescClick),
            Edit("Remove duplicate lines", OnDedupeLinesClick),
            Edit("Clean up document", OnCleanupClick, keywords: "tidy fix normalise normalize"),

            Insert("Insert link", OnLinkClick),
            Insert("Insert image", OnImageClick),
            Insert("Insert table", OnTableClick),
            Insert("Insert code block", OnCodeBlockClick),
            Insert("Insert table from a spreadsheet", OnInsertSpreadsheetClick),
            Insert("Insert workflow", OnInsertWorkflowClick),
            Insert("Insert timeline", OnInsertTimelineClick),
            Insert("Insert SmartArt", OnInsertSmartArtClick),
            Insert("Insert references", OnInsertReferencesClick),
            Do("Copy a table to Excel", "Insert", () => OnExportTableClick(this, click)),

            Do("Code view (editor only)", "View", () => ViewCodeTab.IsSelected = true),
            Do("Split view (editor and preview)", "View", () => ViewSplitTab.IsSelected = true),
            Do("Preview view (rendered page only)", "View", () => ViewPreviewTab.IsSelected = true),
            Do("Toggle focus mode", "View", () => { if (FocusModeToggle != null) FocusModeToggle.IsChecked = FocusModeToggle.IsChecked != true; }, "view.focus"),
            Do("Toggle Looking Glass portal", "View", () => { if (LookingGlassToggle.IsEnabled) LookingGlassToggle.IsChecked = LookingGlassToggle.IsChecked != true; }),
            Do("Toggle preview as email", "View", () => EmailPreviewToggle.IsChecked = EmailPreviewToggle.IsChecked != true),
            Do("Document outline", "View", () => OutlineButton.Flyout?.ShowAt(OutlineButton)),

            Do("Open Diagram Studio", "Studio", () => OnOpenMermaidStudioClick(this, click), "studio.diagram", "mermaid flowchart chart"),
            Do("Open Shape Studio", "Studio", () => OnOpenShapeDesignStudioClick(this, click), keywords: "vector shapes drawing"),
            Do("Open SmartArt Studio", "Studio", () => OnOpenSmartArtDesignStudioClick(this, click)),
            Do("Open Document Galaxy", "Studio", () => OnOpenMindMapGalaxyClick(this, click), keywords: "mind map graph links"),
            Do("Open Suite Hub", "Studio", () => OnSuiteHubClick(this, click), keywords: "integrations extension cli mcp"),

            Do("Open Settings", "App", () => OnSettingsClick(this, click), "app.settings", "preferences options"),
            DoAsync("Take the welcome tour", "App", ShowWelcomeTourAsync),
            DoAsync("Show keyboard shortcuts", "App", ShowShortcutsCheatsheetAsync, "app.shortcuts", "keys hotkeys help"),
        };

        foreach (var theme in App.Themes.All)
        {
            var name = theme.Name;
            cmds.Add(new($"Switch theme: {name}", "Theme", () => { ViewModel.SelectedThemeName = name; return Task.CompletedTask; }));
        }

        foreach (var path in ViewModel.RecentFiles.ToList())
        {
            var p = path;
            cmds.Add(new($"Open recent: {System.IO.Path.GetFileName(p)}", "Recent", () =>
            {
                ViewModel.InputFilePath = p;
                ViewModel.UsePasteSource = false;
                return Task.CompletedTask;
            }));
        }

        return cmds;
    }

    private async Task ShowCommandPaletteAsync()
    {
        var commands = BuildPaletteCommands();

        var search = new TextBox { PlaceholderText = "Type what you want to do: export, find, heading, theme\u2026", FontSize = 14 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(search, "Search commands");
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340, IsItemClickEnabled = true };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<Grid ColumnSpacing='10' Padding='0,4'>" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
            "<TextBlock Text='{Binding Label}' FontSize='13' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>" +
            "<TextBlock Grid.Column='1' Text='{Binding Category}' FontSize='11' VerticalAlignment='Center' Foreground='{ThemeResource TextFillColorTertiaryBrush}'/>" +
            "<Border Grid.Column='2' Visibility='{Binding ShortcutVisibility}' VerticalAlignment='Center' CornerRadius='3' Padding='5,0,5,1' " +
            "Background='{ThemeResource SubtleFillColorSecondaryBrush}' BorderBrush='{ThemeResource ControlStrokeColorDefaultBrush}' BorderThickness='1'>" +
            "<TextBlock Text='{Binding Shortcut}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></Border>" +
            "</Grid></DataTemplate>");
        // Without this a query with no hits just shows an empty box, which reads as broken.
        var noMatches = new TextBlock
        {
            Opacity = 0.6,
            FontSize = 13,
            Margin = new Thickness(4, 6, 4, 6),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void Refresh()
        {
            var q = search.Text.Trim();
            var filtered = Services.CommandSearch.Rank(commands, q, c => c.Label, c => c.Category, c => c.Keywords);
            list.ItemsSource = filtered;
            if (filtered.Count > 0) list.SelectedIndex = 0;
            noMatches.Text = $"No commands, themes or recent files match \u201C{q}\u201D.";
            noMatches.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(noMatches);

        var dialog = new ContentDialog
        {
            Title = "Command palette",
            Content = panel,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };

        PaletteCommand? chosen = null;
        search.TextChanged += (s, e) => Refresh();
        search.KeyDown += (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && list.SelectedItem is PaletteCommand c)
            {
                chosen = c;
                dialog.Hide();
                e.Handled = true;
            }
            else if (e.Key is Windows.System.VirtualKey.Down or Windows.System.VirtualKey.Up && list.Items.Count > 0)
            {
                var idx = list.SelectedIndex;
                idx = e.Key == Windows.System.VirtualKey.Down ? Math.Min(idx + 1, list.Items.Count - 1) : Math.Max(idx - 1, 0);
                list.SelectedIndex = idx;
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
        };
        list.ItemClick += (s, e) => { if (e.ClickedItem is PaletteCommand c) { chosen = c; dialog.Hide(); } };

        Refresh();
        search.Focus(FocusState.Programmatic);

        await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);

        if (chosen is not null) await chosen.Run();
    }

    // Debug mode (Ctrl+Alt+T) writes one log per preview render. On exit this used to read every
    // log back and dump the raw HTML into a single-line TextBox (megabytes on a long session),
    // with only "Close and Exit". Now it says how many logs were kept and where, and offers to
    // open the folder before exiting — the files are the useful artefact, not a wall of HTML.
    private async Task ShowDebugLogsDialogAndExitAsync()
    {
        var logs = _sessionLogFiles.Where(File.Exists).ToList();
        var folder = logs.Count > 0 ? Path.GetDirectoryName(logs[0])! : Path.Combine(Services.AppPaths.ConfigDir, "DebugLogs");

        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
        body.Children.Add(new TextBlock
        {
            Text = logs.Count == 1
                ? "Debug mode saved 1 preview log this session."
                : $"Debug mode saved {logs.Count} preview logs this session.",
            TextWrapping = TextWrapping.Wrap,
        });
        var path = new TextBox
        {
            Text = folder,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(path, "Debug log folder");
        body.Children.Add(path);
        body.Children.Add(new TextBlock
        {
            Text = "Each log holds the Markdown-to-HTML output of one render. Turn debug mode off with Ctrl+Alt+T.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        var dialog = new ContentDialog
        {
            Title = "Debug logs saved",
            Content = body,
            PrimaryButtonText = "Open folder and exit",
            CloseButtonText = "Exit",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) == ContentDialogResult.Primary)
        {
            try { await Windows.System.Launcher.LaunchFolderPathAsync(folder); } catch { }
        }

        _exitRequested = true;
        Close();
    }

    private async void OnConvertPdfClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToPdfAsync();
    }

    private async void OnConvertDocxClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToDocxAsync();
    }

    private async void OnConvertPptxClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToPptxAsync();
    }

    private async void OnExportEpubClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToEpubAsync();
    }

    private async void OnExportGoogleDocsClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToGoogleDocsAsync();
    }

    private async void OnExportMarkdownClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToMarkdownAsync();
    }

    private async void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToHtmlAsync();
    }

    // Preview as email has nothing to show in Code view, so turning it on brings the preview up.
    // Looking Glass reveals the Markdown behind the themed page; the email preview has no such
    // layer, so the portal is switched off while it shows.
    private void OnEmailPreviewToggled(object sender, RoutedEventArgs e)
    {
        var on = EmailPreviewToggle.IsChecked == true;
        if (on)
        {
            if (LookingGlassToggle.IsChecked == true) LookingGlassToggle.IsChecked = false;
            if (ViewCodeTab.IsSelected) ViewSplitTab.IsSelected = true;
        }
        LookingGlassToggle.IsEnabled = !on;
        PreviewWidthContainer.Visibility = !on && _viewMode != ViewMode.Code ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEmailDraftClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.CreateEmailDraftAsync();
    }

    private async void OnSaveEmailClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveEmailAsync();
    }

    private async void OnSaveOutlookMessageClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.SaveOutlookMessageAsync();
    }

    private async void OnCopyAsEmailClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.CopyAsEmailAsync();
    }

    private void OnEmailDraftAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsNotBusy) _ = ViewModel.CreateEmailDraftAsync();
        args.Handled = true;
    }

    // Primary action of the export SplitButton: generate a Word document — ISS-019 made .docx
    // the default export format (was PDF). The remaining formats live in the flyout and reuse
    // the individual OnConvert*Click handlers above. SplitButton.Click raises
    // SplitButtonClickEventArgs, so it needs its own handler signature.
    private async void OnPrimaryExportClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (Models.ProGate.PrimaryExportIsWord(App.License.State)) await ViewModel.ConvertToDocxAsync();
        else await ViewModel.ConvertToPdfAsync();
    }

    private async void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ExportAllAsync();
    }

    // Copy the rendered HTML — the same pipeline as the live preview, minus the interactive-only
    // scripting — to the clipboard, both as plain text and as rich HTML (pastes formatted into Word).
    private async void OnCopyHtmlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var vm = ViewModel;
            string markdown;
            if (vm.UsePasteSource)
            {
                markdown = vm.PastedMarkdown;
            }
            else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            {
                markdown = await Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath);
            }
            else
            {
                markdown = vm.CurrentMarkdown ?? string.Empty;
            }

            var html = vm.BuildPreviewHtml(vm.PrepareMarkdown(markdown), interactive: false);

            var package = new DataPackage();
            package.SetText(html);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(html));
            Clipboard.SetContent(package);

            ViewModel.StatusText = "Rendered HTML copied to the clipboard.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Copy HTML failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Pin/unpin the selected file to the top of the Step-1 picker (persisted across sessions).
    private Views.History.HistoryWindow? _historyWindow;

    private void OnOpenHistoryClick(object sender, RoutedEventArgs e)
    {
        // The hub shows EVERY file ever touched, so it works even with nothing open — the current
        // file (if any) is pre-selected so the timeline lands where the user is working.
        if (_historyWindow == null)
        {
            _historyWindow = new Views.History.HistoryWindow(ViewModel, ViewModel.InputFilePath);
            _historyWindow.Closed += (s, args) => _historyWindow = null;
        }
        _historyWindow.Activate();
    }

    private void OnTogglePinFileClick(object sender, RoutedEventArgs e)
    {
        ViewModel.TogglePinCurrentFile();
    }

    private void OnCenterViewSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ViewPreviewTab == null) return;
        var mode = sender.SelectedItem == ViewPreviewTab ? ViewMode.Preview
                 : sender.SelectedItem == ViewSplitTab ? ViewMode.Split
                 : ViewMode.Code;
        if (_initializingCenterView) return;
        App.Settings.Current.EditorViewMode = mode.ToString();
        App.Settings.Save();
        ApplyViewMode(mode);
    }

    // Switch the centre "Looking Glass" between Code, Split (editor + preview side by side) and
    // Preview. In split mode both panes share the column and a splitter between them appears.
    private void ApplyViewMode(ViewMode mode)
    {
        _viewMode = mode;
        // The step caption says what this view is for, like "Pick a file or paste Markdown" and
        // "Finish the document" beside it (it used to read "Looking Glass Layer" in every view).
        if (EditorStepCaption is not null)
            EditorStepCaption.Text = mode switch
            {
                ViewMode.Split => "Edit with the result beside you",
                ViewMode.Preview => "Check the finished document",
                _ => "Write and edit the Markdown",
            };
        var showEditor = mode is ViewMode.Code or ViewMode.Split;
        var showPreview = mode is ViewMode.Preview or ViewMode.Split;

        // Sync-scroll bookkeeping only matters for the pure preview tab transitions.
        if (mode == ViewMode.Preview) _pendingPreviewScroll = new PreviewScrollState(0.0, GetEditorScrollFraction() ?? 0.0, 0.0, 0.0);
        if (mode == ViewMode.Code) _ = CapturePreviewScrollAndApplyToEditorAsync();

        if (PastePanel != null) PastePanel.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewCard != null) PreviewCard.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        // The width ruler and zoom act on the themed page; the email preview is a fixed 680 px
        // message, so they'd be dead controls there.
        if (PreviewWidthContainer != null) PreviewWidthContainer.Visibility = showPreview && !ViewModel.PreviewAsEmail ? Visibility.Visible : Visibility.Collapsed;
        if (SplitViewSplitter != null) SplitViewSplitter.Visibility = mode == ViewMode.Split ? Visibility.Visible : Visibility.Collapsed;

        // Column widths: give all the space to the visible pane(s); split shares it.
        if (SplitLeftCol != null) SplitLeftCol.Width = mode == ViewMode.Preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (SplitRightCol != null) SplitRightCol.Width = mode == ViewMode.Code ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // Focus mode toggle on Split view: only when AutoFocusOnSplit setting is enabled.
        if (App.Settings.Current.AutoFocusOnSplit && FocusModeToggle != null)
            FocusModeToggle.IsChecked = mode == ViewMode.Split;

        if (showPreview) _ = RefreshPreviewAsync(heavy: mode == ViewMode.Preview);

        // The bottom bar's editing clusters follow the view mode (portal mode is handled by the
        // toggle handler, which also lands here via ApplyViewMode when the view changes).
        UpdateCenterBottomBar();
    }

    // ---- Editor<->preview sync-scroll helpers ----

    // The editor's scroll fraction (0..1), or null when there is nothing to scroll. Reaches into the
    // TextBox's template for its internal ScrollViewer (named ContentElement in the default style).
    private double? GetEditorScrollFraction()
    {
        var sv = FindEditorScrollViewer();
        if (sv is null || sv.ScrollableHeight <= 0) return null;
        return Math.Clamp(sv.VerticalOffset / sv.ScrollableHeight, 0.0, 1.0);
    }

    private ScrollViewer? FindEditorScrollViewer() => FindDescendant<ScrollViewer>(PasteTextBox);

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    // Apply a stashed scroll fraction to the freshly-loaded preview page, then clear it. Skipped while
    // a mermaid harvest owns the WebView so we never scroll a snapshot render page mid-export.
    private void ApplyPendingPreviewScroll()
    {
        if (_pendingPreviewScroll is not { } sc) return;
        _pendingPreviewScroll = null;
        if (_mermaidHarvestActive) return;
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;

        var fxStr = sc.FractionX.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        var fyStr = sc.FractionY.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        var pxStr = sc.PixelX.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        var pyStr = sc.PixelY.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

        _ = core.ExecuteScriptAsync(
            "(function(){" +
            "var maxX=document.documentElement.scrollWidth-window.innerWidth;" +
            "var maxY=document.documentElement.scrollHeight-window.innerHeight;" +
            $"var tx=maxX>0?Math.round({fxStr}*maxX):{pxStr};" +
            $"var ty=maxY>0?Math.round({fyStr}*maxY):{pyStr};" +
            "if(tx>0||ty>0){window.scrollTo(tx,ty);}" +
            "})();");
    }

    // Read the preview's current scroll fraction and mirror it onto the editor's ScrollViewer.
    private async Task CapturePreviewScrollAndApplyToEditorAsync()
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;
        string result;
        try
        {
            result = await core.ExecuteScriptAsync(
                "(function(){var max=document.documentElement.scrollHeight-window.innerHeight;" +
                "return max>0?(window.scrollY/max):0;})();");
        }
        catch { return; }

        if (!double.TryParse(result, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frac)) return;
        var sv = FindEditorScrollViewer();
        if (sv is null || sv.ScrollableHeight <= 0) return;
        sv.ChangeView(null, Math.Clamp(frac, 0.0, 1.0) * sv.ScrollableHeight, null, disableAnimation: true);
    }

    // Editor cursor readout: "Ln 12, Col 8" plus "(N selected)" when there's a selection. Cheap to
    // compute on every SelectionChanged — the editor is a single TextBox, not a virtualized document.
    private void UpdateCursorPosition()
    {
        var tb = PasteTextBox;
        if (tb is null || CursorPosText is null) return;
        var text = tb.Text ?? string.Empty;
        var start = Math.Min(tb.SelectionStart, text.Length);
        var line = 1;
        var lastNewline = -1;
        for (var i = 0; i < start; i++)
        {
            if (IsLineBreak(text[i])) { line++; lastNewline = i; }
        }
        var col = start - lastNewline;
        line = _folds.DocumentLine(text, line); // past a fold, the document's line number
        var sel = tb.SelectionLength;
        CursorPosText.Text = sel > 0
            ? $"Ln {line}, Col {col}  ({sel} selected)"
            : $"Ln {line}, Col {col}";
    }

    // The preview column mirrors the editor column's FindBar row so the two boxes stay the same
    // height: whenever Ctrl+F opens/closes, shift the preview down/up by the find bar's height.
    private void SyncFindBarSpacer()
    {
        if (PreviewFindBarSpacer is null) return;
        PreviewFindBarSpacer.Height = FindBar.Visibility == Visibility.Visible ? FindBar.ActualHeight : 0;
    }

    // ---- Find bar (Ctrl+F): search the Markdown source and jump between matches ----

    // Ctrl+F / Ctrl+H, the palette, and the Find button in the editor bar all land here.
    // - From Preview view the editor column is hidden, so switch to Split first (the bar used to
    //   open invisibly in the collapsed column).
    // - A one-line selection prefills the query, like every other editor.
    // - Focus stays in the find box while matches are highlighted in the editor, so Enter keeps
    //   going to the next match instead of typing a line break over the selection.
    private void ShowFindBar(bool replace = false)
    {
        if (FindBar is null) return;
        EnsureEditorVisible();
        // Search and replace must reach every line, so folds open when the bar does.
        UnfoldAll();

        var selected = PasteTextBox.SelectedText ?? string.Empty;
        if (selected.Length is > 0 and <= 200 && selected.IndexOfAny(new[] { '\r', '\n' }) < 0)
        {
            _suppressFindJump = true;
            FindTextBox.Text = selected;
            _suppressFindJump = false;
        }

        FindBar.Visibility = Visibility.Visible;
        if (replace) ReplaceExpandToggle.IsChecked = true;
        SyncFindBarSpacer();

        RecomputeFindMatches();
        // Start from the caret: the first match at or after it is "current".
        _findMatchIndex = _findMatches.Count == 0 ? -1 : Math.Max(0, _findMatches.FindIndex(m => m >= PasteTextBox.SelectionStart));
        UpdateFindCount();

        if (replace && FindTextBox.Text.Length > 0)
        {
            ReplaceTextBox.Focus(FocusState.Programmatic);
            ReplaceTextBox.SelectAll();
        }
        else
        {
            FindTextBox.Focus(FocusState.Programmatic);
            FindTextBox.SelectAll();
        }
    }

    // Find, insert and format commands act on the editor; from Preview view bring it back (Split
    // keeps the preview the user was looking at).
    private void EnsureEditorVisible()
    {
        if (_viewMode == ViewMode.Preview && ViewSplitTab is not null) ViewSplitTab.IsSelected = true;
    }

    private void OnFindBarSizeChanged(object sender, SizeChangedEventArgs e) => SyncFindBarSpacer();

    private void OnReplaceExpandToggled(object sender, RoutedEventArgs e)
    {
        var open = ReplaceExpandToggle.IsChecked == true;
        ReplaceRow.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ReplaceExpandGlyph.Glyph = open ? "\uE70D" : "\uE76C"; // chevron down / right
        var name = open ? "Hide replace" : "Show replace";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ReplaceExpandToggle, name);
        ToolTipService.SetToolTip(ReplaceExpandToggle, open ? name : $"{name} ({Shortcuts.KeysFor("edit.replace")})");
        UpdateFindCount();
    }

    private void OnFindCloseClick(object sender, RoutedEventArgs e) => CloseFindBar();

    private void CloseFindBar()
    {
        if (FindBar is null) return;
        FindBar.Visibility = Visibility.Collapsed;
        SyncFindBarSpacer();
        // Hand focus back to the editor so typing resumes where the user left off.
        PasteTextBox.Focus(FocusState.Programmatic);
    }

    private void OnFindTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shiftDown) FindPrev(); else FindNext();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    private bool _suppressFindJump;

    // Typing a query jumps to the first match from the caret (search-as-you-type).
    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        RecomputeFindMatches();
        if (_suppressFindJump || _findMatches.Count == 0) return;
        var caret = PasteTextBox.SelectionStart;
        var i = _findMatches.FindIndex(m => m >= caret);
        _findMatchIndex = i < 0 ? 0 : i;
        SelectFindMatch(keepFindFocus: true);
    }

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext();

    private void OnFindPrevClick(object sender, RoutedEventArgs e) => FindPrev();

    // The comparison used by find/replace: the "Aa" toggle turns on case sensitivity.
    private StringComparison FindComparison => MatchCaseCheck?.IsChecked == true
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    // Rebuild the match list for the current query and refresh the "3 of 12" readout.
    // keepPosition (used while the user edits with the bar open) keeps the current match.
    private void RecomputeFindMatches(bool keepPosition = false)
    {
        var previous = keepPosition && _findMatchIndex >= 0 && _findMatchIndex < _findMatches.Count
            ? _findMatches[_findMatchIndex] : -1;
        _findMatches.Clear();
        _findMatchIndex = -1;
        var query = FindTextBox?.Text ?? string.Empty;
        var text = PasteTextBox?.Text ?? string.Empty;
        if (query.Length > 0 && text.Length > 0)
        {
            var cmp = FindComparison;
            var idx = text.IndexOf(query, cmp);
            while (idx >= 0)
            {
                _findMatches.Add(idx);
                idx = text.IndexOf(query, idx + query.Length, cmp);
            }
        }
        if (previous >= 0 && _findMatches.Count > 0)
        {
            var i = _findMatches.FindIndex(m => m >= previous);
            _findMatchIndex = i < 0 ? _findMatches.Count - 1 : i;
        }
        UpdateFindCount();
    }

    private void OnMatchCaseChanged(object sender, RoutedEventArgs e) => RecomputeFindMatches();

    private void UpdateFindCount()
    {
        if (FindCountText is null) return;
        var hasQuery = !string.IsNullOrEmpty(FindTextBox?.Text);
        var count = _findMatches.Count;
        FindCountText.Text = !hasQuery ? string.Empty
            : count == 0 ? "No results"
            : _findMatchIndex >= 0 ? $"{_findMatchIndex + 1} of {count}"
            : count == 1 ? "1 match" : $"{count} matches";
        FindCountText.Foreground = (Brush)Application.Current.Resources[
            hasQuery && count == 0 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(FindCountText, FindCountText.Text);

        // Dead buttons look dead: nothing to step through or replace without a match.
        FindPrevButton.IsEnabled = FindNextButton.IsEnabled = count > 0;
        ReplaceOneButton.IsEnabled = ReplaceAllButton.IsEnabled = count > 0;
    }

    private void FindNext()
    {
        if (_findMatches.Count == 0) { UpdateFindCount(); return; }
        _findMatchIndex = (_findMatchIndex + 1) % _findMatches.Count;
        SelectFindMatch(keepFindFocus: true);
    }

    private void FindPrev()
    {
        if (_findMatches.Count == 0) { UpdateFindCount(); return; }
        _findMatchIndex = _findMatchIndex < 0 ? _findMatches.Count - 1
            : (_findMatchIndex - 1 + _findMatches.Count) % _findMatches.Count;
        SelectFindMatch(keepFindFocus: true);
    }

    // Highlight the current match in the editor and scroll it into view. Selecting text on a
    // focused TextBox makes WinUI bring the caret into view, so the editor is focused for the
    // select and focus then goes back to whichever find box the user was typing in; the
    // editor's not-focused highlight (set in the constructor) keeps the match visible.
    private void SelectFindMatch(bool keepFindFocus = false)
    {
        if (_findMatchIndex < 0 || _findMatchIndex >= _findMatches.Count) return;
        var start = _findMatches[_findMatchIndex];
        var len = (FindTextBox?.Text ?? string.Empty).Length;
        var returnTo = keepFindFocus
            ? (ReplaceTextBox.FocusState != FocusState.Unfocused ? (Control)ReplaceTextBox : FindTextBox)
            : null;
        PasteTextBox.Focus(FocusState.Programmatic);
        PasteTextBox.Select(start, len);
        UpdateCursorPosition();
        returnTo?.Focus(FocusState.Programmatic);
        UpdateFindCount();
    }

    // ---- Replace (the second row of the find bar, or Ctrl+H) ----

    private void OnReplaceTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnReplaceClick(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            CloseFindBar();
            e.Handled = true;
        }
    }

    // Replace the current match (when the editor's selection is it), then go to the next one.
    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox?.Text ?? string.Empty;
        if (query.Length == 0) return;
        var replacement = ReplaceTextBox?.Text ?? string.Empty;
        var text = PasteTextBox.Text ?? string.Empty;
        var cmp = FindComparison;

        var selStart = PasteTextBox.SelectionStart;
        var isMatch = PasteTextBox.SelectionLength == query.Length
            && selStart + query.Length <= text.Length
            && string.Compare(text, selStart, query, 0, query.Length, cmp) == 0;
        if (isMatch)
        {
            ViewModel.BreakUndoBurst();
            PasteTextBox.SelectedText = replacement; // swaps the selected match in place
            selStart += replacement.Length;
            text = PasteTextBox.Text ?? string.Empty;
        }

        // Continue from just after the replacement, wrapping to the top if needed.
        var from = Math.Clamp(selStart, 0, text.Length);
        var next = text.IndexOf(query, from, cmp);
        if (next < 0) next = text.IndexOf(query, 0, cmp);

        RecomputeFindMatches();
        if (next >= 0)
        {
            _findMatchIndex = _findMatches.IndexOf(next);
            SelectFindMatch(keepFindFocus: true);
        }
        else
        {
            ViewModel.StatusText = "Replaced the last match.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
    }

    // Replace every match in the document in one pass (one undo step).
    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox?.Text ?? string.Empty;
        if (query.Length == 0) return;
        var replacement = ReplaceTextBox?.Text ?? string.Empty;
        var text = PasteTextBox.Text ?? string.Empty;
        var cmp = FindComparison;

        var sb = new System.Text.StringBuilder(text.Length);
        var idx = 0;
        var count = 0;
        while (true)
        {
            var found = text.IndexOf(query, idx, cmp);
            if (found < 0) { sb.Append(text, idx, text.Length - idx); break; }
            sb.Append(text, idx, found - idx);
            sb.Append(replacement);
            idx = found + query.Length;
            count++;
        }

        if (count > 0)
        {
            ViewModel.BreakUndoBurst(); // Replace All must undo as its own step
            PasteTextBox.Text = sb.ToString();
            ViewModel.BreakUndoBurst();
            ViewModel.StatusText = $"Replaced {count} occurrence{(count == 1 ? "" : "s")} of \u201C{query}\u201D. Ctrl+Z undoes it.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        else
        {
            ViewModel.StatusText = "No matches to replace.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        }
        RecomputeFindMatches();
    }

    private void OnReplaceAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ShowFindBar(replace: true);
        args.Handled = true;
    }

    // ---- Editor font-size zoom (A−/A+ buttons, Ctrl+wheel) — persisted across sessions ----

    private const double EditorFontBase = 13.0; // must match the PasteTextBox XAML default
    private const double EditorFontMin = 8.0;
    private const double EditorFontMax = 32.0;

    private void OnEditorZoomInClick(object sender, RoutedEventArgs e) => ApplyEditorFontSize(PasteTextBox.FontSize + 1, persist: true);

    private void OnEditorZoomOutClick(object sender, RoutedEventArgs e) => ApplyEditorFontSize(PasteTextBox.FontSize - 1, persist: true);

    // Ctrl+wheel over the editor zooms the source font; a bare wheel still scrolls the text.
    private void OnEditorPointerWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        var delta = e.GetCurrentPoint(PasteTextBox).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ApplyEditorFontSize(PasteTextBox.FontSize + (delta > 0 ? 1 : -1), persist: true);
        e.Handled = true;
    }

    private void ApplyEditorFontSize(double size, bool persist)
    {
        var clamped = Math.Clamp(size, EditorFontMin, EditorFontMax);
        PasteTextBox.FontSize = clamped;
        UpdateLineNumbers(); // the gutter's width and the numbers' size follow the font
        if (EditorZoomText is not null)
            EditorZoomText.Text = $"{(int)Math.Round(clamped / EditorFontBase * 100.0)}%";
        if (persist)
        {
            App.Settings.Current.EditorFontSize = clamped;
            App.Settings.Save();
        }
    }

    // ---- Word wrap + line numbers ----

    private bool _initializingLineNumbers;

    private void OnLineNumbersToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingLineNumbers) return;
        var show = LineNumbersToggle?.IsChecked == true;
        if (LineGutter != null)
            LineGutter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        App.Settings.Current.ShowLineNumbers = show;
        App.Settings.Save();
        ViewModel.ShowLineNumbers = show;

        if (show)
        {
            UpdateLineNumbers();
            SyncLineGutterScroll();
        }
    }

    // The outline flyout says what it's for when there's nothing in it yet.
    private void UpdateOutlineEmptyState()
    {
        if (TocEmptyText is null || TocList is null) return;
        var empty = ViewModel.TocEntries.Count == 0;
        TocEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TocList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Line-number gutter ----

    private int[] _lineStarts = { 0 };
    private int[] _gutterLabels = { 1 };          // document line number per visible line
    private bool[] _gutterFolded = { false };      // visible line is a fold's first line
    private readonly List<TextBlock> _gutterNumbers = new();
    private bool _gutterLayoutQueued;
    private ScrollViewer? _editorScrollViewer;

    // Text changed: re-index the line starts, size the gutter for the widest number, redraw.
    private void UpdateLineNumbers()
    {
        if (LineNumberCanvas is null || LineGutter?.Visibility != Visibility.Visible) return;
        var text = PasteTextBox?.Text ?? "";
        var starts = new List<int>(Math.Max(16, _lineStarts.Length)) { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (!IsLineBreak(text[i])) continue;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
        // Past a fold the numbers jump by the lines it hides, so they stay the document's numbers.
        _gutterLabels = _folds.DocumentLineNumbers(text, _lineStarts.Length);
        _gutterFolded = new bool[_lineStarts.Length];
        if (text.IndexOf('\u00BB') >= 0)
            for (var l = 0; l < _lineStarts.Length; l++)
            {
                var end = l + 1 < _lineStarts.Length ? _lineStarts[l + 1] : text.Length;
                var line = text.Substring(_lineStarts[l], end - _lineStarts[l]).TrimEnd('\r', '\n');
                _gutterFolded[l] = _folds.IsFoldedLine(line);
            }
        var widest = _gutterLabels.Length == 0 ? 1 : _gutterLabels[^1];
        var digits = Math.Max(2, widest.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        LineNumberCanvas.Width = Math.Ceiling(digits * PasteTextBox!.FontSize * 0.62) + 16;
        QueueGutterLayout();
    }

    // The TextBox lays its text out after TextChanged, so positions are read on the next tick.
    private void QueueGutterLayout()
    {
        if (_gutterLayoutQueued || LineGutter?.Visibility != Visibility.Visible) return;
        _gutterLayoutQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _gutterLayoutQueued = false;
            LayoutGutter();
        });
    }

    private void OnLineGutterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LineNumberCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        QueueGutterLayout();
    }

    // Draws the numbers of the lines currently in view, each level with its line.
    private void LayoutGutter()
    {
        if (LineNumberCanvas is null || PasteTextBox is null || LineGutter?.Visibility != Visibility.Visible) return;
        var text = PasteTextBox.Text ?? "";
        var height = LineNumberCanvas.ActualHeight;
        var width = LineNumberCanvas.Width;
        var used = 0;
        if (height > 0)
        {
            // Character rects are measured from the top of the text inside the TextBox's own
            // scroll viewer (inside its border and padding), not from the visible part of it.
            _editorScrollViewer ??= FindEditorScrollViewer();
            if (_editorScrollViewer is null) return;
            double originY;
            try { originY = LineNumberCanvas.TransformToVisual(_editorScrollViewer).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; }
            catch (ArgumentException) { return; } // not in the tree yet
            originY += _editorScrollViewer.VerticalOffset - _editorScrollViewer.Padding.Top;
            // The line box's height, from the first character (a 0-width rect a line tall).
            var lineHeight = text.Length > 0 ? SafeRect(0).Height : 0;
            if (lineHeight <= 0) lineHeight = Math.Max(1, PasteTextBox.FontSize * 1.33);
            var caretLine = LineIndexAt(PasteTextBox.SelectionStart);

            double TopOf(int line)
            {
                var start = _lineStarts[line];
                if (text.Length == 0) return LineTop(0, false) - originY;
                // An empty last line has no character: it sits one line below the final break.
                if (start >= text.Length) return LineTop(text.Length - 1, false) + lineHeight - originY;
                return LineTop(start, false) - originY;
            }

            // First line whose bottom is in view (binary search: tops grow with the line index).
            int lo = 0, hi = _lineStarts.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (TopOf(mid) <= -lineHeight) lo = mid; else hi = mid - 1;
            }
            var secondary = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            var primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
            var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            for (var line = lo; line < _lineStarts.Length; line++)
            {
                var top = TopOf(line);
                if (top > height) break;
                if (top < -lineHeight) continue;
                if (used == _gutterNumbers.Count)
                {
                    var made = new TextBlock
                    {
                        FontFamily = PasteTextBox.FontFamily,
                        TextAlignment = TextAlignment.Right,
                        IsTextSelectionEnabled = false,
                    };
                    _gutterNumbers.Add(made);
                    LineNumberCanvas.Children.Add(made);
                }
                var tb = _gutterNumbers[used++];
                tb.Visibility = Visibility.Visible;
                tb.FontSize = PasteTextBox.FontSize;
                tb.Width = width - 10;
                var label = line < _gutterLabels.Length ? _gutterLabels[line] : line + 1;
                tb.Text = label.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var folded = line < _gutterFolded.Length && _gutterFolded[line];
                tb.Foreground = folded ? accent : line == caretLine ? primary : secondary;
                // Centred on the line's box: the TextBox's line box is taller than a TextBlock's.
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(tb, 0);
                Canvas.SetTop(tb, top + Math.Max(0, (lineHeight - tb.DesiredSize.Height) / 2));
            }
        }
        for (var i = used; i < _gutterNumbers.Count; i++) _gutterNumbers[i].Visibility = Visibility.Collapsed;
    }

    private double LineTop(int index, bool trailing) => SafeRect(index, trailing).Top;

    private Windows.Foundation.Rect SafeRect(int index, bool trailing = false)
    {
        try { return PasteTextBox.GetRectFromCharacterIndex(Math.Clamp(index, 0, Math.Max(0, (PasteTextBox.Text ?? "").Length - 1)), trailing); }
        catch (ArgumentException) { return default; }
    }

    // The 0-based line holding a character offset.
    private int LineIndexAt(int offset)
    {
        var i = Array.BinarySearch(_lineStarts, offset);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    private void SyncLineGutterScroll() => LayoutGutter();

    // Word wrap is a persisted editor display setting.
    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingWordWrap) return;
        ApplyWordWrap(WordWrapToggle?.IsChecked == true, persist: true);
    }

    // ---- Folding: a view of the document, never an edit to it ----
    //
    // The editor shows a folded region as its first line plus a "«+5 lines folded #2»" marker and
    // _folds keeps the hidden lines; the view model always holds the whole document. (The old
    // folding wrote the hidden lines into the document as a base64 comment, so folded sections
    // disappeared from the preview and every export, and Ctrl+S saved the comment to disk.)

    private readonly Services.Editor.EditorFolds _folds = new();
    private bool _pushingEditorToDocument;

    // The whole document behind the editor.
    private string EditorDocument() => _folds.Document(PasteTextBox?.Text ?? string.Empty);

    // Editor -> view model, on every edit (the job the TwoWay binding used to do).
    private void SyncDocumentFromEditor()
    {
        var doc = EditorDocument();
        if (doc == (ViewModel.CurrentMarkdown ?? string.Empty)) return;
        // A programmatic Text set (file load, undo) only differs in its line breaks: the TextBox
        // stores '\r'. Don't let that switch a file-backed document to the paste source.
        if (SameIgnoringLineBreaks(doc, ViewModel.CurrentMarkdown)) return;
        _pushingEditorToDocument = true;
        try { ViewModel.CurrentMarkdown = doc; }
        finally { _pushingEditorToDocument = false; }
    }

    // View model -> editor, when the document changes from anywhere else (file open, Diagram
    // Studio, a cleanup). Folds are dropped: the text they hid may have changed.
    private void SyncEditorFromDocument()
    {
        if (_pushingEditorToDocument || PasteTextBox is null) return;
        var doc = ViewModel.CurrentMarkdown ?? string.Empty;
        if (SameIgnoringLineBreaks(EditorDocument(), doc)) return;

        var repaired = Services.Editor.EditorFolds.RepairLegacy(doc, out var legacyFolds);
        PasteTextBox.Text = repaired;
        UpdateFoldStatus();
        if (legacyFolds > 0)
        {
            // An older version saved folds inside the file; put the hidden lines back where they were.
            ViewModel.CurrentMarkdown = repaired;
            ViewModel.StatusText = legacyFolds == 1
                ? "Restored a folded section that an earlier version saved inside this document."
                : $"Restored {legacyFolds} folded sections that an earlier version saved inside this document.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        }
    }

    private static bool SameIgnoringLineBreaks(string? a, string? b)
    {
        a ??= string.Empty;
        b ??= string.Empty;
        if (a.Length == b.Length && string.Equals(a, b, StringComparison.Ordinal)) return true;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var ca = a[i]; var cb = b[j];
            var la = ca is '\r' or '\n'; var lb = cb is '\r' or '\n';
            if (la && lb)
            {
                i += ca == '\r' && i + 1 < a.Length && a[i + 1] == '\n' ? 2 : 1;
                j += cb == '\r' && j + 1 < b.Length && b[j + 1] == '\n' ? 2 : 1;
                continue;
            }
            if (ca != cb) return false;
            i++; j++;
        }
        return i == a.Length && j == b.Length;
    }

    // Replaces the editor's visible text with a new view of the same document and puts the caret
    // on a line (folding never changes the document, so this adds no undo step).
    private void ShowFoldedView(string visible, int caretLine)
    {
        PasteTextBox.Text = visible;
        var offset = LineStartOffset(visible, caretLine);
        PasteTextBox.Select(offset, 0);
        PasteTextBox.Focus(FocusState.Programmatic);
        UpdateFoldStatus();
        UpdateLineNumbers();
    }

    private static int LineStartOffset(string text, int line)
    {
        var offset = 0;
        for (var l = 1; l < line && offset < text.Length; offset++)
            if (IsLineBreak(text[offset])) l++;
        return Math.Min(offset, text.Length);
    }

    private void OnToggleFoldAtCursorClick(object sender, RoutedEventArgs e) => ToggleFoldAtCursor();

    private void ToggleFoldAtCursor()
    {
        if (PasteTextBox is null) return;
        var text = PasteTextBox.Text ?? string.Empty;
        var cursorLine = GetCursorLine(text, PasteTextBox.SelectionStart) + 1; // GetCursorLine is 0-based
        var view = _folds.Toggle(text, cursorLine, out var at);
        if (view == text)
        {
            ViewModel.StatusText = "Nothing to fold here. Put the cursor on a heading, in a code block or in a ::: block.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
            return;
        }
        ShowFoldedView(view, at);
    }

    private void OnFoldAllCodeClick(object sender, RoutedEventArgs e)
    {
        if (PasteTextBox is null) return;
        var text = PasteTextBox.Text ?? string.Empty;
        var view = _folds.FoldAllCode(text);
        if (view == text)
        {
            ViewModel.StatusText = "There are no code blocks to fold.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
            return;
        }
        ShowFoldedView(view, GetCursorLine(view, Math.Min(PasteTextBox.SelectionStart, view.Length)) + 1);
    }

    private void OnUnfoldAllClick(object sender, RoutedEventArgs e) => UnfoldAll();

    // Opens every fold. Returns false when nothing was folded.
    private bool UnfoldAll()
    {
        if (PasteTextBox is null) return false;
        var text = PasteTextBox.Text ?? string.Empty;
        if (!_folds.HasFolds(text)) return false;
        var line = _folds.DocumentLine(text, GetCursorLine(text, PasteTextBox.SelectionStart) + 1);
        ShowFoldedView(_folds.UnfoldAll(text), line);
        return true;
    }

    private void UpdateFoldStatus()
    {
        if (FoldStatusText is null || PasteTextBox is null) return;
        var count = _folds.Count(PasteTextBox.Text ?? string.Empty);
        FoldStatusText.Text = count > 0 ? $"{count} folded" : "Fold";
        if (FoldMenuUnfoldAll is not null) FoldMenuUnfoldAll.IsEnabled = count > 0;
    }

    // ISS-004: toggle the Looking Glass portal overlay (fog-of-war lens + glowing cursor ring,
    // rendered inside the preview page). The flag lives in AppSettings so MarkdownHtmlService
    // picks it up on the next render — which we trigger right away.
    private void OnLookingGlassToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingLookingGlass) return;
        App.Settings.Current.LookingGlassMode = LookingGlassToggle?.IsChecked == true;
        App.Settings.Save();
        UpdateCenterBottomBar();
        _ = RefreshPreviewAsync();
    }

    // Centre pane bottom bar: the editing clusters show whenever the editor is visible or portal
    // mode turns the preview into an editor; the portal shape + size row only shows while portal
    // mode is on. Copy HTML / Print are always available, so the bar itself never hides.
    //
    // This is the ONE place that decides the editing bar's shape. It used to be split between this
    // method (view mode) and a resize handler (width) that each set the panels' visibility, so a
    // resize in Preview mode brought the editing buttons back, and at the default 1220px window the
    // labelled clusters were clipped at both ends (Copy HTML and "Tools"). Three tiers, widest first:
    //   1. expanded  — one direct button per common action
    //   2. clusters  — the four labelled dropdowns
    //   3. compact   — the same dropdowns, icon only (tooltips + accessible names keep the words)
    private void UpdateCenterBottomBar()
    {
        if (CenterBottomBar is null || EditingExpandedPanel is null || EditingClustersPanel is null) return;
        var portalOn = LookingGlassToggle?.IsChecked == true;
        if (PortalControlsRow is not null)
            PortalControlsRow.Visibility = portalOn ? Visibility.Visible : Visibility.Collapsed;

        var editing = portalOn || _viewMode is ViewMode.Code or ViewMode.Split;
        if (!editing)
        {
            EditingExpandedPanel.Visibility = Visibility.Collapsed;
            EditingClustersPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var available = CenterBottomBar.ActualWidth;
        if (available <= 0) available = double.PositiveInfinity; // before first layout: let it settle
        const double copyPrint = 32 + 4 + 32 + 4; // two 32px buttons + the row spacing
        var expanded = copyPrint + MeasureRow(EditingExpandedPanel) <= available;
        EditingExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        EditingClustersPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        if (expanded) return;

        SetClusterLabelsVisible(true);
        if (copyPrint + MeasureRow(EditingClustersPanel) > available)
            SetClusterLabelsVisible(false);
    }

    // Width a horizontal StackPanel would need, computed from its children so it works while the
    // panel itself is collapsed (a collapsed element measures to zero).
    private static double MeasureRow(StackPanel panel)
    {
        double width = 0;
        int visible = 0;
        foreach (var child in panel.Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            width += child.DesiredSize.Width;
            visible++;
        }
        return width + System.Math.Max(0, visible - 1) * panel.Spacing;
    }

    // The cluster dropdowns' content is [icon, label]; compact mode hides the label.
    private void SetClusterLabelsVisible(bool visible)
    {
        foreach (var child in EditingClustersPanel.Children)
        {
            if (child is DropDownButton { Content: StackPanel content })
            {
                foreach (var part in content.Children)
                    if (part is TextBlock label) label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ISS-004: persist the portal reveal scope and push it straight into the page so an open
    // aperture grows/shrinks in real time as the slider drags. __portalSetReveal is a cheap
    // in-place DOM resize (no re-navigation), so the portal and its caret survive the drag —
    // higher number = bigger circle/band, lower = smaller, live on every tick. The value is also
    // persisted so the next full render bakes the same size in and nothing drifts.
    private void OnPortalRevealChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializingPortalReveal) return;
        var scope = (int)System.Math.Round(PortalRevealSlider?.Value ?? 45);
        App.Settings.Current.PortalRevealScope = scope;
        App.Settings.Save();
        var js = "if (window.__portalSetReveal) { window.__portalSetReveal(" + scope + "); }";
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
    }

    // ISS-004: persist the portal shape (circle spotlight vs full-width focus bands vs square vs
    // logo cutout) and push it straight into the page so an open aperture morphs in real time —
    // same in-place path as the size slider (__portalSetShape re-classes + resizes the aperture
    // and rebuilds its fog mask, no re-navigation, so the caret survives). The value is also
    // persisted so the next full render bakes the same shape in and nothing drifts.
    private void OnPortalShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingPortalReveal) return;
        var shape = (PortalShapeCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "circle";
        App.Settings.Current.PortalShape = shape;
        App.Settings.Save();
        var js = "if (window.__portalSetShape) { window.__portalSetShape('" + shape + "'); }";
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
    }

    private void ApplyWordWrap(bool wrap, bool persist)
    {
        PasteTextBox.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(PasteTextBox, wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(PasteTextBox, wrap ? ScrollMode.Disabled : ScrollMode.Enabled);
        QueueGutterLayout(); // wrapping changes every line's height
        // The line-number gutter is ALWAYS visible and counts logical lines, so word wrap no
        // longer hides it (the old wrap-dependent visibility made a mystery "1" panel pop in and
        // out and read as broken when the text was a single logical line).
        if (persist)
        {
            App.Settings.Current.EditorWordWrap = wrap;
            App.Settings.Save();
        }
    }

    // ---- Style & Export pane fit ----

    private double _rightPanePreferredWidth = 380;
    private double _rightPaneFittedWidth = -1;

    // The Style & Export column is a fixed width (380, or whatever the splitter last set) beside a
    // star editor column with a 400px floor, so once the window got narrower than ~1200px the right
    // pane simply ran off the window edge. It now yields width down to its MinWidth (290) and
    // grows back to the user's preferred width when room returns. A width this method didn't set
    // (splitter drag, focus-mode restore) becomes the new preference.
    private void FitRightPane()
    {
        if (MainLayoutGrid is null || RightPaneCol is null || !RightPaneCol.Width.IsAbsolute) return;
        var current = RightPaneCol.Width.Value;
        if (current <= 0) return; // focus mode hides the pane — leave it alone
        if (System.Math.Abs(current - _rightPaneFittedWidth) > 0.5) _rightPanePreferredWidth = current;

        var cols = MainLayoutGrid.ColumnDefinitions;
        var others = MainLayoutGrid.Padding.Left + MainLayoutGrid.Padding.Right
                     // the left column's Width, not ActualWidth: the drawer just changed it and
                     // layout hasn't run yet
                     + (cols[0].Width.IsAbsolute ? cols[0].Width.Value : cols[0].ActualWidth)
                     + cols[1].ActualWidth + cols[3].ActualWidth
                     + cols[2].MinWidth + MainLayoutGrid.ColumnSpacing * (cols.Count - 1);
        // RootGrid, not MainLayoutGrid: once the columns' minimums exceed the window, the layout
        // grid is arranged wider than the window (and clipped), so its own ActualWidth overstates
        // the room. The root grid is always exactly the window's client width.
        var available = RootGrid.ActualWidth - others;
        var target = System.Math.Max(RightPaneCol.MinWidth, System.Math.Min(_rightPanePreferredWidth, available));
        _rightPaneFittedWidth = target;
        if (System.Math.Abs(target - current) > 0.5) RightPaneCol.Width = new GridLength(target);
    }

    private void OnRightPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (OpenOutputLabel is not null)
            OpenOutputLabel.Visibility = e.NewSize.Width >= 340 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Left-pane hover-drawer ----

    // The drawer slides rather than snapping: the column width is tweened while the pane keeps
    // its full width (so it is clipped, not re-wrapped, mid-slide). Off when Windows animations are.
    private const double LeftDrawerTabWidth = 28;
    private static readonly TimeSpan LeftDrawerSlide = TimeSpan.FromMilliseconds(170);
    private EventHandler<object>? _leftDrawerTween;
    private DispatcherTimer? _leftDrawerDwell;
    private bool _leftDrawerOpenedByKeyboard;

    // Expand the Source/Files pane back to its pre-collapse width.
    private void ExpandLeftPane()
    {
        _leftDrawerDwell?.Stop();
        if (!_leftPaneCollapsed) return;
        _leftPaneCollapsed = false;
        LeftPane.Visibility = Visibility.Visible;
        if (LeftDrawerTab is not null) LeftDrawerTab.Visibility = Visibility.Collapsed;
        AnimateLeftColumn(_leftPaneExpandedWidth, () => FitRightPane());
    }

    // Tuck the pane away to a slim tab (leaving the pane with the mouse, or a short beat after a
    // document is selected). The custom splitter width is preserved for the next expand.
    private void CollapseLeftPane()
    {
        if (_leftPaneCollapsed) return;
        // RULE: when the editor is blank the left pane is FORCIBLY expanded (the user needs the
        // file picker because there is nothing to work on yet) — never tuck it away.
        if (string.IsNullOrWhiteSpace(PasteTextBox?.Text)) return;
        // Focus mode owns the columns while it is on.
        if (FocusModeToggle?.IsChecked == true) return;
        _leftPaneCollapsed = true;
        _leftDrawerOpenedByKeyboard = false;
        if (_leftDrawerTween is null && LeftPaneCol.ActualWidth > LeftDrawerTabWidth) _leftPaneExpandedWidth = LeftPaneCol.ActualWidth;
        AnimateLeftColumn(LeftDrawerTabWidth, () =>
        {
            LeftPane.Visibility = Visibility.Collapsed;
            if (LeftDrawerTab is not null) LeftDrawerTab.Visibility = Visibility.Visible;
            FitRightPane();
        });
    }

    // Tween LeftPaneCol to `target` px (ease-out cubic). A new call takes over from wherever an
    // unfinished slide had got to, so a quick in-out never jumps.
    private void AnimateLeftColumn(double target, Action onDone)
    {
        StopLeftDrawerTween();
        double from = LeftPaneCol.ActualWidth > 0 ? LeftPaneCol.ActualWidth : LeftPaneCol.Width.Value;
        if (!Services.HoverPolish.AnimationsEnabled || Math.Abs(from - target) < 1)
        {
            LeftPaneCol.Width = new GridLength(target);
            onDone();
            return;
        }

        // Hold the pane at its open width so the slide clips it instead of re-flowing every line.
        LeftPane.Width = _leftPaneExpandedWidth;
        LeftPane.HorizontalAlignment = HorizontalAlignment.Left;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _leftDrawerTween = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / LeftDrawerSlide.TotalMilliseconds);
            double eased = 1 - Math.Pow(1 - t, 3);
            LeftPaneCol.Width = new GridLength(from + (target - from) * eased);
            if (t >= 1)
            {
                StopLeftDrawerTween();
                onDone();
            }
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _leftDrawerTween;
    }

    private void StopLeftDrawerTween()
    {
        if (_leftDrawerTween is null) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _leftDrawerTween;
        _leftDrawerTween = null;
        LeftPane.Width = double.NaN;
        LeftPane.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    // Collapse on a short delay so the selection click finishes before the pane slides away.
    private void AutoCollapseLeftPane()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(350);
            if (!_pointerOverLeftPane && !_leftDrawerOpenedByKeyboard) CollapseLeftPane();
        });
    }

    // Hover opens the drawer after a short dwell, so sweeping the pointer past the window's left
    // edge on the way somewhere else doesn't fling the pane open.
    private void OnLeftDrawerTabPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOverLeftPane = true;
        if (_leftDrawerDwell is null)
        {
            _leftDrawerDwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _leftDrawerDwell.Tick += (_, _) => { _leftDrawerDwell.Stop(); ExpandLeftPane(); };
        }
        _leftDrawerDwell.Start();
    }

    private void OnLeftDrawerTabPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _leftDrawerDwell?.Stop();
        _pointerOverLeftPane = false;
    }

    // Click / Enter / Space (and screen readers' Invoke) open it at once. From the keyboard, focus
    // moves into the pane and the pane stays open until focus leaves it.
    private void OnLeftDrawerTabClick(object sender, RoutedEventArgs e)
    {
        // Anything but a pointer click (keyboard, Narrator's Invoke) gets the keyboard behaviour.
        bool keyboard = LeftDrawerTab.FocusState != FocusState.Pointer;
        ExpandLeftPane();
        if (!keyboard) return;
        _leftDrawerOpenedByKeyboard = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            var first = Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(LeftPane);
            if (first is Control c) c.Focus(FocusState.Keyboard);
        });
    }

    // A keyboard-opened drawer tucks away again once focus moves to the rest of the window
    // (but not into a flyout, picker or dialog the pane itself opened).
    private void OnLeftPaneLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_leftDrawerOpenedByKeyboard) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Content?.XamlRoot is not { } root) return;
            if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not DependencyObject focused) return;
            bool insidePane = false, insideWindow = false;
            for (var d = focused; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, LeftPane)) { insidePane = true; break; }
                if (ReferenceEquals(d, RootGrid)) { insideWindow = true; break; }
            }
            if (!insidePane && insideWindow && !_pointerOverLeftPane) CollapseLeftPane();
        });
    }

    private void OnLeftPanePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // PointerExited also fires when the pointer crosses between child elements. Only collapse
        // when the pointer has genuinely left the pane's bounds.
        if (sender is FrameworkElement fe)
        {
            var pt = e.GetCurrentPoint(fe);
            if (pt.Position.X >= 0 && pt.Position.Y >= 0 &&
                pt.Position.X <= fe.ActualWidth && pt.Position.Y <= fe.ActualHeight)
                return;
        }
        _pointerOverLeftPane = false;
        // A keyboard user who opened the drawer keeps it until their focus leaves it.
        if (!_leftDrawerOpenedByKeyboard) CollapseLeftPane();
    }

    private void OnLeftPanePointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        _pointerOverLeftPane = true;


    // ---- Markdown lint ----

    // Re-run the linter and refresh the issue-count chip and the flyout's issue list.
    private void UpdateLintIndicator()
    {
        // Lint the document, not the editor's view of it: a fold's marker isn't Markdown, and the
        // line numbers must be the document's.
        _lintIssues = Services.MarkdownLintService.Analyze(EditorDocument());
        // A clean document shows a check, not the warning triangle it used to show even at
        // "No issues"; the warning glyph is reserved for when there is something to review.
        if (LintIcon is not null) LintIcon.Glyph = _lintIssues.Count == 0 ? "\uE73E" : "\uE7BA";
        UpdateLintLabel();
        if (LintList is not null) LintList.ItemsSource = _lintIssues.Select(i => new LintRow(i.Line, i.Message)).ToList();
        if (LintEmptyText is not null) LintEmptyText.Visibility = _lintIssues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (LintList is not null) LintList.Visibility = _lintIssues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool _editorStripCompact;

    // The lint chip spells out "No issues" / "3 issues" when the strip has room and drops to the
    // bare count (the icon carries the meaning; the accessible name and tooltip keep the words)
    // when the editor column is narrow, so it never collides with the tools on the left.
    private void UpdateLintLabel()
    {
        if (LintCountText is null) return;
        var n = _lintIssues.Count;
        var words = n == 0 ? "No issues" : $"{n} issue{(n == 1 ? "" : "s")}";
        LintCountText.Text = _editorStripCompact ? (n == 0 ? "" : n.ToString()) : words;
        LintCountText.Visibility = LintCountText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LintButton, $"Markdown issues: {words.ToLowerInvariant()}");
    }

    private void OnEditorStripSizeChanged(object sender, SizeChangedEventArgs e) => LayoutEditorStrip(e.NewSize.Width);

    // Fits the strip under the editor into whatever width the column has (Split view at the
    // default window size leaves ~190px). Shed in priority order until it fits: the lint chip's
    // words, then the separators and the zoom % readout, then A−/A+ (Ctrl+wheel still zooms).
    // Lines / Wrap / Fold — the toggles people actually reach for — are the last to go.
    private void LayoutEditorStrip(double width)
    {
        if (EditorStripTools is null || LintButton is null || width <= 0) return;
        var tools = EditorStripTools.Children;
        // A−, the % readout, A+ (by name: Take(3) used to hide the Find button and A−, and keep A+).
        var zoomBits = new UIElement[] { EditorZoomOutButton, EditorZoomText, EditorZoomInButton };
        var readoutAndSeparators = tools.Where(c => c is Border || c == EditorZoomText).ToList();

        foreach (var c in tools) c.Visibility = Visibility.Visible;
        bool Fits()
        {
            LintButton.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            return MeasureRow(EditorStripTools) + EditorStrip.ColumnSpacing + LintButton.DesiredSize.Width <= width;
        }

        _editorStripCompact = false;
        UpdateLintLabel();
        if (Fits()) return;
        _editorStripCompact = true;
        UpdateLintLabel();
        if (Fits()) return;
        foreach (var c in readoutAndSeparators) c.Visibility = Visibility.Collapsed;
        if (Fits()) return;
        foreach (var c in zoomBits) c.Visibility = Visibility.Collapsed;
    }

    // Clicking an issue jumps the editor caret to that line AND drops a red homing radar beacon on
    // the corresponding element in the live preview (ISS-012), so the user's eye lands on both.
    private void OnLintItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LintRow issue)
        {
            GoToDocumentLine(issue.Line);
            TriggerIssueRadarBeacon(issue.Line);
            LintFlyout?.Hide();
        }
    }

    // One row of the Markdown issues flyout. ToString is what a screen reader announces for the
    // list item (it used to read the record's debug text, "LintIssue { Line = 7, ... }").
    [Microsoft.UI.Xaml.Data.Bindable]
    public sealed record LintRow(int Line, string Message)
    {
        public string LineLabel => $"Line {Line}";
        public override string ToString() => $"Line {Line}: {Message}";
    }

    // Fire the preview's triggerRedRadarBeacon(line) (injected by MarkdownHtmlService in interactive
    // mode). Best-effort: if the preview isn't ready or the script is absent this is a silent no-op.
    private void TriggerIssueRadarBeacon(int line)
    {
        var core = PreviewWebView?.CoreWebView2;
        if (core is null) return;
        _ = core.ExecuteScriptAsync(
            $"if (typeof triggerRedRadarBeacon === 'function') {{ triggerRedRadarBeacon({line}); }}");
    }

    // Move the caret to the start of the given 1-based line, select the line, and bring it into view.
    // Moves the caret to a line of the DOCUMENT (lint issues, the outline): a folded line opens
    // first, and folds above it are counted.
    private void GoToDocumentLine(int documentLine)
    {
        var visible = _folds.VisibleLine(PasteTextBox.Text ?? string.Empty, documentLine);
        if (visible == 0)
        {
            UnfoldAll();
            visible = documentLine;
        }
        GoToLine(visible);
    }

    private void GoToLine(int lineNo)
    {
        var text = PasteTextBox.Text ?? string.Empty;
        var offset = 0;
        var line = 1;
        while (line < lineNo && offset < text.Length)
        {
            if (IsLineBreak(text[offset])) line++;
            offset++;
        }

        int lineStart = Math.Clamp(offset, 0, text.Length);
        int lineEnd = lineStart;
        while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
        {
            lineEnd++;
        }

        PasteTextBox.Focus(FocusState.Programmatic);
        PasteTextBox.Select(lineStart, lineEnd - lineStart);
        UpdateCursorPosition();

        try
        {
            var sv = FindEditorScrollViewer();
            if (sv != null)
            {
                double targetY = Math.Max(0, (lineNo - 4) * 20.0);
                sv.ChangeView(null, targetY, null, disableAnimation: false);
            }
        }
        catch { }
    }

    // ---- Text transforms (the Transform dropdown) ----

    // Apply a transform to the current selection, or to the current line when nothing is selected.
    private void TransformSelection(Func<string, string> transform)
    {
        var tb = PasteTextBox;
        var text = EditorText();
        if (text.Length == 0) return;
        var selStart = Math.Clamp(tb.SelectionStart, 0, text.Length);
        var selLen = Math.Clamp(tb.SelectionLength, 0, text.Length - selStart);
        if (selLen == 0)
        {
            var (ls, le) = CurrentLineRange(text, selStart);
            selStart = ls;
            selLen = le - ls;
        }
        if (selLen <= 0) return;
        var segment = text.Substring(selStart, selLen);
        var transformed = transform(segment);
        tb.Text = text.Remove(selStart, selLen).Insert(selStart, transformed);
        tb.SelectionStart = selStart;
        tb.SelectionLength = Math.Clamp(transformed.Length, 0, tb.Text.Length - selStart);
        tb.Focus(FocusState.Programmatic);
    }

    // The [start, end) range of the single line surrounding pos.
    private static (int Start, int End) CurrentLineRange(string text, int pos)
    {
        pos = Math.Clamp(pos, 0, text.Length);
        var start = pos;
        while (start > 0 && !IsLineBreak(text[start - 1])) start--;
        var end = pos;
        while (end < text.Length && !IsLineBreak(text[end])) end++;
        return (start, end);
    }

    // A WinUI TextBox stores every line break as a bare '\r' (it converts "\n" and "\r\n" on the
    // way in), so editor line logic that only looked for '\n' saw the whole document as one line:
    // "current line" transforms hit everything, Alt+Up/Down and Ctrl+D did nothing, lint jumps
    // landed at the end, and Ln always read 1. Count either character as a break.
    private static bool IsLineBreak(char c) => c is '\n' or '\r';

    // The editor text with '\n' line breaks. Same length as PasteTextBox.Text (a char-for-char
    // swap), so selection offsets carry over unchanged; '\n' written back becomes '\r' again.
    private string EditorText() => (PasteTextBox.Text ?? string.Empty).Replace('\r', '\n');

    private void OnTransformUpperClick(object sender, RoutedEventArgs e) => TransformSelection(s => s.ToUpperInvariant());
    private void OnTransformLowerClick(object sender, RoutedEventArgs e) => TransformSelection(s => s.ToLowerInvariant());
    private void OnTransformTitleClick(object sender, RoutedEventArgs e) =>
        TransformSelection(s => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant()));
    private void OnSortLinesAscClick(object sender, RoutedEventArgs e) =>
        TransformSelection(s => string.Join("\n", s.Split('\n').OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
    private void OnSortLinesDescClick(object sender, RoutedEventArgs e) =>
        TransformSelection(s => string.Join("\n", s.Split('\n').OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)));
    private void OnDedupeLinesClick(object sender, RoutedEventArgs e) => TransformSelection(DedupeLines);

    private static string DedupeLines(string s)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var line in s.Split('\n'))
            if (seen.Add(line.TrimEnd('\r'))) kept.Add(line);
        return string.Join("\n", kept);
    }

    // ---- Line operations (Alt+Up/Down to move, Ctrl+D to duplicate) ----

    // These accelerators sit on the window's root, so they fire wherever focus is. They edit the
    // document's lines, so they act only while the editor itself has focus: Ctrl+D in the find
    // box or a Settings field used to duplicate an editor line unseen, and Alt+Down in a combo
    // box is how it opens. Anywhere else the key is left to the focused control.
    private bool EditorHasFocus() =>
        Content?.XamlRoot is { } root
        && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root), PasteTextBox);

    private void OnMoveLineUpAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorHasFocus()) return;
        MoveSelectedLines(-1);
        args.Handled = true;
    }

    private void OnMoveLineDownAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorHasFocus()) return;
        MoveSelectedLines(1);
        args.Handled = true;
    }

    private void OnDuplicateLineAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorHasFocus()) return;
        DuplicateCurrentLines();
        args.Handled = true;
    }

    private static int CountNewlines(string s, int upTo)
    {
        var n = 0;
        var end = Math.Min(upTo, s.Length);
        for (var i = 0; i < end; i++) if (IsLineBreak(s[i])) n++;
        return n;
    }

    // Move the (possibly multi-line) selection up (-1) or down (+1) one line, keeping it selected.
    private void MoveSelectedLines(int direction)
    {
        var tb = PasteTextBox;
        var text = EditorText();
        if (text.Length == 0) return;

        var selStart = Math.Clamp(tb.SelectionStart, 0, text.Length);
        var selLen = Math.Clamp(tb.SelectionLength, 0, text.Length - selStart);
        var anchor = selLen > 0 ? selStart + selLen - 1 : selStart;

        var list = new List<string>(text.Split('\n'));
        var first = CountNewlines(text, selStart);
        var last = CountNewlines(text, anchor);
        if (first > last) (first, last) = (last, first);
        if (first < 0) first = 0;
        if (last > list.Count - 1) last = list.Count - 1;

        int shift;
        if (direction < 0)
        {
            if (first == 0) return; // already at the very top
            shift = -(list[first - 1].Length + 1);
            var above = list[first - 1];
            list.RemoveAt(first - 1);
            list.Insert(last, above);
        }
        else
        {
            if (last >= list.Count - 1) return; // already at the very bottom
            shift = list[last + 1].Length + 1;
            var below = list[last + 1];
            list.RemoveAt(last + 1);
            list.Insert(first, below);
        }

        tb.Text = string.Join("\n", list);
        tb.SelectionStart = Math.Clamp(selStart + shift, 0, tb.Text.Length);
        tb.SelectionLength = Math.Clamp(selLen, 0, tb.Text.Length - tb.SelectionStart);
        tb.Focus(FocusState.Programmatic);
    }

    // Duplicate the current line (or the selected lines) directly below itself.
    private void DuplicateCurrentLines()
    {
        var tb = PasteTextBox;
        var text = EditorText();
        if (text.Length == 0) return;

        var selStart = Math.Clamp(tb.SelectionStart, 0, text.Length);
        var selLen = Math.Clamp(tb.SelectionLength, 0, text.Length - selStart);
        var anchor = selLen > 0 ? selStart + selLen - 1 : selStart;

        var lines = text.Split('\n');
        var first = CountNewlines(text, selStart);
        var last = CountNewlines(text, anchor);
        if (first > last) (first, last) = (last, first);
        if (last > lines.Length - 1) last = lines.Length - 1;

        var block = string.Join("\n", lines, first, last - first + 1);

        // Find the newline that ends line `last` (-1 when it is the final line).
        var lineEnd = -1;
        var ln = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') { if (ln == last) { lineEnd = i; break; } ln++; }
        }

        string newText;
        int caret;
        if (lineEnd >= 0)
        {
            newText = text.Insert(lineEnd + 1, block + "\n");
            caret = lineEnd + 1 + block.Length + 1;
        }
        else
        {
            newText = text + "\n" + block;
            caret = newText.Length;
        }
        tb.Text = newText;
        tb.SelectionStart = Math.Clamp(caret, 0, newText.Length);
        tb.SelectionLength = 0;
        tb.Focus(FocusState.Programmatic);
    }

    // ---- Document cleanup ----

    // One-click tidy-up: strip trailing whitespace, collapse runs of 3+ blank lines to two, and trim
    // leading/trailing blank lines. Reports how much it fixed in the status bar.
    private void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        var tb = PasteTextBox;
        var text = tb.Text ?? string.Empty;
        if (text.Length == 0) return;

        var hadCrlf = text.Contains("\r\n");
        var norm = text.Replace("\r\n", "\n").Replace('\r', '\n'); // the TextBox's own breaks are a bare '\r'
        var lines = norm.Split('\n');

        var changes = 0;
        var cleaned = new List<string>(lines.Length);
        var blankRun = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length != raw.Length) changes++;
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 2) { changes++; continue; } // collapse 3+ consecutive blanks
            }
            else
            {
                blankRun = 0;
            }
            cleaned.Add(line);
        }
        while (cleaned.Count > 0 && cleaned[0].Length == 0) { cleaned.RemoveAt(0); changes++; }
        while (cleaned.Count > 0 && cleaned[^1].Length == 0) { cleaned.RemoveAt(cleaned.Count - 1); changes++; }

        if (changes == 0)
        {
            ViewModel.StatusText = "Document is already clean — nothing to fix.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
            return;
        }

        var result = string.Join("\n", cleaned);
        if (hadCrlf) result = result.Replace("\n", "\r\n");
        var caret = Math.Clamp(tb.SelectionStart, 0, result.Length);
        tb.Text = result;
        tb.SelectionStart = caret;
        tb.SelectionLength = 0;
        ViewModel.StatusText = $"Cleaned up {changes} issue{(changes == 1 ? "" : "s")} (trailing spaces, blank-line runs).";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    // ---- Focus mode (F11): hide the side panes for distraction-free editing ----

    private void OnFocusModeToggled(object sender, RoutedEventArgs e) => ApplyFocusMode(FocusModeToggle?.IsChecked == true);

    private void OnFocusModeAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusModeToggle != null) FocusModeToggle.IsChecked = FocusModeToggle.IsChecked != true;
        args.Handled = true;
    }

    // Ctrl+Alt+X: toggle the Looking Glass portal's focus blur — whether the rendered preview
    // Starts raised: the blur sliders' TwoWay bindings fire ValueChanged during
    // InitializeComponent, which used to greet every launch with a "Surround blur: 6px behind
    // portal aperture" status line nobody asked for. Lowered once the portal UI is initialized.
    private bool _initializingPortalBlur = true;

    // Ctrl+Alt+X: toggle the Looking Glass portal's surrounding focus blur.
    private void OnTogglePortalBlurInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.PortalFocusBlur = !ViewModel.PortalFocusBlur;
        PushPortalBlurToPage(ViewModel.PortalFocusBlur, ViewModel.PortalSurroundBlurRadius);
        args.Handled = true;
    }

    // Pushes the portal surrounding focus-blur state into the live preview page.
    private void PushPortalBlurToPage(bool on, double? radius = null)
    {
        var rad = radius ?? ViewModel.PortalSurroundBlurRadius;
        var js = "if (window.__portalSetSurroundBlur) { window.__portalSetSurroundBlur(" + (on ? "true" : "false") + ", " + rad.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); }";
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
        ViewModel.StatusText = on
            ? $"Surround blur: {rad:0}px behind portal aperture — Ctrl+Alt+X to toggle."
            : "Surround blur: sharp preview behind portal aperture — Ctrl+Alt+X to toggle.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
    }

    // Pushes the glass blur state (inside the looking glass itself) into the live preview page.
    private void PushPortalInsideBlurToPage(bool on, double? radius = null)
    {
        var rad = radius ?? ViewModel.PortalInsideBlurRadius;
        var js = "if (window.__portalSetInsideBlur) { window.__portalSetInsideBlur(" + (on ? "true" : "false") + ", " + rad.ToString(System.Globalization.CultureInfo.InvariantCulture) + "); }";
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
        ViewModel.StatusText = on
            ? $"Glass blur: {rad:0}px within looking glass."
            : "Glass blur: looking glass interior is clear (0px).";
        ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
    }

    private void OnPortalBlurToggled(object sender, RoutedEventArgs e)
    {
        PushPortalBlurToPage(ViewModel.PortalFocusBlur, ViewModel.PortalSurroundBlurRadius);
    }

    private void OnPortalSurroundBlurRadiusChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializingPortalBlur) return;
        var r = PortalSurroundBlurSlider?.Value ?? 6.0;
        ViewModel.PortalSurroundBlurRadius = r;
        PushPortalBlurToPage(ViewModel.PortalFocusBlur, r);
    }

    private void OnPortalInsideBlurToggled(object sender, RoutedEventArgs e)
    {
        PushPortalInsideBlurToPage(ViewModel.PortalInsideBlur, ViewModel.PortalInsideBlurRadius);
    }

    private void OnPortalInsideBlurRadiusChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializingPortalBlur) return;
        var r = PortalInsideBlurSlider?.Value ?? 5.0;
        ViewModel.PortalInsideBlurRadius = r;
        PushPortalInsideBlurToPage(ViewModel.PortalInsideBlur, r);
    }

    private void ApplyFocusMode(bool focus)
    {
        StopLeftDrawerTween();
        if (focus)
        {
            _savedLeftPaneWidth = LeftPaneCol.Width;
            _savedRightPaneWidth = RightPaneCol.Width;
            _savedLeftPaneMinWidth = LeftPaneCol.MinWidth;
            _savedRightPaneMinWidth = RightPaneCol.MinWidth;
            // Zero the MinWidths too — a ColumnDefinition's MinWidth wins over Width, so without this
            // the "collapsed" panes would still hold a 250px gap on each side.
            LeftPaneCol.MinWidth = 0;
            RightPaneCol.MinWidth = 0;
            LeftPaneCol.Width = new GridLength(0);
            RightPaneCol.Width = new GridLength(0);
            if (LeftPane != null) LeftPane.Visibility = Visibility.Collapsed;
            if (RightPane != null) RightPane.Visibility = Visibility.Collapsed;
            if (LeftSplitter != null) LeftSplitter.Visibility = Visibility.Collapsed;
            if (RightSplitter != null) RightSplitter.Visibility = Visibility.Collapsed;
            if (MainLayoutGrid != null) MainLayoutGrid.ColumnSpacing = 0;
        }
        else
        {
            LeftPaneCol.Width = _savedLeftPaneWidth.IsAuto ? new GridLength(320) : _savedLeftPaneWidth;
            RightPaneCol.Width = _savedRightPaneWidth.IsAuto ? new GridLength(380) : _savedRightPaneWidth;
            // The Source column's MinWidth is 0 by design (it collapses to the 28 px drawer tab);
            // restoring a 250 px floor here used to wedge a "collapsed" drawer open.
            LeftPaneCol.MinWidth = _savedLeftPaneMinWidth;
            RightPaneCol.MinWidth = _savedRightPaneMinWidth > 0 ? _savedRightPaneMinWidth : 290;
            // Come back exactly as it was: a tucked-away drawer stays a tab (it used to return
            // as a squeezed pane with the tab drawn over it).
            if (LeftPane != null) LeftPane.Visibility = _leftPaneCollapsed ? Visibility.Collapsed : Visibility.Visible;
            if (LeftDrawerTab != null) LeftDrawerTab.Visibility = _leftPaneCollapsed ? Visibility.Visible : Visibility.Collapsed;
            if (_leftPaneCollapsed) LeftPaneCol.Width = new GridLength(LeftDrawerTabWidth);
            if (RightPane != null) RightPane.Visibility = Visibility.Visible;
            if (LeftSplitter != null) LeftSplitter.Visibility = Visibility.Visible;
            if (RightSplitter != null) RightSplitter.Visibility = Visibility.Visible;
            if (MainLayoutGrid != null) MainLayoutGrid.ColumnSpacing = 10;
        }
    }

    // ---- Print (Ctrl+P): print the rendered document via the WebView ----

    private void OnPrintClick(object sender, RoutedEventArgs e) => PrintDocument();

    private void OnPrintAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        PrintDocument();
        args.Handled = true;
    }

    // Open the system print dialog for the rendered preview. The preview is kept current by the
    // debounced auto-refresh (it renders even while the preview tab is hidden), so what you see is
    // what prints. If the WebView hasn't finished initializing yet, wait for it rather than failing.
    private async void PrintDocument()
    {
        if (!await EnsurePreviewWebViewAsync())
        {
            ViewModel.StatusText = "The preview isn't ready yet — try again in a moment.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        var core = PreviewWebView.CoreWebView2;
        try
        {
            core.ShowPrintUI(Microsoft.Web.WebView2.Core.CoreWebView2PrintDialogKind.System);
            ViewModel.StatusText = "Opening the print dialog…";
            ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Print failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // ---- Preview zoom (buttons + Ctrl+wheel) — persisted across sessions ----

    // Buttons and Ctrl+wheel walk the standard stops in Core ZoomSteps (… 90, 100, 110, 125,
    // 150 …), so a fitted 137% snaps to 150% / 125% rather than drifting by a fixed amount.
    private static double PreviewZoomMin => Services.ZoomSteps.Min;
    private static double PreviewZoomMax => Services.ZoomSteps.Max;

    private void OnPreviewZoomInClick(object sender, RoutedEventArgs e) => ApplyPreviewZoom(Services.ZoomSteps.Next(_lastPreviewZoom));

    private void OnPreviewZoomOutClick(object sender, RoutedEventArgs e) => ApplyPreviewZoom(Services.ZoomSteps.Previous(_lastPreviewZoom));

    // Clicking the percentage goes back to 100%, the size the page will print at.
    private void OnPreviewZoomResetClick(object sender, RoutedEventArgs e) => ApplyPreviewZoom(1.0);

    // Fit page width: the page fills the pane and follows it as the window, splitter or drawer
    // changes its width. Turning it off holds the current scale, so nothing jumps.
    private void OnPreviewFitClick(object sender, RoutedEventArgs e)
    {
        var on = PreviewFitToggle.IsChecked == true;
        SetPreviewFit(on);
        if (!on) PersistPreviewZoom(_lastPreviewZoom);
        SendPreviewZoom(anchor: null);
    }

    private bool _previewFit;

    private void SetPreviewFit(bool on)
    {
        _previewFit = on;
        if (PreviewFitToggle is not null) PreviewFitToggle.IsChecked = on;
        if (App.Settings.Current.PreviewZoomFit != on)
        {
            App.Settings.Current.PreviewZoomFit = on;
            App.Settings.Save();
        }
    }

    // A manual zoom (buttons, Ctrl+wheel) steps from the scale currently on screen — fitted or
    // not — and takes over from fit-width.
    private void ApplyPreviewZoom(double factor, bool cursor = false)
    {
        var clamped = Math.Round(Math.Clamp(factor, PreviewZoomMin, PreviewZoomMax), 2);
        if (_previewFit) SetPreviewFit(false);
        _lastPreviewZoom = clamped;
        UpdatePreviewZoomReadout(clamped);
        PersistPreviewZoom(clamped);
        SendPreviewZoom(anchor: cursor ? "cursor" : "button");
    }

    private void PersistPreviewZoom(double zoom)
    {
        App.Settings.Current.PreviewZoom = zoom;
        App.Settings.Save();
    }

    private void UpdatePreviewZoomReadout(double scale)
    {
        if (PreviewZoomText is null) return;
        var percent = (int)Math.Round(scale * 100.0);
        PreviewZoomText.Text = $"{percent}%";
        var tip = _previewFit
            ? $"Preview zoom: {percent}%, fitted to the pane's width. Click for 100%; Ctrl+wheel zooms."
            : $"Preview zoom: {percent}%. Click for 100%; Ctrl+wheel zooms.";
        ToolTipService.SetToolTip(PreviewZoomResetButton, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PreviewZoomResetButton, $"Preview zoom {percent}%, reset to 100%");
        // The ends of the ladder: a button that can't do anything says so instead of silently
        // doing nothing.
        PreviewZoomInButton.IsEnabled = Services.ZoomSteps.CanZoomIn(scale);
        PreviewZoomOutButton.IsEnabled = Services.ZoomSteps.CanZoomOut(scale);
    }

    // The page-side value of window.__msZoom: 'fit' or the absolute scale.
    private string PreviewZoomArg() => _previewFit
        ? "'fit'"
        : _lastPreviewZoom.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    // Hand the zoom to the page's script (see MarkdownHtmlService's fit-width script). Anchors:
    // "cursor" (Ctrl+wheel) keeps the document point under the mouse still; "button" keeps the
    // horizontal centre and the last-hovered row (or the viewport middle) still; null just
    // re-scales. Best-effort: before the first render the seed in RefreshPreviewAsync covers it.
    private void SendPreviewZoom(string? anchor)
    {
        var core = PreviewWebView?.CoreWebView2;
        if (core is null) return;
        var point = anchor switch
        {
            "cursor" => ",(typeof window.__msX==='number'?window.__msX:innerWidth/2),(typeof window.__msY==='number'?window.__msY:innerHeight/2)",
            "button" => ",innerWidth/2,(typeof window.__msY==='number'?window.__msY:innerHeight/2)",
            _ => "",
        };
        _ = core.ExecuteScriptAsync($"window.__msSetZoom&&window.__msSetZoom({PreviewZoomArg()}{point});");
    }

    // Install the Ctrl+wheel -> "preview-zoom" bridge once. Native browser zoom is disabled so the
    // wheel and the buttons both flow through ApplyPreviewZoom (one source of truth, no compounding).
    // The mousemove tracker feeds the anchored zoom: zoom aims at wherever the cursor last was.
    private async Task SetupPreviewZoomAsync()
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;
        try { core.Settings.IsZoomControlEnabled = false; } catch { /* setting unavailable */ }
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('mousemove', function(e){" +
                "window.__msX=e.clientX;window.__msY=e.clientY;});" +
                "window.addEventListener('wheel', function(e){" +
                "if(e.ctrlKey){e.preventDefault();" +
                "window.__msX=e.clientX;window.__msY=e.clientY;" +
                "try{window.chrome.webview.postMessage(JSON.stringify({type:'preview-zoom',delta:e.deltaY}));}catch(_){}" +
                "}}, {passive:false});");
        }
        catch { /* listener already registered or unavailable */ }
    }

    // ---- Auto-recovery of unsaved editor content ----

    // Debounce the recovery write so a fast typist doesn't hit the disk on every keystroke.
    private void ScheduleAutosave()
    {
        if (_autosaveTimer is null)
        {
            _autosaveTimer = DispatcherQueue.CreateTimer();
            _autosaveTimer.Interval = TimeSpan.FromSeconds(2);
            _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); WriteRecoveryFile(); };
        }
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    // Mirror the paste buffer to the recovery file. When the editor is file-based (or empty) there
    // is nothing unsaved to protect, so any stale recovery file is removed instead.
    private void WriteRecoveryFile()
    {
        try
        {
            if (ViewModel.UsePasteSource && !string.IsNullOrWhiteSpace(ViewModel.PastedMarkdown))
            {
                Directory.CreateDirectory(RecoveryDir);
                File.WriteAllText(RecoveryPath, ViewModel.PastedMarkdown);
            }
            else if (File.Exists(RecoveryPath))
            {
                File.Delete(RecoveryPath);
            }
        }
        catch { /* recovery is best-effort and must never interrupt editing */ }
    }

    // On launch, if a recovery file survived the previous session, offer to restore it. Runs once the
    // visual tree is ready (a ContentDialog needs a XamlRoot).
    private async Task CheckRecoveryAsync()
    {
        try
        {
            if (!File.Exists(RecoveryPath)) return;
            var content = await File.ReadAllTextAsync(RecoveryPath);
            if (string.IsNullOrWhiteSpace(content)) { File.Delete(RecoveryPath); return; }

            var dialog = new ContentDialog
            {
                Title = "Recover unsaved document",
                Content = "MarkSmith found an unsaved document from your last session. Would you like to restore it?",
                PrimaryButtonText = "Restore",
                // Discard is the *secondary* button, not the Close button: Escape (and any other
                // dismissal) reports the Close/None result, and a stray Escape must never delete
                // someone's only copy of their work. Dismissing files the draft away instead.
                SecondaryButtonText = "Discard",
                CloseButtonText = "Keep as file",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var result = await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.BreakUndoBurst(); // restoring the draft must undo as its own step
                ViewModel.CurrentMarkdown = content;
                ViewModel.StatusText = "Unsaved document restored from your last session.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Success;
                await RefreshPreviewAsync(heavy: true);
                File.Delete(RecoveryPath);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                File.Delete(RecoveryPath);
            }
            else
            {
                // Dismissed (Keep as file / Escape). The recovery slot is about to be reused by this
                // session's autosave, so move the draft somewhere it will survive and say where.
                var keptDir = Path.Combine(RecoveryDir, "Recovered drafts");
                Directory.CreateDirectory(keptDir);
                var keptPath = Path.Combine(keptDir, $"Unsaved draft {DateTime.Now:yyyy-MM-dd HHmmss}.md");
                File.Move(RecoveryPath, keptPath);
                ViewModel.StatusText = $"Your previous unsaved draft was kept at {keptPath}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
            }
        }
        catch { /* recovery is best-effort */ }
    }

    private void InsertMarkdown(string prefix, string suffix = "")
    {
        // ISS-004: while a Looking Glass portal is open, the formatting toolbar drives the
        // PORTAL editor — that's where the user's working caret lives, not the main editor
        // behind it. __portalApplyEdit fires a synthetic input, so the edit rides the normal
        // portal-edit sync back into the editor and the live preview.
        if (_portalOpen && PreviewWebView.CoreWebView2 is { } core)
        {
            var pjs = "if (window.__portalApplyEdit) { window.__portalApplyEdit(" +
                      System.Text.Json.JsonSerializer.Serialize(prefix) + ", " +
                      System.Text.Json.JsonSerializer.Serialize(suffix) + "); }";
            _ = core.ExecuteScriptAsync(pjs);
            PreviewWebView.Focus(FocusState.Programmatic); // hand focus back to the portal caret
            return;
        }

        var tb = PasteTextBox;
        if (tb == null) return;

        int selStart = tb.SelectionStart;
        int selLen = tb.SelectionLength;
        string text = tb.Text ?? "";
        string selected = tb.SelectedText ?? "";

        // If selection is empty, insert prefix + suffix at caret and place caret inside
        if (string.IsNullOrEmpty(selected))
        {
            tb.SelectedText = prefix + suffix;
            tb.SelectionStart = selStart + prefix.Length;
            tb.SelectionLength = 0;
            tb.Focus(FocusState.Programmatic);
            return;
        }

        // Split leading and trailing line breaks (\r, \n) from selected text
        int leadEnd = 0;
        while (leadEnd < selected.Length && (selected[leadEnd] == '\r' || selected[leadEnd] == '\n'))
        {
            leadEnd++;
        }

        int trailStart = selected.Length;
        while (trailStart > leadEnd && (selected[trailStart - 1] == '\r' || selected[trailStart - 1] == '\n'))
        {
            trailStart--;
        }

        string leadingBreak = selected.Substring(0, leadEnd);
        string coreText = selected.Substring(leadEnd, trailStart - leadEnd);
        string trailingBreak = selected.Substring(trailStart);

        bool isInlineFormat = !string.IsNullOrEmpty(suffix);
        bool coreHasFormat = isInlineFormat && coreText.Length >= (prefix.Length + suffix.Length) &&
                            coreText.StartsWith(prefix) && coreText.EndsWith(suffix);

        int precedeIdx = selStart + leadEnd - prefix.Length;
        int followIdx = selStart + trailStart;
        bool surroundingHasFormat = isInlineFormat && !coreHasFormat &&
                                   precedeIdx >= 0 && (followIdx + suffix.Length) <= text.Length &&
                                   text.Substring(precedeIdx, prefix.Length) == prefix &&
                                   text.Substring(followIdx, suffix.Length) == suffix;

        if (coreHasFormat)
        {
            // Toggle OFF: selection itself contains surrounding formatting markers
            string unformatted = coreText.Substring(prefix.Length, coreText.Length - prefix.Length - suffix.Length);
            string rep = leadingBreak + unformatted + trailingBreak;
            tb.SelectedText = rep;
            tb.SelectionStart = Math.Clamp(selStart + leadingBreak.Length, 0, (tb.Text ?? "").Length);
            tb.SelectionLength = Math.Clamp(unformatted.Length, 0, (tb.Text ?? "").Length - tb.SelectionStart);
            tb.Focus(FocusState.Programmatic);
            return;
        }
        else if (surroundingHasFormat)
        {
            // Toggle OFF: formatting markers surround the selection in full text
            string newFullText = text.Remove(followIdx, suffix.Length).Remove(precedeIdx, prefix.Length);
            tb.Text = newFullText;
            tb.SelectionStart = Math.Clamp(precedeIdx + leadingBreak.Length, 0, (tb.Text ?? "").Length);
            tb.SelectionLength = Math.Clamp(coreText.Length, 0, (tb.Text ?? "").Length - tb.SelectionStart);
            tb.Focus(FocusState.Programmatic);
            return;
        }

        string replacement;
        int newCoreStartOffset;
        int newCoreLength;

        // Line-level prefix formatting (e.g. # , - , 1. , > ) when suffix is empty
        if (string.IsNullOrEmpty(suffix) && prefix.TrimEnd() is "#" or "##" or "###" or "####" or "-" or "1." or "- [ ]" or ">")
        {
            // The editor TextBox separates lines with a bare '\r', so split on every kind of break
            // (keeping them): splitting on '\n' alone prefixed only the first selected line. A
            // numbered list counts up (1. 2. 3.) instead of repeating "1.".
            var parts = System.Text.RegularExpressions.Regex.Split(coreText, "(\r\n|\r|\n)");
            int number = 0;
            for (int i = 0; i < parts.Length; i += 2)
            {
                if (parts[i].Length == 0) continue;
                parts[i] = (prefix == "1. " ? $"{++number}. " : prefix) + parts[i];
            }
            string formattedCore = string.Concat(parts);
            replacement = leadingBreak + formattedCore + trailingBreak;
            newCoreStartOffset = selStart + leadingBreak.Length;
            newCoreLength = formattedCore.Length;
        }
        else
        {
            // Inline formatting (e.g. **bold**, *italic*, ~~strikethrough~~, ==highlight==, `code`, [text](url))
            replacement = leadingBreak + prefix + coreText + suffix + trailingBreak;
            newCoreStartOffset = selStart + leadingBreak.Length + prefix.Length;
            newCoreLength = coreText.Length;
        }

        tb.SelectedText = replacement;
        tb.SelectionStart = Math.Clamp(newCoreStartOffset, 0, (tb.Text ?? "").Length);
        tb.SelectionLength = Math.Clamp(newCoreLength, 0, (tb.Text ?? "").Length - tb.SelectionStart);
        tb.Focus(FocusState.Programmatic);
    }

    private void OnBoldClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("**", "**");
    }

    private void OnItalicClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("*", "*");
    }

    private void OnStrikethroughClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("~~", "~~");
    }

    private async void OnCodeBlockClick(object sender, RoutedEventArgs e)
    {
        // Quick insert: the classic bare fence straight into the editor, no modal.
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n```\n", "\n```\n");
            return;
        }

        var control = new Views.CodeBlockInsertControl();
        if (await ShowInsertDialogAsync("Insert code block", control) != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(control.Body))
        {
            // No pasted code: insert prefix/suffix so the caret lands inside the fence.
            InsertMarkdown($"\n```{control.SelectedLanguage}\n", "\n```\n");
            return;
        }
        InsertMarkdown(Services.InsertSnippetBuilder.CodeBlock(control.SelectedLanguage, control.Body));
    }

    private void OnBlockquoteClick(object sender, RoutedEventArgs e) => ApplyLineMarker(Services.LineMarker.Quote, "> ");

    private async void OnInsertWorkflowClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::workflow\n- Step 1\n- Step 2\n- Step 3\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl(
            "A row of steps joined by arrows, in the order you list them.",
            "Steps — one per line", "Step 1\nStep 2\nStep 3",
            Services.InsertSnippetBuilder.Workflow, "step", minimum: 2);
        if (await ShowInsertDialogAsync("Insert workflow", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertTimelineClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::timeline\n- 2020: Started\n- 2023: Progress\n- 2026: Done\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl(
            "Milestones along a line, in the order you list them.",
            "Milestones — when: what, one per line", "2020: Started\n2023: Progress\n2026: Done",
            Services.InsertSnippetBuilder.Timeline, "milestone",
            lineIsValid: MarkSmith.Core.AdvancedFeatures.TimelineDetector.IsTimelineEntry,
            lineHint: "write each milestone as when: what, e.g. 2026: Launch.");
        if (await ShowInsertDialogAsync("Insert timeline", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertSmartArtClick(object sender, RoutedEventArgs e)
    {
        var control = new Views.SmartArtInsertControl();
        if (await ShowInsertDialogAsync("Insert SmartArt", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.GeneratedSnippet);
    }

    private async void OnInsertTabsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::tabs\n=== Tab 1\nContent 1\n=== Tab 2\nContent 2\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl(
            "Switchable tabs in the preview. PDF and Word show every tab, one after another.",
            "Tab titles — one per line", "Tab 1\nTab 2",
            Services.InsertSnippetBuilder.Tabs, "tab", minimum: 2);
        if (await ShowInsertDialogAsync("Insert tab group", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertColumnsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::columns count=\"2\"\nColumn 1 content\n===\nColumn 2 content\n:::\n");
            return;
        }

        var control = new Views.NumbersInsertControl(
            "Side-by-side columns. A line containing only === starts the next column.",
            v => Services.InsertSnippetBuilder.Columns(v[0]), ("Columns", 2, 2, 4));
        if (await ShowInsertDialogAsync("Insert multi-column section", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertCanvasClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::canvas\n<svg viewBox=\"0 0 100 100\" width=\"200\" height=\"200\">\n  <circle cx=\"50\" cy=\"50\" r=\"40\" stroke=\"black\" stroke-width=\"3\" fill=\"red\" />\n</svg>\n:::\n");
            return;
        }

        var control = new Views.NumbersInsertControl(
            "An SVG drawing area with a starter shape. Edit the SVG by hand to draw what you need.",
            v => Services.InsertSnippetBuilder.Canvas(v[0], v[1]), ("Width", 200, 10, 4000), ("Height", 200, 10, 4000));
        if (await ShowInsertDialogAsync("Insert drawing canvas", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertWaveFunctionClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            // Same block the dialog makes: the menu item is the procedural tile map, not the
            // quantum :::wavefunction diagram this used to insert.
            InsertMarkdown(Services.InsertSnippetBuilder.WaveFunctionCollapse("Procedural WFC Grid", 5, 5));
            return;
        }

        var control = new Views.NumbersInsertControl(
            "A procedurally generated tile map (grass, road, water, wall) of the size you choose.",
            v => Services.InsertSnippetBuilder.WaveFunctionCollapse("Procedural WFC Grid", v[0], v[1]),
            ("Grid width", 5, 2, 20), ("Grid height", 5, 2, 20));
        if (await ShowInsertDialogAsync("Insert random tile map", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    // Headings and list markers act on whole lines (Core's LineFormatting): the caret's line or
    // every selected line, toggling off when pressed again. They used to insert "# " at the caret.
    private void OnBulletListClick(object sender, RoutedEventArgs e) => ApplyLineMarker(Services.LineMarker.Bullet, "- ");

    private void OnNumberedListClick(object sender, RoutedEventArgs e) => ApplyLineMarker(Services.LineMarker.Numbered, "1. ");

    private void OnTaskListClick(object sender, RoutedEventArgs e) => ApplyLineMarker(Services.LineMarker.Task, "- [ ] ");

    private void OnH1Click(object sender, RoutedEventArgs e) => ApplyHeading(1);

    private void OnH2Click(object sender, RoutedEventArgs e) => ApplyHeading(2);

    private void OnH3Click(object sender, RoutedEventArgs e) => ApplyHeading(3);

    private void OnH4Click(object sender, RoutedEventArgs e) => ApplyHeading(4);

    private void ApplyHeading(int level)
    {
        // The Looking Glass portal edits its own buffer through __portalApplyEdit.
        if (_portalOpen) { InsertMarkdown(new string('#', level) + " ", ""); return; }
        ApplyLineEdit(Services.LineFormatting.Heading(PasteTextBox.Text ?? "", PasteTextBox.SelectionStart, PasteTextBox.SelectionLength, level));
    }

    private void ApplyLineMarker(Services.LineMarker marker, string portalPrefix)
    {
        if (_portalOpen) { InsertMarkdown(portalPrefix, ""); return; }
        ApplyLineEdit(Services.LineFormatting.Toggle(PasteTextBox.Text ?? "", PasteTextBox.SelectionStart, PasteTextBox.SelectionLength, marker));
    }

    // Swaps only the touched lines (not the whole Text), so the editor keeps its scroll position,
    // and the change undoes as one step.
    private void ApplyLineEdit(Services.LineEdit edit)
    {
        var tb = PasteTextBox;
        ViewModel.BreakUndoBurst();
        tb.Select(edit.Start, edit.Length);
        tb.SelectedText = edit.Replacement;
        var length = (tb.Text ?? "").Length;
        var start = Math.Clamp(edit.SelectionStart, 0, length);
        tb.Select(start, Math.Clamp(edit.SelectionLength, 0, length - start));
        tb.Focus(FocusState.Programmatic);
        ViewModel.BreakUndoBurst();
    }

    // Ctrl+B / Ctrl+I / Ctrl+1-4 (editor-scoped accelerators, listed in Core's KeyboardShortcuts).
    private void OnFormatAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        switch (sender.Key)
        {
            case Windows.System.VirtualKey.B: InsertMarkdown("**", "**"); break;
            case Windows.System.VirtualKey.I: InsertMarkdown("*", "*"); break;
            case Windows.System.VirtualKey.Number1: ApplyHeading(1); break;
            case Windows.System.VirtualKey.Number2: ApplyHeading(2); break;
            case Windows.System.VirtualKey.Number3: ApplyHeading(3); break;
            case Windows.System.VirtualKey.Number4: ApplyHeading(4); break;
        }
    }

    private async void OnLinkClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("[", "](url)");
            return;
        }

        var control = new Views.LinkInsertControl();
        if (await ShowInsertDialogAsync("Insert link", control) != ContentDialogResult.Primary) return;
        var url = string.IsNullOrWhiteSpace(control.Url) ? "url" : control.Url.Trim();
        if (string.IsNullOrWhiteSpace(control.Text))
            InsertMarkdown("[", $"]({url})"); // empty text: caret lands between the brackets
        else
            InsertMarkdown(Services.InsertSnippetBuilder.Link(control.Text, url));
    }

    // Shared shell for every Insert-menu modal: builds the ContentDialog, resolves a guaranteed
    // XamlRoot (RootGrid first, then the window Content), and never fails silently — a dialog
    // that can't open reports itself in the status bar instead of vanishing without a trace.
    // Returns the dialog result; errors and a missing root map to ContentDialogResult.None.
    private async Task<ContentDialogResult> ShowInsertDialogAsync(
        string title, FrameworkElement content,
        string? primaryButtonText = "Insert", Action<ContentDialog>? configure = null)
    {
        try
        {
            var root = RootGrid?.XamlRoot ?? Content?.XamlRoot;
            if (root is null)
            {
                ViewModel.StatusText = $"Couldn't open the '{title}' dialog — the window isn't ready yet.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Error;
                return ContentDialogResult.None;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "Cancel",
                XamlRoot = root,
            };
            if (primaryButtonText is not null)
            {
                dialog.PrimaryButtonText = primaryButtonText;
                dialog.DefaultButton = ContentDialogButton.Primary;
            }
            configure?.Invoke(dialog);
            if (content is Views.InsertDialogBody body)
            {
                // Insert stays disabled while the values can't make a usable block (the body says
                // why in red), and the caret starts in the first field with its sample selected.
                dialog.IsPrimaryButtonEnabled = body.IsValid;
                body.ValidityChanged += valid => dialog.IsPrimaryButtonEnabled = valid;
                dialog.Opened += (_, _) => body.FocusFirstField();
            }
            return await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Couldn't open the '{title}' dialog: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return ContentDialogResult.None;
        }
    }

    private async void OnImageClick(object sender, RoutedEventArgs e)
    {
        // Quick insert (Settings ▸ General): the classic one-keystroke placeholder, no modal.
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("![", "](image.png)");
            return;
        }

        // Default experience: drop, browse or paste a path/address, check the thumbnail and alt
        // text, then Insert — the same footer as every other insert dialog.
        var control = new Views.ImageInsertControl(ViewModel.DocumentFolder);
        if (await ShowInsertDialogAsync("Insert image", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnTableClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n| Header 1 | Header 2 |\n| --- | --- |\n| Value 1 | Value 2 |\n");
            return;
        }

        var control = new Views.TableInsertControl();
        if (await ShowInsertDialogAsync("Insert table", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertEmbedClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::embed provider=\"youtube\" src=\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"\n:::\n");
            return;
        }

        var control = new Views.EmbedInsertControl();
        if (await ShowInsertDialogAsync("Insert video embed", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertChartClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::chart type=\"bar\"\nQ1,10\nQ2,25\nQ3,15\n:::\n");
            return;
        }

        var control = new Views.ChartInsertControl();
        if (await ShowInsertDialogAsync("Insert chart", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertDatagridClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::datagrid\nlabel,value\nQ1,10\nQ2,25\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl(
            "A styled data table. Numbers line up on the right; the first line is the header.",
            "Rows — comma-separated, headers first", "label,value\nQ1,10\nQ2,25",
            Services.InsertSnippetBuilder.Datagrid, "row", minimum: 2);
        if (await ShowInsertDialogAsync("Insert data grid", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private async void OnInsertReferencesClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::references\n@paper-id\nauthor: Author Name\ntitle: Publication Title\nyear: 2026\n:::\n");
            return;
        }

        var control = new Views.ReferencesInsertControl();
        if (await ShowInsertDialogAsync("Insert bibliography entry", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.Snippet);
    }

    private void OnInsertAiContextClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::ai-context\npromptHash: abc123\nmodel: Gemini Pro\ntimestamp: " + DateTime.Now.ToString("yyyy-MM-dd") + "\n:::\n");
    }

    // ---- D1: Reverse Document Import (DOCX / PDF → Markdown) ------------------------------------

    private async void OnImportDocumentClick(object sender, RoutedEventArgs e)
    {
        var supportedExts = new[] { ".docx", ".pdf", ".eml", ".msg", ".html", ".htm" }.Concat(Ocr.OcrImport.ImageExtensions);
        var filePath = await Services.NativeFilePicker.PickOpenFileAsync(
            this, "Import a document as Markdown", Services.NativeFilePicker.Purpose.Documents,
            new[]
            {
                Models.FileType.Of("Documents, emails and scans", supportedExts),
                Models.FileType.Of("Word document", ".docx"),
                Models.FileType.Of("PDF", ".pdf"),
                Models.FileType.Of("Email", ".eml", ".msg"),
                Models.FileType.Of("Web page", ".html", ".htm"),
                Models.FileType.Of("Scanned page (read with OCR)", Ocr.OcrImport.ImageExtensions),
            },
            okLabel: "Import");
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var name = Path.GetFileName(filePath);
            if (Ocr.OcrImport.IsImage(filePath))
            {
                // A picture of a page: read it with the OCR engine Settings picked.
                ViewModel.StatusText = $"Reading the text in {name}…";
                var read = await Ocr.OcrImport.ImageToMarkdownAsync(filePath);
                if (string.IsNullOrWhiteSpace(read.Markdown))
                {
                    ViewModel.StatusText = $"No text found in {name}.";
                    ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                    return;
                }
                ViewModel.DetachFromOpenFile();
                ViewModel.PastedMarkdown = read.Markdown;
                ViewModel.UsePasteSource = true;
                ViewModel.StatusText = $"Imported {name} · read with {read.Engine}" + (read.FellBack ? " (the chosen engine isn't available here)" : "");
                ViewModel.StatusSeverity = read.FellBack ? Models.StatusSeverity.Warning : Models.StatusSeverity.Success;
                return;
            }
            if (ext is ".docx" or ".pdf")
            {
                // Word/PDF keep their own result: the tier and the "edited after export" warning.
                var importer = new Services.ReverseImportService();
                ViewModel.StatusText = $"Importing {name}…";
                var result = ext == ".pdf"
                    ? await importer.ImportFromPdfAsync(filePath, new Services.Import.PdfImportOptions
                    {
                        MediaDirectory = Plugins.PluginFileReader.MediaDirFor(filePath),
                        MediaLink = Plugins.PluginFileReader.MediaLinkFor(filePath, Plugins.PluginFileReader.MediaDirFor(filePath)),
                        RenderPage = n => Services.WindowsPdfRenderer.Render(filePath, n),
                        // Scanned pages take a moment each: say which one is being read.
                        Progress = new Progress<string>(s => ViewModel.StatusText = s),
                    })
                    : await importer.ImportFromDocxAsync(filePath);
                Services.WindowsPdfRenderer.Release();

                if (string.IsNullOrWhiteSpace(result.Markdown))
                {
                    ViewModel.StatusText = result.Warning ?? "No content could be extracted from that document.";
                    ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                    return;
                }

                // Load the extracted Markdown into the editor as a new document.
                ViewModel.DetachFromOpenFile();
                ViewModel.PastedMarkdown = result.Markdown;
                ViewModel.UsePasteSource = true;
                ViewModel.StatusText = result.Tier == Services.ImportTier.EmbeddedSource
                    ? $"Imported {name} (its original MarkSmith source)"
                    : $"Imported {name}";
                if (result.Pdf is { } pdf)
                {
                    ViewModel.StatusText += $" · {pdf.Pages} page{(pdf.Pages == 1 ? "" : "s")}";
                    if (pdf.Pictures > 0) ViewModel.StatusText += $", {pdf.Pictures} picture{(pdf.Pictures == 1 ? "" : "s")}";
                }
                if (!result.IsStale && !string.IsNullOrWhiteSpace(result.Warning)) ViewModel.StatusText += " · " + result.Warning;
                ViewModel.StatusSeverity = result.IsStale || result.Pdf?.Notes.Count > 0
                    ? Models.StatusSeverity.Warning
                    : Models.StatusSeverity.Success;
                if (result.IsStale && !string.IsNullOrWhiteSpace(result.Warning))
                    ViewModel.StatusText += " — edited after export; source may lag the visible content";
                return;
            }

            var imported = await Plugins.PluginFileReader.ImportAsync(filePath);
            if (string.IsNullOrWhiteSpace(imported.Markdown))
            {
                ViewModel.StatusText = $"Nothing to import from {Path.GetFileName(filePath)}: it has no readable content.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            ViewModel.DetachFromOpenFile();
            ViewModel.PastedMarkdown = imported.Markdown;
            ViewModel.UsePasteSource = true;
            ViewModel.StatusText = imported.Summary ?? $"Imported {Path.GetFileName(filePath)}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Import failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // ---- D4: Spreadsheet / CSV bidirectional sync ------------------------------------------------

    private async void OnInsertSpreadsheetClick(object sender, RoutedEventArgs e)
    {
        var filePath = await Services.NativeFilePicker.PickOpenFileAsync(
            this, "Insert a spreadsheet as a table", Services.NativeFilePicker.Purpose.Spreadsheets,
            new[]
            {
                Models.FileType.Of("Spreadsheets", ".xlsx", ".csv"),
                Models.FileType.Of("Excel workbook", ".xlsx"),
                Models.FileType.Of("CSV", ".csv"),
            },
            okLabel: "Insert");
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            Services.TableModel model;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".xlsx")
            {
                using var stream = File.OpenRead(filePath);
                var sheets = Services.SpreadsheetService.ReadXlsx(stream);
                if (sheets.Count == 0)
                {
                    ViewModel.StatusText = "No data found in that workbook.";
                    ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                    return;
                }

                // Single sheet → import directly. Multiple → let the user pick.
                if (sheets.Count == 1)
                {
                    model = sheets[0].Model;
                }
                else
                {
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (var (name, _) in sheets) combo.Items.Add(name);
                    combo.SelectedIndex = 0;

                    var dlg = new ContentDialog
                    {
                        Title = "Pick a sheet",
                        Content = new StackPanel { Spacing = 8, Children = {
                            new TextBlock { Text = "This workbook has multiple sheets. Which one should become the table?" },
                            combo } },
                        PrimaryButtonText = "Insert",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = Content.XamlRoot,
                    };
                    if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dlg) != ContentDialogResult.Primary) return;
                    model = sheets[combo.SelectedIndex].Model;
                }
            }
            else
            {
                // CSV (or .tsv / .txt treated as CSV with auto-delimiter detection)
                var text = await File.ReadAllTextAsync(filePath);
                model = Services.SpreadsheetService.ParseCsv(text);
            }

            if (model.ColumnCount == 0)
            {
                ViewModel.StatusText = "That file didn't contain any tabular data.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }

            var markdown = Services.SpreadsheetService.ToMarkdownTable(model);
            InsertMarkdown(markdown);

            var truncated = model.Rows.Count >= Services.SpreadsheetService.MaxImportRows;
            ViewModel.StatusText = truncated
                ? $"Imported {model.Rows.Count} rows (truncated at {Services.SpreadsheetService.MaxImportRows})."
                : $"Imported {model.Rows.Count + 1} rows × {model.ColumnCount} columns from {Path.GetFileName(filePath)}.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Import failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    private async void OnExportTableClick(object sender, RoutedEventArgs e)
    {
        var markdown = ViewModel.CurrentMarkdown ?? "";
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ViewModel.StatusText = "Nothing to export — the editor is empty.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        // Determine which tables to export: the one under the cursor, or all if the cursor isn't
        // inside any table (the "export all tables" path).
        var text = PasteTextBox.Text ?? "";
        // 0-based, like Markdig's table lines; folds above the caret hide document lines.
        var cursorLine = _folds.DocumentLine(text, GetCursorLine(text, PasteTextBox.SelectionStart) + 1) - 1;
        var tableAtCursor = Services.SpreadsheetService.FindTableAtLine(markdown, cursorLine);

        List<(string Name, Services.TableModel Model)> exports;
        if (tableAtCursor is not null)
        {
            var name = tableAtCursor.NearestHeading ?? "Table1";
            exports = new List<(string, Services.TableModel)> { (name, tableAtCursor.Model) };
        }
        else
        {
            var all = Services.SpreadsheetService.ExtractTables(markdown);
            if (all.Count == 0)
            {
                ViewModel.StatusText = "No Markdown tables found in this document.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
                return;
            }
            exports = all.Select((t, i) => (t.NearestHeading ?? $"Table{i + 1}", t.Model)).ToList();
        }

        var filePath = await Services.NativeFilePicker.PickSaveFileAsync(
            this, exports.Count == 1 ? "Export the table" : $"Export {exports.Count} tables", Services.NativeFilePicker.Purpose.Spreadsheets,
            $"{SuggestedNameFromDocument(exports.Count == 1 ? "table" : "tables")}.xlsx",
            new[]
            {
                Models.FileType.Of(exports.Count == 1 ? "Excel workbook" : "Excel workbook, one sheet per table", ".xlsx"),
                Models.FileType.Of(exports.Count == 1 ? "CSV" : "CSV, first table only", ".csv"),
            },
            okLabel: "Export", folder: OpenDocumentFolder);
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            var outExt = Path.GetExtension(filePath).ToLowerInvariant();
            if (outExt == ".csv")
            {
                // CSV: write only the first table (CSV is single-table by nature).
                var csv = Services.SpreadsheetService.WriteCsv(exports[0].Model);
                await File.WriteAllTextAsync(filePath, csv);
            }
            else
            {
                using var stream = File.Create(filePath);
                Services.SpreadsheetService.WriteXlsx(exports, stream);
            }

            ViewModel.StatusText = $"Exported {exports.Count} table{(exports.Count == 1 ? "" : "s")} to {Path.GetFileName(filePath)}.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Export failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // 0-based line index of a character offset in the editor text.
    private static int GetCursorLine(string text, int offset)
    {
        int line = 0;
        for (int i = 0; i < offset && i < text.Length; i++)
            if (IsLineBreak(text[i])) line++;
        return line;
    }

    private void OnOpenMermaidStudioClick(object sender, RoutedEventArgs e)
    {
        var fullMd = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
            ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);        var studioWindow = new Views.Mermaid.MermaidDiagramStudioWindow(fullMd);
        studioWindow.SyncToMarkdownRequested += (s, markdown) =>
        {
            var current = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
                ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);
            var synced = studioWindow.ViewModel.SyncToMarkdown(current);
            ViewModel.BreakUndoBurst(); // studio sync-back must undo as its own step
            ViewModel.CurrentMarkdown = Mermaid.Sync.MermaidSpatialMetadataService.Strip(synced, out _mermaidSpatialStash);
        };
        studioWindow.Activate();
    }

    // SmartArt Design Studio — structure → native Word SmartArt (canvas-first, no tabs)
    private void OnOpenSmartArtDesignStudioClick(object sender, RoutedEventArgs e)
    {
        // Open on the document you already have, not an empty canvas. The layout suggester used to
        // exist only to power a nagging "Can we show you this as SmartArt?" bar — it earns its keep
        // better here, choosing a sensible starting layout for content the user opened the studio
        // to work on. An empty editor just opens the gallery.
        var md = EditorDocument();
        if (string.IsNullOrWhiteSpace(md))
        {
            OpenSmartArtStudio();
            return;
        }
        var suggestion = Services.SmartArtPotentialDetector.Detect(md);
        OpenSmartArtStudio(preloadMarkdown: md, layoutAlias: suggestion.LayoutAlias);
    }

    private void OpenSmartArtStudio(string? preloadMarkdown = null, string? layoutAlias = null)
    {
        if (_smartArtDesignStudio == null)
        {
            _smartArtDesignStudio = new Views.SmartArtStudio.SmartArtDesignStudioWindow();
            _smartArtDesignStudio.Closed += (s, args) => _smartArtDesignStudio = null;
            // Design-stage output: add the hierarchy to the ACTIVE document as Markdown, at the
            // editor caret. It renders in the preview and becomes native Word SmartArt on export —
            // the studio never writes its own document.
            _smartArtDesignStudio.InsertToDocumentRequested += (s, block) =>
            {
                ViewModel.BreakUndoBurst(); // the insertion must undo as its own step
                InsertMarkdown(block);
                ViewModel.StatusText = "SmartArt added to the document — preview, then export to DOCX.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            };
        }
        if (preloadMarkdown is not null)
        {
            _smartArtDesignStudio.ViewModel.Preload(preloadMarkdown, layoutAlias ?? string.Empty);
        }
        _smartArtDesignStudio.Activate();
        ViewModel.StatusText = "SmartArt Studio opened.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    // ── SmartArt offer (non-invasive): detect diagram-shaped pasted content and offer a preview.
    // Shape Studio — free-form native DrawingML shape & diagram composing
    private void OnOpenShapeDesignStudioClick(object sender, RoutedEventArgs e)
    {
        if (_shapeDesignStudio == null)
        {
            _shapeDesignStudio = new Views.ShapeStudio.ShapeDesignStudioWindow();
            _shapeDesignStudio.Closed += (s, args) => _shapeDesignStudio = null;
            _shapeDesignStudio.InsertToDocumentRequested += (s, block) =>
            {
                ViewModel.BreakUndoBurst();
                InsertMarkdown(block);
                ViewModel.StatusText = "DrawingML vector shapes added to document.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            };
        }
        _shapeDesignStudio.Activate();
        ViewModel.StatusText = "Shape Studio opened.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    // Document Galaxy & Mind Map Hub — visual interconnected document library
    private void OnOpenMindMapGalaxyClick(object sender, RoutedEventArgs e)
    {
        if (_mindMapGalaxyWindow == null)
        {
            _mindMapGalaxyWindow = new Views.MindMap.MindMapGalaxyWindow();
            _mindMapGalaxyWindow.Closed += (s, args) => _mindMapGalaxyWindow = null;
            _mindMapGalaxyWindow.OpenDocumentRequested += (s, filePath) =>
            {
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    ViewModel.InputFilePath = filePath;
                    ViewModel.UsePasteSource = false;
                    ViewModel.StatusText = $"Opened {System.IO.Path.GetFileName(filePath)} from Document Galaxy.";
                    ViewModel.StatusSeverity = Models.StatusSeverity.Success;
                }
            };
        }
        _mindMapGalaxyWindow.Activate();
        ViewModel.StatusText = "Document Galaxy opened.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    public Task<MarkSmith.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MarkSmith.Models.AmbiguityCase ambiguity)
    {
        return Task.FromResult<MarkSmith.Models.RenderOption?>(null);
    }
}

