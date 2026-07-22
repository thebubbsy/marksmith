using MdToPdf.Avalonia.Controls;
using System.Linq;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Platform;
using global::Avalonia.Platform.Storage;

using MdToPdf.Avalonia.Hosting;
using MdToPdf.Services;
using MdToPdf.ViewModels;
using Microsoft.Web.WebView2.Core;

using MdToPdf.Avalonia.Controls;
using System.Linq;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Platform;
using global::Avalonia.Platform.Storage;

using MdToPdf.Avalonia.Hosting;
using MdToPdf.Services;
using MdToPdf.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace MdToPdf.Avalonia.Views;

public partial class MainWindow : Window, IWebRenderHost, IUiPrompts
{
    private async void MarkdownEditor_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var pos = MarkdownEditor.GetPositionFromPoint(e.GetPosition(MarkdownEditor));
            if (pos != null)
            {
                int lineNum = pos.Value.Line;
                var ambiguity = _ambiguityColorizer.GetAmbiguityAtLine(lineNum);
                if (ambiguity != null)
                {
                    e.Handled = true;
                    var result = await ShowAmbiguityResolverDialogAsync(ambiguity);
                    if (result != null)
                    {
                        var prefs = AppServices.Settings.Current.AmbiguityPreferences;
                        prefs.RemoveAll(p => p.Kind == ambiguity.Kind);
                        prefs.Add(new MdToPdf.Models.AmbiguityPreference { Kind = ambiguity.Kind, ChosenLabel = result.Label });
                        AppServices.Settings.Save();
                        
                        var doc = Markdig.Markdown.Parse(MarkdownEditor.Text);
                        var ambiguities = AmbiguityDetector.Detect(doc, MarkdownEditor.Text);
                        _ambiguityColorizer.UpdateAmbiguities(ambiguities);
                        MarkdownEditor.TextArea.TextView.Redraw();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MarkdownEditor_PointerPressed error: {ex}");
        }
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private static readonly Uri PreviewBaseUri = new("https://marksmith.local/");

    private readonly ClipboardWatcherService _clipboardWatcher;
    private readonly FolderWatcherService _folderWatcher;
    private readonly AutomationManager _automationManager;
    private readonly ExportCoordinator _exportCoordinator = AppServices.ExportCoordinator;

    private readonly AmbiguityColorizer _ambiguityColorizer = new();
    private bool _isUpdatingEditor = false;
    private bool _loaded;

    private readonly global::Avalonia.Threading.DispatcherTimer _previewDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(180),
    };

    public MainWindow()
    {
        InitializeComponent();

        MarkdownEditor.TextArea.TextView.LineTransformers.Add(_ambiguityColorizer);
        MarkdownEditor.TextChanged += (s, e) =>
        {
            if (_isUpdatingEditor) return;
            _isUpdatingEditor = true;
            ViewModel.PastedMarkdown = MarkdownEditor.Text;
            _isUpdatingEditor = false;

            try 
            {
                var doc = Markdig.Markdown.Parse(MarkdownEditor.Text);
                var ambiguities = AmbiguityDetector.Detect(doc, MarkdownEditor.Text);
                _ambiguityColorizer.UpdateAmbiguities(ambiguities);
                MarkdownEditor.TextArea.TextView.Redraw();
            }
            catch { }
        };

        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            _ = RefreshPreviewAsync();
        };

        _clipboardWatcher = new ClipboardWatcherService(Clipboard!, (text, origin, output) => IngestFromSource(text, origin, output));
        _folderWatcher = new FolderWatcherService(path => _ = OnWatchedFileAsync(path));
        _automationManager = new AutomationManager(
            AppServices.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin, ovr) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => IngestFromSource(md, origin, ovr)),
            ConvertForApiAsync,
            AppServices.Governance,
            () => AppServices.Settings.Current.AllowedExtensionId,
            () => AppServices.Settings.Current,
            settings => { AppServices.Settings.Current.UpdateFrom(settings); AppServices.Settings.Save(); },
            BatchConvertForApiAsync);

        Loaded += async (_, _) =>
        {
            try
            {
                ViewModel.PropertyChanged += OnViewModelPropertyChanged;
                ApplyAutomationSettings();
                ViewModel.LoadPresets();
                UpdateLicenseBanner();

                // Hook WebMessageReceived for Avalonia on Windows
                if (OperatingSystem.IsWindows() &&
                    PreviewWebView.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle)
                {
                    try
                    {
                        var core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                        core.WebMessageReceived += OnPreviewWebMessage;
                    }
                    catch { }
                }

                await RefreshPreviewAsync();
                _loaded = true;
                await LoadMarkdownFilesAsync();
            }
            catch (Exception ex)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.StatusText = $"Startup error: {ex.Message}";
                    vm.StatusSeverity = Models.StatusSeverity.Error;
                }
                System.Diagnostics.Debug.WriteLine($"MainWindow Loaded error: {ex}");
            }
        };

        Closed += (_, _) =>
        {
            _clipboardWatcher.Dispose();
            _folderWatcher.Dispose();
            _automationManager.Dispose();
        };
    }

    private void ApplyAutomationSettings()
    {
        _automationManager.ApplyAutomationSettings(
            ViewModel,
            () => _clipboardWatcher.Start(),
            () => _clipboardWatcher.Stop(),
            _clipboardWatcher.IsRunning,
            folder => _folderWatcher.Start(folder),
            () => _folderWatcher.Stop(),
            _folderWatcher.IsRunning,
            status =>
            {
                ApiUrlText.Text = status;
                ApiUrlText.IsVisible = _automationManager.IsApiRunning;
            });
    }

    private void IngestFromSource(string text, string origin, Models.OutputOverride? output = null)
    {
        ViewModel.IngestMarkdown(text, origin, output);
        if (!ViewModel.AutoConvertIngests) return;
        if (AppServices.License.CanAutomate) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = "Hands-free auto-convert is a Marksmith Pro feature. The content is ready — export it manually, or upgrade in Settings.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

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

    private Task AutoExportIngestAsync(Models.OutputOverride? output) =>
        _exportCoordinator.AutoExportIngestAsync(
            ViewModel, output, this, null, null, () => RefreshPreviewAsync());

    private Task OnWatchedFileAsync(string path) =>
        _exportCoordinator.OnWatchedFileAsync(
            ViewModel, path, this, null, null, () => RefreshPreviewAsync());

    private Task<byte[]> ConvertForApiAsync(string markdown, Models.OutputOverride? output)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var bytes = await _exportCoordinator.ConvertForApiAsync(
                    ViewModel, markdown, output, this, null, () => RefreshPreviewAsync());
                tcs.SetResult(bytes);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task<object> BatchConvertForApiAsync(string folderPath, string format, Models.OutputOverride? ovr)
    {
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var res = await _exportCoordinator.BatchConvertForApiAsync(
                    ViewModel, folderPath, format, ovr, this, null, () => RefreshPreviewAsync());
                tcs.SetResult(res);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // ---- IWebRenderHost ----

    public Task<bool> EnsureReadyAsync() => Task.FromResult(_loaded);

    public Task NavigateToStringAsync(string html)
    {
        PreviewWebView.NavigateToString(html, PreviewBaseUri);
        return Task.CompletedTask;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript) =>
        await PreviewWebView.InvokeScript(javaScript);

    public async Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
    {
        if (OperatingSystem.IsWindows() &&
            PreviewWebView.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle handle)
        {
            try
            {
                var core = CoreWebView2.CreateFromComICoreWebView2(handle.CoreWebView2);
                var printSettings = core.Environment.CreatePrintSettings();
                printSettings.PageWidth = setup.PageWidthIn;
                printSettings.PageHeight = setup.PageHeightIn;
                printSettings.MarginTop = setup.MarginTopIn;
                printSettings.MarginBottom = setup.MarginBottomIn;
                printSettings.MarginLeft = setup.MarginLeftIn;
                printSettings.MarginRight = setup.MarginRightIn;
                printSettings.ShouldPrintBackgrounds = setup.PrintBackgrounds;
                return await core.PrintToPdfAsync(outputPath, printSettings);
            }
            catch
            {
                // Fall through to generic path.
            }
        }

        if (OperatingSystem.IsLinux() &&
            PreviewWebView.TryGetPlatformHandle() is IGtkWebViewPlatformHandle gtkHandle)
        {
            try
            {
                return await Hosting.LinuxNativePrint.PrintToPdfAsync(gtkHandle.WebKitWebView, outputPath, setup);
            }
            catch
            {
                // Fall through to generic path.
            }
        }

        if (OperatingSystem.IsMacOS() &&
            PreviewWebView.TryGetPlatformHandle() is IAppleWKWebViewPlatformHandle appleHandle)
        {
            try
            {
                return await Hosting.MacNativePrint.PrintToPdf(appleHandle.WKWebView, outputPath, setup);
            }
            catch
            {
                // Fall through to generic path.
            }
        }

        try
        {
            var pdfStream = await PreviewWebView.PrintToPdfStreamAsync();
            await using var file = File.Create(outputPath);
            await pdfStream.CopyToAsync(file);
            return true;
        }
        catch { return false; }
    }

    private bool _harvestActive;

    public Task BeginHarvestAsync()
    {
        _harvestActive = true;
        return Task.CompletedTask;
    }

    public async Task EndHarvestAsync()
    {
        _harvestActive = false;
        await RefreshPreviewAsync();
    }

    // ---- IUiPrompts ----

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        PreviewWebView.IsVisible = false;
        try { return await dialog.ShowAsync(); }
        finally { PreviewWebView.IsVisible = true; }
    }

    private async void OnCreateThemeClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var baseTheme = AppServices.Themes.GetOrDefault(ViewModel.SelectedThemeName);
            var editingCustom = !AppServices.Themes.IsBuiltin(baseTheme.Name);

            var nameBox = new TextBox
            {
                Watermark = "Theme name",
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

            var pickers = new ColorPicker[elements.Length];
            var rows = new StackPanel { Spacing = 8 };
            rows.Children.Add(nameBox);
            for (int i = 0; i < elements.Length; i++)
            {
                pickers[i] = new ColorPicker
                {
                    Color = Color.Parse(elements[i].Hex),
                    IsAlphaEnabled = false,
                    HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                    MinWidth = 90,
                };
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                var label = new StackPanel { VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
                label.Children.Add(new TextBlock { Text = elements[i].Label });
                label.Children.Add(new TextBlock { Text = elements[i].Hint, FontSize = 11, Opacity = 0.6 });
                Grid.SetColumn(label, 0);
                Grid.SetColumn(pickers[i], 1);
                grid.Children.Add(label);
                grid.Children.Add(pickers[i]);
                rows.Children.Add(grid);
            }

            var dialog = new ContentDialog
            {
                Title = editingCustom ? $"Edit “{baseTheme.Name}”" : "Create a theme",
                Content = new ScrollViewer { Content = rows, MaxHeight = 520 },
                PrimaryButtonText = "Save theme",
                SecondaryButtonText = editingCustom ? "Delete" : null,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await ShowDialogAsync(dialog);

            if (result == ContentDialogResult.Secondary && editingCustom)
            {
                CustomThemeStore.Remove(baseTheme.Name);
                RefreshThemeNames(select: AppServices.Themes.All[0].Name);
                return;
            }
            if (result != ContentDialogResult.Primary) return;

            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0) name = "My theme";
            if (AppServices.Themes.IsBuiltin(name)) name += " (custom)";

            static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            CustomThemeStore.AddOrUpdate(new Models.ThemeDefinition(
                name,
                Hex(pickers[0].Color), Hex(pickers[1].Color), Hex(pickers[2].Color), Hex(pickers[3].Color),
                Hex(pickers[4].Color), Hex(pickers[5].Color), Hex(pickers[6].Color), Hex(pickers[7].Color)));
            RefreshThemeNames(select: name);
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Theme creation failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    private void RefreshThemeNames(string select)
    {
        var vm = ViewModel;
        vm.ThemeNames.Clear();
        foreach (var t in AppServices.Themes.All) vm.ThemeNames.Add(t.Name);
        vm.SelectedThemeName = select;
    }

    public async Task<int> AskOversizedDiagramModeAsync()
    {
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
            Text = "This document has a large diagram that won't fit a printed page. How should Marksmith put it into Word?"
        });

        var rbGroup = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var rbExact = new RadioButton { Content = "Keep exact layout (Opens in Web Layout view)", IsChecked = true, Tag = 1 };
        var rbReflow = new RadioButton { Content = "Reflow to fit page (Uniform scale)", Tag = 2 };
        var rbCompactSpace = new RadioButton { Content = "Compact spacing (Shrink gaps first)", Tag = 6 };
        var rbCompactShapes = new RadioButton { Content = "Compact shapes (Shrink shapes first)", Tag = 7 };
        var rbUltraCompact = new RadioButton { Content = "Ultra compact (Shrink both equally)", Tag = 8 };

        rbGroup.Children.Add(rbExact);
        rbGroup.Children.Add(rbReflow);
        rbGroup.Children.Add(rbCompactSpace);
        rbGroup.Children.Add(rbCompactShapes);
        rbGroup.Children.Add(rbUltraCompact);
        body.Children.Add(rbGroup);

        var dialog = new ContentDialog
        {
            Title = "Large diagram",
            Content = body,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary) return 1;

        if (rbReflow.IsChecked == true) return 2;
        if (rbCompactSpace.IsChecked == true) return 6;
        if (rbCompactShapes.IsChecked == true) return 7;
        if (rbUltraCompact.IsChecked == true) return 8;
        return 1;
    }

    public Task<MdToPdf.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MdToPdf.Models.AmbiguityCase ambiguity)
    {
        return AmbiguityResolverDialog.ShowAsync(this, ambiguity);
    }

    // ---- Preview ----

    private async Task RefreshPreviewAsync()
    {
        if (_harvestActive) return;
        var vm = ViewModel;
        string markdown;
        if (vm.UsePasteSource) markdown = vm.PastedMarkdown;
        else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            markdown = await MdToPdf.Plugins.PluginFileReader.ReadAsMarkdownAsync(vm.InputFilePath);
        else markdown = "# Marksmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";

        var html = vm.BuildPreviewHtml(vm.PrepareMarkdown(markdown), interactive: true);
        await NavigateToStringAsync(html);
    }

    private async void OnGetExtensionClick(object? sender, RoutedEventArgs e)
    {
        try { await global::Avalonia.Controls.TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri("https://github.com/thebubbsy/marksmith/tree/main/extension")); }
        catch { /* no browser */ }
    }

    private void OnExtensionTipClosed(object? sender, RoutedEventArgs args) => ViewModel.ShowExtensionTip = false;

    private static readonly HashSet<string> AutomationProperties = new()
    {
        nameof(MainViewModel.AutoClipboardIngest),
        nameof(MainViewModel.WatchFolderEnabled),
        nameof(MainViewModel.WatchFolder),
        nameof(MainViewModel.ApiEnabled),
        nameof(MainViewModel.ApiPort),
    };

    private static SemaphoreSlim _convertLock => AppServices.ExportCoordinator.ConvertLock;
    private bool _plainPasteHintShown;
    private int _lastMarkdownLen;
    private const int HeavyChangeThreshold = 250;

    private void MaybeShowPlainPasteHint(string text)
    {
        if (_plainPasteHintShown || text.Length < 250) return;
        if (!LooksLikePlainText(text)) return;
        _plainPasteHintShown = true;
        ExtensionHintBar.IsVisible = true;
        Services.ApiServer.AttentionTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool LooksLikePlainText(string t)
    {
        var lineCount = 0;
        for (int i = 0; i < t.Length; i++) if (t[i] == '\n') lineCount++;
        if (lineCount < 5) return false;
        if (t.Contains('#') || t.Contains("```") || t.Contains("**") || t.Contains("- ")) return false;
        return true;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PastedMarkdown))
        {
            if (!_isUpdatingEditor && MarkdownEditor.Text != ViewModel.PastedMarkdown)
            {
                _isUpdatingEditor = true;
                MarkdownEditor.Text = ViewModel.PastedMarkdown;
                _isUpdatingEditor = false;
            }
            var len = ViewModel.PastedMarkdown?.Length ?? 0;
            if (Math.Abs(len - _lastMarkdownLen) > HeavyChangeThreshold)
            {
                MaybeShowPlainPasteHint(ViewModel.PastedMarkdown ?? "");
            }
            _lastMarkdownLen = len;
            _previewDebounce.Stop();
        }
        else if (e.PropertyName is nameof(MainViewModel.InputFilePath)
            or nameof(MainViewModel.UsePasteSource) or nameof(MainViewModel.SelectedThemeName)
            or nameof(MainViewModel.ContentWidth) or nameof(MainViewModel.A4FixedWidth)
            or nameof(MainViewModel.UnlimitedHeight) or nameof(MainViewModel.IncludeToc)
            or nameof(MainViewModel.ShowAttribution) or nameof(MainViewModel.NoEmoji)
            or nameof(MainViewModel.BrandFontFamily))
        {
            _previewDebounce.Stop();
            _previewDebounce.Start();
        }

        if (e.PropertyName is not null && AutomationProperties.Contains(e.PropertyName))
            ApplyAutomationSettings();
            
        if (e.PropertyName == nameof(MainViewModel.ShowExtensionTip))
            ExtensionTip.IsOpen = ViewModel.ShowExtensionTip;
    }

    private void OnTakeTourClick(object? sender, RoutedEventArgs e) => _ = ShowWelcomeTourAsync();

    private async Task ShowWelcomeTourAsync()
    {
        WelcomeTour? tour = null;
        try
        {
            tour = new WelcomeTour();
            var dialog = new ContentDialog
            {
                Content = tour,
                Padding = new Thickness(0),
            };
            // The tour card is 540 wide; the ContentDialog's default ContentDialogMaxWidth (~548)
            // leaves no room for the dialog's own chrome, so the right edge — the Next / Get started
            // button — was being clipped.
            // In FluentAvalonia, ContentDialog max width can be controlled, or we just rely on standard bounds.
            dialog.Resources["ContentDialogMaxWidth"] = 760.0;
            dialog.Resources["ContentDialogMinWidth"] = 560.0;
            dialog.Resources["ContentDialogMaxHeight"] = 940.0;
            tour.Completed += (_, _) => dialog.Hide();
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Tour failed to open: {ex.GetType().Name}: {ex.Message}";
        }

        if (!AppServices.Settings.Current.HasSeenWelcome)
        {
            AppServices.Settings.Current.HasSeenWelcome = true;
            AppServices.Settings.Save();
        }

        if (tour?.LoadSampleRequested == true) LoadSampleDocument();
    }

    private const string SampleMarkdown = """
        # Quarterly Review — Sample Document

        This is a **sample** so you can try Marksmith without hunting for a Markdown file.
        Restyle it on the right, then hit **Generate PDF** below.

        > [!TIP]
        > Everything here survives export: the table, the math, and the diagrams.

        ## Numbers that hold up

        | Region | Revenue | Change |
        |--------|---------|--------|
        | APAC   | $4.2M   | +12%   |
        | EU     | $3.1M   | +5%    |
        | US     | $5.5M   | +9%    |

        Reserves follow $R = \sum_{i=1}^{n} p_i \cdot L_i$ — and in Word export this becomes a
        real, editable equation, not a picture.

        ## A live diagram

        ```mermaid
        flowchart LR
          A[Paste a chat] --> B{Marksmith}
          B --> C[Polished PDF]
          B --> D[Editable Word]
        ```

        ## Plugin engines

        ```plantuml
        You -> Marksmith: paste markdown
        Marksmith --> You: finished document
        ```

        Six diagram languages render from plain code fences — Mermaid is built in, and PlantUML,
        Graphviz, D2, Typst and Vega-Lite are one-click installs in **Settings → Plugins**.
        """;

    private void LoadSampleDocument()
    {
        if (!string.IsNullOrWhiteSpace(ViewModel.PastedMarkdown)) return;
        ViewModel.UsePasteSource = true;
        ViewModel.PastedMarkdown = SampleMarkdown;
    }

    // ---- License banner ----

    private void UpdateLicenseBanner()
    {
        var st = AppServices.License.State;
        if (st.Edition == Models.Edition.Pro) { LicenseBanner.IsOpen = false; return; }

        if (st.Edition == Models.Edition.Trial)
        {
            var daysLeft = st.ExpiresUtc is { } exp ? Math.Max(0, (int)Math.Ceiling((exp - DateTimeOffset.UtcNow).TotalDays)) : 0;
            if (daysLeft > 5) { LicenseBanner.IsOpen = false; return; }
            LicenseBanner.Severity = InfoBarSeverity.Informational;
            LicenseBanner.Title = $"Pro trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left";
            LicenseBanner.Message = "Keep DOCX export, editable equations, automation, and footer-free exports.";
        }
        else
        {
            LicenseBanner.Severity = InfoBarSeverity.Warning;
            LicenseBanner.Title = "Your Pro trial has ended";
            LicenseBanner.Message = "Upgrade to unlock DOCX export, editable equations, automation, and remove the footer.";
        }
        LicenseBanner.IsOpen = true;
    }

    private void OnUpgradeClick(object? sender, RoutedEventArgs e)
    {
        try { OpenUrl(Services.LicenseService.StoreUrl); } catch { }
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    // ---- Source panel ----

    private void OnFileTabClick(object? sender, RoutedEventArgs e) => ViewModel.UsePasteSource = false;
    private void OnPasteTabClick(object? sender, RoutedEventArgs e) => ViewModel.UsePasteSource = true;

    private void OnSourceDragOver(object? sender, DragEventArgs e)
    {
        var hasFile = e.DataTransfer.Items.Any(i => i.TryGetFile() is not null);
        e.DragEffects = hasFile ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        // Importer plugins (e.g. Pandoc) widen what "a droppable document" means — see
        // MdToPdf.Core/Plugins/PluginFileReader for where the conversion happens on read.
        var importerExts = AppServices.Plugins.AllImporterExtensions;
        var file = e.DataTransfer.Items
            .Select(i => i.TryGetFile())
            .Select(f => f?.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && (Path.GetExtension(p) is ".md" or ".markdown" or ".txt"
                || importerExts.Contains(Path.GetExtension(p).TrimStart('.').ToLowerInvariant())));
        if (file is not null)
        {
            ViewModel.InputFilePath = file;
            ViewModel.UsePasteSource = false;
        }
    }

    private async void OnBrowseFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown") { Patterns = new[] { "*.md", "*.markdown" } },
                new FilePickerFileType("Importable documents")
                {
                    Patterns = AppServices.Plugins.AllImporterExtensions.Select(x => "*." + x).ToArray(),
                },
            },
        });
        var file = files.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path)
        {
            ViewModel.InputFilePath = path;
            ViewModel.UsePasteSource = false;
        }
    }

    private void OnMarkdownFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Services.MarkdownFileEntry entry })
            ViewModel.LoadRecentCommand.Execute(entry.Path);
    }

    private async void OnRescanMarkdownClick(object? sender, RoutedEventArgs e) =>
        await LoadMarkdownFilesAsync();

    private async Task LoadMarkdownFilesAsync()
    {
        RescanButton.IsEnabled = false;
        ScanningLabel.IsVisible = true;
        try { await ViewModel.RefreshMarkdownFilesAsync(); }
        finally { ScanningLabel.IsVisible = false; RescanButton.IsEnabled = true; }
    }

    private async void OnBrowseRunningDocClick(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "AI-notebook",
            FileTypeChoices = new[] { new FilePickerFileType("Word document") { Patterns = new[] { "*.docx" } } },
        });
        if (file?.TryGetLocalPath() is { } path) ViewModel.RunningDocPath = path;
    }

    private async void OnBatchConvertClick(object? sender, RoutedEventArgs e)
    {
        if (!AppServices.License.CanAutomate)
        {
            ViewModel.StatusText = "Batch conversion is a Marksmith Pro feature. Upgrade in Settings ⚙.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select folder to batch convert",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var folderPath = folders[0].Path.LocalPath;

        var files = System.IO.Directory.GetFiles(folderPath, "*.md", System.IO.SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            ViewModel.StatusText = $"No .md files found in {folderPath}.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        var fmt = await AskBatchFormatAsync(files.Length);
        if (fmt is null) return;
        var docxGated = fmt is "docx" && !AppServices.License.CanExportDocx;
        if (docxGated) { ViewModel.StatusText = "Word export is a Marksmith Pro feature."; ViewModel.StatusSeverity = Models.StatusSeverity.Warning; return; }

        if (fmt == "pdf" && !await EnsureReadyAsync())
        {
            ViewModel.StatusText = "Batch failed: the preview engine couldn't start. Try again.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
            return;
        }

        int done = 0, failed = 0;
        var outFolder = AppServices.Settings.Current.OutputFolder;
        System.IO.Directory.CreateDirectory(outFolder);
        foreach (var fPath in files)
        {
            await _convertLock.WaitAsync();
            try
            {
                var md = ViewModel.PrepareMarkdown(await Plugins.PluginFileReader.ReadAsMarkdownAsync(fPath));
                var outPath = System.IO.Path.Combine(outFolder, System.IO.Path.GetFileNameWithoutExtension(fPath) + "." + fmt);
                switch (fmt)
                {
                    case "pdf":
                        await new Services.PdfExportService().ExportAsync(this, ViewModel.BuildPreviewHtml(md), outPath, AppServices.Settings.Current);
                        break;
                    case "docx":
                        await new Services.DocxExportService().ExportAsync(md, outPath, AppServices.Settings.Current);
                        break;
                    case "pptx":
                        await new Services.PptxExportService().ExportAsync(md, outPath, AppServices.Settings.Current);
                        break;
                    case "epub":
                        await new Services.EpubExportService().ExportAsync(md, outPath, AppServices.Settings.Current);
                        break;
                }
                done++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Batch convert failed on {fPath}: {ex}");
                failed++;
            }
            finally
            {
                _convertLock.Release();
            }
        }
        ViewModel.StatusText = $"Batch converted {done} file{(done == 1 ? "" : "s")} to {fmt.ToUpper()}" + (failed > 0 ? $" ({failed} failed)." : ".");
        ViewModel.StatusSeverity = failed > 0 ? Models.StatusSeverity.Warning : Models.StatusSeverity.Success;
    }

    private async Task<string?> AskBatchFormatAsync(int count)
    {
        var combo = new ComboBox { SelectedIndex = 0, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch, Margin = new Thickness(0, 12, 0, 0) };
        combo.ItemsSource = new[] { "PDF", "Word (DOCX)", "PowerPoint (PPTX)", "EPUB" };
        var dialog = new ContentDialog
        {
            Title = $"Batch convert {count} file{(count == 1 ? "" : "s")}",
            Content = new StackPanel { Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Every .md file in the folder is converted to your chosen format, using the current Style settings." }, combo } },
            PrimaryButtonText = "Convert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return combo.SelectedIndex switch { 1 => "docx", 2 => "pptx", 3 => "epub", _ => "pdf" };
    }

    private async void OnBrowseWatchFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) ViewModel.WatchFolder = path;
    }

    // ---- Style panel ----

    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: Models.ExportPreset preset }) ViewModel.ApplyPreset(preset);
    }

    private async void OnSavePresetClick(object? sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "e.g. Client report — dark, branded", Margin = new Thickness(0, 12, 0, 0) };
        var dialog = new ContentDialog
        {
            Title = "Save preset",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Save the current theme, width, cleanup, formatting, diagram mode and branding as a named preset." },
                    box,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            ViewModel.SavePreset(box.Text);
            ViewModel.StatusText = $"Preset saved: {box.Text.Trim()}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
    }

    private void OnDeletePresetClick(object? sender, RoutedEventArgs e)
    {
        if (PresetsCombo.SelectedItem is Models.ExportPreset preset)
        {
            ViewModel.DeletePreset(preset);
            PresetsCombo.SelectedItem = null;
        }
    }

    private async void OnBrowseLogoClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Image") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg" } } },
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path) ViewModel.BrandLogoPath = path;
    }

    // ---- Preview & export panel ----

    private void OnHistoryItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not Models.HistoryEntry entry) return;
        if (File.Exists(entry.OutputPath))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.OutputPath) { UseShellExecute = true });
        else
        {
            ViewModel.StatusText = $"File no longer exists: {entry.OutputPath}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = new SettingsView(),
            CloseButtonText = "Close",
        };
        await ShowDialogAsync(dialog);

        // The license banner reads AppServices.License.State directly, so refresh it in case the
        // user activated/removed a license or the trial state otherwise changed while the dialog was open.
        UpdateLicenseBanner();
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) ViewModel.OutputFolder = path;
    }

    private void OnOpenOutputClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.LastOutputPath is { } path && File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private async void OnConvertPdfClick(object? sender, RoutedEventArgs e) =>
        await ViewModel.ConvertToPdfAsync();

    private async void OnConvertDocxClick(object? sender, RoutedEventArgs e) =>
        await ViewModel.ConvertToDocxAsync();

    private async void OnPreviewWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            if (type == "mermaid-error")
            {
                var error = root.GetProperty("error").GetString() ?? "Unknown parse error";
                System.Diagnostics.Debug.WriteLine($"[Mermaid Error] {error}");
                ViewModel.StatusText = $"Mermaid Syntax Error: {error}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
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

            var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Diagram",
                SuggestedFileName = "diagram",
                DefaultExtension = format,
                FileTypeChoices = new[] { new FilePickerFileType(format.ToUpperInvariant() + " image") { Patterns = new[] { "*." + format } } }
            });
            if (file is null) return;

            var localPath = file.Path.LocalPath;
            if (format == "svg")
                await File.WriteAllTextAsync(localPath, data);
            else
            {
                var b64 = data.Contains(',') ? data[(data.IndexOf(',') + 1)..] : data;
                await File.WriteAllBytesAsync(localPath, Convert.FromBase64String(b64));
            }
            ViewModel.StatusText = $"Diagram saved: {localPath}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Error processing message: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
    }
}
