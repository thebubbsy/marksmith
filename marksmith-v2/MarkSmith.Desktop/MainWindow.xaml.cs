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
using Windows.Storage.Pickers;
using WinRT.Interop;
using MarkSmith.Mermaid.Sync;

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
    };

    private static readonly HashSet<string> AutomationProperties = new()
    {
        nameof(ViewModels.MainViewModel.AutoClipboardIngest),
        nameof(ViewModels.MainViewModel.WatchFolderEnabled),
        nameof(ViewModels.MainViewModel.WatchFolder),
        nameof(ViewModels.MainViewModel.ApiEnabled),
        nameof(ViewModels.MainViewModel.ApiPort),
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
    private double? _pendingPreviewScrollFraction;

    // Find bar (Ctrl+F): the current query's match offsets into the editor text, and which match is
    // highlighted. Recomputed on every keystroke of the query so the "n/m" count stays live.
    private readonly List<int> _findMatches = new();
    private int _findMatchIndex = -1;

    // Preview zoom: WinUI 3's WebView2 exposes no ZoomFactor, so zoom is applied as a CSS zoom on the
    // document via script. A document-created listener turns Ctrl+wheel into a "preview-zoom" message
    // so the buttons and the wheel share one code path and one source of truth (_lastPreviewZoom).
    private double _lastPreviewZoom = 1.0;

    // Auto-recovery: the paste buffer is debounced-written to a recovery file so an unexpected exit
    // (crash, power loss, forced close) never loses an unsaved document; it's offered back on launch.
    private static readonly string RecoveryDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");
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
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1220, 800));
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootGrid.DataContext = ViewModel;

        // Style-panel expanders auto-scroll their newly-revealed fields into view. Wired in
        // code-behind because the XAML Expanded="…" attribute crashes XamlCompiler (it exits 1 with
        // no output.json), whereas subscribing here is equivalent and build-safe. Note the control
        // exposes Expanding/Collapsed (there is no Expanded event in this Windows App SDK).
        ExportBrandingExpander.Expanding += OnStyleExpanderExpanded;

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

        // Word wrap + line numbers: apply the persisted wrap setting (the gutter is always visible).
        _initializingWordWrap = true;
        WordWrapToggle.IsChecked = App.Settings.Current.EditorWordWrap;
        _initializingWordWrap = false;
        ApplyWordWrap(App.Settings.Current.EditorWordWrap, persist: false);

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

        // Centre pane bottom bar: sync the portal row + editing clusters with the persisted
        // portal/view state now that both toggles are initialized.
        UpdateCenterBottomBar();

        // Markdown lint refresh on every edit + the non-invasive SmartArt offer (debounced so a
        // paste of a long ChatGPT answer is scanned once, not per keystroke).
        PasteTextBox.TextChanged += (_, _) =>
        {
            UpdateLintIndicator();
            // RULE: blank editor -> the left Source/Files pane is forcibly expanded again.
            if (string.IsNullOrWhiteSpace(PasteTextBox?.Text))
            {
                ExpandLeftPane();
                if (SmartArtOfferBar is not null)
                {
                    SmartArtOfferBar.IsOpen = false;
                    SmartArtOfferBar.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                ScheduleSmartArtOffer();
            }
        };
        UpdateLintIndicator();
        InitSmartArtOffer();

        // Export-completion toast: manual exports raise ExportCompleted from the ViewModel.
        ViewModel.ExportCompleted += (kind, path) => ShowExportToast(kind, path);

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

        _clipboardIngest = new Services.ClipboardIngestService(DispatcherQueue, (text, origin, output) => IngestFromSource(text, origin, output));
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

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        WireStreamingApi();

        // Expanded editing bar: when the bottom bar has room, common actions become direct buttons
        // instead of hiding under the cluster dropdowns (split view, full code mode, fullscreen).
        BuildEditingExpandedButtons();
        RootGrid.SizeChanged += (_, _) => UpdateEditingExpansion();
        UpdateEditingExpansion();
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

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.BetaExpirationGuard.FeedbackUrl));
        }

        Environment.Exit(0);
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

    // "Export history" moved into the ⋯ menu but kept its rich ListView flyout: the menu item
    // re-opens it as the button's attached flyout (enqueued so the closing menu doesn't eat it).
    private void OnExportHistoryMenuClick(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
            Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(MoreMenuButton));

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
        (string Content, string Tip, RoutedEventHandler Click)[] actions =
        {
            ("B", "Bold (Ctrl+B)", OnBoldClick),
            ("I", "Italic (Ctrl+I)", OnItalicClick),
            ("S", "Strikethrough", OnStrikethroughClick),
            ("H1", "Heading 1 (#)", OnH1Click),
            ("H2", "Heading 2 (##)", OnH2Click),
            ("H3", "Heading 3 (###)", OnH3Click),
            ("H4", "Heading 4 (####)", OnH4Click),
            ("•", "Bullet list", OnBulletListClick),
            ("1.", "Numbered list", OnNumberedListClick),
            ("☑", "Task list", OnTaskListClick),
            ("❝", "Blockquote", OnBlockquoteClick),
            ("Link", "Insert link", OnLinkClick),
            ("Img", "Insert image", OnImageClick),
            ("Tbl", "Insert table", OnTableClick),
            ("<>", "Code block", OnCodeBlockClick),
        };
        for (int i = 0; i < actions.Length; i++)
        {
            var (content, tip, click) = actions[i];
            var button = new Button
            {
                Content = new TextBlock { Text = content, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                Width = 30,
                Height = 32,
                Padding = new Thickness(0),
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(button, tip);
            button.Click += click;
            EditingExpandedPanel.Children.Add(button);
            if (i is 2 or 6 or 10) // band ends: text styles, headings, lists
            {
                EditingExpandedPanel.Children.Add(new Border
                {
                    Width = 1,
                    Height = 16,
                    Background = ResolveDividerBrush(EditingExpandedPanel.ActualTheme),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
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

    // Toggles the expanded vs clustered editing bars based on available width. Called on every
    // resize and view-mode change; the cluster dropdowns return automatically when space is tight.
    private void UpdateEditingExpansion()
    {
        if (EditingExpandedPanel is null || EditingClustersPanel is null) return;
        var expanded = CenterBottomBar.ActualWidth >= 620; // strip is ~594px with Copy/Print + bands
        EditingExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        EditingClustersPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
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
        if (t.Contains("```") || t.Contains("](") || t.Contains("**") || t.Contains("【")) return false;
        foreach (var raw in t.Split('\n'))
        {
            var s = raw.TrimStart();
            if (s.StartsWith('#') || s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("> ")
                || s.StartsWith('|') || (s.Length > 2 && char.IsDigit(s[0]) && s[1] == '.' && s[2] == ' '))
                return false; // any structural markdown → not a plain paste
        }
        return true;
    }



    // Shows the upgrade banner only when the Pro trial is nearly up (<= 5 days) or has ended.
    private void UpdateLicenseBanner()
    {
        var st = App.License.State;
        if (st.Edition == Models.Edition.Pro) { LicenseBanner.IsOpen = false; return; }

        if (st.Edition == Models.Edition.Trial)
        {
            // The trial is FULL Pro — never a paywall message, just the remaining export count.
            LicenseBanner.Severity = InfoBarSeverity.Informational;
            LicenseBanner.Title = st.TrialExportsRemaining == 1
                ? "Trial — 1 DOCX export remaining"
                : $"Trial — {st.TrialExportsRemaining} DOCX exports remaining";
            LicenseBanner.Message = "Full Pro, capped at 3 DOCX exports — then back to Free.";
            if (LicenseActionButton is not null) LicenseActionButton.Visibility = Visibility.Collapsed;
        }
        else // Free
        {
            LicenseBanner.Severity = InfoBarSeverity.Warning;
            LicenseBanner.Title = "MarkSmith Free";
            LicenseBanner.Message = "DOCX/PPTX export and automation are Pro features. Start your 3-export trial or upgrade.";
            if (LicenseActionButton is not null)
            {
                LicenseActionButton.Content = "Start 3-export trial";
                LicenseActionButton.Click -= OnStartTrialClick;
                LicenseActionButton.Click += OnStartTrialClick;
                LicenseActionButton.Visibility = Visibility.Visible;
            }
        }
        LicenseBanner.IsOpen = true;
    }

    // Start the 3-export trial straight from the banner (also the trigger point for testing the
    // free -> trial transition without digging into Settings).
    private void OnStartTrialClick(object sender, RoutedEventArgs e)
    {
        var (ok, message) = App.License.StartTrial();
        ViewModel.StatusText = message;
        ViewModel.StatusSeverity = ok ? Models.StatusSeverity.Success : Models.StatusSeverity.Warning;
    }

    private async void OnUpgradeClick(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.StoreUrl)); }
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
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

        var result = await dialog.ShowAsync();

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
            await dialog.ShowAsync();
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
                "Export history, the tour, keyboard shortcuts, Settings — and a ☕ tip jar if MarkSmith saves your day.");
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
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = settingsView,
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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
            status => ApiUrlText.Text = status);
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
        if (App.License.CanAutomate) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = "Hands-free auto-convert is a MarkSmith Pro feature. The content is ready — export it manually, or upgrade in Settings ⚙.";
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
            ShowPdfToast,
            () => RefreshPreviewAsync());
    }

    private async Task OnWatchedFileAsync(string path)
    {
        await _exportCoordinator.OnWatchedFileAsync(
            ViewModel,
            path,
            this,
            () => new OffscreenScope(this),
            ShowPdfToast,
            () => RefreshPreviewAsync());
    }

    private static void ShowPdfToast(string pdfPath) => ShowExportToast("PDF", pdfPath);

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
        if (File.Exists(entry.OutputPath))
        {
            var ext = System.IO.Path.GetExtension(entry.OutputPath).ToLowerInvariant();
            if (ext is ".pdf" or ".docx" or ".pptx" or ".epub" or ".md")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.OutputPath) { UseShellExecute = true });
            }
            else
            {
                ViewModel.StatusText = $"Blocked opening untrusted file type: {ext}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            }
        }
        else
        {
            ViewModel.StatusText = $"File no longer exists: {entry.OutputPath}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    // Document outline (Task 17): scroll the preview to the clicked heading. The anchor is the exact
    // id Markdig rendered on the heading element, so getElementById + scrollIntoView lands on it.
    private async void OnTocItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not Services.TocEntry entry || string.IsNullOrEmpty(entry.Anchor)) return;
        if (PreviewWebView.CoreWebView2 is not { } core) return;
        var js = "(function(){var el=document.getElementById(" +
                 System.Text.Json.JsonSerializer.Serialize(entry.Anchor) +
                 ");if(el){el.scrollIntoView({behavior:'smooth',block:'start'});}})();";
        try { await core.ExecuteScriptAsync(js); } catch { /* best-effort */ }
    }

    private async void OnBrowseWatchFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.WatchFolder = folder.Path;
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
        var coffeeUrl = new Uri("https://buymeacoffee.com/marksmith");
        await Windows.System.Launcher.LaunchUriAsync(coffeeUrl);
    }

    // Batch: convert every .md in a chosen folder (optionally its subfolders too) to a chosen
    // format — PDF/DOCX/PPTX/EPUB — one by one through the same classify → normalize → render
    // pipeline the watched folder uses. Pro (automation) feature.
    // Standardized output for a free user attempting a paid feature: one modal, consistent copy,
    // with the trial + upgrade actions. Returns whether the user is now allowed to proceed.
    private async Task<bool> ShowProGateAsync(Models.FeatureId feature)
    {
        var name = Models.FeatureClassifier.DisplayName(feature);
        var trialUnlocks = feature == Models.FeatureId.DocxExport; // the trial is a single DOCX export
        var dialog = new ContentDialog
        {
            Title = name + " is a MarkSmith Pro feature",
            Content = trialUnlocks
                ? "Your free plan covers Markdown, PDF, HTML and Markdown exports. " + name +
                  " is a Pro feature — start your 3-export trial to try it, or upgrade to unlock it permanently."
                : "Your free plan covers Markdown, PDF, HTML and Markdown exports. " + name +
                  " is a Pro feature — upgrade to unlock it.",
            PrimaryButtonText = trialUnlocks ? "Start 3-export trial" : "Upgrade to Pro",
            SecondaryButtonText = "Upgrade to Pro",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (trialUnlocks)
            {
                var (ok, message) = App.License.StartTrial();
                ViewModel.StatusText = message;
                ViewModel.StatusSeverity = ok ? Models.StatusSeverity.Success : Models.StatusSeverity.Warning;
                return ok;
            }
            return true; // "Upgrade to Pro" opens the store below
        }
        if (result == ContentDialogResult.Secondary)
        {
            try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.StoreUrl)); }
            catch { /* no browser / bad uri — ignore */ }
        }
        return false;
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
        App.License.ResetToFree();
        ViewModel.StatusText = "License reset to Free (hidden command) — free-tier limits are active. Start 3-export trial from Settings to re-test.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
        UpdateLicenseBanner();
    }

    private async void OnBatchConvertClick(object sender, RoutedEventArgs e)
    {
        if (!App.License.CanAutomate)
        {
            ViewModel.StatusText = Models.FeatureClassifier.DisplayName(Models.FeatureId.BatchConvert) + " is a MarkSmith Pro feature. Upgrade in Settings.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            ViewModel.NotifyProFeatureAttempted(Models.FeatureId.BatchConvert);
            return;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        // Count the whole tree so the dialog reflects every file we could reach, and we don't bail
        // on a folder whose .md files all live in subfolders.
        var files = Directory.GetFiles(folder.Path, "*.md", SearchOption.AllDirectories);
        if (files.Length == 0)
        {
            ViewModel.StatusText = $"No .md files found in {folder.Path} or its subfolders.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        // Which format? PDF/DOCX/PPTX/EPUB — the same choice the individual exports offer — plus
        // whether to descend into subfolders.
        var (fmt, recursive) = await AskBatchFormatAsync(files.Length);
        if (fmt is null) return;
        var docxGated = fmt is "docx" && !App.License.CanExportDocx;
        if (docxGated) { ViewModel.StatusText = "Word export is a MarkSmith Pro feature."; ViewModel.StatusSeverity = Models.StatusSeverity.Warning; return; }

        if (fmt == "pdf" && !await EnsurePreviewWebViewAsync())
        {
            ViewModel.StatusText = "Batch failed: the preview engine couldn't start. Try again.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }

        try
        {
            var result = await _exportCoordinator.BatchConvertForApiAsync(
                ViewModel,
                folder.Path,
                fmt,
                null,
                this,
                () => new OffscreenScope(this),
                () => RefreshPreviewAsync(false),
                recursive);

            var propDone = result.GetType().GetProperty("done")?.GetValue(result);
            var propFailed = result.GetType().GetProperty("failed")?.GetValue(result);
            var propFolder = result.GetType().GetProperty("outputFolder")?.GetValue(result);

            int done = propDone is int d ? d : 0;
            int failed = propFailed is int f ? f : 0;
            string outFolder = propFolder is string s ? s : App.Settings.Current.OutputFolder;

            ViewModel.StatusText = failed == 0
                ? $"Batch done: {done} {fmt.ToUpperInvariant()} file{(done == 1 ? "" : "s")} in {outFolder}"
                : $"Batch done: {done} converted, {failed} failed — see {outFolder}";
            ViewModel.StatusSeverity = failed == 0 ? Models.StatusSeverity.Success : Models.StatusSeverity.Warning;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Batch convert failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Ask which format to batch-convert to and whether to include subfolders; returns
    // ("pdf"/"docx"/"pptx"/"epub", recursive) or (null, false) if cancelled.
    private async Task<(string? Format, bool Recursive)> AskBatchFormatAsync(int count)
    {
        var combo = new ComboBox { SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        combo.Items.Add("PDF");
        combo.Items.Add("Word (DOCX)");
        combo.Items.Add("PowerPoint (PPTX)");
        combo.Items.Add("EPUB");
        var recurse = new CheckBox { Content = "Include subfolders", Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Batch convert up to {count} .md file{(count == 1 ? "" : "s")}",
            Content = new StackPanel { Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Every .md file in the folder is converted to your chosen format, using the current Style settings. Tick \u201CInclude subfolders\u201D to also convert nested folders \u2014 their structure is recreated in the output." }, combo, recurse } },
            PrimaryButtonText = "Convert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return (null, false);
        var fmt = combo.SelectedIndex switch { 1 => "docx", 2 => "pptx", 3 => "epub", _ => "pdf" };
        return (fmt, recurse.IsChecked == true);
    }

    private async void OnBrowseBrandLogoClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) ViewModel.BrandLogoPath = file.Path;
    }

    private async void OnBrowseRunningDocClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "AI-notebook" };
        picker.FileTypeChoices.Add("Word document", new List<string> { ".docx" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.RunningDocPath = file.Path;
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
                tcs.SetResult(result);
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
                e.DragUIOverride.Caption = "Drop to load";
            }
        }
    }

    private async void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        // Importer plugins (e.g. Pandoc) widen what "a droppable document" means — see
        // MarkSmith.Core/Plugins/PluginFileReader for where the conversion happens on read.
        var importerExts = App.Plugins.AllImporterExtensions;
        var docs = items.OfType<StorageFile>()
            .Where(f => f.FileType is ".md" or ".markdown" or ".txt"
                || importerExts.Contains(f.FileType.TrimStart('.').ToLowerInvariant()))
            .ToList();
        if (docs.Count == 0) return;

        // Single file: load it into the editor exactly as before.
        if (docs.Count == 1)
        {
            ViewModel.InputFilePath = docs[0].Path;
            ViewModel.UsePasteSource = false;
            return;
        }

        // Multi-file drop (Task 11): stage the dropped documents into a temp folder and run them
        // through the batch converter as one queue — a multi-file drop becomes a single batch job in
        // the current default format, written to the configured output folder.
        await RunMultiFileBatchAsync(docs);
    }

    private async Task RunMultiFileBatchAsync(List<StorageFile> docs)
    {
        var staging = Path.Combine(Path.GetTempPath(), "mk-batch-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var f in docs)
                File.Copy(f.Path, Path.Combine(staging, Path.GetFileName(f.Path)), overwrite: true);

            var format = App.Settings.Current.TargetFormat;
            var outDir = App.Settings.Current.OutputFolder;
            await ViewModel.BatchConvertAsync(staging, outDir, format);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Batch conversion failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Drag-and-drop image embedding: dropping picture files onto the Markdown editor copies them
    // alongside the document (or into the output folder for pasted sources) and inserts a ready-to-
    // render ![alt](path) reference at the caret, so screenshots flow straight into the export.
    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
                e.DragUIOverride.Caption = "Drop to embed image";
        }
    }

    private async void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
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

        var refs = new List<string>();
        foreach (var img in images)
        {
            // Already living next to the document? Reference it in place instead of duplicating.
            var srcDir = System.IO.Path.GetDirectoryName(img.Path);
            if (string.Equals(srcDir, targetDir.Path, StringComparison.OrdinalIgnoreCase))
            {
                refs.Add($"![{System.IO.Path.GetFileNameWithoutExtension(img.Name)}]({img.Path})");
                continue;
            }
            var dest = await img.CopyAsync(targetDir, img.Name, NameCollisionOption.GenerateUniqueName);
            refs.Add($"![{System.IO.Path.GetFileNameWithoutExtension(dest.Name)}]({dest.Path})");
        }

        InsertMarkdown(string.Join("\n", refs) + "\n");
        ViewModel.StatusText = $"Embedded {refs.Count} image(s) into the document.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
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
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        foreach (var ext in App.Plugins.AllImporterExtensions) picker.FileTypeFilter.Add("." + ext);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.InputFilePath = file.Path;
            AutoCollapseLeftPane();
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseLogoClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.BrandLogoPath = file.Path;
        }
    }

    private async void OnBrowseFontClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".ttf");
        picker.FileTypeFilter.Add(".otf");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.CustomFontPath = file.Path;
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
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.OutputFolder = folder.Path;
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

        var result = await dialog.ShowAsync();
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

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.LastOutputPath is { } path && File.Exists(path))
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".pdf" or ".docx" or ".pptx" or ".epub" or ".md")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
    }

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

        // Preview zoom: restore the persisted factor, install the Ctrl+wheel listener, and re-apply
        // the CSS zoom after every navigation (NavigateToString rebuilds the DOM, dropping the style).
        ApplyPreviewZoom(App.Settings.Current.PreviewZoom, persist: false);
        await SetupPreviewZoomAsync();
        PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => ApplyPreviewCssZoom(_lastPreviewZoom);

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
            if (type == "preview-zoom")
            {
                var delta = root.TryGetProperty("delta", out var dProp) ? dProp.GetDouble() : 0;
                if (delta != 0) ApplyPreviewZoom(_lastPreviewZoom + (delta < 0 ? PreviewZoomStep : -PreviewZoomStep), persist: true);
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

            var picker = new FileSavePicker { SuggestedFileName = "diagram" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant() + " image", new List<string> { "." + format });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (format == "svg")
                await File.WriteAllTextAsync(file.Path, data);
            else
            {
                var b64 = data.Contains(',') ? data[(data.IndexOf(',') + 1)..] : data;
                await File.WriteAllBytesAsync(file.Path, Convert.FromBase64String(b64));
            }
            ViewModel.StatusText = $"Diagram saved: {file.Path}";
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
    private static void MapAssetHost(Microsoft.Web.WebView2.Core.CoreWebView2 core)
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

    // ---- IWebRenderHost / IUiPrompts: the portable seam MainViewModel and MermaidHarvestService
    // (both in MarkSmith.Core) drive PDF export and mermaid harvesting through, instead of reaching
    // into a WebView2 control directly. See MarkSmith.Core/Rendering/IWebRenderHost.cs.

    public Task<bool> EnsureReadyAsync() => EnsurePreviewWebViewAsync();

    public Task NavigateToStringAsync(string html)
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
        return tcs.Task;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        return await core.ExecuteScriptAsync(javaScript);
    }

    public async Task<bool> PrintToPdfAsync(string outputPath, Services.PdfPageSetup setup)
    {
        var core = PreviewWebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 is not initialized.");
        var hasHeaderFooter = !string.IsNullOrEmpty(setup.HeaderTemplate) || !string.IsNullOrEmpty(setup.FooterTemplate);

        // Fast path — no header/footer: use the native PrintToPdfAsync (zero-margin, edge-to-edge
        // theme backgrounds), exactly as before Task 10.
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

        // Header/footer path (Task 10): WebView2's native print settings only expose HeaderTitle /
        // FooterUri, not custom templates — so drive Chromium's Page.printToPDF over the DevTools
        // protocol instead. It accepts headerTemplate / footerTemplate HTML with the auto-substituting
        // pageNumber / totalPages / date / title spans that PdfExportService.BuildChromiumTemplate emits.
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

    // The mermaid render page must not be clobbered by the live preview's debounced auto-refresh
    // while a harvest is in flight; restore the live preview once the harvest ends. Exactly the
    // wrapper the three harvest methods used to inline before their bodies moved to
    // MarkSmith.Core/Services/MermaidHarvestService.cs.
    public Task BeginHarvestAsync()
    {
        _mermaidHarvestActive = true;
        _previewDebounce.Stop();
        return Task.CompletedTask;
    }

    public async Task EndHarvestAsync()
    {
        _mermaidHarvestActive = false;
        await RefreshPreviewAsync(false);
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

        // Same classify/normalize step the exports run, so the preview shows what will ship
        // (and the detection badge appears for manual paste and file input, not just auto-ingest).
        var html = vm.BuildPreviewHtml(vm.PrepareMarkdown(markdown), interactive: true);
        _lastLiveCanvasMd = markdown; // the fresh page will show this — keep the live path's dedupe honest

        if (vm.IsDebugModeEnabled)
        {
            try
            {
                var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "DebugLogs");
                Directory.CreateDirectory(logsDir);
                var logFile = Path.Combine(logsDir, $"Preview_Session_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("n").Substring(0, 4)}.log");
                
                var prompt = "Tell me everything wrong with the way we displayed the MD format in this HTML and how to resolve it:\n\n";
                File.WriteAllText(logFile, prompt + html);
                _sessionLogFiles.Add(logFile);
            }
            catch { }
        }

        // (Blur-while-rendering used to be injected here as an ms-loading body class; removed so
        // the render happens in plain sight.)

        // ISS-003: a re-render (NavigateToString) resets window.scrollY to 0. Stash the preview's
        // current scroll fraction so ApplyPendingPreviewScroll (wired to NavigationCompleted) puts
        // the reader back where they were. Only capture when nothing is already pending — a pending
        // value set by a tab switch (editor->preview hand-off) takes precedence and must survive.
        if (_pendingPreviewScrollFraction is null)
            _pendingPreviewScrollFraction = await CapturePreviewScrollFractionAsync();

        // Restore the scroll position during the initial parse — before the first paint — so the
        // reader never sees the page land at the top and then jump back down. NavigateToString resets
        // scrollY to 0; this inline script runs synchronously as the fresh document loads and puts us
        // back where we were while the content is still blurred (heavy) or mid-repaint (light).
        // ApplyPendingPreviewScroll (NavigationCompleted) still runs afterwards to fine-tune once
        // async layout (mermaid / images) settles.
        if (_pendingPreviewScrollFraction is { } frac)
        {
            var f = Math.Clamp(frac, 0.0, 1.0).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            html = html.Replace("</body>",
                "<script>(function(){var m=document.documentElement.scrollHeight-window.innerHeight;" +
                $"if(m>0){{window.scrollTo(0,{f}*m);}}}})();</script></body>");
        }

        PreviewWebView.CoreWebView2.NavigateToString(html);
    }

    // ISS-004 "watch it generate": full refreshes re-navigate the page, which both tears any open
    // portal out of the DOM and repaints under the reader — so typing-sized updates re-render the
    // Markdown and swap ONLY the #canvas contents in place. The portal is a sibling of #canvas on
    // <body>, so it — and the user's caret inside it — survive untouched while the preview updates
    // live behind the shape. The template brackets the canvas contents with ms-canvas-start/end
    // markers so extraction never has to parse nested divs.
    //
    // Returns false when the swap can't happen here — no page yet, markers missing, or the current
    // page never loaded a renderer the new content needs (KaTeX / highlight.js are included
    // per-navigation on demand; mermaid ships on every page) — so the caller can fall back to a
    // real refresh. Pass null to render whatever the current preview source resolves to.
    private async Task<bool> UpdatePreviewCanvasLiveAsync(string? markdown = null)
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null || _mermaidHarvestActive) return false;

        markdown ??= await ResolvePreviewMarkdownAsync();
        if (markdown == _lastLiveCanvasMd) return true; // canvas already shows exactly this source

        var vm = ViewModel;
        // Canvas-only render: produces just the inner HTML (attribution + TOC + body + footer)
        // without the ~50 KB page shell. Returns null for focused-diagram docs that need a full
        // navigation (dedicated viewer page with zoom/pan controls).
        var inner = vm.BuildPreviewCanvasHtml(vm.PrepareMarkdown(markdown));
        if (inner is null) return false;

        var js = "(function(){var c=document.getElementById('canvas');if(!c)return 'nav';" +
                 "var h=" + System.Text.Json.JsonSerializer.Serialize(inner) + ";" +
                 // First math/code block since the last navigation: its renderer isn't on this
                 // page (BuildExtraHead includes them on demand) — ask for a real refresh.
                 "if(h.indexOf('class=\"math\"')>=0&&!window.__msRenderMath)return 'nav';" +
                 "if(h.indexOf('language-')>=0&&!window.hljs)return 'nav';" +
                 "c.innerHTML=h;" +
                 // Fresh fences/spans arrive unrendered — best-effort re-runs, ignore if absent.
                 "try{if(window.mermaid&&window.mermaid.run){window.mermaid.run();}}catch(x){}" +
                 "try{if(window.__msRenderMath){window.__msRenderMath(c);}}catch(x){}" +
                 "try{if(window.hljs){c.querySelectorAll('pre code').forEach(function(el){window.hljs.highlightElement(el);});}}catch(x){}" +
                 "return 'ok';})();";
        string result;
        try { result = await core.ExecuteScriptAsync(js); } catch { return false; }
        if (result != "\"ok\"") return false;
        _lastLiveCanvasMd = markdown;
        return true;
    }

    // The preview's markdown source, in priority order — shared by the full refresh and the live
    // in-place canvas swap so both always render the same document.
    private async Task<string> ResolvePreviewMarkdownAsync()
    {
        var vm = ViewModel;
        if (vm.UsePasteSource) return vm.PastedMarkdown;
        if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            return await Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath);
        return "# MarkSmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";
    }

    // Read the preview's current vertical scroll fraction (0..1), or null when the page isn't ready
    // or the read fails — callers treat null as "nothing to restore". Mirror of the fraction math in
    // CapturePreviewScrollAndApplyToEditorAsync, but returns the value instead of moving the editor.
    private async Task<double?> CapturePreviewScrollFractionAsync()
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return null;
        string result;
        try
        {
            result = await core.ExecuteScriptAsync(
                "(function(){var max=document.documentElement.scrollHeight-window.innerHeight;" +
                "return max>0?(window.scrollY/max):0;})();");
        }
        catch { return null; }

        if (!double.TryParse(result, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frac)) return null;
        return Math.Clamp(frac, 0.0, 1.0);
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
            ViewModel.StatusText = "Nothing to save: open a file first (Ctrl+O). Pasted content lives in the editor and is exported, not saved.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }
        try
        {
            // Restore any stashed %% position metadata so the saved file keeps studio layouts.
            var toSave = Mermaid.Sync.MermaidSpatialMetadataService.Reinject(
                ViewModel.CurrentMarkdown ?? "", _mermaidSpatialStash);
            await File.WriteAllTextAsync(path, toSave);
            ViewModel.StatusText = $"Saved changes to {path}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Save failed: {ex.Message}";
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

    private async Task ShowShortcutsCheatsheetAsync()
    {
        var shortcuts = new (string Keys, string Action)[]
        {
            ("Ctrl + O", "Open a Markdown file"),
            ("Ctrl + E", "Generate PDF"),
            ("Ctrl + Shift + P", "Instant PDF export"),
            ("Ctrl + Shift + E", "Instant DOCX export"),
            ("Ctrl + Shift + D", "Export DOCX"),
            ("Ctrl + Shift + T", "Export PPTX"),
            ("Ctrl + Shift + M", "Open the Visual Mermaid Studio"),
            ("Ctrl + ,", "Open Settings"),
            ("Ctrl + K", "Command palette"),
            ("Ctrl + S", "Save edits back to the source file"),
            ("Ctrl + F", "Find in the editor"),
            ("Ctrl + Alt + T", "Toggle debug mode"),
            ("Ctrl + Alt + X", "Portal focus: blur / unblur the preview behind the aperture"),
            ("F1", "Show this cheatsheet"),
        };

        var rows = new StackPanel { Spacing = 10 };
        foreach (var (keys, action) in shortcuts)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyBox = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = keys, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12.5 },
            };
            Grid.SetColumn(keyBox, 0);

            var desc = new TextBlock { Text = action, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            Grid.SetColumn(desc, 1);

            row.Children.Add(keyBox);
            row.Children.Add(desc);
            rows.Children.Add(row);
        }

        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = rows,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    // ---- Command palette (Ctrl+K): fuzzy search across actions, themes, and recent files ----

    private sealed record PaletteCommand(string Label, string Category, Func<Task> Run);

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var cmds = new List<PaletteCommand>
        {
            new("Generate PDF", "Action", () => ViewModel.ConvertToPdfAsync()),
            new("Export DOCX", "Action", () => ViewModel.ConvertToDocxAsync()),
            new("Export PPTX", "Action", () => ViewModel.ConvertToPptxAsync()),
            new("Export all formats", "Action", () => ViewModel.ExportAllAsync()),
            new("Open a Markdown file", "Action", () => { OnBrowseFileClick(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Open Diagram Studio", "Action", () => { OnOpenMermaidStudioClick(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Open Settings", "Action", () => { OnSettingsClick(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("Take the welcome tour", "Action", ShowWelcomeTourAsync),
            new("Show keyboard shortcuts", "Action", ShowShortcutsCheatsheetAsync),
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

    private static bool FuzzyMatch(string text, string query)
    {
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        // Subsequence match: every query character appears in order (case-insensitive).
        var qi = 0;
        foreach (var ch in text)
        {
            if (qi < query.Length && char.ToLowerInvariant(ch) == char.ToLowerInvariant(query[qi])) qi++;
        }
        return qi == query.Length;
    }

    private async Task ShowCommandPaletteAsync()
    {
        var commands = BuildPaletteCommands();

        var search = new TextBox { PlaceholderText = "Type a command, theme, or recent file\u2026", FontSize = 14 };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 340, IsItemClickEnabled = true };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<StackPanel Orientation='Horizontal' Spacing='10' Padding='0,2'>" +
            "<TextBlock Text='{Binding Label}' FontWeight='SemiBold' FontSize='13'/>" +
            "<TextBlock Text='{Binding Category}' Opacity='0.5' FontSize='11' VerticalAlignment='Center'/>" +
            "</StackPanel></DataTemplate>");

        void Refresh()
        {
            var q = search.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? commands
                : commands.Where(c => FuzzyMatch(c.Label, q) || FuzzyMatch(c.Category, q)).ToList();
            list.ItemsSource = filtered;
            if (filtered.Count > 0) list.SelectedIndex = 0;
        }

        var panel = new StackPanel { Spacing = 10, Width = 480 };
        panel.Children.Add(search);
        panel.Children.Add(list);

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
                e.Handled = true;
            }
        };
        list.ItemClick += (s, e) => { if (e.ClickedItem is PaletteCommand c) { chosen = c; dialog.Hide(); } };

        Refresh();
        search.Focus(FocusState.Programmatic);

        await dialog.ShowAsync();

        if (chosen is not null) await chosen.Run();
    }

    private async Task ShowDebugLogsDialogAndExitAsync()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var file in _sessionLogFiles)
        {
            if (File.Exists(file))
            {
                sb.AppendLine($"--- LOG FILE: {Path.GetFileName(file)} ---");
                sb.AppendLine(File.ReadAllText(file));
                sb.AppendLine();
            }
        }

        var textBox = new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MaxHeight = 400
        };
        ScrollViewer.SetVerticalScrollBarVisibility(textBox, ScrollBarVisibility.Auto);

        var dialog = new ContentDialog
        {
            Title = "Debug Mode - Session Logs",
            Content = textBox,
            CloseButtonText = "Close and Exit",
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
        
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

    private async void OnExportMarkdownClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToMarkdownAsync();
    }

    // Primary action of the export SplitButton: generate a Word document — ISS-019 made .docx
    // the default export format (was PDF). The remaining formats live in the flyout and reuse
    // the individual OnConvert*Click handlers above. SplitButton.Click raises
    // SplitButtonClickEventArgs, so it needs its own handler signature.
    private async void OnPrimaryExportClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        await ViewModel.ConvertToDocxAsync();
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
        var showEditor = mode is ViewMode.Code or ViewMode.Split;
        var showPreview = mode is ViewMode.Preview or ViewMode.Split;

        // Sync-scroll bookkeeping only matters for the pure preview tab transitions.
        if (mode == ViewMode.Preview) _pendingPreviewScrollFraction = GetEditorScrollFraction();
        if (mode == ViewMode.Code) _ = CapturePreviewScrollAndApplyToEditorAsync();

        if (PastePanel != null) PastePanel.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewCard != null) PreviewCard.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        if (PreviewWidthContainer != null) PreviewWidthContainer.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
        if (SplitViewSplitter != null) SplitViewSplitter.Visibility = mode == ViewMode.Split ? Visibility.Visible : Visibility.Collapsed;

        // Column widths: give all the space to the visible pane(s); split shares it.
        if (SplitLeftCol != null) SplitLeftCol.Width = mode == ViewMode.Preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        if (SplitRightCol != null) SplitRightCol.Width = mode == ViewMode.Code ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

        // ISS-017: Split view squeezes the editor + preview between the two step panels, so entering
        // Split auto-collapses them into a full-canvas workspace and leaving restores them. Reuse the
        // existing focus-mode machinery: syncing the toggle fires its Checked/Unchecked handler, which
        // runs ApplyFocusMode (and saves/restores the pane widths). Setting the same value twice is a
        // no-op, so we never re-save already-collapsed widths.
        if (FocusModeToggle != null)
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
        if (_pendingPreviewScrollFraction is not { } frac) return;
        _pendingPreviewScrollFraction = null;
        if (_mermaidHarvestActive) return;
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;
        var f = Math.Clamp(frac, 0.0, 1.0).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        _ = core.ExecuteScriptAsync(
            "(function(){var max=document.documentElement.scrollHeight-window.innerHeight;" +
            $"if(max>0){{window.scrollTo(0,{f}*max);}}}})();");
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
            if (text[i] == '\n') { line++; lastNewline = i; }
        }
        var col = start - lastNewline;
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

    private void ShowFindBar()
    {
        if (FindBar is null) return;
        FindBar.Visibility = Visibility.Visible;
        SyncFindBarSpacer();
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
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

    private void OnFindTextChanged(object sender, TextChangedEventArgs e) => RecomputeFindMatches();

    private void OnFindNextClick(object sender, RoutedEventArgs e) => FindNext();

    private void OnFindPrevClick(object sender, RoutedEventArgs e) => FindPrev();

    // The comparison used by find/replace — the "Aa" checkbox toggles case sensitivity.
    private StringComparison FindComparison => MatchCaseCheck?.IsChecked == true
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    // Rebuild the match list for the current query and refresh the "n/m" readout.
    private void RecomputeFindMatches()
    {
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
        UpdateFindCount();
    }

    private void OnMatchCaseChanged(object sender, RoutedEventArgs e) => RecomputeFindMatches();

    private void UpdateFindCount()
    {
        if (FindCountText is null) return;
        FindCountText.Text = _findMatches.Count == 0
            ? (string.IsNullOrEmpty(FindTextBox?.Text) ? string.Empty : "No matches")
            : $"{_findMatchIndex + 1}/{_findMatches.Count}";
    }

    private void FindNext()
    {
        if (_findMatches.Count == 0) { UpdateFindCount(); return; }
        _findMatchIndex = (_findMatchIndex + 1) % _findMatches.Count;
        SelectFindMatch();
    }

    private void FindPrev()
    {
        if (_findMatches.Count == 0) { UpdateFindCount(); return; }
        _findMatchIndex = (_findMatchIndex - 1 + _findMatches.Count) % _findMatches.Count;
        SelectFindMatch();
    }

    // Highlight the current match in the editor and scroll it into view. Selecting text on a focused
    // TextBox makes WinUI bring the caret into view, which gives us scroll-into-view for free.
    private void SelectFindMatch()
    {
        if (_findMatchIndex < 0 || _findMatchIndex >= _findMatches.Count) return;
        var start = _findMatches[_findMatchIndex];
        var len = (FindTextBox?.Text ?? string.Empty).Length;
        PasteTextBox.Focus(FocusState.Programmatic);
        PasteTextBox.Select(start, len);
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

    // Replace the currently-highlighted match (if the selection is one), then jump to the next match.
    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox?.Text ?? string.Empty;
        if (query.Length == 0) return;
        var replacement = ReplaceTextBox?.Text ?? string.Empty;

        if (PasteTextBox.SelectionLength == query.Length)
        {
            PasteTextBox.SelectedText = replacement; // swaps the selected match in place
        }

        // Continue searching from just after the caret, wrapping to the top if needed.
        var text = PasteTextBox.Text ?? string.Empty;
        var cmp = FindComparison;
        var from = Math.Clamp(PasteTextBox.SelectionStart, 0, text.Length);
        var next = text.IndexOf(query, from, cmp);
        if (next < 0) next = text.IndexOf(query, 0, cmp);

        RecomputeFindMatches();
        if (next >= 0)
        {
            _findMatchIndex = _findMatches.IndexOf(next);
            SelectFindMatch();
        }
    }

    // Replace every match in the document in one pass.
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
            ViewModel.StatusText = $"Replaced {count} occurrence{(count == 1 ? "" : "s")}.";
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
        ShowFindBar();
        ReplaceTextBox?.Focus(FocusState.Programmatic);
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
        if (EditorZoomText is not null)
            EditorZoomText.Text = $"{(int)Math.Round(clamped / EditorFontBase * 100.0)}%";
        if (persist)
        {
            App.Settings.Current.EditorFontSize = clamped;
            App.Settings.Save();
        }
    }

    // ---- Word wrap + line numbers ----

    // Word wrap is a persisted editor display setting. When wrapping is off the editor scrolls
    // horizontally and shows a line-number gutter (wrapping would break line-number alignment).
    private void OnWordWrapToggled(object sender, RoutedEventArgs e)
    {
        if (_initializingWordWrap) return;
        ApplyWordWrap(WordWrapToggle?.IsChecked == true, persist: true);
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
    private void UpdateCenterBottomBar()
    {
        if (CenterBottomBar is null) return;
        var portalOn = LookingGlassToggle?.IsChecked == true;
        if (PortalControlsRow is not null)
            PortalControlsRow.Visibility = portalOn ? Visibility.Visible : Visibility.Collapsed;
        if (EditingClustersPanel is not null)
            EditingClustersPanel.Visibility = portalOn || _viewMode is ViewMode.Code or ViewMode.Split
                ? Visibility.Visible : Visibility.Collapsed;
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
        // The line-number gutter is ALWAYS visible and counts logical lines, so word wrap no
        // longer hides it (the old wrap-dependent visibility made a mystery "1" panel pop in and
        // out and read as broken when the text was a single logical line).
        if (persist)
        {
            App.Settings.Current.EditorWordWrap = wrap;
            App.Settings.Save();
        }
    }

    // ---- Left-pane hover-drawer ----

    // Expand the Source/Files pane back to its pre-collapse width (hovering the drawer tab).
    private void ExpandLeftPane()
    {
        if (!_leftPaneCollapsed) return;
        _leftPaneCollapsed = false;
        LeftPaneCol.Width = new GridLength(_leftPaneExpandedWidth);
        LeftPane.Visibility = Visibility.Visible;
        if (LeftDrawerTab is not null) LeftDrawerTab.Visibility = Visibility.Collapsed;
    }

    // Tuck the pane away to a slim tab (leaving the pane with the mouse, or a short beat after a
    // document is selected). The custom splitter width is preserved for the next expand.
    private void CollapseLeftPane()
    {
        if (_leftPaneCollapsed) return;
        // RULE: when the editor is blank the left pane is FORCIBLY expanded (the user needs the
        // file picker because there is nothing to work on yet) — never tuck it away.
        if (string.IsNullOrWhiteSpace(PasteTextBox?.Text)) return;
        _leftPaneCollapsed = true;
        if (LeftPaneCol.ActualWidth > 28) _leftPaneExpandedWidth = LeftPaneCol.ActualWidth;
        LeftPaneCol.Width = new GridLength(28);
        LeftPane.Visibility = Visibility.Collapsed;
        if (LeftDrawerTab is not null) LeftDrawerTab.Visibility = Visibility.Visible;
    }

    // Collapse on a short delay so the selection click finishes before the pane slides away.
    private void AutoCollapseLeftPane()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(350);
            CollapseLeftPane();
        });
    }

    private void OnLeftDrawerTabPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => ExpandLeftPane();

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
        CollapseLeftPane();
    }


    // ---- Markdown lint ----

    // Re-run the linter and refresh the issue-count chip and the flyout's issue list.
    private void UpdateLintIndicator()
    {
        _lintIssues = Services.MarkdownLintService.Analyze(PasteTextBox?.Text);
        if (LintCountText is not null)
            LintCountText.Text = _lintIssues.Count == 0 ? "No issues" : $"{_lintIssues.Count} issue{(_lintIssues.Count == 1 ? "" : "s")}";
        if (LintList is not null) LintList.ItemsSource = _lintIssues.ToList();
    }

    // Clicking an issue jumps the editor caret to that line AND drops a red homing radar beacon on
    // the corresponding element in the live preview (ISS-012), so the user's eye lands on both.
    private void OnLintItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Services.MarkdownLintService.LintIssue issue)
        {
            GoToLine(issue.Line);
            TriggerIssueRadarBeacon(issue.Line);
            LintFlyout?.Hide();
        }
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
    private void GoToLine(int lineNo)
    {
        var text = PasteTextBox.Text ?? string.Empty;
        var offset = 0;
        var line = 1;
        while (line < lineNo && offset < text.Length)
        {
            if (text[offset] == '\n') line++;
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
        var text = tb.Text ?? string.Empty;
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
        while (start > 0 && text[start - 1] != '\n') start--;
        var end = pos;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end);
    }

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

    private void OnMoveLineUpAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        MoveSelectedLines(-1);
        args.Handled = true;
    }

    private void OnMoveLineDownAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        MoveSelectedLines(1);
        args.Handled = true;
    }

    private void OnDuplicateLineAcceleratorInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        DuplicateCurrentLines();
        args.Handled = true;
    }

    private static int CountNewlines(string s, int upTo)
    {
        var n = 0;
        var end = Math.Min(upTo, s.Length);
        for (var i = 0; i < end; i++) if (s[i] == '\n') n++;
        return n;
    }

    // Move the (possibly multi-line) selection up (-1) or down (+1) one line, keeping it selected.
    private void MoveSelectedLines(int direction)
    {
        var tb = PasteTextBox;
        var text = tb.Text ?? string.Empty;
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
        var text = tb.Text ?? string.Empty;
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
        var norm = text.Replace("\r\n", "\n");
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
    // behind an open aperture blurs (focus WITH blur) or stays sharp (focus WITHOUT blur). The
    // choice is persisted and pushed straight into the live page (__portalSetBlur); when no
    // portal is open the page just stores it for the next aperture.
    private void OnTogglePortalBlurInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.PortalFocusBlur = !ViewModel.PortalFocusBlur;
        PushPortalBlurToPage(ViewModel.PortalFocusBlur);
        args.Handled = true;
    }

    // Pushes the portal focus-blur state into the live preview page and reports it in the status
    // bar. Called from both the Ctrl+Alt+X accelerator and the portal-row blur toggle button.
    private void PushPortalBlurToPage(bool on)
    {
        var js = "if (window.__portalSetBlur) { window.__portalSetBlur(" + (on ? "true" : "false") + "); }";
        _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(js);
        ViewModel.StatusText = on
            ? "Portal focus: preview blurred behind the aperture — Ctrl+Alt+X for sharp."
            : "Portal focus: preview sharp behind the aperture — Ctrl+Alt+X for blur.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Informational;
    }

    private void OnPortalBlurToggled(object sender, RoutedEventArgs e)
    {
        // The TwoWay binding has already written ViewModel.PortalFocusBlur; push it to the page.
        PushPortalBlurToPage(ViewModel.PortalFocusBlur);
    }

    private void ApplyFocusMode(bool focus)
    {
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
            LeftPaneCol.MinWidth = _savedLeftPaneMinWidth > 0 ? _savedLeftPaneMinWidth : 250;
            RightPaneCol.MinWidth = _savedRightPaneMinWidth > 0 ? _savedRightPaneMinWidth : 250;
            if (LeftPane != null) LeftPane.Visibility = Visibility.Visible;
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

    private const double PreviewZoomMin = 0.25;
    private const double PreviewZoomMax = 4.0;
    private const double PreviewZoomStep = 0.1;

    private void OnPreviewZoomInClick(object sender, RoutedEventArgs e) => ApplyPreviewZoom(_lastPreviewZoom + PreviewZoomStep, persist: true);

    private void OnPreviewZoomOutClick(object sender, RoutedEventArgs e) => ApplyPreviewZoom(_lastPreviewZoom - PreviewZoomStep, persist: true);

    private void ApplyPreviewZoom(double factor, bool persist)
    {
        var clamped = Math.Clamp(factor, PreviewZoomMin, PreviewZoomMax);
        _lastPreviewZoom = clamped;
        ApplyPreviewCssZoom(clamped);
        if (PreviewZoomText is not null)
        {
            var percent = (int)Math.Round(clamped * 100.0);
            PreviewZoomText.Text = $"{percent}%";
            // Live zoom diagnostics: the status-bar readout always carries the current level
            // in its tooltip, so hovering shows the exact preview zoom ("Preview Zoom: 100%").
            ToolTipService.SetToolTip(PreviewZoomText, $"Preview Zoom: {percent}%");
        }
        if (persist)
        {
            App.Settings.Current.PreviewZoom = clamped;
            App.Settings.Save();
        }
    }

    // Apply the zoom as a CSS zoom on the root element. Best-effort: the CoreWebView2 may not be
    // ready on the very first call (the value is re-applied after every navigation regardless).
    private void ApplyPreviewCssZoom(double factor)
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;
        var f = factor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        _ = core.ExecuteScriptAsync($"document.documentElement.style.zoom = '{f}';");
    }

    // Install the Ctrl+wheel -> "preview-zoom" bridge once. Native browser zoom is disabled so the
    // wheel and the buttons both flow through ApplyPreviewZoom (one source of truth, no compounding).
    private async Task SetupPreviewZoomAsync()
    {
        var core = PreviewWebView.CoreWebView2;
        if (core is null) return;
        try { core.Settings.IsZoomControlEnabled = false; } catch { /* setting unavailable */ }
        try
        {
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "window.addEventListener('wheel', function(e){" +
                "if(e.ctrlKey){e.preventDefault();" +
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
            var content = File.ReadAllText(RecoveryPath);
            if (string.IsNullOrWhiteSpace(content)) { File.Delete(RecoveryPath); return; }

            var dialog = new ContentDialog
            {
                Title = "Recover unsaved document",
                Content = "MarkSmith found an unsaved document from your last session. Would you like to restore it?",
                PrimaryButtonText = "Restore",
                CloseButtonText = "Discard",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                ViewModel.BreakUndoBurst(); // restoring the draft must undo as its own step
                ViewModel.CurrentMarkdown = content;
                ViewModel.StatusText = "Unsaved document restored from your last session.";
                ViewModel.StatusSeverity = Models.StatusSeverity.Success;
                await RefreshPreviewAsync(heavy: true);
            }
            File.Delete(RecoveryPath);
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
        if (string.IsNullOrEmpty(suffix) && (prefix.TrimEnd() == "#" || prefix.TrimEnd() == "##" || prefix.TrimEnd() == "###" || prefix.TrimEnd() == "####" || prefix.TrimEnd() == "-" || prefix.TrimEnd() == "1." || prefix.TrimEnd() == "- []" || prefix.TrimEnd() == ">"))
        {
            string[] lines = coreText.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0)
                {
                    if (line.EndsWith("\r"))
                        lines[i] = prefix + line.Substring(0, line.Length - 1) + "\r";
                    else
                        lines[i] = prefix + line;
                }
            }
            string formattedCore = string.Join("\n", lines);
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
        // Pro mode: the classic bare fence straight into the editor, no modal.
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

    private void OnBlockquoteClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("> ", "");
    }

    private async void OnInsertWorkflowClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::workflow\n- Step 1\n- Step 2\n- Step 3\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl("One step per line:", "Step 1\nStep 2\nStep 3");
        if (await ShowInsertDialogAsync("Insert workflow", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Workflow(control.Lines));
    }

    private async void OnInsertTimelineClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::timeline\n- 2020: Started\n- 2023: Progress\n- 2026: Done\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl("One entry per line (year: label):", "2020: Started\n2023: Progress\n2026: Done");
        if (await ShowInsertDialogAsync("Insert timeline", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Timeline(control.Lines));
    }

    private async void OnInsertSmartArtClick(object sender, RoutedEventArgs e)
    {
        var control = new Views.SmartArtInsertControl();
        if (await ShowInsertDialogAsync("Insert SmartArt Diagram", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(control.GeneratedSnippet);
    }

    private async void OnInsertTabsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::tabs\n=== Tab 1\nContent 1\n=== Tab 2\nContent 2\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl("One tab title per line:", "Tab 1\nTab 2");
        if (await ShowInsertDialogAsync("Insert tab group", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Tabs(control.Lines));
    }

    private async void OnInsertColumnsClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::columns count=\"2\"\nColumn 1 content\n===\nColumn 2 content\n:::\n");
            return;
        }

        var control = new Views.NumbersInsertControl(("Columns", 2, 2, 4));
        if (await ShowInsertDialogAsync("Insert multi-column section", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Columns(control.Value(0)));
    }

    private async void OnInsertCanvasClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::canvas\n<svg viewBox=\"0 0 100 100\" width=\"200\" height=\"200\">\n  <circle cx=\"50\" cy=\"50\" r=\"40\" stroke=\"black\" stroke-width=\"3\" fill=\"red\" />\n</svg>\n:::\n");
            return;
        }

        var control = new Views.NumbersInsertControl(("Width", 200, 10, 4000), ("Height", 200, 10, 4000));
        if (await ShowInsertDialogAsync("Insert drawing canvas", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Canvas(control.Value(0), control.Value(1)));
    }

    private void OnBulletListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("- ", "");
    }

    private void OnNumberedListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("1. ", "");
    }

    private void OnTaskListClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("- [ ] ", "");
    }

    private void OnH1Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("# ", "");
    }

    private void OnH2Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("## ", "");
    }

    private void OnH3Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("### ", "");
    }

        private void OnH4Click(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("#### ", "");
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
            return await dialog.ShowAsync();
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
        // Pro mode (Settings ▸ General): the classic one-keystroke placeholder, no modal.
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("![", "](image.png)");
            return;
        }

        // Default experience: an interactive picker — drag & drop, browse, or paste a URL.
        var control = new Views.ImageInsertControl();
        ContentDialog? dialog = null;
        control.ImagePicked += source =>
        {
            dialog?.Hide();
            InsertImageMarkdown(source);
        };
        await ShowInsertDialogAsync("Insert image", control, primaryButtonText: null, configure: d => dialog = d);
    }

    // Turns a picked source (local path or URL) into markdown image syntax. Local paths get
    // forward slashes so the WebView2 preview and the DOCX image embedder resolve them, and the
    // alt text comes from the file name; URLs keep a generic alt.
    private void InsertImageMarkdown(string source)
    {
        string src, alt;
        if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            src = source;
            alt = "image";
        }
        else
        {
            src = source.Replace('\\', '/');
            alt = System.IO.Path.GetFileNameWithoutExtension(source);
            if (string.IsNullOrWhiteSpace(alt)) alt = "image";
        }
        InsertMarkdown($"\n![{alt}]({src})\n");
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
        InsertMarkdown(Services.InsertSnippetBuilder.Table(control.Rows, control.Columns, control.IncludeHeaderRow));
    }

    private async void OnInsertEmbedClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::embed provider=\"youtube\" src=\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"\n:::\n");
            return;
        }

        var control = new Views.EmbedInsertControl();
        if (await ShowInsertDialogAsync("Insert web embed", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Embed(control.Provider, control.Url));
    }

    private async void OnInsertChartClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::chart type=\"bar\"\nQ1,10\nQ2,25\nQ3,15\n:::\n");
            return;
        }

        var control = new Views.TypeAndLinesInsertControl(
            "Chart type", new[] { "bar", "line", "pie" }, "bar",
            "One data point per line (label,value):", "Q1,10\nQ2,25\nQ3,15");
        if (await ShowInsertDialogAsync("Insert chart", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Chart(control.SelectedType, control.Lines));
    }

    private async void OnInsertDatagridClick(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Current.ProMode)
        {
            InsertMarkdown("\n:::datagrid\nlabel,value\nQ1,10\nQ2,25\n:::\n");
            return;
        }

        var control = new Views.LinesInsertControl(
            "First line = column headers, then one row per line (comma-separated):",
            "label,value\nQ1,10\nQ2,25");
        if (await ShowInsertDialogAsync("Insert data grid", control) != ContentDialogResult.Primary) return;
        InsertMarkdown(Services.InsertSnippetBuilder.Datagrid(control.Lines));
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
        InsertMarkdown(Services.InsertSnippetBuilder.References(control.Id, control.Author, control.Title, control.Year));
    }

    private void OnInsertAiContextClick(object sender, RoutedEventArgs e)
    {
        InsertMarkdown("\n:::ai-context\npromptHash: abc123\nmodel: Gemini Pro\ntimestamp: " + DateTime.Now.ToString("yyyy-MM-dd") + "\n:::\n");
    }

    // ---- D1: Reverse Document Import (DOCX / PDF → Markdown) ------------------------------------

    private async void OnImportDocumentClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".pdf");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var importer = new Services.ReverseImportService();
            Services.ReverseImportResult result;
            var ext = Path.GetExtension(file.Path).ToLowerInvariant();

            if (ext == ".pdf")
                result = await importer.ImportFromPdfAsync(file.Path);
            else
                result = await importer.ImportFromDocxAsync(file.Path);

            if (string.IsNullOrWhiteSpace(result.Markdown))
            {
                ViewModel.StatusText = result.Warning ?? "No content could be extracted from that document.";
                return;
            }

            // Load the extracted Markdown into the editor.
            ViewModel.PastedMarkdown = result.Markdown;
            ViewModel.UsePasteSource = true;
            ViewModel.StatusText = $"Imported {Path.GetFileName(file.Path)} ({result.Tier})";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Import failed: {ex.Message}";
        }
    }

    // ---- D4: Spreadsheet / CSV bidirectional sync ------------------------------------------------

    private async void OnInsertSpreadsheetClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".xlsx");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            Services.TableModel model;
            var ext = Path.GetExtension(file.Path).ToLowerInvariant();

            if (ext == ".xlsx")
            {
                using var stream = await file.OpenStreamForReadAsync();
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
                    if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
                    model = sheets[combo.SelectedIndex].Model;
                }
            }
            else
            {
                // CSV (or .tsv / .txt treated as CSV with auto-delimiter detection)
                var text = await File.ReadAllTextAsync(file.Path);
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
                : $"Imported {model.Rows.Count + 1} rows × {model.ColumnCount} columns from {Path.GetFileName(file.Path)}.";
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
        var cursorLine = GetCursorLine(text, PasteTextBox.SelectionStart);
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

        var picker = new FileSavePicker { SuggestedFileName = "table" };
        picker.FileTypeChoices.Add("Excel workbook", new List<string> { ".xlsx" });
        picker.FileTypeChoices.Add("CSV (single table)", new List<string> { ".csv" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            var outExt = Path.GetExtension(file.Path).ToLowerInvariant();
            if (outExt == ".csv")
            {
                // CSV: write only the first table (CSV is single-table by nature).
                var csv = Services.SpreadsheetService.WriteCsv(exports[0].Model);
                await File.WriteAllTextAsync(file.Path, csv);
            }
            else
            {
                using var stream = await file.OpenStreamForWriteAsync();
                Services.SpreadsheetService.WriteXlsx(exports, stream);
            }

            ViewModel.StatusText = $"Exported {exports.Count} table{(exports.Count == 1 ? "" : "s")} to {Path.GetFileName(file.Path)}.";
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
            if (text[i] == '\n') line++;
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
    private void OnOpenSmartArtDesignStudioClick(object sender, RoutedEventArgs e) => OpenSmartArtStudio();

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
        ViewModel.StatusText = "SmartArt Design Studio opened.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    // ── SmartArt offer (non-invasive): detect diagram-shaped pasted content and offer a preview.
    private readonly Services.SmartArtOfferGate _smartArtOfferGate = new();
    private DispatcherQueueTimer? _smartArtOfferDebounce;
    private Services.SmartArtSuggestion _smartArtOfferTag = new(Services.SmartArtKind.None, 0, "");

    private void InitSmartArtOffer()
    {
        _smartArtOfferDebounce = DispatcherQueue.CreateTimer();
        _smartArtOfferDebounce.Interval = TimeSpan.FromMilliseconds(900);
        _smartArtOfferDebounce.IsRepeating = false;
        _smartArtOfferDebounce.Tick += (_, _) => EvaluateSmartArtOffer();
    }

    private void ScheduleSmartArtOffer()
    {
        if (_smartArtOfferDebounce is null) return;
        _smartArtOfferDebounce.Stop();
        _smartArtOfferDebounce.Start();
    }

    private void EvaluateSmartArtOffer()
    {
        if (SmartArtOfferBar is null || PasteTextBox is null) return;
        var md = PasteTextBox.Text ?? "";
        var suggestion = Services.SmartArtPotentialDetector.Detect(md);
        if (suggestion.IsOffered && _smartArtOfferGate.ShouldOffer(md, suggestion))
        {
            _smartArtOfferTag = suggestion;
            SmartArtOfferBar.Message = suggestion.Reason;
            SmartArtOfferBar.IsOpen = true;
            SmartArtOfferBar.Visibility = Visibility.Visible;
        }
        else if (string.IsNullOrWhiteSpace(md) || !suggestion.IsOffered)
        {
            SmartArtOfferBar.IsOpen = false;
            SmartArtOfferBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSmartArtOfferPreviewClick(object sender, RoutedEventArgs e)
    {
        var md = PasteTextBox?.Text ?? "";
        // Re-detect at click time: the user may have edited the document since the bar appeared
        // (a stale _smartArtOfferTag would open the studio with the wrong layout family).
        var suggestion = Services.SmartArtPotentialDetector.Detect(md);
        OpenSmartArtStudio(preloadMarkdown: md, layoutAlias: suggestion.LayoutAlias);
        SmartArtOfferBar.IsOpen = false;
        SmartArtOfferBar.Visibility = Visibility.Collapsed;
    }

    private void OnSmartArtOfferClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        // "Not now" / the close X: the offer gate already remembers this content's hash, so the
        // offer stays quiet until the user actually changes the document.
        _smartArtOfferTag = new Services.SmartArtSuggestion(Services.SmartArtKind.None, 0, "");
        SmartArtOfferBar.Visibility = Visibility.Collapsed;
    }

    // MLShape Design Studio — free-form native DrawingML shape composing
    private void OnOpenShapeDesignStudioClick(object sender, RoutedEventArgs e)
    {
        if (_shapeDesignStudio == null)
        {
            _shapeDesignStudio = new Views.ShapeStudio.ShapeDesignStudioWindow();
            _shapeDesignStudio.Closed += (s, args) => _shapeDesignStudio = null;
        }
        _shapeDesignStudio.Activate();
        ViewModel.StatusText = "MLShape Design Studio opened.";
        ViewModel.StatusSeverity = Models.StatusSeverity.Success;
    }

    public Task<MarkSmith.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MarkSmith.Models.AmbiguityCase ambiguity)
    {
        return Task.FromResult<MarkSmith.Models.RenderOption?>(null);
    }
}

