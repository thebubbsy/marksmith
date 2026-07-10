using System.Linq;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using MdToPdf.Avalonia.Hosting;
using MdToPdf.Services;
using MdToPdf.ViewModels;

namespace MdToPdf.Avalonia.Views;

public partial class MainWindow : Window, IWebRenderHost, IUiPrompts
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private static readonly Uri PreviewBaseUri = new("https://marksmith.local/");

    private readonly ClipboardWatcherService _clipboardWatcher;
    private readonly FolderWatcherService _folderWatcher;
    private readonly ApiServer _apiServer;
    private readonly SemaphoreSlim _convertLock = new(1, 1);

    // Set once the window's startup sequence (first navigate included) has actually run. Lets
    // EnsureReadyAsync report real readiness instead of unconditionally claiming true — a request
    // (e.g. via the REST API) that somehow arrives before Loaded fires would otherwise let
    // MermaidHarvestService drive PreviewWebView before it's ever navigated anything.
    private bool _loaded;

    // Coalesces preview refreshes so typing in the paste editor doesn't re-navigate NativeWebView
    // on every keystroke — the WinUI build has always had this (_previewDebounce there); it never
    // got ported here, which is why typing normal text could make the preview lag by a minute or
    // more: PastedMarkdown updates on every character, and each one was triggering an immediate,
    // un-cancelled full-page NavigateToString (2.5MB mermaid.min.js re-parsed each time content
    // mentions mermaid, but even without that, WebView2 navigations queue up faster than they
    // drain when fired every keystroke).
    private readonly global::Avalonia.Threading.DispatcherTimer _previewDebounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(180),
    };

    public MainWindow()
    {
        InitializeComponent();

        _previewDebounce.Tick += (_, _) =>
        {
            _previewDebounce.Stop();
            _ = RefreshPreviewAsync();
        };

        _clipboardWatcher = new ClipboardWatcherService(Clipboard!, (text, origin, output) => IngestFromSource(text, origin, output));
        _folderWatcher = new FolderWatcherService(path => _ = OnWatchedFileAsync(path));
        _apiServer = new ApiServer(
            AppServices.LlmSource,
            () => ViewModel.ThemeNames.ToList(),
            (md, origin, ovr) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => IngestFromSource(md, origin, ovr)),
            ConvertForApiAsync,
            AppServices.Governance);

        Loaded += async (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ApplyAutomationSettings();
            ViewModel.LoadPresets();
            UpdateLicenseBanner();
            // RefreshPreviewAsync is the one step that actually determines "is the web view ready
            // to navigate/render" — _loaded (and therefore EnsureReadyAsync) must flip true right
            // after it, not after the whole handler. LoadMarkdownFilesAsync does a recursive
            // filesystem scan (Downloads/Documents/Desktop/OneDrive) that can take several seconds;
            // gating readiness on it too meant a user who pasted and hit "Export DOCX" quickly
            // after the window appeared could have EnsureReadyAsync still returning false, silently
            // emptying MermaidHarvestService's results and making every diagram fall back to a
            // plain code block. That's exactly what happened — moving the flag here fixes it.
            await RefreshPreviewAsync();
            _loaded = true;
            await LoadMarkdownFilesAsync();
        };

        Closed += (_, _) =>
        {
            _clipboardWatcher.Dispose();
            _folderWatcher.Dispose();
            _apiServer.Dispose();
        };
    }

    // ---- Automation (clipboard watcher / folder watcher / REST API) ----
    // Portable equivalents of MdToPdf/MainWindow.xaml.cs's ClipboardIngestService/
    // FolderIngestService/ApiServer wiring — ApiServer itself is already in MdToPdf.Core
    // (platform-agnostic); only the clipboard/folder watchers needed Avalonia-specific
    // implementations (see MdToPdf.Avalonia/Hosting).

    private void ApplyAutomationSettings()
    {
        var vm = ViewModel;

        if (vm.AutoClipboardIngest && !_clipboardWatcher.IsRunning) _clipboardWatcher.Start();
        else if (!vm.AutoClipboardIngest && _clipboardWatcher.IsRunning) _clipboardWatcher.Stop();

        if (vm.WatchFolderEnabled && Directory.Exists(vm.WatchFolder)) _folderWatcher.Start(vm.WatchFolder);
        else _folderWatcher.Stop();

        try
        {
            if (vm.ApiEnabled && (!_apiServer.IsRunning || _apiServer.Port != vm.ApiPort)) _apiServer.Start(vm.ApiPort);
            else if (!vm.ApiEnabled) _apiServer.Stop();
            ApiUrlText.Text = _apiServer.IsRunning ? $"http://127.0.0.1:{_apiServer.Port}/api/health" : "";
            ApiUrlText.IsVisible = _apiServer.IsRunning;
        }
        catch (Exception ex)
        {
            vm.StatusText = $"Local REST API failed to start: {ex.Message}";
            vm.StatusSeverity = Models.StatusSeverity.Error;
        }
    }

    // Every automated ingest path (clipboard, watched folder, REST API) funnels through here: load
    // the content into the paste editor, then — if "Auto-generate PDF from AI-chat ingests" is on —
    // also export a PDF, so a conversation sent here at its end produces a finished document hands-free.
    private void IngestFromSource(string text, string origin, Models.OutputOverride? output = null)
    {
        // A font detected via the "Copy as Markdown" button (clipboard) becomes the live brand
        // font immediately, not just a one-shot export override — so the preview matches the
        // source page's font right away, same as any other Style-panel change.
        if (!string.IsNullOrWhiteSpace(output?.SourceFontFamily))
            ViewModel.BrandFontFamily = output.SourceFontFamily;

        ViewModel.IngestMarkdown(text, origin);
        if (!ViewModel.AutoConvertIngests) return;
        if (AppServices.License.CanAutomate) _ = AutoExportIngestAsync(output);
        else
        {
            ViewModel.StatusText = "Hands-free auto-convert is a Marksmith Pro feature. The content is ready — export it manually, or upgrade in Settings.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
        }
    }

    // Which file formats an automated export should produce.
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

    // The export uses the caller's output profile (theme, layout, formatting, folder) merged over
    // the app's settings, and produces whichever format(s) the caller asked for. Unlike the WinUI
    // build, this doesn't do an offscreen-show/hide dance for tray mode — NativeWebView's behavior
    // while the window is hidden hasn't been characterized on this build yet.
    private async Task AutoExportIngestAsync(Models.OutputOverride? output)
    {
        await _convertLock.WaitAsync();
        try
        {
            var md = ViewModel.PastedMarkdown;
            if (string.IsNullOrWhiteSpace(md)) return;

            var settings = AppServices.Settings.Current.CloneWith(output);
            Directory.CreateDirectory(settings.OutputFolder);
            var label = (ViewModel.LastClassification?.SourceName ?? "chat").Replace(" ", "");
            var stem = Path.Combine(settings.OutputFolder, $"{label}_{DateTime.Now:yyyyMMdd_HHmmss}");

            var produced = new List<string>();
            var formats = ParseFormats(output?.Format);

            List<byte[]?>? mermaidImgs = null;
            if (formats.Contains("docx") && md.Contains("```mermaid", StringComparison.Ordinal))
                mermaidImgs = await new MermaidHarvestService().RenderMermaidPngsAsync(this, md, settings, AppServices.Themes.GetOrDefault(settings.Theme));

            foreach (var fmt in formats)
            {
                var outPath = $"{stem}.{fmt}";
                try
                {
                    switch (fmt)
                    {
                        case "pdf":
                            var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                            var html = AppServices.MarkdownHtml.Render(md, settings, theme, ViewModel.LastClassification);
                            await new PdfExportService().ExportAsync(this, html, outPath, settings);
                            break;
                        case "docx":
                            if (settings.AppendToRunningDoc && !string.IsNullOrWhiteSpace(settings.RunningDocPath))
                            {
                                await new DocxExportService().ExportAppendAsync(md, settings.RunningDocPath, settings, mermaidImgs);
                                outPath = settings.RunningDocPath;
                            }
                            else await new DocxExportService().ExportAsync(md, outPath, settings, mermaidImgs,
                                settings.NormalizeLlm ? ViewModel.LastClassification?.AppliedFixes : null);
                            break;
                        case "pptx": await new PptxExportService().ExportAsync(md, outPath, settings); break;
                        case "epub": await new EpubExportService().ExportAsync(md, outPath, settings); break;
                    }
                    produced.Add(outPath);
                    ViewModel.RecordExport(fmt.ToUpperInvariant(), outPath, md);
                }
                catch (Exception ex)
                {
                    ViewModel.StatusText = $"Auto-export ({fmt.ToUpperInvariant()}) failed: {ex.Message}";
                    ViewModel.StatusSeverity = Models.StatusSeverity.Error;
                    return;
                }
            }

            if (produced.Count > 0)
            {
                ViewModel.LastOutputPath = produced[^1];
                ViewModel.StatusText = $"Auto-converted: {string.Join(", ", produced)}";
                ViewModel.StatusSeverity = Models.StatusSeverity.Success;
            }
        }
        finally { _convertLock.Release(); }
    }

    // A watched file always ingests into the UI; in auto-convert mode it also goes straight to PDF
    // in the output folder — the fully hands-off pipeline.
    private async Task OnWatchedFileAsync(string path)
    {
        ViewModel.IngestFile(path);
        if (!ViewModel.WatchFolderAutoConvert) return;
        if (!AppServices.License.CanAutomate)
        {
            ViewModel.StatusText = "Hands-free watch-folder conversion is a Marksmith Pro feature. Upgrade in Settings.";
            ViewModel.StatusSeverity = Models.StatusSeverity.Warning;
            return;
        }

        await _convertLock.WaitAsync();
        try
        {
            var html = ViewModel.BuildPreviewHtml(ViewModel.PastedMarkdown); // IngestFile already normalized it
            var folder = AppServices.Settings.Current.OutputFolder;
            Directory.CreateDirectory(folder);
            var outPath = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + ".pdf");
            await new PdfExportService().ExportAsync(this, html, outPath, AppServices.Settings.Current);

            ViewModel.LastOutputPath = outPath;
            ViewModel.RecordExport("PDF", outPath, ViewModel.PastedMarkdown);
            ViewModel.StatusText = $"Auto-converted: {outPath}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Success;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Watch-folder auto-convert failed: {ex.Message}";
            ViewModel.StatusSeverity = Models.StatusSeverity.Error;
        }
        finally { _convertLock.Release(); }
    }

    // Serves /api/convert: classify/normalize per the caller's output profile, render, and print to
    // a temp PDF using the live preview host (NativeWebView can't render off-tree, same constraint
    // WebView2 has), then return the bytes and clean up.
    //
    // ApiServer.HandleAsync runs this on a raw ThreadPool thread (Task.Run off the HttpListener
    // loop) — but PreviewWebView is a WebView2-backed control and every one of its members is
    // apartment-affine (COM STA), so calling straight into it here throws "This method can only be
    // called from the thread that created the object (0x802A000C)" — confirmed via a real
    // /api/convert call, which returned exactly that as a 500. The WinUI build (MdToPdf/MainWindow
    // .xaml.cs's ConvertForApiAsync) already gets this right by wrapping the whole body in
    // DispatcherQueue.TryEnqueue + a TaskCompletionSource; mirrored here with Avalonia's Dispatcher.
    private Task<byte[]> ConvertForApiAsync(string markdown, Models.OutputOverride? output)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        global::Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await _convertLock.WaitAsync();
            try
            {
                var settings = AppServices.Settings.Current.CloneWith(output);
                var md = markdown;
                Models.LlmClassification? classification = null;
                if (settings.NormalizeLlm)
                {
                    classification = AppServices.LlmSource.Classify(md);
                    (md, _) = AppServices.LlmSource.Normalize(md, classification);
                }
                var theme = AppServices.Themes.GetOrDefault(settings.Theme);
                var html = AppServices.MarkdownHtml.Render(md, settings, theme, classification);
                var tmp = Path.Combine(Path.GetTempPath(), $"mdpdfm_api_{Guid.NewGuid():N}.pdf");
                await new PdfExportService().ExportAsync(this, html, tmp, settings);
                var bytes = await File.ReadAllBytesAsync(tmp);
                File.Delete(tmp);
                tcs.SetResult(bytes);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally { _convertLock.Release(); }
        });
        return tcs.Task;
    }

    // ---- IWebRenderHost: drives PDF export + mermaid harvesting via NativeWebView instead of
    // WebView2, mirroring MdToPdf/MainWindow.xaml.cs on the WinUI side. ----
    //
    // Avalonia.Controls.WebView's native print-settings API (WebViewPrintSettings) has no
    // PageWidth/PageHeight at all — confirmed by reading its own source
    // (github.com/AvaloniaUI/Avalonia.Controls.WebView): even though the underlying WebView2 COM
    // interface it wraps on Windows (ICoreWebView2PrintSettings) has put_PageWidth/put_PageHeight,
    // the adapter never calls them. So page sizing has to come from somewhere else —
    // MdToPdf.Core/Services/PdfExportService.cs injects an `@page` CSS rule before printing instead.
    //
    // RETRACTED CLAIM, READ BEFORE TRUSTING ANYTHING ABOUT PAGE SIZE ON THIS HOST: a single earlier
    // /api/convert test measured a MediaBox that exactly matched the configured @page size
    // (600x324.96pt for an 800px-wide request) and got written up here as "confirmed working."
    // Several follow-up tests — fresh app instances, different content-width values, both the
    // zero-args PrintToPdfStreamAsync() and an explicit Orientation=Portrait settings object —
    // all instead returned Letter landscape (792x612pt), ignoring @page entirely. The original
    // measurement was too precise to have been a coincidental default, so *something* made it work
    // that one time, but it hasn't reproduced since and the actual cause (timing? some now-changed
    // WebView2 profile state? content-dependent?) is unknown. Bottom line: page size via this host
    // is NOT reliable — don't re-claim it works without watching it happen, not just reading one
    // past PDF. `print-color-adjust: exact` (also in that CSS block) is a separate, independently
    // well-supported mechanism and is not in question here — only the `size` part of `@page` is.
    //
    // Margins are the one part of PdfPageSetup that genuinely can't be passed natively from here:
    // Avalonia's WebViewPrintSettings documents MarginTop/Bottom/Left/Right as pixels, and its GTK
    // backend correctly converts pixels→points — but its Windows/WebView2 backend passes the raw int
    // straight into put_MarginTop(double), which WebView2 interprets as INCHES. A real pixel value
    // (e.g. 38, meaning ~0.4in) would set a 38-INCH margin on Windows — wildly wrong, not merely
    // imprecise. This looks like a genuine unit-handling bug in the third-party package itself
    // (inconsistent between its own backends), not something fixable from here — so
    // `PrintToPdfStreamAsync()` below is deliberately called with no settings at all (every backend
    // defaults to zero margins in that case, "to match GTK and Apple" per the package's own source
    // comment) rather than risk passing a value that means something different depending on OS. The
    // `@page` CSS block already sets margins too, the same way it sets page size — so this isn't a
    // loss, just why the native `settings` parameter below stays unused.

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
        // `setup` (page width/height/margins) is intentionally unused — see the comment above this
        // interface implementation. UNRESOLVED: repeat testing (several /api/convert calls against
        // fresh app instances, both with and without an explicit Orientation=Portrait
        // WebViewPrintSettings) consistently returned Letter landscape (792x612pt) regardless of
        // the @page rule's requested size — contradicting an earlier single test that measured
        // 600x324.96pt matching the configured size exactly. That earlier result was real (too
        // precise to be a coincidental default), but whatever made it work isn't reproducing now
        // and the cause hasn't been identified. Treat page-size control via this host as unreliable
        // until someone can actually watch it happen rather than infer it from one past reading.
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

    // NativeWebView wraps a genuine OS-level window (WebView2 on Windows), which paints in the
    // Win32 z-order rather than Avalonia's own compositor — so it always renders in front of
    // Avalonia-drawn overlays like FAContentDialog regardless of visual-tree z-index ("airspace"
    // problem, a known limitation of hosted native controls in every UI framework that has them).
    // Hiding it for the dialog's lifetime is the standard workaround: IsVisible=false unmaps the
    // underlying native window, not just skips Avalonia-side painting, so the dialog is no longer
    // obscured. The preview reappearing with stale content when the dialog closes is expected and
    // harmless — nothing navigated away, it was just hidden.
    private async Task<FAContentDialogResult> ShowDialogAsync(FAContentDialog dialog)
    {
        PreviewWebView.IsVisible = false;
        try { return await dialog.ShowAsync(this); }
        finally { PreviewWebView.IsVisible = true; }
    }

    public async Task<int> AskOversizedDiagramModeAsync()
    {
        var dialog = new FAContentDialog
        {
            Title = "Large diagram",
            Content = "This document has a large diagram that won't fit a printed page. Keep Mermaid's " +
                      "exact layout (opens in Word's Web Layout view) or reflow it to fit standard pages?",
            PrimaryButtonText = "Keep exact layout",
            SecondaryButtonText = "Reflow to fit page",
            DefaultButton = FAContentDialogButton.Primary,
        };
        var result = await ShowDialogAsync(dialog);
        return result == FAContentDialogResult.Primary ? 1 : 2;
    }

    // ---- Preview ----

    private async Task RefreshPreviewAsync()
    {
        if (_harvestActive) return;
        var vm = ViewModel;
        string markdown;
        if (vm.UsePasteSource) markdown = vm.PastedMarkdown;
        else if (!string.IsNullOrWhiteSpace(vm.InputFilePath) && File.Exists(vm.InputFilePath))
            markdown = await File.ReadAllTextAsync(vm.InputFilePath);
        else markdown = "# Marksmith\n\nDrop a Markdown file on **1 · Source**, or switch to **Paste** and start typing.";

        var html = vm.BuildPreviewHtml(vm.PrepareMarkdown(markdown), interactive: true);
        await NavigateToStringAsync(html);
    }

    private static readonly HashSet<string> AutomationProperties = new()
    {
        nameof(MainViewModel.AutoClipboardIngest),
        nameof(MainViewModel.WatchFolderEnabled),
        nameof(MainViewModel.WatchFolder),
        nameof(MainViewModel.ApiEnabled),
        nameof(MainViewModel.ApiPort),
    };

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PastedMarkdown) or nameof(MainViewModel.InputFilePath)
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
            LicenseBanner.Severity = FAInfoBarSeverity.Informational;
            LicenseBanner.Title = $"Pro trial — {daysLeft} day{(daysLeft == 1 ? "" : "s")} left";
            LicenseBanner.Message = "Keep DOCX export, editable equations, automation, and footer-free exports.";
        }
        else
        {
            LicenseBanner.Severity = FAInfoBarSeverity.Warning;
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
        var file = e.DataTransfer.Items
            .Select(i => i.TryGetFile())
            .Select(f => f?.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && Path.GetExtension(p) is ".md" or ".markdown" or ".txt");
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
            FileTypeFilter = new[] { new FilePickerFileType("Markdown") { Patterns = new[] { "*.md", "*.markdown" } } },
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
        var dialog = new FAContentDialog
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
            DefaultButton = FAContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(dialog) == FAContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
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
        var dialog = new FAContentDialog
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
}
