using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace MarkSmith.Views.History;

public sealed partial class HistoryWindow : Window
{
    private const string ScratchKey = "scratch://workspace-session.md";

    private readonly HistoryWindowViewModel _vm;
    private readonly MainViewModel? _main;
    private readonly Microsoft.UI.Xaml.Controls.WebView2 _preview;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private bool _webViewReady;
    private bool _dialogOpen;

    private static HistoryWindow? _open;

    /// <summary>The editor's view model, set by the main window. A history window opened from
    /// anywhere (Document Galaxy, a node's menu) restores into the editor through it; before,
    /// Galaxy opened one without it and its Restore button silently did nothing.</summary>
    public static MainViewModel? Editor { get; set; }

    /// <summary>Shows the one Version History window, on <paramref name="filePath"/> (the unsaved
    /// text when null). Asked for again while open, it refreshes, so versions recorded since it
    /// opened appear, and moves to that document instead of stacking a second window.</summary>
    public static HistoryWindow ShowFor(string? filePath)
    {
        if (_open is { } window)
        {
            _ = window._vm.ShowFileAsync(string.IsNullOrWhiteSpace(filePath) ? ScratchKey : filePath);
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
        }
        else
        {
            window = new HistoryWindow(Editor, filePath);
            window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
            _open = window;
        }
        window.Activate();
        return window;
    }

    public static bool IsOpen => _open is not null;

    public HistoryWindow(MainViewModel? mainViewModel = null, string? initialFilePath = null)
    {
        InitializeComponent();
        // "Right-click for options" menus: open them from Shift+F10 / the Menu key too.
        KeyboardContextMenu.BridgeAll(
            (StarredFilterToggle, OnStarredFilterToggleRightTapped),
            (KindFilterButton, OnKindFilterButtonRightTapped),
            (TakeCheckpointButton, OnTakeCheckpointButtonRightTapped),
            (RestoreButton, OnRestoreButtonRightTapped),
            (ClearFiltersButton, OnClearFiltersButtonRightTapped),
            (UnifiedToggle, OnDiffModeToggleRightTapped),
            (SplitToggle, OnDiffModeToggleRightTapped),
            (PreviewToggle, OnDiffModeToggleRightTapped),
            (DeltaMeterButton, OnDeltaMeterRightTapped),
            (DiffWordDeltaPill, OnDiffWordDeltaPillRightTapped),
            (FileNameButton, OnFileNameRightTapped),
            (SelectedHeaderButton, OnTimelineHeaderRightTapped),
            (TimelineFilterStatusPill, OnTimelineFilterStatusPillRightTapped),
            (CommitShaCopyPill, OnDiffSummaryRightTapped),
            (ChangeStepperButton, OnChangeStepperButtonRightTapped),
            (DocumentsRefreshButton, OnDocumentsHeaderRightTapped));
        TextBoxMenu.Attach(SearchBox, FillSearchBoxMenu);
        Title = "Version History — MarkSmith";
        _main = mainViewModel;

        // The header is the title bar, like the studios: one band instead of a system bar
        // stacked on a header, with caption buttons drawn in the content's theme.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        TitleBarInsets.Reserve(this, AppTitleBar, gap: 16);
        CaptionButtons.Follow(this, AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        SizeForDisplay();

        // Unsaved text is recorded under the scratch key, so land on it when no file is open.
        var initial = string.IsNullOrWhiteSpace(initialFilePath) ? ScratchKey : initialFilePath;

        _vm = new HistoryWindowViewModel(
            md => FitToPane(mainViewModel != null ? mainViewModel.BuildPreviewHtml(md) : AppServices.MarkdownHtml.Render(md, AppServices.Settings.Current, AppServices.Themes.GetOrDefault(AppServices.Settings.Current.Theme))),
            id => mainViewModel != null ? mainViewModel.RestoreVersionAsync(id) : Task.FromResult(false),
            initialFilePath: initial,
            editorTextFor: EditorTextFor);
        RootGrid.DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _preview = PreviewWeb;
        _ = InitializeWebViewAsync();
        _ = _vm.LoadCommand.ExecuteAsync(null);

        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); NoticeBar.IsOpen = false; };
        RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
        RootGrid.KeyDown += OnRootKeyDown;
        HoverPolish.Track(RootGrid);

