using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MdToPdf;

public sealed partial class MainWindow : Window
{
    private static readonly HashSet<string> PreviewAffectingProperties = new()
    {
        nameof(ViewModels.MainViewModel.PastedMarkdown),
        nameof(ViewModels.MainViewModel.InputFilePath),
        nameof(ViewModels.MainViewModel.UsePasteSource),
        nameof(ViewModels.MainViewModel.SelectedThemeName),
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
    private readonly Services.ClipboardIngestService _clipboardIngest;
    private readonly Services.FolderIngestService _folderIngest;
    private readonly Services.ApiServer _apiServer;
    private readonly SemaphoreSlim _convertLock = new(1, 1);
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private bool _exitRequested;

    // Preview loading spinner state. The spinner ticks at ~60fps, animating one of three randomly
    // chosen styles, and stays up for at least SpinMinSec so a fast render never looks like a
    // white flash. It hides only once BOTH the navigation has completed and the minimum time passed.
    private const double SpinMinSec = 0.65;
    private const double SpinDt = 1.0 / 60.0;
    private static readonly Random _spinRng = new();
    private static readonly Windows.UI.Color[] DvdColors =
    {
        Windows.UI.Color.FromArgb(255, 0x2E, 0x7D, 0xFF), // blue
        Windows.UI.Color.FromArgb(255, 0x7C, 0x4D, 0xFF), // purple
        Windows.UI.Color.FromArgb(255, 0x3F, 0xB9, 0x50), // green
        Windows.UI.Color.FromArgb(255, 0xD2, 0x99, 0x22), // amber
        Windows.UI.Color.FromArgb(255, 0xF8, 0x51, 0x49), // red
    };
    private DispatcherQueueTimer? _spinTimer;
    private int _spinMode;         // 0 clock, 1 figure-eight, 2 DVD bounce
    private double _spinPhase;     // seconds the spinner has been visible
    private bool _spinNavDone;
    private bool _spinActive;
    private double _dvdX, _dvdY, _dvdVX, _dvdVY;
    private int _dvdColorIdx;

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

        Title = "Marksmith";
        // Unpackaged app: the exe icon covers Explorer/taskbar, but the title bar needs an
        // explicit runtime assignment (relative paths resolve against the CWD, so anchor to base).
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        RootGrid.DataContext = ViewModel;

        // Typing in the paste editor fires PropertyChanged per keystroke; coalesce preview
        // reloads so WebView2 isn't re-navigated on every character.
        _previewDebounce = DispatcherQueue.CreateTimer();
        _previewDebounce.Interval = TimeSpan.FromMilliseconds(180);
        _previewDebounce.IsRepeating = false;
        _previewDebounce.Tick += async (_, _) => await RefreshPreviewAsync();

        _spinTimer = DispatcherQueue.CreateTimer();
        _spinTimer.Interval = TimeSpan.FromMilliseconds(16);
        _spinTimer.IsRepeating = true;
        _spinTimer.Tick += (_, _) => OnSpinTick();

        _clipboardIngest = new Services.ClipboardIngestService(DispatcherQueue, (text, origin) => ViewModel.IngestMarkdown(text, origin));
        _folderIngest = new Services.FolderIngestService(DispatcherQueue, path => _ = OnWatchedFileAsync(path));
        _apiServer = new Services.ApiServer(
            App.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin) => DispatcherQueue.TryEnqueue(() => ViewModel.IngestMarkdown(md, origin)),
            ConvertForApiAsync,
            App.Governance);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncSourcePanels();
        ApplyAutomationSettings();
        HistoryList.ItemsSource = ViewModel.History; // Flyout popups don't inherit DataContext
        InitTrayIcon();

        AppWindow.Closing += (_, e) =>
        {
            if (ViewModel.MinimizeToTray && !_exitRequested)
            {
                e.Cancel = true;
                AppWindow.Hide(); // watchers + API keep running; tray icon brings it back
            }
        };

