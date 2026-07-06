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

    // Preview refresh intensity. Typing does a silent "light" refresh (no sprite, no blur); a paste or
    // a style/theme change does a "heavy" refresh — the loading sprite over a blur that clears as it
    // completes. PropertyChanged fires per keystroke, so a single edit's delta is our typing signal.
    private int _lastMarkdownLen;
    private bool _nextRefreshHeavy;
    // True while the mermaid snapshot renderer owns the WebView — preview refreshes (e.g. the ingest
    // debounce firing mid-harvest) must not navigate away from the render page.
    private bool _mermaidHarvestActive;
    private const int HeavyChangeThreshold = 32; // chars changed in one edit above which it's a paste, not typing
    private readonly Services.ClipboardIngestService _clipboardIngest;
    private readonly Services.FolderIngestService _folderIngest;
    private readonly Services.ApiServer _apiServer;
    private readonly SemaphoreSlim _convertLock = new(1, 1);
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private bool _exitRequested;

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
        _previewDebounce.Tick += async (_, _) =>
        {
            var heavy = _nextRefreshHeavy;
            _nextRefreshHeavy = false;
            await RefreshPreviewAsync(heavy);
        };

        _spinTimer = DispatcherQueue.CreateTimer();
        _spinTimer.Interval = TimeSpan.FromMilliseconds(16);
        _spinTimer.IsRepeating = true;
        _spinTimer.Tick += (_, _) => OnSpinTick();

        _clipboardIngest = new Services.ClipboardIngestService(DispatcherQueue, (text, origin) => IngestFromSource(text, origin));
        _folderIngest = new Services.FolderIngestService(DispatcherQueue, path => _ = OnWatchedFileAsync(path));
        _apiServer = new Services.ApiServer(
            App.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin, ovr) => DispatcherQueue.TryEnqueue(() => IngestFromSource(md, origin, ovr)),
            ConvertForApiAsync,
            App.Governance);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        App.License.Changed += () => DispatcherQueue.TryEnqueue(() => { SyncAdvancedSection(); UpdateLicenseBanner(); });
        SyncSourcePanels();
        ApplyAutomationSettings();
        SyncAdvancedSection();
        UpdateLicenseBanner();
        ExtensionTip.IsOpen = ViewModel.ShowExtensionTip;
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

        if (e.PropertyName == nameof(ViewModels.MainViewModel.AdvancedMode))
        {
            SyncAdvancedSection();
        }

        if (e.PropertyName is not null && AutomationProperties.Contains(e.PropertyName))
        {
            ApplyAutomationSettings();
        }

        if (e.PropertyName is not null && PreviewAffectingProperties.Contains(e.PropertyName))
        {
            if (e.PropertyName == nameof(ViewModels.MainViewModel.PastedMarkdown))
            {
                // Small per-keystroke delta = typing (light); a big jump = paste (heavy).
                var len = ViewModel.PastedMarkdown?.Length ?? 0;
                if (Math.Abs(len - _lastMarkdownLen) > HeavyChangeThreshold) _nextRefreshHeavy = true;
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

    // Shows the upgrade banner only when the Pro trial is nearly up (<= 5 days) or has ended.
    private void UpdateLicenseBanner()
    {
        var st = App.License.State;
        if (st.Edition == Models.Edition.Pro) { LicenseBanner.IsOpen = false; return; }

        if (st.Edition == Models.Edition.Trial)
        {
            var daysLeft = st.ExpiresUtc is { } e ? Math.Max(0, (int)Math.Ceiling((e - DateTimeOffset.UtcNow).TotalDays)) : 0;
            if (daysLeft > 5) { LicenseBanner.IsOpen = false; return; }
            LicenseBanner.Severity = InfoBarSeverity.Informational;
            LicenseBanner.Title = $"Pro trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left";
            LicenseBanner.Message = "Keep DOCX export, editable equations, automation, and footer-free exports.";
        }
        else // Free
        {
            LicenseBanner.Severity = InfoBarSeverity.Warning;
            LicenseBanner.Title = "Your Pro trial has ended";
            LicenseBanner.Message = "Upgrade to unlock DOCX export, editable equations, automation, and remove the footer.";
        }
        LicenseBanner.IsOpen = true;
    }

    private async void OnUpgradeClick(object sender, RoutedEventArgs e)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(Services.LicenseService.StoreUrl)); }
        catch { /* no browser / bad uri */ }
    }

    // Advanced formatting personalization is a Pro feature: the section shows only when Advanced mode
    // is on AND the user is Pro (or on trial). Re-synced whenever either changes.
    private void SyncAdvancedSection() =>
        AdvancedStyleSection.Visibility =
            (ViewModel.AdvancedMode && App.License.IsPro) ? Visibility.Visible : Visibility.Collapsed;

    private async void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new Views.SettingsView(),
            CloseButtonText = "Close",
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
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

    private void OnExtensionTipClosed(InfoBar sender, object args) => ViewModel.ShowExtensionTip = false;

    // Clipboard / API / extension ingests land here. Always update the UI; when "auto-generate PDF
    // from AI-chat ingests" is on, also export a PDF — so the extension sending a conversation at
    // its end produces a finished document with no clicks.
    private void IngestFromSource(string text, string origin, Models.OutputOverride? output = null)
    {
        ViewModel.IngestMarkdown(text, origin);
        if (!ViewModel.AutoConvertIngests) return;
        if (App.License.CanAutomate) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = "Hands-free auto-convert is a Marksmith Pro feature. The content is ready — export it manually, or upgrade in Settings ⚙.";
            ViewModel.StatusSeverity = InfoBarSeverity.Warning;
        }
    }

    // Which file formats an automated export should produce. "both" = pdf,docx; pptx/epub are
    // recognized but currently throw NotImplemented (roadmap groundwork).
    private static string[] ParseFormats(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return new[] { "pdf" };
        if (format.Trim().Equals("both", StringComparison.OrdinalIgnoreCase)) return new[] { "pdf", "docx" };
        var fmts = format.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .Where(f => f is "pdf" or "docx" or "pptx" or "epub")
            .Distinct().ToArray();
        return fmts.Length > 0 ? fmts : new[] { "pdf" };
    }

    // The export uses the caller's output profile (theme, layout, formatting, folder) merged over the
    // app's settings, and produces whichever format(s) the caller asked for — so the extension fully
    // drives automated output without touching the app UI.
    private async Task AutoExportIngestAsync(Models.OutputOverride? output)
    {
        await _convertLock.WaitAsync();
        try
        {
            var md = ViewModel.PastedMarkdown;
            if (string.IsNullOrWhiteSpace(md)) return;

            var settings = App.Settings.Current.CloneWith(output);
            Directory.CreateDirectory(settings.OutputFolder);
            var label = (ViewModel.LastClassification?.SourceName ?? "chat").Replace(" ", "");
            var stem = Path.Combine(settings.OutputFolder, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var produced = new List<string>();
            var pending = new List<string>();
            var formats = ParseFormats(output?.Format);

            // Pre-rasterize mermaid diagrams once if a DOCX will need them (Snapshot mode, or as
            // ShapeForge's fallback for unsupported diagram types).
            List<byte[]?>? mermaidImgs = null;
            if (formats.Contains("docx") && md.Contains("```mermaid", StringComparison.Ordinal))
                mermaidImgs = await RenderMermaidPngsAsync(md, settings);

            foreach (var fmt in formats)
            {
                var outPath = $"{stem}.{fmt}";
                try
                {
                    switch (fmt)
                    {
                        case "pdf":
                            var theme = App.Themes.GetOrDefault(settings.Theme);
                            var html = App.MarkdownHtml.Render(md, settings, theme, ViewModel.LastClassification);
                            await new Services.PdfExportService().ExportAsync(PreviewWebView, html, outPath, settings);
                            break;
                        case "docx":
                            if (settings.AppendToRunningDoc && !string.IsNullOrWhiteSpace(settings.RunningDocPath))
                            {
                                await new Services.DocxExportService().ExportAppendAsync(md, settings.RunningDocPath, settings, mermaidImgs);
                                outPath = settings.RunningDocPath;
                            }
                            else await new Services.DocxExportService().ExportAsync(md, outPath, settings, mermaidImgs);
                            break;
                        case "pptx": await new Services.PptxExportService().ExportAsync(md, outPath, settings); break;
                        case "epub": await new Services.EpubExportService().ExportAsync(md, outPath, settings); break;
                    }
                    produced.Add(outPath);
                    ViewModel.RecordExport(fmt.ToUpperInvariant(), outPath, md);
                }
                catch (NotImplementedException)
                {
                    pending.Add(fmt.ToUpperInvariant()); // roadmap format — skip gracefully
                }
            }

            if (produced.Count > 0)
            {
                ViewModel.LastOutputPath = produced[^1];
                ViewModel.StatusText = $"Auto-generated: {string.Join(", ", produced.Select(Path.GetFileName))}"
                    + (pending.Count > 0 ? $"  ({string.Join("/", pending)} coming soon)" : "");
                ViewModel.StatusSeverity = InfoBarSeverity.Success;
                ShowPdfToast(produced[^1]);
            }
            else if (pending.Count > 0)
            {
                ViewModel.StatusText = $"{string.Join("/", pending)} export is on the roadmap — not yet available.";
                ViewModel.StatusSeverity = InfoBarSeverity.Warning;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Auto-generate failed: {ex.Message}";
            ViewModel.StatusSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            _convertLock.Release();
            await RefreshPreviewAsync();
        }
    }

    // A watched file always ingests into the UI; in auto-convert mode it also goes straight to
    // PDF in the output folder with a toast — the fully hands-off pipeline.
    private async Task OnWatchedFileAsync(string path)
    {
        ViewModel.IngestFile(path);
        if (!ViewModel.WatchFolderAutoConvert) return;
        if (!App.License.CanAutomate)
        {
            ViewModel.StatusText = "Hands-free watch-folder conversion is a Marksmith Pro feature. Upgrade in Settings ⚙.";
            ViewModel.StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

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
                ViewModel.RecordExport("PDF", outPath, ViewModel.PastedMarkdown);
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

    private async void OnBrowseRunningDocClick(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "AI-notebook" };
        picker.FileTypeChoices.Add("Word document", new List<string> { ".docx" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is not null) ViewModel.RunningDocPath = file.Path;
    }

    // Runs an /api/convert request on the UI thread using the shared preview WebView2 (WebView2
    // can't render off-tree — see PdfExportService). Serialized by _convertLock; the preview is
    // restored afterwards.
    private async Task<byte[]> ConvertForApiAsync(string markdown, Models.OutputOverride? output)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(async () =>
        {
            await _convertLock.WaitAsync();
            try
            {
                var settings = App.Settings.Current.CloneWith(output);
                var md = markdown;
                Models.LlmClassification? classification = null;
                if (settings.NormalizeLlm)
                {
                    classification = App.LlmSource.Classify(md);
                    (md, _) = App.LlmSource.Normalize(md, classification);
                }
                var theme = App.Themes.GetOrDefault(settings.Theme);
                var html = App.MarkdownHtml.Render(md, settings, theme, classification);
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.pdf");
                await new Services.PdfExportService().ExportAsync(PreviewWebView, html, tmp, settings);
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

    // Rasterizes every ```mermaid fence in the markdown to a PNG (2x scale) using the preview
    // WebView2: navigate to a tiny self-contained render page (mermaid.js + svg→canvas), poll for
    // completion, then restore the live preview. Returns one entry per fence, null where a diagram
    // failed to render — DocxExportService then falls back per-diagram. Used by Snapshot mode and as
    // ShapeForge's fallback for diagram types the shape engine can't parse.
    public async Task<List<byte[]?>> RenderMermaidPngsAsync(string markdown, Models.AppSettings settings)
    {
        var fences = System.Text.RegularExpressions.Regex.Matches(
                markdown.Replace("\r", ""), "```mermaid[ \\t]*\\n(.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        if (fences.Count == 0) return new();
        if (PreviewWebView.CoreWebView2 is null)
        {
            try { await PreviewWebView.EnsureCoreWebView2Async(); } catch { return new(); }
            if (PreviewWebView.CoreWebView2 is null) return new();
        }

        var theme = App.Themes.GetOrDefault(settings.Theme);
        var sourcesJson = System.Text.Json.JsonSerializer.Serialize(fences);
        var html = $$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8">
            <script src="https://cdn.jsdelivr.net/npm/mermaid@11.4.1/dist/mermaid.min.js"></script></head>
            <body><script>
            window.__pngs = null;
            const sources = {{sourcesJson}};
            mermaid.initialize({ startOnLoad: false, theme: "base",
              themeVariables: { primaryColor: "{{theme.Background}}", primaryTextColor: "{{theme.Primary}}",
                primaryBorderColor: "{{theme.Line}}", lineColor: "{{theme.Line}}",
                secondaryColor: "{{theme.Secondary}}", tertiaryColor: "{{theme.Background}}" },
              flowchart: { useMaxWidth: false, htmlLabels: false, curve: "linear" },
              securityLevel: "loose" });
            (async () => {
              const out = [];
              for (let i = 0; i < sources.length; i++) {
                try { out.push(await toPng((await mermaid.render("m" + i, sources[i])).svg)); }
                catch (e) { out.push(null); }
              }
              window.__pngs = out;
            })();
            async function toPng(svgText) {
              // Give the SVG explicit pixel dimensions from its viewBox so <img> sizes it correctly.
              const vb = /viewBox="[-\d.]+ [-\d.]+ ([\d.]+) ([\d.]+)"/.exec(svgText);
              const w = vb ? Math.ceil(parseFloat(vb[1])) : 600, h = vb ? Math.ceil(parseFloat(vb[2])) : 400;
              // Set explicit dimensions via the DOM (mermaid may already carry width/height attrs —
              // string-injecting duplicates would make the XML invalid and the <img> reject it).
              const parsed = new DOMParser().parseFromString(svgText, "image/svg+xml");
              if (parsed.querySelector("parsererror")) throw new Error("svg parse failed");
              const el = parsed.documentElement;
              el.setAttribute("width", String(w)); el.setAttribute("height", String(h));
              el.removeAttribute("style");
              svgText = new XMLSerializer().serializeToString(el);
              // data: URI, not a blob URL — NavigateToString pages have an opaque origin, where
              // blob: URLs refuse to load into <img>.
              const url = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(svgText);
              const img = new Image();
              await new Promise((res, rej) => { img.onload = res; img.onerror = rej; img.src = url; });
              const c = document.createElement("canvas"); c.width = w * 2; c.height = h * 2;
              const ctx = c.getContext("2d");
              ctx.fillStyle = "{{theme.Background}}"; ctx.fillRect(0, 0, c.width, c.height);
              ctx.drawImage(img, 0, 0, c.width, c.height);
              return c.toDataURL("image/png");
            }
            </script></body></html>
            """;

        var result = new List<byte[]?>();
        try
        {
            _mermaidHarvestActive = true;
            _previewDebounce.Stop(); // a pending ingest refresh must not wipe the render page
            PreviewWebView.CoreWebView2.NavigateToString(html);
            for (int i = 0; i < 60; i++) // up to ~9s (CDN + render)
            {
                await Task.Delay(150);
                var raw = await PreviewWebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.__pngs)");
                if (raw is null or "null" or "\"null\"") continue;
                var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrEmpty(json) || json == "null") continue;
                var urls = System.Text.Json.JsonSerializer.Deserialize<List<string?>>(json) ?? new();
                foreach (var u in urls)
                    result.Add(u is not null && u.StartsWith("data:image/png;base64,")
                        ? Convert.FromBase64String(u["data:image/png;base64,".Length..])
                        : null);
                break;
            }
        }
        catch { /* rendering is best-effort; exporter falls back per-diagram */ }
        finally
        {
            _mermaidHarvestActive = false;
            await RefreshPreviewAsync(false); // restore the live preview silently
        }
        while (result.Count < fences.Count) result.Add(null);
        return result;
    }

    private async Task RefreshPreviewAsync(bool heavy = true)
    {
        if (PreviewWebView.CoreWebView2 is null) return;
        if (_mermaidHarvestActive) return; // snapshot renderer owns the WebView right now

        if (heavy) StartSpinner();

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
        // Heavy refreshes render blurred, then unblur when the spinner clears (see HideSpinner).
        if (heavy) html = html.Replace("<body>", "<body class=\"ms-loading\">");
        PreviewWebView.CoreWebView2.NavigateToString(html);
    }
}