        // Open on the selected version, not in the search box (the first tab stop): a caret
        // blinking in an empty search field read as "type something", and Ctrl+F reaches it.
        _openedAt = DateTime.UtcNow;
        TimelineScroll.LayoutUpdated += OnTimelineLayoutUpdated;
    }

    // Null until the window's content has a XamlRoot. The first timeline load runs from the constructor,
    // and its selection changes ask where focus is: GetFocusedElement(null) threw, which the load reported
    // as "Couldn't read the history of …" and left no version selected.
    private DependencyObject? FocusedElement() =>
        RootGrid.XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject : null;

    private DateTime _openedAt;
    private bool _initialFocusDone;

    private void OnTimelineLayoutUpdated(object? sender, object e)
    {
        if (_initialFocusDone) return;
        // Only while the window is opening, and never over something the person chose themselves.
        var focused = FocusedElement();
        bool untouched = focused is null || (ReferenceEquals(focused, SearchBox) && SearchBox.Text.Length == 0);
        if (!untouched || DateTime.UtcNow - _openedAt > TimeSpan.FromSeconds(3))
        {
            StopInitialFocus();
            return;
        }
        if (_vm.Selected is not { } selected || FindVersionButton(TimelineScroll, selected) is not { } button) return;
        StopInitialFocus();
        button.Focus(FocusState.Programmatic);
        button.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
    }

    private void StopInitialFocus()
    {
        _initialFocusDone = true;
        TimelineScroll.LayoutUpdated -= OnTimelineLayoutUpdated;
    }

    private static Button? FindVersionButton(DependencyObject parent, object item)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is Button b && ReferenceEquals(b.DataContext, item)) return b;
            if (FindVersionButton(child, item) is { } found) return found;
        }
        return null;
    }

    // The document HTML is laid out at page width (the export layout), wider than this pane, so
    // it opened clipped behind a horizontal scrollbar. Scale the whole page down to the pane's
    // width, and again on resize or when late content (mermaid, images) widens it.
    private const string FitScript =
        "<script>(function(){var de=document.documentElement,t=0;" +
        "function fit(){de.style.zoom='';var w=de.scrollWidth,v=de.clientWidth;" +
        "if(w>v+1)de.style.zoom=Math.max(v/w,0.3).toFixed(3);}" +
        "function later(){clearTimeout(t);t=setTimeout(fit,80);}" +
        "addEventListener('resize',later);addEventListener('load',fit);" +
        "new MutationObserver(later).observe(document.body,{childList:true,subtree:true});fit();})();</script>";

    private static string FitToPane(string html)
    {
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html + FitScript : html.Insert(i, FitScript);
    }

    /// <summary>The editor's live text when <paramref name="historyKey"/> is the document it has
    /// open. Mirrors MainViewModel's autosave key: no file open means the scratch entry.</summary>
    private string? EditorTextFor(string historyKey)
    {
        if (_main is null) return null;
        var openPath = _main.InputFilePath;
        bool isScratch = historyKey.StartsWith("scratch://", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(openPath)) return isScratch ? _main.PastedMarkdown ?? "" : null;
        if (isScratch) return null;
        try
        {
            return string.Equals(Path.GetFullPath(openPath), Path.GetFullPath(historyKey), StringComparison.OrdinalIgnoreCase)
                ? _main.PastedMarkdown ?? ""
                : null;
        }
        catch { return null; }
    }

    private string OpenDocumentName =>
        _main is null || string.IsNullOrWhiteSpace(_main.InputFilePath)
            ? "the unsaved text"
            : Path.GetFileName(_main.InputFilePath);

    // Sized in DIPs like the main window. AppWindow sizes are physical pixels, so a fixed size
    // opens cramped on a scaled laptop. Three panes need ~980 DIPs before the diff column squeezes.
    private void SizeForDisplay()
    {
        try
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0) scale = 1;
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                Math.Min((int)(1280 * scale), work.Width), Math.Min((int)(800 * scale), work.Height)));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = Math.Min((int)(1000 * scale), work.Width);
                presenter.PreferredMinimumHeight = Math.Min((int)(560 * scale), work.Height);
            }
        }
        catch { /* best effort: the default size still works */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private async Task InitializeWebViewAsync()
    {
        try
        {
            // Same environment and asset host as the main preview. The default environment could
            // clash with the app's (WebView2 refuses a second set of browser options), and without
            // the marksmith.assets mapping mermaid, maths and code highlighting never loaded here.
            await _preview.EnsureCoreWebView2Async(await WebView2EnvironmentFactory.CreateAsync());
            var core = _preview.CoreWebView2;
            MainWindow.MapAssetHost(core);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.NavigationStarting += (s, e) =>
            {
                var uri = e.Uri ?? "";
                if (uri.Length == 0 ||
                    uri.StartsWith("https://" + WebAssets.Host, StringComparison.OrdinalIgnoreCase) ||
                    uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    return;
                // A link in an old version opens in the browser, not inside the preview pane.
                e.Cancel = true;
                if (Uri.TryCreate(uri, UriKind.Absolute, out var target) &&
                    (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeMailto))
                    _ = Launcher.LaunchUriAsync(target);
            };
            _webViewReady = true;
            RenderPreview();
        }
        catch { /* best-effort WebView2 initialization */ }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryWindowViewModel.PreviewHtml))
        {
            RenderPreview();
        }
        else if (e.PropertyName == nameof(HistoryWindowViewModel.Notice))
        {
            ShowNotice();
        }
        else if (e.PropertyName == nameof(HistoryWindowViewModel.Selected))
        {
            UpdateCopyToolbarButton(_vm.DiffMode);
            ResetChangeStepper();
            UpdateVisualDiffStatsBar();
            UpdateDiffSearchMatches();
        }
        else if (e.PropertyName is nameof(HistoryWindowViewModel.HasDiffStats) or nameof(HistoryWindowViewModel.DiffStats))
        {
            // A {Binding} on AutomationProperties.Name never resolved here (UIA read the tooltip).
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DeltaMeterButton,
                string.IsNullOrWhiteSpace(_vm.DiffStats) ? "Line delta" : $"Line delta {_vm.DiffStats}");
            ResetChangeStepper();
            UpdateVisualDiffStatsBar();
            UpdateDiffSearchMatches();
            if (!_vm.HasDiffStats) HandFocusOffCollapsingDiffTools();
        }
        else if (e.PropertyName == nameof(HistoryWindowViewModel.SearchQuery) || e.PropertyName == nameof(HistoryWindowViewModel.IsStarredOnlyFilter))
        {
            UpdateTimelineFilterStatus();
        }
        else if (e.PropertyName == nameof(HistoryWindowViewModel.SelectedHeader))
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SelectedHeaderButton,
                _vm.HasSelectedHeader ? $"Selected version {_vm.SelectedHeader}" : "Selected version");
            // The header shows the compact form; the tooltip carries the full date.
            ToolTipService.SetToolTip(SelectedHeaderButton, _vm.HasSelectedHeader
                ? $"{_vm.SelectedHeader} · Click to copy, Right-click or Shift+F10 for version options"
                : "Selected version · Click to copy its timestamp, Right-click or Shift+F10 for version options");
            if (_vm.HasSelectedHeader) return;
            // The timestamp button collapses (on HasSelectedHeader, raised next) with the selection;
            // move focus while it still shows.
            var focused = FocusedElement();
            if (ReferenceEquals(focused, SelectedHeaderButton))
                TimelineFilterStatusPill.Focus(FocusState.Programmatic);
        }
        else if (e.PropertyName == nameof(HistoryWindowViewModel.ShowTimeline) && !_vm.ShowTimeline)
        {
            // The jump arrows disable with nothing to jump to; don't strand keyboard focus on them.
            var focused = FocusedElement();
            if (ReferenceEquals(focused, JumpLatestButton) || ReferenceEquals(focused, JumpOldestButton))
                SearchBox.Focus(FocusState.Programmatic);
        }
    }

    // The page fits itself to the pane's width when it loads. Loaded while the pane was collapsed
    // it measured 0 px and stayed clipped, with a horizontal scrollbar, once the user switched to
    // Preview. So render only while it is visible, and again on switching to it.
    private string? _renderedHtml;

    private void RenderPreview()
    {
        if (string.IsNullOrEmpty(_vm.PreviewHtml))
        {
            _renderedHtml = null;
            return;
        }
        if (!_webViewReady || !_vm.ShowPreview) return;
        if (ReferenceEquals(_renderedHtml, _vm.PreviewHtml)) return;
        try
        {
            _preview.CoreWebView2?.NavigateToString(_vm.PreviewHtml);
            _renderedHtml = _vm.PreviewHtml;
        }
        catch { /* best effort */ }
    }

    private void ShowNotice()
    {
        _noticeTimer.Stop();
        if (string.IsNullOrEmpty(_vm.Notice))
        {
            NoticeBar.IsOpen = false;
            return;
        }
        NoticeBar.Severity = _vm.NoticeIsError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        NoticeBar.Message = _vm.Notice;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(NoticeBar, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NoticeBar, _vm.Notice);
        NoticeBar.IsOpen = true;
        // Confirmations clear themselves; errors stay until dismissed.
        if (!_vm.NoticeIsError) _noticeTimer.Start();
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _noticeTimer.Stop();
        if (!string.IsNullOrEmpty(_vm.Notice)) _vm.DismissNoticeCommand.Execute(null);
    }

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSummaryViewModel file })
            _ = _vm.SelectFileCommand.ExecuteAsync(file);
    }

    private void OnFileContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: FileSummaryViewModel file } fe) return;
        args.Handled = true;
        var menu = BuildFileMenu(file);
        if (args.TryGetPosition(fe, out var point))
            menu.ShowAt(fe, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(fe);
    }

    private MenuFlyout BuildFileMenu(FileSummaryViewModel file)
    {
        var menu = new MenuFlyout();
        string fullPath = file.Summary.FilePath;
        bool onDisk = !file.IsScratch && File.Exists(fullPath);

        var openInEditor = new MenuFlyoutItem
        {
            Text = "Open in editor",
            Icon = new FontIcon { Glyph = "\uE8E5" },
            IsEnabled = _main is not null && onDisk,
        };
        openInEditor.Click += async (_, _) =>
        {
            if (_main is null || !File.Exists(fullPath)) return;
            if (string.Equals(_main.InputFilePath, fullPath, StringComparison.OrdinalIgnoreCase))
                _main.InputFilePath = string.Empty;
            _main.InputFilePath = fullPath;
            _main.UsePasteSource = false;
            await _main.LastFileReadTask;
            await _vm.ShowFileAsync(fullPath);
            _vm.ShowNotice($"Opened {file.FileName} in the editor.");
        };
        menu.Items.Add(openInEditor);

        var checkpoint = new MenuFlyoutItem
        {
            Text = "Take checkpoint…",
            Icon = new FontIcon { Glyph = "\uE74E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+S",
        };
        checkpoint.Click += async (_, _) =>
        {
            if (!ReferenceEquals(_vm.SelectedFile, file))
                await _vm.SelectFileCommand.ExecuteAsync(file);
            OnTakeSnapshotClick(this, new RoutedEventArgs());
        };
        menu.Items.Add(checkpoint);

        if (!file.IsScratch)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var showInExplorer = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uEC50" },
                IsEnabled = onDisk,
            };
            showInExplorer.Click += (_, _) =>
            {
                if (!File.Exists(fullPath)) return;
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\""); }
                catch { }
            };
            menu.Items.Add(showInExplorer);

            var copyPath = new MenuFlyoutItem
            {
                Text = "Copy file path",
                Icon = new FontIcon { Glyph = "\uE8C8" },
            };
            copyPath.Click += (_, _) =>
            {
                CopyAndNotify(file.Detail, "Copied file path to clipboard.");
            };
            menu.Items.Add(copyPath);
        }

        return menu;
    }

    private int _currentHunkIndex = -1;
    private int _lastAddedWords = 0;
    private int _lastRemovedWords = 0;

    private void UpdateVisualDiffStatsBar()
    {
        if (DiffVisualStatBar is null || DiffAddedCol is null || DiffRemovedCol is null) return;
        var stats = _vm.DiffStats;
        if (string.IsNullOrWhiteSpace(stats) || !_vm.HasDiffStats)
        {
            DiffVisualStatBar.Visibility = Visibility.Collapsed;
            CollapseWordDeltaPill();
            return;
        }

        int added = 0;
        int removed = 0;
        var parts = stats.Split('·', StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p.EndsWith("added", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = p.Replace("added", "").Trim();
                int.TryParse(numStr, out added);
            }
            else if (p.EndsWith("removed", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = p.Replace("removed", "").Trim();
                int.TryParse(numStr, out removed);
            }
        }

        int total = added + removed;
        if (total == 0)
        {
            DiffVisualStatBar.Visibility = Visibility.Collapsed;
            CollapseWordDeltaPill();
            return;
        }

        DiffVisualStatBar.Visibility = Visibility.Visible;
        double addedRatio = (double)added / total;
        double removedRatio = (double)removed / total;

        DiffAddedCol.Width = new GridLength(Math.Max(addedRatio, 0.05), GridUnitType.Star);
        DiffRemovedCol.Width = new GridLength(Math.Max(removedRatio, 0.05), GridUnitType.Star);
        if (DiffAddedRect != null) DiffAddedRect.Visibility = added > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DiffRemovedRect != null) DiffRemovedRect.Visibility = removed > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateWordDeltaStats();
    }

    // The meters are tab stops now: never collapse one that holds focus without first handing
    // focus to its neighbour, the copy button (or the search box when nothing is selected).
    private void CollapseWordDeltaPill()
    {
        if (DiffWordDeltaPill is null) return;
        if (DiffWordDeltaPill.FocusState != FocusState.Unfocused
            || (DeltaMeterButton is not null && DeltaMeterButton.FocusState != FocusState.Unfocused))
        {
            if (CopyToolbarButton is { Visibility: Visibility.Visible }) CopyToolbarButton.Focus(FocusState.Programmatic);
            else SearchBox?.Focus(FocusState.Programmatic);
        }
        DiffWordDeltaPill.Visibility = Visibility.Collapsed;
    }

    private void UpdateWordDeltaStats()
    {
        if (DiffWordDeltaPill is null || DiffWordDeltaText is null) return;

        int addedWords = 0;
        int removedWords = 0;

        if (_vm.UnifiedDiffRows.Count > 0)
        {
            foreach (var row in _vm.UnifiedDiffRows)
            {
                if (row.IsAdded) addedWords += CountWordsInLine(row.Text);
                else if (row.IsRemoved) removedWords += CountWordsInLine(row.Text);
            }
        }
        else if (_vm.DiffRows.Count > 0)
        {
            foreach (var row in _vm.DiffRows)
            {
                if (row.Right != null && row.Right.IsAdded) addedWords += CountWordsInLine(row.Right.Text);
                if (row.Left != null && row.Left.IsRemoved) removedWords += CountWordsInLine(row.Left.Text);
            }
        }

        _lastAddedWords = addedWords;
        _lastRemovedWords = removedWords;

        DiffWordDeltaPill.Visibility = Visibility.Visible;
        int net = addedWords - removedWords;
        string netSign = net >= 0 ? $"+{net:N0}" : $"{net:N0}";
        DiffWordDeltaText.Text = $"+{addedWords:N0} / -{removedWords:N0} words";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiffWordDeltaPill, $"Word delta {DiffWordDeltaText.Text}");
        ToolTipService.SetToolTip(DiffWordDeltaPill, $"Word delta: +{addedWords:N0} added, -{removedWords:N0} removed (net {netSign} words) · Click to copy, Right-click or Shift+F10 for options");
    }

    private static int CountWordsInLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int words = 0;
        bool inToken = false;
        bool tokenHasAlnum = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (inToken && tokenHasAlnum) words++;
                inToken = false;
                tokenHasAlnum = false;
            }
            else
            {
                inToken = true;
                if (char.IsLetterOrDigit(ch)) tokenHasAlnum = true;
            }
        }
        if (inToken && tokenHasAlnum) words++;
        return words;
    }

    private void ResetChangeStepper()
    {
        _currentHunkIndex = -1;
        var hunks = GetChangeHunkIndices();
        SetChangeStepper(
            hunks.Count > 0 ? $"{hunks.Count} {(hunks.Count == 1 ? "hunk" : "hunks")}" : "0 hunks",
            hunks.Count > 0
                ? $"{hunks.Count} change {(hunks.Count == 1 ? "hunk" : "hunks")} · Click or F7 to jump to next hunk, Shift+F7 for previous · Right-click or Shift+F10 to pick one"
                : "No change hunks in this version",
            hunks.Count > 0 ? $"{hunks.Count} change {(hunks.Count == 1 ? "hunk" : "hunks")}" : "No change hunks",
            announce: false);
    }

    /// <summary>
    /// Updates the hunk counter: its short text ("Hunk 2/5"), its tooltip, and the name screen
    /// readers hear ("Change hunk 2 of 5"), announced politely when the user stepped.
    /// </summary>
    private void SetChangeStepper(string text, string tooltip, string spoken, bool announce = true)
    {
        if (ChangeStepperLabel == null || ChangeStepperButton == null) return;
        ChangeStepperLabel.Text = text;
        ToolTipService.SetToolTip(ChangeStepperButton, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ChangeStepperButton, spoken);
        if (announce)
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(ChangeStepperButton)
                ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private List<int> GetChangeHunkIndices()
    {
        var indices = new List<int>();
        if (_vm.DiffMode == HistoryDiffMode.Unified)
        {
            if (_vm.UnifiedDiffRows.Count == 0) return indices;
            bool inHunk = false;
            for (int i = 0; i < _vm.UnifiedDiffRows.Count; i++)
            {
                var row = _vm.UnifiedDiffRows[i];
                if (row.IsAdded || row.IsRemoved)
                {
                    if (!inHunk)
                    {
                        indices.Add(i);
                        inHunk = true;
                    }
                }
                else
                {
                    inHunk = false;
                }
            }
        }
        else
        {
            if (_vm.DiffRows.Count == 0) return indices;
            bool inHunk = false;
            for (int i = 0; i < _vm.DiffRows.Count; i++)
            {
                var row = _vm.DiffRows[i];
                bool isChanged = (row.Left != null && row.Left.IsRemoved) || (row.Right != null && row.Right.IsAdded);
                if (isChanged)
                {
                    if (!inHunk)
                    {
                        indices.Add(i);
                        inHunk = true;
                    }
                }
                else
                {
                    inHunk = false;
                }
            }
        }
        return indices;
    }

    private void StepToChangeHunk(int direction)
    {
        var hunks = GetChangeHunkIndices();
        if (hunks.Count == 0)
        {
            SetChangeStepper("0 hunks", "No change hunks in this version", "No change hunks");
            return;
        }

        if (direction > 0)
        {
            _currentHunkIndex = (_currentHunkIndex + 1) % hunks.Count;
        }
        else
        {
            _currentHunkIndex = (_currentHunkIndex - 1 + hunks.Count) % hunks.Count;
        }

        int targetRow = hunks[_currentHunkIndex];
        SetChangeStepper($"Hunk {_currentHunkIndex + 1}/{hunks.Count}",
            $"Hunk {_currentHunkIndex + 1} of {hunks.Count} (Line {targetRow + 1}) · F7 next, Shift+F7 prev · Right-click or Shift+F10 to pick one",
            $"Change hunk {_currentHunkIndex + 1} of {hunks.Count}, line {targetRow + 1}");

        double targetY = Math.Max(0, targetRow * 20.0);
        if (_vm.DiffMode == HistoryDiffMode.Unified)
        {
            UnifiedScroll?.ChangeView(null, targetY, null);
        }
        else
        {
            SplitScroll?.ChangeView(null, targetY, null);
        }
    }

    private void OnPrevChangeButtonClick(object sender, RoutedEventArgs e)
    {
        StepToChangeHunk(-1);
    }

    private void OnNextChangeButtonClick(object sender, RoutedEventArgs e)
    {
        StepToChangeHunk(1);
    }

    // One action per click: the label used to step on Tapped and jump to the first hunk on
    // DoubleTapped, so a double-click did both. Jumping to any hunk (the first included) is in the
    // pill's context menu, which right-click and Shift+F10 open.
    private void OnChangeStepperButtonClick(object sender, RoutedEventArgs e) => StepToChangeHunk(1);

    // Keyboard only (bridged): a right-click reaches the pill's own ContextFlyout. Without the
    // bridge, Shift+F10 here would bubble to the window's handler and open the version menu.
    private void OnChangeStepperButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        DiffContextFlyout?.ShowAt(DiffChangeStepperPill, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private void JumpToHunkIndex(int hunkIndex)
    {
        var hunks = GetChangeHunkIndices();
        if (hunkIndex < 0 || hunkIndex >= hunks.Count) return;
        _currentHunkIndex = hunkIndex;
        int targetRow = hunks[_currentHunkIndex];
        SetChangeStepper($"Hunk {_currentHunkIndex + 1}/{hunks.Count}",
            $"Hunk {_currentHunkIndex + 1} of {hunks.Count} (Line {targetRow + 1}) · F7 next, Shift+F7 prev · Right-click or Shift+F10 to pick one",
            $"Change hunk {_currentHunkIndex + 1} of {hunks.Count}, line {targetRow + 1}");

        double targetY = Math.Max(0, targetRow * 20.0);
        if (_vm.DiffMode == HistoryDiffMode.Unified)
        {
            UnifiedScroll?.ChangeView(null, targetY, null);
        }
        else
        {
            SplitScroll?.ChangeView(null, targetY, null);
        }
    }

    private void OnDiffContextFlyoutOpening(object? sender, object e)
    {
        if (DiffContextFlyout == null) return;
        while (DiffContextFlyout.Items.Count > 5)
        {
            DiffContextFlyout.Items.RemoveAt(DiffContextFlyout.Items.Count - 1);
        }

        var hunks = GetChangeHunkIndices();
        if (hunks.Count == 0) return;

        DiffContextFlyout.Items.Add(new MenuFlyoutSeparator());
        for (int h = 0; h < hunks.Count; h++)
        {
            int hunkIdx = h;
            int targetRow = hunks[h];
            string snippet = "";
            if (_vm.DiffMode == HistoryDiffMode.Unified && targetRow < _vm.UnifiedDiffRows.Count)
            {
                var r = _vm.UnifiedDiffRows[targetRow];
                snippet = (r.Text ?? "").Trim();
            }
            else if (targetRow < _vm.DiffRows.Count)
            {
                var r = _vm.DiffRows[targetRow];
                snippet = (r.Right?.Text ?? r.Left?.Text ?? "").Trim();
            }
            if (snippet.Length > 28) snippet = snippet.Substring(0, 28) + "…";
            string label = string.IsNullOrEmpty(snippet)
                ? $"Jump to Hunk {hunkIdx + 1} (Line {targetRow + 1})"
                : $"Hunk {hunkIdx + 1}: Line {targetRow + 1} (\"{snippet}\")";

            var item = new MenuFlyoutItem
            {
                Text = label,
                Icon = new FontIcon { Glyph = "\uE8B9" }
            };
            item.Click += (_, _) => JumpToHunkIndex(hunkIdx);
            DiffContextFlyout.Items.Add(item);
        }
    }

    private void OnDiffContextChangesOnlyClick(object sender, RoutedEventArgs e) => ApplyDiffContext(0);
    private void OnDiffContextCompactClick(object sender, RoutedEventArgs e) => ApplyDiffContext(1);
    private void OnDiffContextStandardClick(object sender, RoutedEventArgs e) => ApplyDiffContext(3);
    private void OnDiffContextFullClick(object sender, RoutedEventArgs e) => ApplyDiffContext(-1);

    private void ApplyDiffContext(int contextLines)
    {
        _vm.SetDiffContextLines(contextLines);
        _currentHunkIndex = -1;
        var hunks = GetChangeHunkIndices();
        SetChangeStepper(hunks.Count > 0 ? $"Hunk 1/{hunks.Count}" : "0 hunks",
            $"{_vm.DiffContextLabel} · {hunks.Count} hunk{(hunks.Count == 1 ? "" : "s")} · Click to step (F7), right-click or Shift+F10 to jump to a hunk or change context",
            hunks.Count > 0 ? $"{hunks.Count} change hunk{(hunks.Count == 1 ? "" : "s")}" : "No change hunks");
        if (hunks.Count > 0)
        {
            StepToChangeHunk(1);
        }
    }

    #region Diff Line Search & Match Stepper

    private readonly List<int> _diffSearchMatches = new();
    private int _currentDiffSearchIndex = -1;

    private void UpdateDiffSearchMatches()
    {
        _diffSearchMatches.Clear();
        _currentDiffSearchIndex = -1;
        if (DiffSearchBox == null || DiffMatchCountLabel == null) return;
        string query = DiffSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            SetDiffMatchCount("", "");
            if (DiffSearchPill != null)
                ToolTipService.SetToolTip(DiffSearchPill, "Search within diff rows (Enter/F3 next, Shift+F3 prev, Esc clear)");
            return;
        }

        if (_vm.DiffMode == HistoryDiffMode.Unified)
        {
            for (int i = 0; i < _vm.UnifiedDiffRows.Count; i++)
            {
                var row = _vm.UnifiedDiffRows[i];
                if (row.Text != null && row.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _diffSearchMatches.Add(i);
                }
            }
        }
        else
        {
            for (int i = 0; i < _vm.DiffRows.Count; i++)
            {
                var row = _vm.DiffRows[i];
                bool matchLeft = row.Left?.Text != null && row.Left.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
                bool matchRight = row.Right?.Text != null && row.Right.Text.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (matchLeft || matchRight)
                {
                    _diffSearchMatches.Add(i);
                }
            }
        }

        if (_diffSearchMatches.Count == 0)
        {
            SetDiffMatchCount("0/0", $"No matches for {query}");
            if (DiffSearchPill != null)
                ToolTipService.SetToolTip(DiffSearchPill, $"No matches for '{query}'");
        }
        else
        {
            _currentDiffSearchIndex = 0;
            SetDiffMatchCount($"1/{_diffSearchMatches.Count}", $"Match 1 of {_diffSearchMatches.Count}");
            if (DiffSearchPill != null)
                ToolTipService.SetToolTip(DiffSearchPill, $"Match 1 of {_diffSearchMatches.Count} for '{query}' · F3 next, Shift+F3 prev");
            ScrollToDiffRow(_diffSearchMatches[0]);
        }
    }

    private void StepToSearchMatch(int direction)
    {
        if (_diffSearchMatches.Count == 0) return;
        _currentDiffSearchIndex = (_currentDiffSearchIndex + direction + _diffSearchMatches.Count) % _diffSearchMatches.Count;
        int targetRow = _diffSearchMatches[_currentDiffSearchIndex];
        string query = DiffSearchBox?.Text?.Trim() ?? "";
        if (DiffMatchCountLabel != null)
        {
            SetDiffMatchCount($"{_currentDiffSearchIndex + 1}/{_diffSearchMatches.Count}",
                $"Match {_currentDiffSearchIndex + 1} of {_diffSearchMatches.Count}");
            if (DiffSearchPill != null)
                ToolTipService.SetToolTip(DiffSearchPill, $"Match {_currentDiffSearchIndex + 1} of {_diffSearchMatches.Count} for '{query}' (Row {targetRow + 1}) · F3 next, Shift+F3 prev");
        }
        ScrollToDiffRow(targetRow);
    }

    /// <summary>
    /// Shows the compact "2/5" count and gives the polite live region a spoken form ("Match 2 of 5"),
    /// so a screen reader announces each step instead of reading "2 slash 5".
    /// </summary>
    private void SetDiffMatchCount(string text, string spoken)
    {
        if (DiffMatchCountLabel == null) return;
        DiffMatchCountLabel.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DiffMatchCountLabel, spoken);
        if (spoken.Length > 0)
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(DiffMatchCountLabel)
                ?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void ScrollToDiffRow(int targetRow)
    {
        double targetY = Math.Max(0, targetRow * 20.0);
        if (_vm.DiffMode == HistoryDiffMode.Unified)
        {
            UnifiedScroll?.ChangeView(null, targetY, null);
        }
        else
        {
            SplitScroll?.ChangeView(null, targetY, null);
        }
    }

    private void OnDiffSearchBoxTextChanged(object sender, TextChangedEventArgs e) => UpdateDiffSearchMatches();

    private void OnDiffSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            StepToSearchMatch(isShift ? -1 : 1);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (DiffSearchBox != null) DiffSearchBox.Text = "";
        }
        else if (e.Key == VirtualKey.Down)
        {
            e.Handled = true;
            StepToSearchMatch(1);
        }
        else if (e.Key == VirtualKey.Up)
        {
            e.Handled = true;
            StepToSearchMatch(-1);
        }
    }

    private void OnDiffPrevMatchClick(object sender, RoutedEventArgs e) => StepToSearchMatch(-1);
    private void OnDiffNextMatchClick(object sender, RoutedEventArgs e) => StepToSearchMatch(1);

    #endregion

    private void OnVersionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _vm.SelectVersionCommand.Execute(item);
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _ = _vm.ToggleStarCommand.ExecuteAsync(item);
    }

    // ---- Per-version actions (⋯ button, right-click, Shift+F10 / Menu key) ----

    private void OnVersionMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item } fe)
            BuildVersionMenu(item).ShowAt(fe, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void OnVersionContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item } fe) return;
        args.Handled = true;
        var menu = BuildVersionMenu(item);
        if (args.TryGetPosition(fe, out var point))
            menu.ShowAt(fe, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(fe);
    }

    // Built in code: flyouts opened from inside a DataTemplate don't reliably inherit its
    // DataContext, so binding the items would target nothing.
    private MenuFlyout BuildVersionMenu(VersionItemViewModel item)
    {
        var menu = new MenuFlyout();

        var rename = new MenuFlyoutItem
        {
            Text = item.HasLabel ? "Rename…" : "Add a label…",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "F2",
        };
        rename.Click += async (_, _) => await RenameAsync(item);
        menu.Items.Add(rename);

        var star = new MenuFlyoutItem
        {
            Text = item.IsStarred ? "Remove star" : "Star",
            Icon = new FontIcon { Glyph = item.IsStarred ? "" : "" },
            KeyboardAcceleratorTextOverride = "S",
        };
        star.Click += (_, _) => _ = _vm.ToggleStarCommand.ExecuteAsync(item);
        menu.Items.Add(star);

        var copyMd = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+C",
        };
        copyMd.Click += async (_, _) => await CopyVersionMarkdownAsync(item);
        menu.Items.Add(copyMd);

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
        };
        copyPatch.Click += async (_, _) =>
        {
            if (!ReferenceEquals(_vm.Selected, item))
                _vm.SelectVersionCommand.Execute(item);
            await CopyDiffPatchAsync(item);
        };
        menu.Items.Add(copyPatch);

        // The delta badge on the row is mouse-only (right-click / double-click), so its one
        // action of its own lives here too, where Shift+F10 and the ⋯ button reach it.
        if (item.HasDelta)
        {
            var copyDelta = new MenuFlyoutItem
            {
                Text = $"Copy line delta ({item.DeltaBadge})",
                Icon = new FontIcon { Glyph = "" },
            };
            copyDelta.Click += (_, _) => CopyVersionDelta(item);
            menu.Items.Add(copyDelta);
        }

        var restore = new MenuFlyoutItem
        {
            Text = "Restore to editor…",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
        };
        restore.Click += async (_, _) =>
        {
            _vm.SelectVersionCommand.Execute(item);
            await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restore);

        menu.Items.Add(new MenuFlyoutSeparator());

        var delete = new MenuFlyoutItem
        {
            Text = "Delete version…",
            Icon = new FontIcon { Glyph = "" },
            KeyboardAcceleratorTextOverride = "Delete",
        };
        delete.Click += async (_, _) => await DeleteAsync(item);
        menu.Items.Add(delete);

        return menu;
    }

    private async void OnCopyToolbarButtonClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is null) return;
        if (_vm.DiffMode == HistoryDiffMode.Preview)
        {
            await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        }
        else
        {
            await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        }
    }

    private async void OnCopyDiffPatchContextClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not null)
            await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
    }

    private async void OnCopyMarkdownContextClick(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not null)
            await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
    }

    private void OnCopyAddedLinesContextClick(object sender, RoutedEventArgs e)
    {
        var added = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Added);
        if (added.Count == 0)
        {
            _vm.ShowNotice("No added lines in this diff.");
            return;
        }
        var text = string.Join(Environment.NewLine, added);
        CopyAndNotify(text, $"Copied {added.Count} added line(s) to clipboard.", CopyToolbarButton, $"\u2713 {added.Count} added");
    }

    private void OnCopyRemovedLinesContextClick(object sender, RoutedEventArgs e)
    {
        var removed = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Removed);
        if (removed.Count == 0)
        {
            _vm.ShowNotice("No removed lines in this diff.");
            return;
        }
        var text = string.Join(Environment.NewLine, removed);
        CopyAndNotify(text, $"Copied {removed.Count} removed line(s) to clipboard.", CopyToolbarButton, $"\u2713 {removed.Count} removed");
    }

    private void OnCopyUnchangedLinesContextClick(object sender, RoutedEventArgs e)
    {
        var unchanged = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Same);
        if (unchanged.Count == 0)
        {
            _vm.ShowNotice("No unchanged context lines in this diff.");
            return;
        }
        var text = string.Join(Environment.NewLine, unchanged);
        CopyAndNotify(text, $"Copied {unchanged.Count} unchanged context line(s) to clipboard.", CopyToolbarButton, $"\u2713 {unchanged.Count} context");
    }

    private void OnCopyDiffStatsContextClick(object sender, RoutedEventArgs e)
    {
        int added = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Added).Count;
        int removed = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Removed).Count;
        int unchanged = MarkSmith.Services.LineDiff.ExtractModifiedLines(_vm.UnifiedDiffRows, MarkSmith.Services.LineDiff.Kind.Same).Count;
        int hunkCount = GetChangeHunkIndices().Count;
        var summary = MarkSmith.Services.LineDiff.FormatDiffStatistics(added, removed, unchanged, hunkCount);
        CopyAndNotify(summary, $"Copied diff statistics ({summary}) to clipboard.", CopyToolbarButton, "\u2713 Stats copied");
    }

    private bool _diffWrapEnabled = false;

    private void OnDiffWrapButtonClick(object sender, RoutedEventArgs e)
    {
        _diffWrapEnabled = !_diffWrapEnabled;
        if (DiffWrapLabel != null)
        {
            DiffWrapLabel.Text = _diffWrapEnabled ? "Wrap: On" : "Wrap: Off";
        }
        ApplyDiffTextWrap(_diffWrapEnabled);
        _vm.ShowNotice(_diffWrapEnabled ? "Enabled soft text wrapping for diff lines" : "Disabled soft text wrapping for diff lines");
        if (DiffWrapButton != null) CopyFeedback.ApplyButtonTick(DiffWrapButton, null);
    }

    private void OnDiffIgnoreWhitespaceButtonClick(object sender, RoutedEventArgs e)
    {
        _vm.IgnoreWhitespace = !_vm.IgnoreWhitespace;
        if (DiffIgnoreWhitespaceLabel != null)
        {
            DiffIgnoreWhitespaceLabel.Text = _vm.IgnoreWhitespace ? "Ignore WS: On" : "Ignore WS: Off";
        }
        _vm.ShowNotice(_vm.IgnoreWhitespace ? "Ignoring whitespace-only changes in diff" : "Showing all whitespace changes in diff");
        if (DiffIgnoreWhitespaceButton != null) CopyFeedback.ApplyButtonTick(DiffIgnoreWhitespaceButton, null);
    }

    private void ApplyDiffTextWrap(bool wrap)
    {
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ApplyWrappingToRepeater(UnifiedDiffRepeater, wrapping);
        ApplyWrappingToRepeater(SplitDiffRepeater, wrapping);
    }

    private static void ApplyWrappingToRepeater(ItemsRepeater? repeater, TextWrapping wrapping)
    {
        if (repeater == null) return;
        int count = VisualTreeHelper.GetChildrenCount(repeater);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(repeater, i);
            FindAndSetWrapping(child, wrapping);
        }
    }

    private static void FindAndSetWrapping(DependencyObject? parent, TextWrapping wrapping)
    {
        if (parent == null) return;
        if (parent is TextBlock tb && tb.FontFamily?.Source?.Contains("Mono", StringComparison.OrdinalIgnoreCase) == true)
        {
            tb.TextWrapping = wrapping;
        }
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            FindAndSetWrapping(VisualTreeHelper.GetChild(parent, i), wrapping);
        }
    }

    private async void OnDiffLineJumpButtonClick(object sender, RoutedEventArgs e)
    {
        await ShowDiffLineJumpDialogAsync();
    }

    private async Task ShowDiffLineJumpDialogAsync()
    {
        int maxLine = 1;
        if (_vm.UnifiedDiffRows.Count > 0)
        {
            maxLine = Math.Max(1, _vm.UnifiedDiffRows.Max(r => Math.Max(r.NewNumber ?? 0, r.OldNumber ?? 0)));
        }

        var input = new TextBox
        {
            PlaceholderText = $"Line 1 - {maxLine}",
            InputScope = new Microsoft.UI.Xaml.Input.InputScope
            {
                Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) }
            },
            Width = 240,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Enter document line number to jump to (1 - {maxLine}):",
            FontSize = 13
        });
        panel.Children.Add(input);

        var quickButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var topBtn = new Button { Content = "Top (Line 1)", Padding = new Thickness(8, 4, 8, 4) };
        topBtn.Click += (_, _) => input.Text = "1";
        var midBtn = new Button { Content = $"Mid (Line {Math.Max(1, maxLine / 2)})", Padding = new Thickness(8, 4, 8, 4) };
        midBtn.Click += (_, _) => input.Text = Math.Max(1, maxLine / 2).ToString();
        var endBtn = new Button { Content = $"End (Line {maxLine})", Padding = new Thickness(8, 4, 8, 4) };
        endBtn.Click += (_, _) => input.Text = maxLine.ToString();

        quickButtons.Children.Add(topBtn);
        if (maxLine > 2) quickButtons.Children.Add(midBtn);
        quickButtons.Children.Add(endBtn);
        panel.Children.Add(quickButtons);

        var dialog = new ContentDialog
        {
            Title = "Go to Document Line in Diff",
            Content = panel,
            PrimaryButtonText = "Go to Line",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary &&
            int.TryParse(input.Text.Trim(), out int targetLine) && targetLine > 0)
        {
            JumpToDocumentLineInDiff(targetLine);
        }
    }

    private void JumpToDocumentLineInDiff(int targetLine)
    {
        int targetRowIdx = -1;
        for (int i = 0; i < _vm.UnifiedDiffRows.Count; i++)
        {
            var row = _vm.UnifiedDiffRows[i];
            if (row.NewNumber == targetLine || (row.NewNumber == null && row.OldNumber == targetLine))
            {
                targetRowIdx = i;
                break;
            }
        }

        if (targetRowIdx >= 0)
        {
            ScrollToDiffRow(targetRowIdx);
            _vm.ShowNotice($"Jumped to document line {targetLine} in diff view.");
        }
        else
        {
            _vm.ShowNotice($"Line {targetLine} was not found in the active diff.", isError: true);
        }
    }

    private void UpdateCopyToolbarButton(HistoryDiffMode mode)
    {
        if (CopyToolbarButton is null || CopyToolbarText is null || CopyToolbarIcon is null) return;
        if (mode == HistoryDiffMode.Preview)
        {
            CopyToolbarText.Text = "Copy Markdown";
            CopyToolbarIcon.Glyph = "\uE8C8";
            ToolTipService.SetToolTip(CopyToolbarButton, "Copy version Markdown (Ctrl+C)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CopyToolbarButton, "Copy version Markdown (Ctrl+C)");
        }
        else
        {
            CopyToolbarText.Text = "Copy diff";
            CopyToolbarIcon.Glyph = "\uE943";
            ToolTipService.SetToolTip(CopyToolbarButton, "Copy diff patch (Ctrl+Shift+C)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CopyToolbarButton, "Copy diff patch (Ctrl+Shift+C)");
        }
    }

    private async Task CopyVersionMarkdownAsync(VersionItemViewModel? item = null, Button? feedbackButton = null, FontIcon? feedbackIcon = null)
    {
        var target = item ?? _vm.Selected;
        if (target is null) return;
        var text = await _vm.GetVersionContentAsync(target);
        if (string.IsNullOrEmpty(text))
        {
            _vm.ShowNotice("Couldn't copy that version's text: no content stored.", isError: true);
            var btnErr = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode == HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btnErr is not null) CopyFeedback.ApplyButtonTick(btnErr, "No content", "No content");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, "No content");
            return;
        }
        var error = await ClipboardWriter.TrySetTextAsync(text);
        if (error is null)
        {
            _vm.ShowNotice($"Copied the {target.TimestampLabel} version's Markdown to the clipboard.");
            var btn = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode == HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btn is not null) CopyFeedback.ApplyButtonTick(btn, null, "✓ Copied");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, null);
        }
        else
        {
            _vm.ShowNotice($"Couldn't copy: {error}", isError: true);
            var btn = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode == HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btn is not null) CopyFeedback.ApplyButtonTick(btn, error, "Failed");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, error);
        }
    }

    private async Task CopyDiffPatchAsync(VersionItemViewModel? item = null, Button? feedbackButton = null, FontIcon? feedbackIcon = null)
    {
        var target = item ?? _vm.Selected;
        if (target is null) return;
        var patch = await _vm.BuildUnifiedDiffPatchAsync(target);
        if (string.IsNullOrWhiteSpace(patch))
        {
            _vm.ShowNotice("No line changes in this version to copy as a diff patch.", isError: true);
            var btnErr = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode != HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btnErr is not null) CopyFeedback.ApplyButtonTick(btnErr, "No changes", "No changes");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, "No changes");
            return;
        }
        var error = await ClipboardWriter.TrySetTextAsync(patch);
        if (error is null)
        {
            _vm.ShowNotice($"Copied unified diff patch ({target.TimestampLabel}) to the clipboard.");
            var btn = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode != HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btn is not null) CopyFeedback.ApplyButtonTick(btn, null, "✓ Copied");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, null);
        }
        else
        {
            _vm.ShowNotice($"Couldn't copy: {error}", isError: true);
            var btn = feedbackButton ?? (CopyToolbarButton?.Visibility == Visibility.Visible && _vm.DiffMode != HistoryDiffMode.Preview ? CopyToolbarButton : null);
            if (btn is not null) CopyFeedback.ApplyButtonTick(btn, error, "Failed");
            else if (feedbackIcon is not null) CopyFeedback.ApplyIconTick(feedbackIcon, feedbackButton, error);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // Only one ContentDialog may be open per window; a second ShowAsync throws.
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try
        {
            dialog.XamlRoot = RootGrid.XamlRoot;
            dialog.RequestedTheme = RootGrid.ActualTheme;
            return await dialog.ShowPolishedAsync();
        }
        finally { _dialogOpen = false; }
    }

    private async Task RenameAsync(VersionItemViewModel item)
    {
        var input = new TextBox
        {
            Text = item.Label,
            PlaceholderText = "e.g. Sent to the client",
            MaxLength = 80,
            Margin = new Thickness(0, 8, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Version label");
        input.Loaded += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        var dialog = new ContentDialog
        {
            Title = item.HasLabel ? "Rename version" : "Label this version",
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"{item.TimestampLabel} · {item.SourceLabel}", Opacity = 0.75 },
                    input,
                    new TextBlock { Text = "Leave it empty to remove the label.", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) },
                }
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        bool enterSubmitted = false;
        input.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter)
            {
                ke.Handled = true;
                enterSubmitted = true;
                dialog.Hide();
            }
        };
        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary || enterSubmitted)
            await _vm.RenameVersionCommand.ExecuteAsync((item, input.Text));
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedVersionButton);
    }

    private async Task DeleteAsync(VersionItemViewModel item)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete this version?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"The {item.TimestampLabel} version ({item.SourceLabel}" + (item.HasLabel ? $", “{item.Label}”" : "") +
                       ") is removed from history for good. Your document and the other versions aren't affected.",
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await _vm.DeleteVersionCommand.ExecuteAsync(item);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedVersionButton);
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e) => await ConfirmAndRestoreAsync();

    /// <summary>Restoring replaces the editor's whole text, so say exactly what will be replaced.
    /// A version from a different document gets a plain warning, because nothing stops its text
    /// landing in whichever document is open.</summary>
    private async Task ConfirmAndRestoreAsync()
    {
        var item = _vm.Selected;
        if (item is null || _main is null) return;

        bool sameDocument = _vm.IsSelectedFileOpen;
        var dialog = new ContentDialog
        {
            Title = sameDocument ? "Restore this version?" : "Restore into a different document?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = sameDocument
                    ? $"The editor's text is replaced with the {item.TimestampLabel} version. The current text is " +
                      "saved to history first, and Ctrl+Z in the editor undoes the restore."
                    : $"This version belongs to {_vm.FileName}, but the editor has {OpenDocumentName} open. " +
                      $"Restoring puts {_vm.FileName}'s text into {OpenDocumentName}. The current text is saved to history first.",
            },
            PrimaryButtonText = sameDocument ? "Restore" : "Restore anyway",
            CloseButtonText = "Cancel",
            DefaultButton = sameDocument ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary &&
            _vm.RestoreCommand.CanExecute(null))
            await _vm.RestoreCommand.ExecuteAsync(null);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedVersionButton);
    }

    private void OnUnifiedToggleClick(object sender, RoutedEventArgs e) => SetMode(HistoryDiffMode.Unified);
    private void OnSplitToggleClick(object sender, RoutedEventArgs e) => SetMode(HistoryDiffMode.Split);
    private void OnPreviewToggleClick(object sender, RoutedEventArgs e) => SetMode(HistoryDiffMode.Preview);

    private void SetMode(HistoryDiffMode mode)
    {
        UnifiedToggle.IsChecked = mode == HistoryDiffMode.Unified;
        SplitToggle.IsChecked = mode == HistoryDiffMode.Split;
        PreviewToggle.IsChecked = mode == HistoryDiffMode.Preview;
        _vm.SetDiffMode(mode);
        UpdateCopyToolbarButton(mode);
        if (DiffSearchPill != null)
        {
            DiffSearchPill.Visibility = mode != HistoryDiffMode.Preview && _vm.HasDiffStats ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateDiffSearchMatches();
        // Wait one layout pass so the WebView has its real width before the page measures it.
        if (mode == HistoryDiffMode.Preview)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, RenderPreview);
    }

    private async void OnTakeSnapshotClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsSelectedFileOpen)
        {
            // The VM explains why (open the document first) rather than a dialog that can't succeed.
            await _vm.TakeSnapshotCommand.ExecuteAsync(null);
            return;
        }

        var input = new TextBox { PlaceholderText = "e.g. Cleaned up the Mermaid diagram", MaxLength = 80, Margin = new Thickness(0, 8, 0, 0) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Checkpoint label");
        input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
        var dialog = new ContentDialog
        {
            Title = "Take a checkpoint",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Saves the editor's current text of {_vm.FileName} as a starred version you can find and restore later.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    input
                }
            },
            PrimaryButtonText = "Save checkpoint",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        bool enterSubmitted = false;
        input.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter)
            {
                ke.Handled = true;
                enterSubmitted = true;
                dialog.Hide();
            }
        };

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.Primary || enterSubmitted)
            await _vm.TakeSnapshotCommand.ExecuteAsync(input.Text);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedVersionButton);
    }

    private void FocusSelectedVersionButton()
    {
        if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
        {
            btn.Focus(FocusState.Programmatic);
            btn.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.35 });
        }
    }

    private void FocusFileButton(FileSummaryViewModel file)
    {
        if (FindVersionButton(FilesScroll, file) is { } btn)
        {
            btn.Focus(FocusState.Programmatic);
            btn.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.35 });
        }
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        for (var cur = node; cur is not null; cur = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(cur))
        {
            if (ReferenceEquals(cur, ancestor)) return true;
        }
        return false;
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen) return;
        // Keys pressed inside an open version / document menu are the menu's: Down used to move the
        // timeline selection (and close the menu) instead of moving to the next menu item.
        if (PopupKeys.FocusIsOutside(RootGrid)) return;
        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (isCtrl || isShift) return;

        if (e.OriginalSource is TextBox)
        {
            if (ReferenceEquals(e.OriginalSource, SearchBox) && e.Key == VirtualKey.Down && _vm.ShowTimeline)
            {
                e.Handled = true;
                FocusSelectedVersionButton();
            }
            return;
        }

        // If keyboard focus is on a Document button in Pane 1, Up/Down/Home/End navigates documents within Pane 1.
        if (e.OriginalSource is FrameworkElement { DataContext: FileSummaryViewModel currentFile })
        {
            int idx = _vm.Files.IndexOf(currentFile);
            if (e.Key == VirtualKey.Down && idx >= 0 && idx + 1 < _vm.Files.Count)
            {
                e.Handled = true;
                var next = _vm.Files[idx + 1];
                _ = _vm.SelectFileCommand.ExecuteAsync(next);
                FocusFileButton(next);
            }
            else if (e.Key == VirtualKey.Up && idx > 0)
            {
                e.Handled = true;
                var prev = _vm.Files[idx - 1];
                _ = _vm.SelectFileCommand.ExecuteAsync(prev);
                FocusFileButton(prev);
            }
            else if (e.Key == VirtualKey.Home && idx > 0 && _vm.Files.Count > 0)
            {
                e.Handled = true;
                var first = _vm.Files[0];
                _ = _vm.SelectFileCommand.ExecuteAsync(first);
                FocusFileButton(first);
            }
            else if (e.Key == VirtualKey.End && idx >= 0 && idx + 1 < _vm.Files.Count)
            {
                e.Handled = true;
                var last = _vm.Files[^1];
                _ = _vm.SelectFileCommand.ExecuteAsync(last);
                FocusFileButton(last);
            }
            else if (e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)
            {
                e.Handled = true;
            }
            return;
        }

        // Allow native scrolling when focus is inside Pane 3's diff ScrollViewers.
        var srcDep = e.OriginalSource as DependencyObject;
        if (IsDescendantOf(srcDep, UnifiedScroll) || IsDescendantOf(srcDep, SplitScroll))
            return;

        if (e.Key == VirtualKey.Down && _vm.SelectNextVersion())
        {
            e.Handled = true;
            FocusSelectedVersionButton();
        }
        else if (e.Key == VirtualKey.Up && _vm.SelectPreviousVersion())
        {
            e.Handled = true;
            FocusSelectedVersionButton();
        }
        else if (e.Key == VirtualKey.Home && _vm.SelectFirstVersion())
        {
            e.Handled = true;
            FocusSelectedVersionButton();
        }
        else if (e.Key == VirtualKey.End && _vm.SelectLastVersion())
        {
            e.Handled = true;
            FocusSelectedVersionButton();
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen) return;
        // S, F2, Delete and Ctrl+C inside an open menu starred, renamed, deleted or copied the version behind it.
        if (PopupKeys.FocusIsOutside(RootGrid)) return;
        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isAlt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool inTextBox = e.OriginalSource is TextBox;
        bool inFileList = e.OriginalSource is FrameworkElement { DataContext: FileSummaryViewModel };
        var focusedVersion = (e.OriginalSource as FrameworkElement)?.DataContext as VersionItemViewModel;

        if (isCtrl && e.Key == VirtualKey.F)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (isCtrl && isShift && e.Key == VirtualKey.S)
        {
            // Advertised on the Take Checkpoint button's tooltip.
            e.Handled = true;
            OnTakeSnapshotClick(this, new RoutedEventArgs());
        }
        else if (isCtrl && !isShift && e.Key == VirtualKey.Enter && !inTextBox && _vm.HasSelection)
        {
            e.Handled = true;
            _ = ConfirmAndRestoreAsync();
        }
        else if (isCtrl && !isShift && e.Key == VirtualKey.Number1)
        {
            e.Handled = true;
            SetMode(HistoryDiffMode.Unified);
        }
        else if (isCtrl && !isShift && e.Key == VirtualKey.Number2)
        {
            e.Handled = true;
            SetMode(HistoryDiffMode.Split);
        }
        else if (isCtrl && !isShift && e.Key == VirtualKey.Number3)
        {
            e.Handled = true;
            SetMode(HistoryDiffMode.Preview);
        }
        else if (isCtrl && isShift && e.Key == VirtualKey.C && !inTextBox && (focusedVersion ?? _vm.Selected) is { } patchTarget)
        {
            e.Handled = true;
            _ = CopyDiffPatchAsync(patchTarget, CopyToolbarButton, CopyToolbarIcon);
        }
        else if (isCtrl && !isShift && e.Key == VirtualKey.C && !inTextBox && (focusedVersion ?? _vm.Selected) is { } copyTarget)
        {
            e.Handled = true;
            _ = CopyVersionMarkdownAsync(copyTarget, CopyToolbarButton, CopyToolbarIcon);
        }
        else if (e.Key == VirtualKey.F5 && !isCtrl && !isAlt)
        {
            // Advertised as F5 on the version and Documents-header menus.
            e.Handled = true;
            _ = RefreshHistoryAsync();
        }
        else if (e.Key == VirtualKey.F7 || (isAlt && e.Key == VirtualKey.Down))
        {
            e.Handled = true;
            StepToChangeHunk(isShift ? -1 : 1);
        }
        else if (isAlt && e.Key == VirtualKey.Up)
        {
            e.Handled = true;
            StepToChangeHunk(-1);
        }
        else if (e.Key == VirtualKey.F3)
        {
            e.Handled = true;
            StepToSearchMatch(isShift ? -1 : 1);
        }
        else if (isCtrl && isShift && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            DiffSearchBox?.Focus(FocusState.Programmatic);
            DiffSearchBox?.SelectAll();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (inTextBox && ReferenceEquals(e.OriginalSource, SearchBox))
            {
                if (!string.IsNullOrEmpty(_vm.SearchQuery))
                {
                    _vm.SearchQuery = "";
                    SearchBox.Text = "";
                }
                else
                {
                    if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
                        btn.Focus(FocusState.Programmatic);
                    else
                        TimelineScroll.Focus(FocusState.Programmatic);
                }
                e.Handled = true;
            }
            else if (inTextBox && !string.IsNullOrEmpty(_vm.SearchQuery))
            {
                _vm.SearchQuery = "";
                e.Handled = true;
            }
            else if (_vm.IsStarredOnlyFilter || !string.IsNullOrEmpty(_vm.SearchQuery))
            {
                _vm.ClearFiltersCommand.Execute(null);
                SearchBox.Text = "";
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedVersionButton);
                e.Handled = true;
            }
        }
        else if (!inTextBox && !inFileList && !isCtrl && e.Key == VirtualKey.S && (focusedVersion ?? _vm.Selected) is { } toStar)
        {
            e.Handled = true;
            _ = _vm.ToggleStarCommand.ExecuteAsync(toStar);
        }
        else if (!inTextBox && ((isShift && e.Key == VirtualKey.F10) || e.Key == VirtualKey.Application))
        {
            if (e.OriginalSource is FrameworkElement { DataContext: FileSummaryViewModel fileTarget } fileFe)
            {
                e.Handled = true;
                BuildFileMenu(fileTarget).ShowAt(fileFe, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
            }
            else if ((focusedVersion ?? _vm.Selected) is { } menuTarget)
            {
                e.Handled = true;
                var anchor = (FrameworkElement?)FindVersionButton(TimelineScroll, menuTarget) ?? TimelineScroll;
                BuildVersionMenu(menuTarget).ShowAt(anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
            }
        }
        else if (!inTextBox && !inFileList && e.Key == VirtualKey.F2 && (focusedVersion ?? _vm.Selected) is { } toRename)
        {
            e.Handled = true;
            _ = RenameAsync(toRename);
        }
        else if (!inTextBox && !inFileList && e.Key == VirtualKey.Delete && (focusedVersion ?? _vm.Selected) is { } toDelete)
        {
            e.Handled = true;
            _ = DeleteAsync(toDelete);
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Enter or VirtualKey.Down)
        {
            e.Handled = true;
            if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
            {
                btn.Focus(FocusState.Programmatic);
                btn.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
            }
            else
            {
                TimelineScroll.Focus(FocusState.Programmatic);
            }
        }
        else if (e.Key is VirtualKey.Up)
        {
            e.Handled = true;
            if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
            {
                btn.Focus(FocusState.Programmatic);
                btn.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
            }
            else
            {
                TimelineScroll.Focus(FocusState.Programmatic);
            }
        }
        else if (e.Key is VirtualKey.Escape)
        {
            e.Handled = true;
            if (!string.IsNullOrEmpty(_vm.SearchQuery) || !string.IsNullOrEmpty(SearchBox.Text))
            {
                _vm.SearchQuery = string.Empty;
                SearchBox.Text = string.Empty;
                SearchBox.Focus(FocusState.Programmatic);
            }
            else
            {
                if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
                    btn.Focus(FocusState.Programmatic);
                else
                    TimelineScroll.Focus(FocusState.Programmatic);
            }
        }
    }

    private void OnTimelineClearSearchClick(object sender, RoutedEventArgs e)
    {
        // Move focus first: clearing the query collapses the pill holding this button.
        SearchBox.Focus(FocusState.Programmatic);
        _vm.SearchQuery = string.Empty;
        SearchBox.Text = string.Empty;
        if (KindFilterLabel != null) KindFilterLabel.Text = "All Kinds";
    }

    private void OnKindFilterButtonClick(object sender, RoutedEventArgs e)
    {
        KindFilterFlyout?.ShowAt(sender as FrameworkElement ?? KindFilterButton);
    }

    private void OnKindFilterButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        KindFilterFlyout?.ShowAt(sender as FrameworkElement ?? KindFilterButton);
    }

    private void OnKindFilterAllClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        _vm.SearchQuery = string.Empty;
        if (KindFilterLabel != null) KindFilterLabel.Text = "All Kinds";
    }

    private void OnKindFilterCheckpointsClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "Checkpoint";
        _vm.SearchQuery = "Checkpoint";
        if (KindFilterLabel != null) KindFilterLabel.Text = "Checkpoints";
    }

    private void OnKindFilterAutoSavesClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "Auto-Save";
        _vm.SearchQuery = "Auto-Save";
        if (KindFilterLabel != null) KindFilterLabel.Text = "Auto-Saves";
    }

    private void OnKindFilterExportsClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "Export";
        _vm.SearchQuery = "Export";
        if (KindFilterLabel != null) KindFilterLabel.Text = "Exports";
    }

    // The search box's options menu, opened by right-click or Shift+F10 while nothing is selected
    // (TextBoxMenu keeps the box's own Cut / Copy / Paste menu for a selection).
    private void FillSearchBoxMenu(MenuFlyout menu)
    {
        var focusTimeline = new MenuFlyoutItem
        {
            Text = "Focus timeline",
            KeyboardAcceleratorTextOverride = "Enter",
            Icon = new FontIcon { Glyph = "\uE70D" }
        };
        focusTimeline.Click += (_, _) =>
        {
            if (_vm.Selected is { } sel && FindVersionButton(TimelineScroll, sel) is { } btn)
            {
                btn.Focus(FocusState.Programmatic);
                btn.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3 });
            }
            else
            {
                TimelineScroll.Focus(FocusState.Programmatic);
            }
        };
        menu.Items.Add(focusTimeline);

        var clearItem = new MenuFlyoutItem
        {
            Text = "Clear search filter",
            KeyboardAcceleratorTextOverride = "Esc",
            Icon = new FontIcon { Glyph = "\uE894" },
            IsEnabled = !string.IsNullOrEmpty(_vm.SearchQuery) || !string.IsNullOrEmpty(SearchBox.Text)
        };
        clearItem.Click += (_, _) =>
        {
            _vm.SearchQuery = "";
            SearchBox.Text = "";
            SearchBox.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(clearItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var starToggle = new ToggleMenuFlyoutItem
        {
            Text = "Show starred versions only",
            Icon = new FontIcon { Glyph = "\uE734" },
            IsChecked = _vm.IsStarredOnlyFilter
        };
        starToggle.Click += (_, _) =>
        {
            _vm.IsStarredOnlyFilter = !_vm.IsStarredOnlyFilter;
        };
        menu.Items.Add(starToggle);

        var checkpointItem = new MenuFlyoutItem
        {
            Text = "Take checkpoint",
            Icon = new FontIcon { Glyph = "\uE74E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+S"
        };
        checkpointItem.Click += (_, _) => OnTakeSnapshotClick(this, new RoutedEventArgs());
        menu.Items.Add(checkpointItem);
    }

    private void OnFileNameRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var file = _vm.SelectedFile;
        var filePath = file?.Summary.FilePath ?? _vm.FileName;
        var menu = new MenuFlyout();

        var copyPath = new MenuFlyoutItem
        {
            Text = "Copy full file path",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPath.Click += (_, _) =>
        {
            CopyAndNotify(filePath, $"Copied file path: {filePath}");
        };
        menu.Items.Add(copyPath);

        var copyName = new MenuFlyoutItem
        {
            Text = "Copy file name",
            Icon = new FontIcon { Glyph = "\uE8A5" }
        };
        copyName.Click += (_, _) =>
        {
            var name = file?.FileName ?? Path.GetFileName(filePath);
            CopyAndNotify(name, $"Copied file name: {name}");
        };
        menu.Items.Add(copyName);

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var showInExplorer = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uEC50" }
            };
            showInExplorer.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\""); }
                catch { }
            };
            menu.Items.Add(showInExplorer);

            var openInEditor = new MenuFlyoutItem
            {
                Text = "Open in editor",
                Icon = new FontIcon { Glyph = "\uE8E5" },
                IsEnabled = _main is not null
            };
            openInEditor.Click += async (_, _) =>
            {
                if (_main is null || !File.Exists(filePath)) return;
                if (string.Equals(_main.InputFilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    _main.InputFilePath = string.Empty;
                _main.InputFilePath = filePath;
                _main.UsePasteSource = false;
                await _main.LastFileReadTask;
                await _vm.ShowFileAsync(filePath);
                _vm.ShowNotice($"Opened {Path.GetFileName(filePath)} in the editor.");
            };
            menu.Items.Add(openInEditor);
        }

        menu.ShowAt(FileNameButton, KeyboardContextMenu.PointIn(e, FileNameButton));
    }

    private void OnFileNameButtonClick(object sender, RoutedEventArgs e)
    {
        var filePath = _vm.SelectedFile?.Summary.FilePath ?? _vm.FileName;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            CopyAndNotify(filePath, $"Copied file path: {filePath}");
        }
    }

    private void OnTimelineBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer and not StackPanel and not Border) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        var checkpointItem = new MenuFlyoutItem
        {
            Text = "Take checkpoint",
            Icon = new FontIcon { Glyph = "\uE74E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+S"
        };
        checkpointItem.Click += (_, _) => OnTakeSnapshotClick(this, new RoutedEventArgs());
        menu.Items.Add(checkpointItem);

        var clearFilter = new MenuFlyoutItem
        {
            Text = "Clear filters",
            Icon = new FontIcon { Glyph = "\uE894" },
            IsEnabled = _vm.IsStarredOnlyFilter || !string.IsNullOrEmpty(_vm.SearchQuery)
        };
        clearFilter.Click += (_, _) =>
        {
            _vm.ClearFiltersCommand.Execute(null);
            SearchBox.Text = "";
        };
        menu.Items.Add(clearFilter);

        menu.ShowAt(TimelineScroll, KeyboardContextMenu.PointIn(e, TimelineScroll));
    }

    private void OnDiffSummaryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        // A double-click on the version-ID button bubbles here after its two Clicks copied the ID;
        // copying the title on top would leave the wrong text on the clipboard.
        if (IsWithin(e.OriginalSource as DependencyObject, CommitShaCopyPill)) return;
        CopyDiffTitle();
    }

    // Double-click on the summary, or "Copy version title" in its menu (Shift+F10 on the version ID).
    private void CopyDiffTitle()
    {
        var title = _vm.DiffTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            CopyAndNotify(title, $"Copied version title: {title}");
        }
    }

    // The diff tools collapse with the diff stats (Preview, an unchanged version, no selection).
    // Before run #228 the hunk counter and version ID were not focusable; now they are, so a
    // keyboard user on one of them would be left with focus on nothing. Land on the view switcher.
    private void HandFocusOffCollapsingDiffTools()
    {
        if (FocusedElement() is not DependencyObject focused) return;
        UIElement[] collapsing = { DiffChangeStepperPill, DiffSearchPill, DeltaMeterButton, DiffWordDeltaPill,
            DiffWrapButton, DiffLineJumpButton, DiffIgnoreWhitespaceButton, CommitShaCopyPill, CopyToolbarButton };
        if (!collapsing.Any(c => IsWithin(focused, c))) return;
        var target = PreviewToggle.IsChecked == true ? PreviewToggle : SplitToggle.IsChecked == true ? SplitToggle : UnifiedToggle;
        target.Focus(FocusState.Programmatic);
    }

    private static bool IsWithin(DependencyObject? start, DependencyObject ancestor)
    {
        for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private void OnCommitShaCopyPillClick(object sender, RoutedEventArgs e)
    {
        var sha = _vm.Selected?.Id;
        if (!string.IsNullOrWhiteSpace(sha))
        {
            CopyAndNotify(sha, $"Copied version ID: {sha}");
        }
    }

    private void OnDiffSummaryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowDiffAreaContextMenu(DiffSummaryPanel, KeyboardContextMenu.PointIn(e, DiffSummaryPanel));
    }

    private void OnDeltaMeterClick(object sender, RoutedEventArgs e) => CopyDiffStats();

    private void CopyDiffStats()
    {
        var stats = _vm.DiffStats;
        if (!string.IsNullOrWhiteSpace(stats))
        {
            CopyAndNotify(stats, $"Copied diff statistics: {stats}");
        }
    }

    private void OnDeltaMeterRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowDiffAreaContextMenu(DeltaMeterButton, KeyboardContextMenu.PointIn(e, DeltaMeterButton));
    }

    // One click, one copy: the pill used to copy on Tapped and again on DoubleTapped, so a
    // double-click overwrote the short form with the detailed one. The detailed copy is in the menu.
    private void OnDiffWordDeltaPillClick(object sender, RoutedEventArgs e)
    {
        int net = _lastAddedWords - _lastRemovedWords;
        string netSign = net >= 0 ? $"+{net:N0}" : $"{net:N0}";
        string text = $"+{_lastAddedWords:N0} / -{_lastRemovedWords:N0} words (net {netSign})";
        CopyAndNotify(text, $"Copied word delta statistics: {text}");
    }

    private void CopyDetailedWordDelta()
    {
        int net = _lastAddedWords - _lastRemovedWords;
        string netSign = net >= 0 ? $"+{net:N0}" : $"{net:N0}";
        var target = _vm.Selected;
        string label = target is not null ? $" for version {target.TimestampLabel}" : "";
        string detailed = $"Diff word delta{label}: +{_lastAddedWords:N0} added, -{_lastRemovedWords:N0} removed, net {netSign} words.";
        CopyAndNotify(detailed, $"Copied detailed word delta: {detailed}");
    }

    private void OnDiffWordDeltaPillRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var copyWordDelta = new MenuFlyoutItem
        {
            Text = "Copy word delta",
            Icon = new FontIcon { Glyph = "\uE8C1" }
        };
        copyWordDelta.Click += (_, _) =>
        {
            int net = _lastAddedWords - _lastRemovedWords;
            string netSign = net >= 0 ? $"+{net:N0}" : $"{net:N0}";
            string text = $"+{_lastAddedWords:N0} / -{_lastRemovedWords:N0} words (net {netSign})";
            CopyAndNotify(text, $"Copied word delta: {text}");
        };
        menu.Items.Add(copyWordDelta);

        var copyDetailed = new MenuFlyoutItem
        {
            Text = "Copy detailed word delta",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyDetailed.Click += (_, _) => CopyDetailedWordDelta();
        menu.Items.Add(copyDetailed);

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            IsEnabled = _vm.HasSelection
        };
        copyPatch.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyPatch);

        var copyMarkdown = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+C",
            IsEnabled = _vm.HasSelection
        };
        copyMarkdown.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyMarkdown);

        menu.ShowAt(DiffWordDeltaPill, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, DiffWordDeltaPill) });
    }

    private void OnDiffAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
        {
            ShowDiffAreaContextMenu(fe, KeyboardContextMenu.PointIn(e, fe));
        }
    }

    private void ShowDiffAreaContextMenu(FrameworkElement anchor, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();

        // The line-delta meter opens this menu, so its own copy belongs in it.
        if (_vm.HasDiffStats && !string.IsNullOrWhiteSpace(_vm.DiffStats))
        {
            var copyStats = new MenuFlyoutItem
            {
                Text = $"Copy line delta ({_vm.DiffStats})",
                Icon = new FontIcon { Glyph = "\uE9D2" }
            };
            copyStats.Click += (_, _) => CopyDiffStats();
            menu.Items.Add(copyStats);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // The summary's double-click copy, reachable from the keyboard: Shift+F10 on the version ID opens this menu.
        if (_vm.HasSelection && !string.IsNullOrWhiteSpace(_vm.DiffTitle))
        {
            var copyTitle = new MenuFlyoutItem
            {
                Text = "Copy version title",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyTitle.Click += (_, _) => CopyDiffTitle();
            menu.Items.Add(copyTitle);
        }

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            IsEnabled = _vm.HasSelection
        };
        copyPatch.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyPatch);

        var copyMarkdown = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            IsEnabled = _vm.HasSelection
        };
        copyMarkdown.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyMarkdown);

        if (_vm.HasDiffStats)
        {
            var copyStats = new MenuFlyoutItem
            {
                Text = $"Copy diff statistics ({_vm.DiffStats})",
                Icon = new FontIcon { Glyph = "\uE946" }
            };
            copyStats.Click += (_, _) =>
            {
                var stats = _vm.DiffStats;
                if (!string.IsNullOrWhiteSpace(stats))
                {
                    CopyAndNotify(stats, $"Copied diff statistics: {stats}");
                }
            };
            menu.Items.Add(copyStats);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var restoreItem = new MenuFlyoutItem
        {
            Text = "Restore version to editor",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = _vm.HasSelection
        };
        restoreItem.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restoreItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var unifiedMode = new MenuFlyoutItem
        {
            Text = "Unified diff view",
            Icon = new FontIcon { Glyph = "\uE8D2" },
            KeyboardAcceleratorTextOverride = "Ctrl+1"
        };
        unifiedMode.Click += (_, _) => SetMode(HistoryDiffMode.Unified);
        menu.Items.Add(unifiedMode);

        var splitMode = new MenuFlyoutItem
        {
            Text = "Side by side diff view",
            Icon = new FontIcon { Glyph = "\uE8D3" },
            KeyboardAcceleratorTextOverride = "Ctrl+2"
        };
        splitMode.Click += (_, _) => SetMode(HistoryDiffMode.Split);
        menu.Items.Add(splitMode);

        var previewMode = new MenuFlyoutItem
        {
            Text = "Rendered document preview",
            Icon = new FontIcon { Glyph = "\uE7B3" },
            KeyboardAcceleratorTextOverride = "Ctrl+3"
        };
        previewMode.Click += (_, _) => SetMode(HistoryDiffMode.Preview);
        menu.Items.Add(previewMode);

        menu.ShowAt(anchor, position);
    }

    private void OnStarredFilterToggleRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var starredOnly = new MenuFlyoutItem
        {
            Text = "Show starred versions only",
            Icon = new FontIcon { Glyph = "\uE735" }
        };
        starredOnly.Click += (_, _) => _vm.IsStarredOnlyFilter = true;
        menu.Items.Add(starredOnly);

        var showAll = new MenuFlyoutItem
        {
            Text = "Show all versions",
            Icon = new FontIcon { Glyph = "\uE734" }
        };
        showAll.Click += (_, _) => _vm.IsStarredOnlyFilter = false;
        menu.Items.Add(showAll);

        var checkpointsOnly = new MenuFlyoutItem
        {
            Text = "Filter checkpoints only",
            Icon = new FontIcon { Glyph = "\uE74E" }
        };
        checkpointsOnly.Click += (_, _) =>
        {
            _vm.SearchQuery = "Checkpoint";
            SearchBox.Text = "Checkpoint";
        };
        menu.Items.Add(checkpointsOnly);

        var clearFilter = new MenuFlyoutItem
        {
            Text = "Clear all filters",
            Icon = new FontIcon { Glyph = "\uE894" },
            IsEnabled = _vm.IsStarredOnlyFilter || !string.IsNullOrEmpty(_vm.SearchQuery) || !string.IsNullOrEmpty(SearchBox.Text)
        };
        clearFilter.Click += (_, _) =>
        {
            _vm.IsStarredOnlyFilter = false;
            _vm.SearchQuery = "";
            SearchBox.Text = "";
            SearchBox.Focus(FocusState.Programmatic);
        };
        menu.Items.Add(clearFilter);

        menu.Items.Add(new MenuFlyoutSeparator());

        var reload = new MenuFlyoutItem
        {
            Text = "Reload history",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        reload.Click += (_, _) => _ = _vm.LoadAsync();
        menu.Items.Add(reload);

        if (sender is FrameworkElement fe)
            menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void OnUnifiedDiffRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is LineDiff.UnifiedRow row)
        {
            if (!row.IsGap && !string.IsNullOrEmpty(row.Text))
            {
                CopyAndNotify(row.Text, $"Copied line: {row.Text}");
            }
        }
    }

    private void OnUnifiedDiffRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        var row = fe.DataContext as LineDiff.UnifiedRow;
        var menu = new MenuFlyout();

        if (row != null && !row.IsGap)
        {
            var copyLine = new MenuFlyoutItem
            {
                Text = "Copy line text",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyLine.Click += (_, _) =>
            {
                CopyAndNotify(row.Text, $"Copied line: {row.Text}");
            };
            menu.Items.Add(copyLine);

            var lineWithPrefix = $"{row.Prefix} {row.Text}".Trim();
            var copyWithPrefix = new MenuFlyoutItem
            {
                Text = $"Copy line with prefix ({row.Prefix})",
                Icon = new FontIcon { Glyph = "\uE943" }
            };
            copyWithPrefix.Click += (_, _) =>
            {
                CopyAndNotify(lineWithPrefix, $"Copied line with prefix: {lineWithPrefix}");
            };
            menu.Items.Add(copyWithPrefix);

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            IsEnabled = _vm.HasSelection
        };
        copyPatch.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyPatch);

        var copyMarkdown = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            IsEnabled = _vm.HasSelection
        };
        copyMarkdown.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyMarkdown);

        menu.Items.Add(new MenuFlyoutSeparator());

        var splitMode = new MenuFlyoutItem
        {
            Text = "Side by side diff view",
            Icon = new FontIcon { Glyph = "\uE8D3" },
            KeyboardAcceleratorTextOverride = "Ctrl+2"
        };
        splitMode.Click += (_, _) => SetMode(HistoryDiffMode.Split);
        menu.Items.Add(splitMode);

        var previewMode = new MenuFlyoutItem
        {
            Text = "Rendered document preview",
            Icon = new FontIcon { Glyph = "\uE7B3" },
            KeyboardAcceleratorTextOverride = "Ctrl+3"
        };
        previewMode.Click += (_, _) => SetMode(HistoryDiffMode.Preview);
        menu.Items.Add(previewMode);

        var restoreItem = new MenuFlyoutItem
        {
            Text = "Restore version to editor",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = _vm.HasSelection
        };
        restoreItem.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restoreItem);

        menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void OnUnifiedDiffRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement fe)
        {
            fe.DoubleTapped -= OnUnifiedDiffRowDoubleTapped;
            fe.DoubleTapped += OnUnifiedDiffRowDoubleTapped;
            fe.RightTapped -= OnUnifiedDiffRowRightTapped;
            fe.RightTapped += OnUnifiedDiffRowRightTapped;
        }
    }

    private void OnSplitDiffRepeaterElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement fe)
        {
            fe.DoubleTapped -= OnSplitRowDoubleTapped;
            fe.DoubleTapped += OnSplitRowDoubleTapped;
            fe.RightTapped -= OnSplitRowRightTapped;
            fe.RightTapped += OnSplitRowRightTapped;
        }
    }

    private void OnSplitRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not DiffRowViewModel row || row.IsGap) return;

        var pos = e.GetPosition(fe);
        bool isLeft = pos.X < (fe.ActualWidth / 2.0);
        var cell = isLeft ? row.Left : row.Right;
        if (cell != null && !string.IsNullOrEmpty(cell.Text))
        {
            CopyAndNotify(cell.Text, $"Copied {(isLeft ? "previous" : "current")} line: {cell.Text}");
        }
    }

    private void OnSplitRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not DiffRowViewModel row || row.IsGap) return;

        var pos = KeyboardContextMenu.PointIn(e, fe);
        bool isLeft = pos.X < (fe.ActualWidth / 2.0);
        var cell = isLeft ? row.Left : row.Right;
        ShowSplitRowContextMenu(fe, pos, cell?.Text, isLeft);
    }

    private void ShowSplitRowContextMenu(FrameworkElement fe, Windows.Foundation.Point position, string? lineText, bool isLeft)
    {
        var menu = new MenuFlyout();

        if (!string.IsNullOrEmpty(lineText))
        {
            var copyLine = new MenuFlyoutItem
            {
                Text = isLeft ? "Copy previous line text" : "Copy current line text",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyLine.Click += (_, _) =>
            {
                CopyAndNotify(lineText, $"Copied line: {lineText}");
            };
            menu.Items.Add(copyLine);

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            IsEnabled = _vm.HasSelection
        };
        copyPatch.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyPatch);

        var copyMarkdown = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            IsEnabled = _vm.HasSelection
        };
        copyMarkdown.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyMarkdown);

        menu.Items.Add(new MenuFlyoutSeparator());

        var unifiedMode = new MenuFlyoutItem
        {
            Text = "Unified diff view",
            Icon = new FontIcon { Glyph = "\uE8D2" },
            KeyboardAcceleratorTextOverride = "Ctrl+1"
        };
        unifiedMode.Click += (_, _) => SetMode(HistoryDiffMode.Unified);
        menu.Items.Add(unifiedMode);

        var previewMode = new MenuFlyoutItem
        {
            Text = "Rendered document preview",
            Icon = new FontIcon { Glyph = "\uE7B3" },
            KeyboardAcceleratorTextOverride = "Ctrl+3"
        };
        previewMode.Click += (_, _) => SetMode(HistoryDiffMode.Preview);
        menu.Items.Add(previewMode);

        var restoreItem = new MenuFlyoutItem
        {
            Text = "Restore version to editor",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = _vm.HasSelection
        };
        restoreItem.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restoreItem);

        menu.ShowAt(fe, position);
    }

    private void OnTakeCheckpointButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var checkpointItem = new MenuFlyoutItem
        {
            Text = "Take checkpoint",
            Icon = new FontIcon { Glyph = "\uE74E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+S"
        };
        checkpointItem.Click += (_, _) => OnTakeSnapshotClick(this, new RoutedEventArgs());
        menu.Items.Add(checkpointItem);

        var restoreItem = new MenuFlyoutItem
        {
            Text = "Restore selected version",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = _vm.HasSelection
        };
        restoreItem.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restoreItem);

        var filePath = _vm.SelectedFile?.Summary.FilePath ?? _vm.FileName;
        var showInExplorer = new MenuFlyoutItem
        {
            Text = "Show in File Explorer",
            Icon = new FontIcon { Glyph = "\uEC50" },
            IsEnabled = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath)
        };
        showInExplorer.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\""); }
            catch { }
        };
        menu.Items.Add(showInExplorer);

        var clearFilter = new MenuFlyoutItem
        {
            Text = "Clear filters",
            Icon = new FontIcon { Glyph = "\uE894" },
            IsEnabled = _vm.IsStarredOnlyFilter || !string.IsNullOrEmpty(_vm.SearchQuery)
        };
        clearFilter.Click += (_, _) =>
        {
            _vm.ClearFiltersCommand.Execute(null);
            SearchBox.Text = "";
        };
        menu.Items.Add(clearFilter);

        if (sender is FrameworkElement fe)
            menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void OnRestoreButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var restoreItem = new MenuFlyoutItem
        {
            Text = "Restore version to editor",
            Icon = new FontIcon { Glyph = "\uE7A7" },
            KeyboardAcceleratorTextOverride = "Ctrl+Enter",
            IsEnabled = _vm.HasSelection
        };
        restoreItem.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await ConfirmAndRestoreAsync();
        };
        menu.Items.Add(restoreItem);

        var copyMarkdown = new MenuFlyoutItem
        {
            Text = "Copy version Markdown",
            Icon = new FontIcon { Glyph = "\uE8C8" },
            KeyboardAcceleratorTextOverride = "Ctrl+C",
            IsEnabled = _vm.HasSelection
        };
        copyMarkdown.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyVersionMarkdownAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyMarkdown);

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C",
            IsEnabled = _vm.HasSelection
        };
        copyPatch.Click += async (_, _) =>
        {
            if (_vm.Selected is not null)
                await CopyDiffPatchAsync(_vm.Selected, CopyToolbarButton, CopyToolbarIcon);
        };
        menu.Items.Add(copyPatch);

        if (sender is FrameworkElement fe)
            menu.ShowAt(fe, KeyboardContextMenu.PointIn(e, fe));
    }

    private void OnDiffModeToggleRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
        {
            ShowDiffAreaContextMenu(fe, KeyboardContextMenu.PointIn(e, fe));
        }
    }

    private void OnClearFiltersButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var clearFilter = new MenuFlyoutItem
        {
            Text = "Clear search and filters",
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearFilter.Click += (_, _) =>
        {
            _vm.ClearFiltersCommand.Execute(null);
            SearchBox.Text = "";
        };
        menu.Items.Add(clearFilter);

        var showAll = new MenuFlyoutItem
        {
            Text = "Show all versions",
            Icon = new FontIcon { Glyph = "\uE734" }
        };
        showAll.Click += (_, _) =>
        {
            _vm.IsStarredOnlyFilter = false;
        };
        menu.Items.Add(showAll);

        var showStarred = new MenuFlyoutItem
        {
            Text = "Show starred versions only",
            Icon = new FontIcon { Glyph = "\uE735" }
        };
        showStarred.Click += (_, _) =>
        {
            _vm.IsStarredOnlyFilter = true;
        };
        menu.Items.Add(showStarred);

        menu.Items.Add(new MenuFlyoutSeparator());

        var reloadHistory = new MenuFlyoutItem
        {
            Text = "Reload version history",
            Icon = new FontIcon { Glyph = "\uE72C" }
        };
        reloadHistory.Click += async (_, _) =>
        {
            await _vm.LoadCommand.ExecuteAsync(null);
        };
        menu.Items.Add(reloadHistory);

        var anchor = sender as FrameworkElement ?? (FrameworkElement)ClearFiltersButton;
        menu.ShowAt(anchor, KeyboardContextMenu.PointIn(e, anchor));
    }

    // Each row's star is generated by the timeline template, so it is bridged as it loads.
    private void OnStarButtonLoaded(object sender, RoutedEventArgs e) =>
        KeyboardContextMenu.BridgeOnLoad(sender, OnStarButtonRightTapped);

    private void OnStarButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item } fe) return;
        e.Handled = true;
        _vm.SelectVersionCommand.Execute(item);
        var menu = BuildStarButtonMenu(item);
        menu.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }

    private MenuFlyout BuildStarButtonMenu(VersionItemViewModel item)
    {
        var menu = new MenuFlyout();

        var toggleStar = new MenuFlyoutItem
        {
            Text = item.IsStarred ? "Remove star" : "Star this version",
            Icon = new FontIcon { Glyph = item.IsStarred ? "\uE735" : "\uE734" },
            KeyboardAcceleratorTextOverride = "S"
        };
        toggleStar.Click += (_, _) => _ = _vm.ToggleStarCommand.ExecuteAsync(item);
        menu.Items.Add(toggleStar);

        var filterStarred = new MenuFlyoutItem
        {
            Text = _vm.IsStarredOnlyFilter ? "Show all versions" : "Show starred versions only",
            Icon = new FontIcon { Glyph = "\uE734" }
        };
        filterStarred.Click += (_, _) => _vm.IsStarredOnlyFilter = !_vm.IsStarredOnlyFilter;
        menu.Items.Add(filterStarred);

        menu.Items.Add(new MenuFlyoutSeparator());

        var copyInfo = new MenuFlyoutItem
        {
            Text = "Copy timestamp & label",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyInfo.Click += (_, _) =>
        {
            CopyAndNotify($"{item.TimestampLabel} — {item.Snippet}", $"Copied version info for {item.TimestampLabel}.");
        };
        menu.Items.Add(copyInfo);

        var moreActions = new MenuFlyoutItem
        {
            Text = "More actions…",
            Icon = new FontIcon { Glyph = "\uE712" }
        };
        moreActions.Click += (_, _) =>
        {
            if (FindVersionButton(TimelineScroll, item) is { } btn)
                BuildVersionMenu(item).ShowAt(btn);
        };
        menu.Items.Add(moreActions);

        return menu;
    }

    private void OnVersionDeltaBadgeDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item }) return;
        e.Handled = true;
        _vm.SelectVersionCommand.Execute(item);
        CopyVersionDelta(item);
    }

    private void CopyVersionDelta(VersionItemViewModel item)
    {
        CopyAndNotify(item.DeltaBadge, $"Copied line delta {item.DeltaBadge} to clipboard.");
    }

    private void OnVersionDeltaBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item } fe) return;
        e.Handled = true;
        _vm.SelectVersionCommand.Execute(item);
        var menu = BuildDeltaBadgeMenu(item);
        menu.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }

    private MenuFlyout BuildDeltaBadgeMenu(VersionItemViewModel item)
    {
        var menu = new MenuFlyout();

        var copyDelta = new MenuFlyoutItem
        {
            Text = $"Copy line delta ({item.DeltaBadge})",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyDelta.Click += (_, _) => CopyVersionDelta(item);
        menu.Items.Add(copyDelta);

        var copyPatch = new MenuFlyoutItem
        {
            Text = "Copy diff patch",
            Icon = new FontIcon { Glyph = "\uE943" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+C"
        };
        copyPatch.Click += async (_, _) =>
        {
            if (!ReferenceEquals(_vm.Selected, item))
                _vm.SelectVersionCommand.Execute(item);
            await CopyDiffPatchAsync(item);
        };
        menu.Items.Add(copyPatch);

        menu.Items.Add(new MenuFlyoutSeparator());

        var viewUnified = new MenuFlyoutItem
        {
            Text = "Unified diff view",
            Icon = new FontIcon { Glyph = "\uE8A5" },
            KeyboardAcceleratorTextOverride = "Ctrl+1"
        };
        viewUnified.Click += (_, _) => SetMode(HistoryDiffMode.Unified);
        menu.Items.Add(viewUnified);

        var viewSplit = new MenuFlyoutItem
        {
            Text = "Side by side diff view",
            Icon = new FontIcon { Glyph = "\uE8A5" },
            KeyboardAcceleratorTextOverride = "Ctrl+2"
        };
        viewSplit.Click += (_, _) => SetMode(HistoryDiffMode.Split);
        menu.Items.Add(viewSplit);

        return menu;
    }

    private void OnTimelineHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        TimelineScroll.ChangeView(null, 0, null);
        _vm.SelectFirstVersion();
    }

    private void OnTimelineJumpLatestClick(object sender, RoutedEventArgs e)
    {
        TimelineScroll?.ChangeView(null, 0, null);
        _vm.SelectFirstVersion();
    }

    private void OnTimelineJumpOldestClick(object sender, RoutedEventArgs e)
    {
        TimelineScroll?.ChangeView(null, TimelineScroll.ScrollableHeight, null);
        _vm.SelectLastVersion();
    }

    private void OnSelectedHeaderButtonClick(object sender, RoutedEventArgs e)
    {
        var header = _vm.SelectedHeader;
        if (string.IsNullOrWhiteSpace(header)) return;
        CopyAndNotify(header, $"Copied version timestamp: {header}");
    }

    // Copy, then say so only if it worked. A clipboard manager or remote desktop session holding
    // the clipboard used to leave "Copied …" on the notice bar while nothing had been copied.
    private async void CopyAndNotify(string text, string message)
    {
        var error = await Services.ClipboardWriter.TrySetTextAsync(text);
        if (error is null) _vm.ShowNotice(message);
        else _vm.ShowNotice($"Couldn't copy: {error}", isError: true);
    }

    // The same, plus the Copy toolbar button's tick ("✓ 3 added", or "⚠ Busy" when nothing was copied).
    // These copies called ClipboardWriter.Set bare: on a busy clipboard it threw after its retries, the
    // app-wide handler swallowed it, and the click did nothing at all.
    private async void CopyAndNotify(string text, string message, Button? tickButton, string tickLabel)
    {
        var error = await Services.ClipboardWriter.TrySetTextAsync(text);
        if (error is null) _vm.ShowNotice(message);
        else _vm.ShowNotice($"Couldn't copy: {error}", isError: true);
        if (tickButton is not null) CopyFeedback.ApplyButtonTick(tickButton, error, tickLabel);
    }

    private void OnTimelineFilterStatusPillRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        TimelineFilterFlyout?.ShowAt(sender as FrameworkElement ?? TimelineFilterStatusPill);
    }

    private void OnFilterAllVersionsClick(object sender, RoutedEventArgs e)
    {
        _vm.ClearFiltersCommand.Execute(null);
        UpdateTimelineFilterStatus();
    }

    private void OnFilterStarredOnlyClick(object sender, RoutedEventArgs e)
    {
        _vm.IsStarredOnlyFilter = true;
        UpdateTimelineFilterStatus();
    }

    private void OnFilterCheckpointsClick(object sender, RoutedEventArgs e)
    {
        _vm.SearchQuery = "snapshot";
        UpdateTimelineFilterStatus();
    }

    private void OnFilterExportsClick(object sender, RoutedEventArgs e)
    {
        _vm.SearchQuery = "export";
        UpdateTimelineFilterStatus();
    }

    private void OnFilterAutoSavesClick(object sender, RoutedEventArgs e)
    {
        _vm.SearchQuery = "save";
        UpdateTimelineFilterStatus();
    }

    private void OnFilterClearAllClick(object sender, RoutedEventArgs e)
    {
        _vm.ClearFiltersCommand.Execute(null);
        UpdateTimelineFilterStatus();
    }

    private void UpdateTimelineFilterStatus()
    {
        if (TimelineFilterStatusText is null) return;
        string spoken;
        if (_vm.IsStarredOnlyFilter)
        {
            TimelineFilterStatusText.Text = spoken = "Starred";
        }
        else if (!string.IsNullOrWhiteSpace(_vm.SearchQuery))
        {
            spoken = _vm.SearchQuery;
            TimelineFilterStatusText.Text = _vm.SearchQuery.Length > 10 ? _vm.SearchQuery[..9] + "…" : _vm.SearchQuery;
        }
        else
        {
            TimelineFilterStatusText.Text = spoken = "All";
        }
        // The pill trims long queries; screen readers get the whole filter.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TimelineFilterStatusPill, $"Timeline filter: {spoken}");
    }

    private void OnTimelineHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        if (!string.IsNullOrWhiteSpace(_vm.SelectedHeader))
        {
            var copyHeader = new MenuFlyoutItem
            {
                Text = $"Copy version timestamp ({_vm.SelectedHeader})",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyHeader.Click += (_, _) =>
            {
                CopyAndNotify(_vm.SelectedHeader, $"Copied version timestamp: {_vm.SelectedHeader}");
            };
            menu.Items.Add(copyHeader);

            if (_vm.Selected is { } sel)
            {
                var deltaSummary = MarkSmith.Services.LineDiff.FormatVersionDeltaSummary(sel.Label, sel.LinesAdded, sel.LinesRemoved);
                var copyDelta = new MenuFlyoutItem
                {
                    Text = $"Copy version delta ({deltaSummary})",
                    Icon = new FontIcon { Glyph = "\uE9F9" }
                };
                copyDelta.Click += (_, _) =>
                {
                    CopyAndNotify(deltaSummary, $"Copied version delta: {deltaSummary}");
                };
                menu.Items.Add(copyDelta);

                var cardSummary = MarkSmith.Services.LineDiff.FormatVersionStatsCard(sel.Label, sel.Entry.CreatedAt, sel.LinesAdded, sel.LinesRemoved);
                var copyCard = new MenuFlyoutItem
                {
                    Text = "Copy version summary card",
                    Icon = new FontIcon { Glyph = "\uE8A5" }
                };
                copyCard.Click += (_, _) =>
                {
                    CopyAndNotify(cardSummary, $"Copied version card: {cardSummary}");
                };
                menu.Items.Add(copyCard);

                var auditRecord = MarkSmith.Services.LineDiff.FormatVersionAuditRecord(sel.Label, sel.Entry.CreatedAt, sel.LinesAdded, sel.LinesRemoved);
                var copyAudit = new MenuFlyoutItem
                {
                    Text = "Copy version audit record",
                    Icon = new FontIcon { Glyph = "\uE81C" }
                };
                copyAudit.Click += (_, _) =>
                {
                    CopyAndNotify(auditRecord, $"Copied version audit: {auditRecord}");
                };
                menu.Items.Add(copyAudit);

                var velocitySummary = MarkSmith.Services.LineDiff.FormatVersionChangeVelocity(sel.LinesAdded, sel.LinesRemoved);
                var copyVelocity = new MenuFlyoutItem
                {
                    Text = "Copy version change velocity",
                    Icon = new FontIcon { Glyph = "\uE823" }
                };
                copyVelocity.Click += (_, _) =>
                {
                    CopyAndNotify(velocitySummary, $"Copied version velocity: {velocitySummary}");
                };
                menu.Items.Add(copyVelocity);

                var growthSummary = MarkSmith.Services.LineDiff.FormatVersionGrowthAndChurn(sel.LinesAdded, sel.LinesRemoved);
                var copyGrowth = new MenuFlyoutItem
                {
                    Text = "Copy version net growth & churn ratio",
                    Icon = new FontIcon { Glyph = "\uE8EB" }
                };
                copyGrowth.Click += (_, _) =>
                {
                    CopyAndNotify(growthSummary, $"Copied growth & churn: {growthSummary}");
                };
                menu.Items.Add(copyGrowth);

                var retentionSummary = MarkSmith.Services.LineDiff.FormatVersionRetentionRate(sel.LinesAdded + sel.LinesRemoved, sel.LinesAdded);
                var copyRetention = new MenuFlyoutItem
                {
                    Text = "Copy line retention & survival rate",
                    Icon = new FontIcon { Glyph = "\uE8EB" }
                };
                copyRetention.Click += (_, _) =>
                {
                    CopyAndNotify(retentionSummary, $"Copied retention rate: {retentionSummary}");
                };
                menu.Items.Add(copyRetention);

                var entropySummary = MarkSmith.Services.LineDiff.FormatVersionEditEntropy(sel.LinesAdded, sel.LinesRemoved);
                var copyEntropy = new MenuFlyoutItem
                {
                    Text = "Copy edit dispersion & entropy index",
                    Icon = new FontIcon { Glyph = "\uE9F9" }
                };
                copyEntropy.Click += (_, _) =>
                {
                    CopyAndNotify(entropySummary, $"Copied edit entropy: {entropySummary}");
                };
                menu.Items.Add(copyEntropy);

                var hunksList = GetChangeHunkIndices();
                var hunksSummary = MarkSmith.Services.LineDiff.FormatDiffHunkSummary(hunksList.Count, sel.LinesAdded, sel.LinesRemoved);
                var copyHunks = new MenuFlyoutItem
                {
                    Text = "Copy edit hunk distribution & dispersion",
                    Icon = new FontIcon { Glyph = "\uE8A9" }
                };
                copyHunks.Click += (_, _) =>
                {
                    CopyAndNotify(hunksSummary, $"Copied edit distribution: {hunksSummary}");
                };
                menu.Items.Add(copyHunks);

                var copyDiffAgainstLatest = new MenuFlyoutItem
                {
                    Text = "Copy diff summary against latest version (HEAD)",
                    Icon = new FontIcon { Glyph = "\uE895" }
                };
                copyDiffAgainstLatest.Click += async (_, _) =>
                {
                    var selContent = await _vm.GetVersionContentAsync(sel) ?? string.Empty;
                    var latestItem = _vm.VisibleVersions.FirstOrDefault() ?? sel;
                    var latestContent = await _vm.GetVersionContentAsync(latestItem) ?? string.Empty;
                    var headSummary = MarkSmith.Services.LineDiff.ComputeDiffAgainstLatestSummary(
                        selContent,
                        latestContent,
                        sel.TimestampLabel,
                        latestItem.TimestampLabel);
                    CopyAndNotify(headSummary, $"Copied HEAD comparison: {headSummary}");
                };
                menu.Items.Add(copyDiffAgainstLatest);

                var copyCherryPickHunks = new MenuFlyoutItem
                {
                    Text = "Copy numbered cherry-pick hunks payload",
                    Icon = new FontIcon { Glyph = "\uE8C8" }
                };
                copyCherryPickHunks.Click += (_, _) =>
                {
                    var payload = MarkSmith.Services.LineDiff.FormatCherryPickHunksPayload(_vm.UnifiedDiffRows);
                    var firstLine = payload.Split('\n')[0].Trim();
                    CopyAndNotify(payload, $"Copied cherry-pick payload: {firstLine}");
                };
                menu.Items.Add(copyCherryPickHunks);

                var copyHeadingDiff = new MenuFlyoutItem
                {
                    Text = "Copy structural heading diff (# H1–H6)",
                    Icon = new FontIcon { Glyph = "\uE8FD" }
                };
                copyHeadingDiff.Click += (_, _) =>
                {
                    var headingSummary = MarkSmith.Services.LineDiff.FormatStructuralHeadingDiffSummary(_vm.UnifiedDiffRows);
                    CopyAndNotify(headingSummary, $"Copied heading diff: {headingSummary}");
                };
                menu.Items.Add(copyHeadingDiff);

                var copyMicroSaveAudit = new MenuFlyoutItem
                {
                    Text = "Copy auto-save micro-saves audit (low-entropy snapshots)",
                    Icon = new FontIcon { Glyph = "\uE74D" }
                };
                copyMicroSaveAudit.Click += (_, _) =>
                {
                    var deltas = _vm.VisibleVersions.Select(v => (v.LinesAdded, v.LinesRemoved)).ToList();
                    var microSummary = MarkSmith.Services.LineDiff.FormatLowEntropySnapshotAudit(deltas);
                    CopyAndNotify(microSummary, $"Copied micro-save audit: {microSummary}");
                };
                menu.Items.Add(copyMicroSaveAudit);

                var copyFencedBlockDiff = new MenuFlyoutItem
                {
                    Text = "Copy fenced code & diagram blocks diff audit",
                    Icon = new FontIcon { Glyph = "\uE943" }
                };
                copyFencedBlockDiff.Click += (_, _) =>
                {
                    var fencedDiff = MarkSmith.Services.LineDiff.FormatFencedBlockDiffSummary(_vm.UnifiedDiffRows);
                    CopyAndNotify(fencedDiff, $"Copied fenced blocks diff: {fencedDiff}");
                };
                menu.Items.Add(copyFencedBlockDiff);

                var copyVolatilityIndex = new MenuFlyoutItem
                {
                    Text = "Copy timeline volatility index & session churn audit",
                    Icon = new FontIcon { Glyph = "\uE9F9" }
                };
                copyVolatilityIndex.Click += (_, _) =>
                {
                    var volatility = MarkSmith.Services.LineDiff.FormatTimelineVolatilitySummary(_vm.UnifiedDiffRows);
                    CopyAndNotify(volatility, $"Copied timeline volatility: {volatility}");
                };
                menu.Items.Add(copyVolatilityIndex);

                var assessConflictRisk = new MenuFlyoutItem
                {
                    Text = "Assess cherry-pick conflict risk vs current document",
                    Icon = new FontIcon { Glyph = "\uE7BA" }
                };
                assessConflictRisk.Click += (_, _) =>
                {
                    string currentDoc = _main?.PastedMarkdown ?? string.Empty;
                    var (_, _, _, _, _, riskSummary) = MarkSmith.Services.LineDiff.AssessCherryPickConflictRisk(_vm.UnifiedDiffRows, currentDoc);
                    CopyAndNotify(riskSummary, $"Copied conflict risk: {riskSummary}");
                };
                menu.Items.Add(assessConflictRisk);

                var synthesizeCommitMessage = new MenuFlyoutItem
                {
                    Text = "Synthesize semantic version commit message",
                    Icon = new FontIcon { Glyph = "\uE8A1" }
                };
                synthesizeCommitMessage.Click += (_, _) =>
                {
                    var message = MarkSmith.Services.LineDiff.SynthesizeSemanticVersionMessage(_vm.UnifiedDiffRows);
                    CopyAndNotify(message, $"Copied commit message: {message.Split('\n')[0]}");
                };
                menu.Items.Add(synthesizeCommitMessage);

                var synthesizeCommitScope = new MenuFlyoutItem
                {
                    Text = "Synthesize scoped commit & breaking change message",
                    Icon = new FontIcon { Glyph = "\uE82D" }
                };
                synthesizeCommitScope.Click += (_, _) =>
                {
                    var message = MarkSmith.Services.LineDiff.SynthesizeConventionalCommitWithScope(_vm.UnifiedDiffRows, _main?.PastedMarkdown);
                    CopyAndNotify(message, $"Copied scoped commit: {message.Split('\n')[0]}");
                };
                menu.Items.Add(synthesizeCommitScope);

                var formatChangelogSection = new MenuFlyoutItem
                {
                    Text = "Format conventional CHANGELOG.md section",
                    Icon = new FontIcon { Glyph = "\uE8A1" }
                };
                formatChangelogSection.Click += (_, _) =>
                {
                    var section = MarkSmith.Services.LineDiff.FormatConventionalChangelogSection(_vm.UnifiedDiffRows, _main?.PastedMarkdown);
                    CopyAndNotify(section, $"Copied changelog section: {section.Split('\n')[0]}");
                };
                menu.Items.Add(formatChangelogSection);

                var synthesizeMilestoneHeader = new MenuFlyoutItem
                {
                    Text = "Synthesize release announcement milestone header",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                synthesizeMilestoneHeader.Click += (_, _) =>
                {
                    var header = MarkSmith.Services.LineDiff.SynthesizeReleaseMilestoneHeader(_vm.UnifiedDiffRows);
                    CopyAndNotify(header, $"Copied release milestone: {header.Split('\n')[0]}");
                };
                menu.Items.Add(synthesizeMilestoneHeader);

                var synthesizeReleaseBadges = new MenuFlyoutItem
                {
                    Text = "Synthesize Shields.io release & status badges markdown",
                    Icon = new FontIcon { Glyph = "\uE734" }
                };
                synthesizeReleaseBadges.Click += (_, _) =>
                {
                    var badges = MarkSmith.Services.LineDiff.SynthesizeReleaseBadgesMarkdown(_vm.UnifiedDiffRows);
                    CopyAndNotify(badges, $"Copied release badges: {badges.Split(' ')[0]}");
                };
                menu.Items.Add(synthesizeReleaseBadges);

                var synthesizeReleaseAnnouncement = new MenuFlyoutItem
                {
                    Text = "Synthesize release announcement social card & markdown",
                    Icon = new FontIcon { Glyph = "\uE8A1" }
                };
                synthesizeReleaseAnnouncement.Click += (_, _) =>
                {
                    var snippet = MarkSmith.Services.LineDiff.SynthesizeReleaseAnnouncementSnippet(_vm.UnifiedDiffRows);
                    CopyAndNotify(snippet, "Copied release announcement markdown snippet");
                };
                menu.Items.Add(synthesizeReleaseAnnouncement);

                var synthesizeMultiChannelDigest = new MenuFlyoutItem
                {
                    Text = "Synthesize multi-channel release digest (Teams / Slack / Discord)",
                    Icon = new FontIcon { Glyph = "\uE90A" }
                };
                synthesizeMultiChannelDigest.Click += (_, _) =>
                {
                    var digest = MarkSmith.Services.LineDiff.SynthesizeMultiChannelReleaseDigest(_vm.UnifiedDiffRows);
                    CopyAndNotify(digest, "Copied multi-channel release digest");
                };
                menu.Items.Add(synthesizeMultiChannelDigest);

                var auditDiffChurnAcceleration = new MenuFlyoutItem
                {
                    Text = "Audit version diff churn acceleration & hotspot density",
                    Icon = new FontIcon { Glyph = "\uE9D9" }
                };
                auditDiffChurnAcceleration.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditDiffChurnAcceleration(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditDiffChurnAcceleration);

                var synthesizePullRequest = new MenuFlyoutItem
                {
                    Text = "Synthesize GitHub PR description & risk assessment",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                synthesizePullRequest.Click += (_, _) =>
                {
                    var pr = MarkSmith.Services.LineDiff.SynthesizePullRequestDescription(_vm.UnifiedDiffRows);
                    CopyAndNotify(pr.PullRequestMarkdown, $"\u2713 {pr.Summary}");
                };
                menu.Items.Add(synthesizePullRequest);

                var synthesizeConventionalCommit = new MenuFlyoutItem
                {
                    Text = "Synthesize Conventional Commit message & breaking change evaluation",
                    Icon = new FontIcon { Glyph = "\uE8A1" }
                };
                synthesizeConventionalCommit.Click += (_, _) =>
                {
                    var commit = MarkSmith.Services.LineDiff.SynthesizeConventionalCommitMessage(_vm.UnifiedDiffRows);
                    CopyAndNotify(commit.FullCommitMessage, $"\u2713 {commit.Summary}");
                };
                menu.Items.Add(synthesizeConventionalCommit);

                var synthesizeSemanticVersion = new MenuFlyoutItem
                {
                    Text = "Synthesize Semantic Version bump & changelog entry",
                    Icon = new FontIcon { Glyph = "\uE8EC" }
                };
                synthesizeSemanticVersion.Click += (_, _) =>
                {
                    var semver = MarkSmith.Services.LineDiff.SynthesizeSemanticVersionRecommendation(_vm.UnifiedDiffRows);
                    CopyAndNotify(semver.ChangelogMarkdown, $"\u2713 {semver.Summary}");
                };
                menu.Items.Add(synthesizeSemanticVersion);

                var synthesizeReleaseNotes = new MenuFlyoutItem
                {
                    Text = "Synthesize release notes & migration checklist",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                synthesizeReleaseNotes.Click += (_, _) =>
                {
                    var notes = MarkSmith.Services.LineDiff.SynthesizeReleaseNotesAndMigrationGuide(_vm.UnifiedDiffRows);
                    CopyAndNotify(notes.ReleaseNotesMarkdown, $"\u2713 {notes.Summary}");
                };
                menu.Items.Add(synthesizeReleaseNotes);

                var auditCommitChurn = new MenuFlyoutItem
                {
                    Text = "Audit commit churn hotspots & volatility index",
                    Icon = new FontIcon { Glyph = "\uE9D9" }
                };
                auditCommitChurn.Click += (_, _) =>
                {
                    var churn = MarkSmith.Services.LineDiff.AuditCommitChurnHotspots(_vm.UnifiedDiffRows);
                    CopyAndNotify(churn.Summary, $"\u2713 {churn.Summary}");
                };
                menu.Items.Add(auditCommitChurn);

                var auditChunkBoundaries = new MenuFlyoutItem
                {
                    Text = "Audit diff chunk semantic boundaries & hunk overlaps",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                auditChunkBoundaries.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditChunkContextBoundaries(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditChunkBoundaries);

                var auditSemanticFolding = new MenuFlyoutItem
                {
                    Text = "Audit diff hunk semantic folding & scope context pinning",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                auditSemanticFolding.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditSemanticHunkFoldingAndTokenContext(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditSemanticFolding);

                var auditWordDiff = new MenuFlyoutItem
                {
                    Text = "Audit intra-line word-level diff chunking & token shifts",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                auditWordDiff.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditWordLevelDiffChunkingAndTokenShifts(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditWordDiff);

                var auditLevenshtein = new MenuFlyoutItem
                {
                    Text = "Audit character edit distance, transpositions & typo granularity",
                    Icon = new FontIcon { Glyph = "\uE8D2" }
                };
                auditLevenshtein.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditCharacterLevenshteinEditDistance(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditLevenshtein);

                var auditDiffTokenEntropy = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff token entropy, Shannon bits & revision drift",
                    Icon = new FontIcon { Glyph = "\uE9D9" }
                };
                auditDiffTokenEntropy.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditSemanticDiffTokenEntropy(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditDiffTokenEntropy);

                var auditDiffLineWrap = new MenuFlyoutItem
                {
                    Text = "Audit diff line-wrap boundary, long line overflow & horizontal scroll spill",
                    Icon = new FontIcon { Glyph = "\uE8E4" }
                };
                auditDiffLineWrap.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditDiffLineWrapAndOverflow(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditDiffLineWrap);

                var auditGitConflictMarkers = new MenuFlyoutItem
                {
                    Text = "Audit Git conflict markers, unmerged hunks & rebase residue",
                    Icon = new FontIcon { Glyph = "\uE8E4" }
                };
                auditGitConflictMarkers.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditGitConflictMarkersAndRebaseResidue(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditGitConflictMarkers);

                var auditSemanticCoChange = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff co-change clusters & coupled hunk density",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditSemanticCoChange.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditSemanticDiffCoChangeClusters(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditSemanticCoChange);

                var auditHunkCoupling = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff hunk coupling & cross-hunk propagation",
                    Icon = new FontIcon { Glyph = "\uE71D" }
                };
                auditHunkCoupling.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditSemanticHunkCouplingAndPropagation(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditHunkCoupling);

                var auditWordLevelDiff = new MenuFlyoutItem
                {
                    Text = "Audit word-level diff granularity & Levenshtein edit distance",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditWordLevelDiff.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditWordLevelDiffResolution(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditWordLevelDiff);

                var auditScopeTransitions = new MenuFlyoutItem
                {
                    Text = "Audit identifier scope transitions & declaration promotions",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditScopeTransitions.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditIdentifierScopeTransitions(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditScopeTransitions);

                var auditBlockRelocation = new MenuFlyoutItem
                {
                    Text = "Audit structural block relocations & method movement",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditBlockRelocation.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditBlockRelocationAndMoves(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditBlockRelocation);

                var auditEntropy = new MenuFlyoutItem
                {
                    Text = "Audit formatting whitespace vs syntactic token change entropy",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditEntropy.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditFormattingVsSyntacticChangeEntropy(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditEntropy);

                var auditChurnVolatility = new MenuFlyoutItem
                {
                    Text = "Audit line addition/deletion churn volatility & change symmetry",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditChurnVolatility.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditCodeChurnVolatilityIndex(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditChurnVolatility);

                var auditInlineIntensity = new MenuFlyoutItem
                {
                    Text = "Audit inline modification intensity & token Levenshtein edit distance",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditInlineIntensity.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditInlineModificationIntensity(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditInlineIntensity);

                var auditHunkLocality = new MenuFlyoutItem
                {
                    Text = "Audit diff hunk locality & spatial fragmentation entropy",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditHunkLocality.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditDiffHunkLocalityAndFragmentation(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditHunkLocality);

                var auditHunkClustering = new MenuFlyoutItem
                {
                    Text = "Audit diff hunk inter-hunk distance & semantic clustering",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditHunkClustering.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditDiffHunkClusteringDistance(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditHunkClustering);

                var auditMethodSignatureDrift = new MenuFlyoutItem
                {
                    Text = "Audit method signature interface drift & parameter mutations",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMethodSignatureDrift.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMethodSignatureAndParameterDrift(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMethodSignatureDrift);

                var auditIdentifierRenaming = new MenuFlyoutItem
                {
                    Text = "Audit token-level identifier renaming patterns & mutation scope entropy",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditIdentifierRenaming.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditIdentifierRenamingEntropy(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditIdentifierRenaming);

                var auditVariableScopeLocality = new MenuFlyoutItem
                {
                    Text = "Audit variable scope lifetime & mutation locality distribution",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditVariableScopeLocality.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditVariableScopeLocality(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditVariableScopeLocality);

                var auditClosureCaptureScope = new MenuFlyoutItem
                {
                    Text = "Audit variable closure capturing, lifetime escapes, race hazards & immutability",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditClosureCaptureScope.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditClosureCaptureAndScopeEscapes(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditClosureCaptureScope);

                var auditControlFlow = new MenuFlyoutItem
                {
                    Text = "Audit control flow nesting, cyclomatic complexity delta & guard clause inversion",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditControlFlow.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditControlFlowAndGuardClauses(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditControlFlow);

                var auditExceptionHandling = new MenuFlyoutItem
                {
                    Text = "Audit exception handling hierarchy, catch specificity & rethrow anti-patterns",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditExceptionHandling.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditExceptionHandlingAndRethrows(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditExceptionHandling);

                var auditAsyncThreading = new MenuFlyoutItem
                {
                    Text = "Audit asynchronous await, task deadlock hazards & threading context switches",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditAsyncThreading.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditAsyncAwaitAndThreadContext(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditAsyncThreading);

                var auditMemoryAllocation = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff memory allocations, hot path boxing & loop churn",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMemoryAllocation.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMemoryAllocationAndBoxingHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMemoryAllocation);

                var auditThreadSafety = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff thread safety, lock primitives & deadlock hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditThreadSafety.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditThreadSafetyAndLockPrimitives(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditThreadSafety);

                var auditDefensiveCopying = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff defensive copying, struct immutability & 'in' parameter hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditDefensiveCopying.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditDefensiveCopyingAndStructParameters(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditDefensiveCopying);

                var auditMethodInlining = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff method inlining, AggressiveInlining & PGO deoptimization hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMethodInlining.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMethodInliningAndPgoHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMethodInlining);

                var auditExceptionFilterHazards = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff exception handling, stack unwinding & filter expression hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditExceptionFilterHazards.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditExceptionHandlingAndFilterHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditExceptionFilterHazards);

                var auditAllocationClosure = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff memory allocation, boxing & closure capture hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditAllocationClosure.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditAllocationAndClosureCaptureHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditAllocationClosure);

                var auditCacheLocality = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff CPU cache locality, false sharing & pointer chasing hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditCacheLocality.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditCacheLocalityAndFalseSharingHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditCacheLocality);
                var auditSynchronization = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff synchronization primitives, lock contention & deadlock hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditSynchronization.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditSynchronizationAndDeadlockHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditSynchronization);
                var auditMemorySafety = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff memory safety, buffer overruns, unsafe pointers & span hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMemorySafety.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMemorySafetyAndUnsafePointerHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMemorySafety);
                var auditStringAllocation = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff string allocations & memory pressure hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditStringAllocation.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditStringAllocationAndMemoryPressureHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditStringAllocation);
                var auditPatternMatching = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff exhaustive pattern matching & unhandled enum branches",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditPatternMatching.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditPatternMatchingAndEnumBranchExhaustiveness(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditPatternMatching);
                var auditTuplePatternMatching = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff tuple deconstruction & recursive subpattern exhaustiveness",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditTuplePatternMatching.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditPatternMatchingAndEnumBranchExhaustiveness(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditTuplePatternMatching);
                var auditTypePatternMatching = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff active patterns, type tests & relational boundaries",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditTypePatternMatching.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditPatternMatchingAndEnumBranchExhaustiveness(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditTypePatternMatching);
                var auditNullPatternMatching = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff null-conditional patterns & nullable value type switches",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditNullPatternMatching.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditPatternMatchingAndEnumBranchExhaustiveness(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditNullPatternMatching);

                var auditStringInterpolationBoxing = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff string interpolation & value-type boxing hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditStringInterpolationBoxing.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditStringAllocationsAndSpanSafety(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditStringInterpolationBoxing);

                var auditMemorySpanSafety = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff Memory<T> / Span<T> slice safety & closure captures",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMemorySpanSafety.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMemoryAndSpanSafety(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMemorySpanSafety);
                var auditChannelStreamHazards = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff Channel<T> backpressure & IAsyncEnumerable stream completion hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditChannelStreamHazards.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditChannelAndAsyncStreamHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditChannelStreamHazards);
                var auditValueTaskHazards = new MenuFlyoutItem
                {
                    Text = "Audit semantic diff ValueTask multiple await & async state machine allocation hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditValueTaskHazards.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditValueTaskAndAsyncAllocationHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditValueTaskHazards);
                var auditThreadPoolStarvationItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff ThreadPool Starvation & Sync-Over-Async Patterns",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditThreadPoolStarvationItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditThreadPoolStarvationAndSyncOverAsync(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditThreadPoolStarvationItem);

                var auditTaskWhenAllExceptionItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Task.WhenAll & AggregateException Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditTaskWhenAllExceptionItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditTaskWhenAllExceptionPropagation(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditTaskWhenAllExceptionItem);
                var auditCancellationTokenPropagationItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff CancellationToken Propagation & Cancellation Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditCancellationTokenPropagationItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditCancellationTokenPropagation(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditCancellationTokenPropagationItem);

var auditResourceDisposalLeaksItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Resource Disposal & IAsyncDisposable Leak Detection",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditResourceDisposalLeaksItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditResourceDisposalAndAsyncDisposableLeaks(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditResourceDisposalLeaksItem);

                var auditConcurrentCollectionItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Task Parallelism & Concurrent Collection Thread Safety",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditConcurrentCollectionItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditConcurrentCollectionThreadSafety(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditConcurrentCollectionItem);

                var auditLockHierarchyItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Lock Hierarchy & Deadlock Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditLockHierarchyItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditLockHierarchyDeadlocks(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditLockHierarchyItem);

                var auditPatternMatchingGuardsItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Pattern Matching Guards & Expression Bodies",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditPatternMatchingGuardsItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditPatternMatchingGuardsAndExpressionBodies(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditPatternMatchingGuardsItem);
                var auditMemoryAllocationBoxingItem = new MenuFlyoutItem
                {
                    Text = "Audit Semantic Diff Memory Allocation & Boxing Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditMemoryAllocationBoxingItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditMemoryAllocationBoxingHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditMemoryAllocationBoxingItem);
                var auditStringConcatenationItem = new MenuFlyoutItem
                {
                    Text = "Audit Diff String Concatenation & StringBuilder Allocation Efficiency",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditStringConcatenationItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditStringConcatenationAllocations(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditStringConcatenationItem);
                var auditLinqMultipleEnumerationItem = new MenuFlyoutItem
                {
                    Text = "Audit Diff LINQ Multiple Enumeration & Allocation Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditLinqMultipleEnumerationItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditLinqMultipleEnumerationHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditLinqMultipleEnumerationItem);
                var auditExceptionSwallowingItem = new MenuFlyoutItem
                {
                    Text = "Audit Diff Exception Swallowing & Bare Catch Block Hazards",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditExceptionSwallowingItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditExceptionSwallowingHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditExceptionSwallowingItem);
                var auditNullForgivingItem = new MenuFlyoutItem
                {
                    Text = "Audit Diff Null-Forgiving Bang Operators & Suppression Hazards (!)",
                    Icon = new FontIcon { Glyph = "\uE8B9" }
                };
                auditNullForgivingItem.Click += (_, _) =>
                {
                    var audit = MarkSmith.Services.LineDiff.AuditNullForgivingHazards(_vm.UnifiedDiffRows);
                    CopyAndNotify(audit.Summary, $"\u2713 {audit.Summary}");
                };
                menu.Items.Add(auditNullForgivingItem);








            }

            menu.Items.Add(new MenuFlyoutSeparator());

        }

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh version history",
            Icon = new FontIcon { Glyph = "\uE72C" },
            KeyboardAcceleratorTextOverride = "F5"
        };
        refresh.Click += async (_, _) => await RefreshHistoryAsync();
        menu.Items.Add(refresh);

        var checkpoint = new MenuFlyoutItem
        {
            Text = "Take checkpoint…",
            Icon = new FontIcon { Glyph = "\uE74E" },
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+S"
        };
        checkpoint.Click += (_, _) => OnTakeSnapshotClick(this, new RoutedEventArgs());
        menu.Items.Add(checkpoint);

        menu.Items.Add(new MenuFlyoutSeparator());

        var scrollToTop = new MenuFlyoutItem
        {
            Text = "Scroll to latest version",
            Icon = new FontIcon { Glyph = "\uE74A" },
            KeyboardAcceleratorTextOverride = "Home"
        };
        scrollToTop.Click += (_, _) =>
        {
            TimelineScroll.ChangeView(null, 0, null);
            _vm.SelectFirstVersion();
        };
        menu.Items.Add(scrollToTop);

        var scrollToBottom = new MenuFlyoutItem
        {
            Text = "Scroll to oldest version",
            Icon = new FontIcon { Glyph = "\uE74B" },
            KeyboardAcceleratorTextOverride = "End"
        };
        scrollToBottom.Click += (_, _) =>
        {
            TimelineScroll.ChangeView(null, TimelineScroll.ScrollableHeight, null);
            _vm.SelectLastVersion();
        };
        menu.Items.Add(scrollToBottom);

        var clearFilters = new MenuFlyoutItem
        {
            Text = "Clear filters",
            Icon = new FontIcon { Glyph = "\uE894" }
        };
        clearFilters.Click += (_, _) => _vm.ClearFiltersCommand.Execute(null);
        menu.Items.Add(clearFilters);

        MenuDiagnostics.Fold(menu);
        menu.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }

    // F5 and the menus' Refresh: reload the open document's versions, or the documents list when
    // none is open. Skipped while a load is running so a held key doesn't stack reloads.
    private async Task RefreshHistoryAsync()
    {
        if (_vm.IsLoading) return;
        if (_vm.SelectedFile is { } file)
            await _vm.ShowFileAsync(file.Summary.FilePath);
        else
            await _vm.LoadCommand.ExecuteAsync(null);
        // Said aloud too: a reload that changes nothing is otherwise silent. A failed read already
        // shows its own error in the documents pane.
        if (!_vm.HasLoadError)
            _vm.ShowNotice(_vm.SelectedFile is { } shown
                ? $"Refreshed {shown.FileName}: {shown.VersionCountLabel}."
                : "Refreshed the documents list.");
    }

    private void OnDocumentsHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        // A double-click on the refresh button already refreshed through its Click.
        if (IsWithin(e.OriginalSource as DependencyObject, DocumentsRefreshButton)) return;
        _ = RefreshHistoryAsync();
    }

    private void OnDocumentsRefreshButtonClick(object sender, RoutedEventArgs e) => _ = RefreshHistoryAsync();

    private void OnDocumentsHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh documents list",
            Icon = new FontIcon { Glyph = "\uE72C" },
            KeyboardAcceleratorTextOverride = "F5"
        };
        refresh.Click += async (_, _) => await RefreshHistoryAsync();
        menu.Items.Add(refresh);

        if (_vm.SelectedFile is { } file && !file.IsScratch)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var showExplorer = new MenuFlyoutItem
            {
                Text = "Show in File Explorer",
                Icon = new FontIcon { Glyph = "\uEC50" }
            };
            showExplorer.Click += (_, _) =>
            {
                if (File.Exists(file.Summary.FilePath))
                {
                    try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file.Summary.FilePath}\""); }
                    catch { }
                }
            };
            menu.Items.Add(showExplorer);

            var copyPath = new MenuFlyoutItem
            {
                Text = "Copy file path",
                Icon = new FontIcon { Glyph = "\uE8C8" }
            };
            copyPath.Click += (_, _) =>
            {
                CopyAndNotify(file.Summary.FilePath, "Copied file path to clipboard.");
            };
            menu.Items.Add(copyPath);
        }
        menu.ShowAt(fe, new FlyoutShowOptions { Position = KeyboardContextMenu.PointIn(e, fe) });
    }
}