        Closed += (_, _) =>
        {
            _clipboardIngest.Dispose();
            _folderIngest.Dispose();
            _apiServer.Dispose();
            _trayIcon?.Dispose();
        };

        _ = InitializePreviewAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.UsePasteSource))
        {
            SyncSourcePanels();
        }

        if (e.PropertyName == nameof(ViewModels.MainViewModel.DetectedSourceText))
        {
            DetectedBadge.Visibility = string.IsNullOrEmpty(ViewModel.DetectedSourceText)
                ? Visibility.Collapsed : Visibility.Visible;
        }

        if (e.PropertyName is not null && AutomationProperties.Contains(e.PropertyName))
        {
            ApplyAutomationSettings();
        }

        if (e.PropertyName is not null && PreviewAffectingProperties.Contains(e.PropertyName))
        {
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }
    }

    // ---- Automation (clipboard watcher / folder watcher / REST API) ----

    private void ApplyAutomationSettings()
    {
        var vm = ViewModel;

        if (vm.AutoClipboardIngest && !_clipboardIngest.IsRunning) _clipboardIngest.Start();
        else if (!vm.AutoClipboardIngest && _clipboardIngest.IsRunning) _clipboardIngest.Stop();

        if (vm.WatchFolderEnabled && Directory.Exists(vm.WatchFolder)) _folderIngest.Start(vm.WatchFolder);
        else _folderIngest.Stop();

        try
        {
            if (vm.ApiEnabled && (!_apiServer.IsRunning || _apiServer.Port != vm.ApiPort)) _apiServer.Start(vm.ApiPort);
            else if (!vm.ApiEnabled) _apiServer.Stop();
            ApiUrlText.Text = _apiServer.IsRunning ? $"http://127.0.0.1:{_apiServer.Port}/api/health" : "";
        }
        catch (Exception ex)
        {
            ApiUrlText.Text = $"API failed to start: {ex.Message}";
        }
    }

    // Tray icon is created in code, not markup — the WASDK 1.6 XAML compiler crashes on
    // H.NotifyIcon's XAML types, but consuming them from C# works fine.
    private void InitTrayIcon()
    {
        try
        {
            var menu = new MenuFlyout();
            menu.Items.Add(new MenuFlyoutItem { Text = "Open Marksmith", Command = ShowWindowCommand });
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(new MenuFlyoutItem { Text = "Exit", Command = ExitApplicationCommand });

            _trayIcon = new H.NotifyIcon.TaskbarIcon
            {
                ToolTipText = "Marksmith",
                IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/tray.png")),
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

    // A watched file always ingests into the UI; in auto-convert mode it also goes straight to
    // PDF in the output folder with a toast — the fully hands-off pipeline.
    private async Task OnWatchedFileAsync(string path)
    {
        ViewModel.IngestFile(path);
        if (!ViewModel.WatchFolderAutoConvert) return;

        try
        {
            await _convertLock.WaitAsync();
            try
            {
                var html = ViewModel.BuildPreviewHtml(ViewModel.PastedMarkdown); // IngestFile already normalized it
                var folder = App.Settings.Current.OutputFolder;
                Directory.CreateDirectory(folder);
                var outPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".pdf");
                await new Services.PdfExportService().ExportAsync(PreviewWebView, html, outPath, App.Settings.Current);

                ViewModel.LastOutputPath = outPath;
                ViewModel.RecordExport("PDF", outPath);
                ViewModel.StatusText = $"Auto-converted: {outPath}";
                ViewModel.StatusSeverity = InfoBarSeverity.Success;
                ShowPdfToast(outPath);
            }
            finally
            {
                _convertLock.Release();
                await RefreshPreviewAsync();
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Auto-convert failed for {Path.GetFileName(path)}: {ex.Message}";
            ViewModel.StatusSeverity = InfoBarSeverity.Error;
        }
    }

    private static void ShowPdfToast(string pdfPath)
    {
        try
        {
            var toast = new AppNotificationBuilder()
                .AddText("PDF ready")
                .AddText(Path.GetFileName(pdfPath))
                .AddArgument("action", "open")
                .AddArgument("path", pdfPath)
                .AddButton(new AppNotificationButton("Open PDF")
                    .AddArgument("action", "open").AddArgument("path", pdfPath))
                .AddButton(new AppNotificationButton("Show in folder")
                    .AddArgument("action", "folder").AddArgument("path", pdfPath))
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
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.OutputPath) { UseShellExecute = true });
        }
        else
        {
            ViewModel.StatusText = $"File no longer exists: {entry.OutputPath}";
            ViewModel.StatusSeverity = InfoBarSeverity.Warning;
        }
    }

    private async void OnBrowseWatchFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.WatchFolder = folder.Path;
    }

    // Runs an /api/convert request on the UI thread using the shared preview WebView2 (WebView2
    // can't render off-tree — see PdfExportService). Serialized by _convertLock; the preview is
    // restored afterwards.
    private async Task<byte[]> ConvertForApiAsync(string markdown, string? themeName, bool normalize)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await _convertLock.WaitAsync();
            try
            {
                var md = markdown;
                Models.LlmClassification? classification = null;
                if (normalize)
                {
                    classification = App.LlmSource.Classify(md);
                    (md, _) = App.LlmSource.Normalize(md, classification);
                }
                var theme = App.Themes.GetOrDefault(themeName ?? ViewModel.SelectedThemeName);
                var html = App.MarkdownHtml.Render(md, App.Settings.Current, theme, classification);
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.pdf");
                await new Services.PdfExportService().ExportAsync(PreviewWebView, html, tmp, App.Settings.Current);
                var bytes = await File.ReadAllBytesAsync(tmp);
                File.Delete(tmp);
                tcs.SetResult(bytes);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                _convertLock.Release();
                await RefreshPreviewAsync();
            }
        });
        return await tcs.Task;
    }

    // ---- Source panel (File | Paste) ----

    private void OnSourceSelectorChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.UsePasteSource = sender.SelectedItem == PasteTab;
    }

    private void SyncSourcePanels()
    {
        var paste = ViewModel.UsePasteSource;
        SourceSelector.SelectedItem = paste ? PasteTab : FileTab;
        FilePanel.Visibility = paste ? Visibility.Collapsed : Visibility.Visible;
        PastePanel.Visibility = paste ? Visibility.Visible : Visibility.Collapsed;
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
        var file = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType is ".md" or ".markdown" or ".txt");
        if (file is not null)
        {
            ViewModel.InputFilePath = file.Path;
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.InputFilePath = file.Path;
            ViewModel.UsePasteSource = false;
        }
    }

    private void OnRecentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: string path })
        {
            ViewModel.LoadRecentCommand.Execute(path);
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

    private async void OnConvertPdfClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToPdfAsync(PreviewWebView);
    }

    private async void OnConvertDocxClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.ConvertToDocxAsync();
    }

    private void OnOpenOutputClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.LastOutputPath is { } path && File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    // ---- Preview ----

    private async Task InitializePreviewAsync()
    {
        await PreviewWebView.EnsureCoreWebView2Async();
        // The preview auto-refreshes on every change (debounced). Navigation completing satisfies
        // one of the two conditions to hide the spinner; the minimum-time gate satisfies the other.
        PreviewWebView.CoreWebView2.NavigationCompleted += (_, _) => _spinNavDone = true;
        await RefreshPreviewAsync();
    }

    // Called on every refresh. Starts the animated spinner if it isn't already running; otherwise
    // just marks the new navigation in-flight so it stays up until the latest render finishes.
    private void StartSpinner()
    {
        if (_spinActive) { _spinNavDone = false; return; }

        _spinActive = true;
        _spinNavDone = false;
        _spinPhase = 0;
        _spinMode = _spinRng.Next(3);

        OrbitLayer.Visibility = _spinMode == 0 ? Visibility.Visible : Visibility.Collapsed;
        SpinnerLogoTransform.TranslateX = 0;
        SpinnerLogoTransform.TranslateY = 0;
        SpinnerLogoTransform.Rotation = 0;
        OrbitRotate.Angle = 0;

        if (_spinMode == 2)
        {
            _dvdX = _dvdY = 0;
            _dvdVX = 165; _dvdVY = 120;
            _dvdColorIdx = 0;
        }
        SpinnerLogoBg.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(DvdColors[0]); // blue to start

        PreviewSpinner.Visibility = Visibility.Visible;
        _spinTimer!.Start();
    }

    private void OnSpinTick()
    {
        _spinPhase += SpinDt;

        switch (_spinMode)
        {
            case 0: // clock: arrow orbits the M
                OrbitRotate.Angle = (_spinPhase * 240) % 360;
                break;

            case 1: // figure-eight (Gerono lemniscate): x = A sin t, y = (A/2) sin 2t
            {
                var t = _spinPhase * 2.4;
                const double a = 44;
                SpinnerLogoTransform.TranslateX = a * Math.Sin(t);
                SpinnerLogoTransform.TranslateY = a * 0.6 * Math.Sin(2 * t);
                SpinnerLogoTransform.Rotation = Math.Sin(t) * 12;
                break;
            }

            case 2: // DVD-logo bounce, recoloring on each wall hit
            {
                var w = PreviewSpinner.ActualWidth;
                var h = PreviewSpinner.ActualHeight;
                if (w > 0 && h > 0)
                {
                    const double half = 28;
                    var maxX = Math.Max(0, w / 2 - half);
                    var maxY = Math.Max(0, h / 2 - half);
                    _dvdX += _dvdVX * SpinDt;
                    _dvdY += _dvdVY * SpinDt;
                    var bounced = false;
                    if (_dvdX > maxX) { _dvdX = maxX; _dvdVX = -_dvdVX; bounced = true; }
                    else if (_dvdX < -maxX) { _dvdX = -maxX; _dvdVX = -_dvdVX; bounced = true; }
                    if (_dvdY > maxY) { _dvdY = maxY; _dvdVY = -_dvdVY; bounced = true; }
                    else if (_dvdY < -maxY) { _dvdY = -maxY; _dvdVY = -_dvdVY; bounced = true; }
                    if (bounced)
                    {
                        _dvdColorIdx = (_dvdColorIdx + 1) % DvdColors.Length;
                        SpinnerLogoBg.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(DvdColors[_dvdColorIdx]);
                    }
                    SpinnerLogoTransform.TranslateX = _dvdX;
                    SpinnerLogoTransform.TranslateY = _dvdY;
                }
                break;
            }
        }

        if (_spinNavDone && _spinPhase >= SpinMinSec) HideSpinner();
    }

    private void HideSpinner()
    {
        _spinTimer!.Stop();
        _spinActive = false;
        PreviewSpinner.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshPreviewAsync()
    {
        if (PreviewWebView.CoreWebView2 is null) return;

        StartSpinner();

        var vm = ViewModel;
        string markdown;
        if (vm.UsePasteSource)
        {
            markdown = vm.PastedMarkdown;
        }
        else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
        {
            markdown = await File.ReadAllTextAsync(vm.InputFilePath);
        }
        else
        {
            markdown = "# Marksmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";
        }

        // Same classify/normalize step the exports run, so the preview shows what will ship
        // (and the detection badge appears for manual paste and file input, not just auto-ingest).
        var html = vm.BuildPreviewHtml(vm.PrepareMarkdown(markdown));
        PreviewWebView.CoreWebView2.NavigateToString(html);
    }
}
