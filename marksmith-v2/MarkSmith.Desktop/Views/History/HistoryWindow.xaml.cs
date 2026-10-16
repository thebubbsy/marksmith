using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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

    private DateTime _openedAt;
    private bool _initialFocusDone;

    private void OnTimelineLayoutUpdated(object? sender, object e)
    {
        if (_initialFocusDone) return;
        // Only while the window is opening, and never over something the person chose themselves.
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(RootGrid.XamlRoot);
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
        }
        else if (e.PropertyName is nameof(HistoryWindowViewModel.HasDiffStats) or nameof(HistoryWindowViewModel.DiffStats))
        {
            ResetChangeStepper();
            UpdateVisualDiffStatsBar();
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
                try
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(file.Detail);
                    ClipboardWriter.Set(dp);
                    _vm.ShowNotice("Copied file path to clipboard.");
                }
                catch { }
            };
            menu.Items.Add(copyPath);
        }

        return menu;
    }

    private int _currentHunkIndex = -1;

    private void UpdateVisualDiffStatsBar()
    {
        if (DiffVisualStatBar is null || DiffAddedCol is null || DiffRemovedCol is null) return;
        var stats = _vm.DiffStats;
        if (string.IsNullOrWhiteSpace(stats) || !_vm.HasDiffStats)
        {
            DiffVisualStatBar.Visibility = Visibility.Collapsed;
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
            return;
        }

        DiffVisualStatBar.Visibility = Visibility.Visible;
        double addedRatio = (double)added / total;
        double removedRatio = (double)removed / total;

        DiffAddedCol.Width = new GridLength(Math.Max(addedRatio, 0.05), GridUnitType.Star);
        DiffRemovedCol.Width = new GridLength(Math.Max(removedRatio, 0.05), GridUnitType.Star);
        if (DiffAddedRect != null) DiffAddedRect.Visibility = added > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DiffRemovedRect != null) DiffRemovedRect.Visibility = removed > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetChangeStepper()
    {
        _currentHunkIndex = -1;
        var hunks = GetChangeHunkIndices();
        if (ChangeStepperLabel != null)
        {
            ChangeStepperLabel.Text = hunks.Count > 0 ? $"{hunks.Count} {(hunks.Count == 1 ? "hunk" : "hunks")}" : "0 hunks";
            ToolTipService.SetToolTip(ChangeStepperLabel, hunks.Count > 0
                ? $"{hunks.Count} change {(hunks.Count == 1 ? "hunk" : "hunks")} · Click or F7 to jump to next hunk, Shift+F7 for previous"
                : "No change hunks in this version");
        }
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
            if (ChangeStepperLabel != null) ChangeStepperLabel.Text = "0 hunks";
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
        if (ChangeStepperLabel != null)
        {
            ChangeStepperLabel.Text = $"Hunk {_currentHunkIndex + 1}/{hunks.Count}";
            ToolTipService.SetToolTip(ChangeStepperLabel, $"Hunk {_currentHunkIndex + 1} of {hunks.Count} (Line {targetRow + 1}) · F7 next, Shift+F7 prev");
        }

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

    private void OnChangeStepperLabelTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        StepToChangeHunk(1);
    }

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
        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
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
        else if (e.Key == VirtualKey.F7)
        {
            e.Handled = true;
            StepToChangeHunk(isShift ? -1 : 1);
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

    private void OnSearchBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        SearchBox.SelectAll();
    }

    private void OnSearchBoxRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var focusTimeline = new MenuFlyoutItem
        {
            Text = "Focus timeline (Enter / Down)",
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
            Text = "Clear search filter (Esc)",
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
            Text = "Show starred versions only (Ctrl+Shift+S)",
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
            Text = "Take checkpoint (Ctrl+Shift+C)",
            Icon = new FontIcon { Glyph = "\uE74E" }
        };
        checkpointItem.Click += (_, _) => OnTakeSnapshotClick(this, new RoutedEventArgs());
        menu.Items.Add(checkpointItem);

        menu.ShowAt(SearchBox, e.GetPosition(SearchBox));
    }

    private void OnFileNameRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var file = _vm.SelectedFile;
        var filePath = file?.Summary.FilePath ?? _vm.FileName;
        var menu = new MenuFlyout();

        var copyPath = new MenuFlyoutItem
        {
            Text = "Copy full file path (Ctrl+Shift+C)",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyPath.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(filePath);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied file path: {filePath}");
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
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(name);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied file name: {name}");
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

        menu.ShowAt(FileNameTextBlock, e.GetPosition(FileNameTextBlock));
    }

    private void OnFileNameDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var filePath = _vm.SelectedFile?.Summary.FilePath ?? _vm.FileName;
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(filePath);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied file path: {filePath}");
        }
    }

    private void OnTimelineBackgroundRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer and not StackPanel and not Border) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        var checkpointItem = new MenuFlyoutItem
        {
            Text = "Take checkpoint (Ctrl+Shift+C)",
            Icon = new FontIcon { Glyph = "\uE74E" }
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

        menu.ShowAt(TimelineScroll, e.GetPosition(TimelineScroll));
    }

    private void OnDiffSummaryDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var title = _vm.DiffTitle;
        if (!string.IsNullOrWhiteSpace(title))
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(title);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied version title: {title}");
        }
    }

    private void OnDiffSummaryRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowDiffAreaContextMenu(DiffSummaryPanel, e.GetPosition(DiffSummaryPanel));
    }

    private void OnDeltaMeterDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var stats = _vm.DiffStats;
        if (!string.IsNullOrWhiteSpace(stats))
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(stats);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied diff statistics: {stats}");
        }
    }

    private void OnDeltaMeterRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowDiffAreaContextMenu(DeltaMeterBorder, e.GetPosition(DeltaMeterBorder));
    }

    private void OnDiffAreaRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
        {
            ShowDiffAreaContextMenu(fe, e.GetPosition(fe));
        }
    }

    private void ShowDiffAreaContextMenu(FrameworkElement anchor, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();

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
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(stats);
                    ClipboardWriter.Set(dp);
                    _vm.ShowNotice($"Copied diff statistics: {stats}");
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

    private void OnCopyToolbarButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        OnCopyToolbarButtonClick(sender, new RoutedEventArgs());
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
            menu.ShowAt(fe, e.GetPosition(fe));
    }

    private void OnUnifiedDiffRowDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is LineDiff.UnifiedRow row)
        {
            if (!row.IsGap && !string.IsNullOrEmpty(row.Text))
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(row.Text);
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied line: {row.Text}");
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
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(row.Text);
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied line: {row.Text}");
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
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(lineWithPrefix);
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied line with prefix: {lineWithPrefix}");
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

        menu.ShowAt(fe, e.GetPosition(fe));
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
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(cell.Text);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied {(isLeft ? "previous" : "current")} line: {cell.Text}");
        }
    }

    private void OnSplitRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not DiffRowViewModel row || row.IsGap) return;

        var pos = e.GetPosition(fe);
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
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(lineText);
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied line: {lineText}");
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

    private void OnTakeCheckpointButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        OnTakeSnapshotClick(sender, new RoutedEventArgs());
    }

    private void OnTakeCheckpointButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var menu = new MenuFlyout();

        var checkpointItem = new MenuFlyoutItem
        {
            Text = "Take checkpoint (Ctrl+Shift+C)",
            Icon = new FontIcon { Glyph = "\uE74E" }
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
            menu.ShowAt(fe, e.GetPosition(fe));
    }

    private void OnRestoreButtonDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (_vm.HasSelection)
        {
            OnRestoreClick(sender, new RoutedEventArgs());
        }
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
            menu.ShowAt(fe, e.GetPosition(fe));
    }

    private void OnDiffModeToggleRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe)
        {
            ShowDiffAreaContextMenu(fe, e.GetPosition(fe));
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
        menu.ShowAt(anchor, e.GetPosition(anchor));
    }

    private void OnStarButtonRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item } fe) return;
        e.Handled = true;
        _vm.SelectVersionCommand.Execute(item);
        var menu = BuildStarButtonMenu(item);
        menu.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
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
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText($"{item.TimestampLabel} — {item.Snippet}");
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied version info for {item.TimestampLabel}.");
            }
            catch { }
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
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(item.DeltaBadge);
            ClipboardWriter.Set(dp);
            _vm.ShowNotice($"Copied line delta {item.DeltaBadge} to clipboard.");
        }
        catch { }
    }

    private void OnVersionDeltaBadgeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: VersionItemViewModel item } fe) return;
        e.Handled = true;
        _vm.SelectVersionCommand.Execute(item);
        var menu = BuildDeltaBadgeMenu(item);
        menu.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
    }

    private MenuFlyout BuildDeltaBadgeMenu(VersionItemViewModel item)
    {
        var menu = new MenuFlyout();

        var copyDelta = new MenuFlyoutItem
        {
            Text = $"Copy line delta ({item.DeltaBadge})",
            Icon = new FontIcon { Glyph = "\uE8C8" }
        };
        copyDelta.Click += (_, _) =>
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(item.DeltaBadge);
                ClipboardWriter.Set(dp);
                _vm.ShowNotice($"Copied line delta {item.DeltaBadge} to clipboard.");
            }
            catch { }
        };
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

    private void OnTimelineHeaderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        e.Handled = true;
        var menu = new MenuFlyout();

        var refresh = new MenuFlyoutItem
        {
            Text = "Refresh version history",
            Icon = new FontIcon { Glyph = "\uE72C" },
            KeyboardAcceleratorTextOverride = "F5"
        };
        refresh.Click += async (_, _) =>
        {
            if (_vm.SelectedFile is { } file)
                await _vm.ShowFileAsync(file.Summary.FilePath);
            else
                await _vm.LoadCommand.ExecuteAsync(null);
        };
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

        menu.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
    }

    private void OnDocumentsHeaderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        _ = _vm.LoadCommand.ExecuteAsync(null);
    }

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
        refresh.Click += async (_, _) => await _vm.LoadCommand.ExecuteAsync(null);
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
                try
                {
                    var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    dp.SetText(file.Summary.FilePath);
                    ClipboardWriter.Set(dp);
                    _vm.ShowNotice("Copied file path to clipboard.");
                }
                catch { }
            };
            menu.Items.Add(copyPath);
        }
        menu.ShowAt(fe, new FlyoutShowOptions { Position = e.GetPosition(fe) });
    }
}

