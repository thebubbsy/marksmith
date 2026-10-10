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
    }

    // The page fits itself to the pane's width when it loads. Loaded while the pane was collapsed
    // it measured 0 px and stayed clipped, with a horizontal scrollbar, once the user switched to
    // Preview. So render only while it is visible, and again on switching to it.
    private string? _renderedHtml;

    private void RenderPreview()
    {
        if (!_webViewReady || !_vm.ShowPreview || string.IsNullOrEmpty(_vm.PreviewHtml)) return;
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
        };
        star.Click += (_, _) => _ = _vm.ToggleStarCommand.ExecuteAsync(item);
        menu.Items.Add(star);

        var restore = new MenuFlyoutItem { Text = "Restore to editor…", Icon = new FontIcon { Glyph = "" } };
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
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await _vm.RenameVersionCommand.ExecuteAsync((item, input.Text));
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

        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await _vm.TakeSnapshotCommand.ExecuteAsync(input.Text);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var isShift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool inTextBox = e.OriginalSource is TextBox;
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
                _vm.SearchQuery = "";
                SearchBox.Text = "";
                _vm.IsStarredOnlyFilter = false;
                e.Handled = true;
            }
        }
        else if (!inTextBox && e.Key == VirtualKey.F2 && _vm.Selected is { } toRename)
        {
            e.Handled = true;
            _ = RenameAsync(toRename);
        }
        else if (!inTextBox && e.Key == VirtualKey.Delete && _vm.Selected is { } toDelete)
        {
            e.Handled = true;
            _ = DeleteAsync(toDelete);
        }
    }

    private void OnSearchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
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
    }
}
