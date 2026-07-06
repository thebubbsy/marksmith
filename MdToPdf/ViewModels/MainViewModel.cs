using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MdToPdf.Models;
using MdToPdf.Services;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService = App.Settings;
    private readonly RecentFilesService _recentFilesService = App.RecentFiles;
    private readonly MarkdownHtmlService _markdownHtml = App.MarkdownHtml;
    private readonly ThemeCatalog _themes = App.Themes;
    private readonly PdfExportService _pdfExport = new();
    private readonly DocxExportService _docxExport = new();

    private CancellationTokenSource? _conversionCts;

    [ObservableProperty] private string _inputFilePath = string.Empty;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _selectedThemeName;
    [ObservableProperty] private int _contentWidth;
    [ObservableProperty] private bool _a4FixedWidth;
    [ObservableProperty] private bool _unlimitedHeight;
    [ObservableProperty] private bool _usePasteSource;
    [ObservableProperty] private bool _normalizeLlm;
    [ObservableProperty] private bool _autoClipboardIngest;
    [ObservableProperty] private bool _watchFolderEnabled;
    [ObservableProperty] private string _watchFolder;
    [ObservableProperty] private bool _watchFolderAutoConvert;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoConvertIngests;
    [ObservableProperty] private bool _appendToRunningDoc;
    [ObservableProperty] private string _runningDocPath = "";
    [ObservableProperty] private bool _showExtensionTip;
    [ObservableProperty] private bool _includeToc;
    [ObservableProperty] private int _mermaidDocxMode = 1; // 0 Snapshot picture, 1 ShapeForge shapes
    [ObservableProperty] private bool _brandCoverPage;
    [ObservableProperty] private string _brandLogoPath = "";
    [ObservableProperty] private string _brandFontFamily = "";
    [ObservableProperty] private bool _showAttribution;
    [ObservableProperty] private bool _noEmoji;
    [ObservableProperty] private int _dashMode;
    [ObservableProperty] private string _dashCustom;
    [ObservableProperty] private int _headingShift;
    [ObservableProperty] private int _boldMode;
    [ObservableProperty] private int _italicMode;
    [ObservableProperty] private bool _advancedMode;
    [ObservableProperty] private bool _apiEnabled;
    [ObservableProperty] private int _apiPort;
    [ObservableProperty] private string _detectedSourceText = string.Empty;

    // Classification of the last ingested document; feeds the preview badge and export attribution strip.
    public LlmClassification? LastClassification { get; private set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WordCountText))]
    private string _pastedMarkdown = string.Empty;

    public string WordCountText
    {
        get
        {
            var words = PastedMarkdown.Split(new[] { ' ', '\n', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
            return $"{words} words · {PastedMarkdown.Length} chars";
        }
    }
    [ObservableProperty] private string _statusText = "Ready.";
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    private string? _lastOutputPath;

    public bool IsNotBusy => !IsBusy;
    public bool HasOutput => !string.IsNullOrEmpty(LastOutputPath);

    // Licensing (drives the paywall UI). Backed by App.License; kept in sync via its Changed event.
    public bool IsPro => App.License.IsPro;
    public bool IsFree => !App.License.IsPro;
    public string EditionStatus => App.License.State.Status ?? "Free";
    public Microsoft.UI.Xaml.Visibility ProBadgeVisibility =>
        IsFree ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    private void OnLicenseChanged()
    {
        OnPropertyChanged(nameof(IsPro));
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(EditionStatus));
        OnPropertyChanged(nameof(ProBadgeVisibility));
    }

    public ObservableCollection<string> ThemeNames { get; }
    public ObservableCollection<string> RecentFiles { get; } = new();
    public ObservableCollection<HistoryEntry> History { get; } = new();

    public void RecordExport(string kind, string outputPath, string markdown)
    {
        var entry = new HistoryEntry
        {
            Timestamp = DateTime.Now,
            SourceLabel = UsePasteSource ? "pasted" : Path.GetFileName(InputFilePath),
            Detected = LastClassification?.SourceName ?? "Markdown",
            Theme = SelectedThemeName,
            OutputPath = outputPath,
            Kind = kind,
            DocumentTitle = HistoryEntry.ExtractTitle(markdown),
        };
        History.Insert(0, entry);
        App.History.Add(entry);
    }

    public MainViewModel()
    {
        var settings = _settingsService.Current;
        _outputFolder = settings.OutputFolder;
        _selectedThemeName = settings.Theme;
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
        foreach (var h in App.History.All) History.Add(h);
        _includeToc = settings.IncludeToc;
        _mermaidDocxMode = settings.MermaidDocxMode;
        _brandCoverPage = settings.BrandCoverPage;
        _brandLogoPath = settings.BrandLogoPath;
        _brandFontFamily = settings.BrandFontFamily;
        _showAttribution = settings.ShowAttribution;
        _noEmoji = settings.NoEmoji;
        _dashMode = settings.DashMode;
        _dashCustom = settings.DashCustom;
        _headingShift = settings.HeadingShift;
        _boldMode = settings.BoldMode;
        _italicMode = settings.ItalicMode;
        _advancedMode = settings.AdvancedMode;
        _apiEnabled = settings.ApiEnabled;
        _apiPort = settings.ApiPort;

        ThemeNames = new ObservableCollection<string>(_themes.All.Select(t => t.Name));
        foreach (var f in _recentFilesService.Load()) RecentFiles.Add(f);

        App.License.Changed += OnLicenseChanged;
    }

    partial void OnOutputFolderChanged(string value) { _settingsService.Current.OutputFolder = value; _settingsService.Save(); }
    partial void OnSelectedThemeNameChanged(string value) { _settingsService.Current.Theme = value; _settingsService.Save(); }
    partial void OnContentWidthChanged(int value) { _settingsService.Current.ContentWidth = value; _settingsService.Save(); }
    partial void OnA4FixedWidthChanged(bool value) { _settingsService.Current.A4FixedWidth = value; _settingsService.Save(); }
    partial void OnUnlimitedHeightChanged(bool value) { _settingsService.Current.UnlimitedHeight = value; _settingsService.Save(); }
    partial void OnNormalizeLlmChanged(bool value) { _settingsService.Current.NormalizeLlm = value; _settingsService.Save(); }
    partial void OnAutoClipboardIngestChanged(bool value) { _settingsService.Current.AutoClipboardIngest = value; _settingsService.Save(); }
    partial void OnWatchFolderEnabledChanged(bool value) { _settingsService.Current.WatchFolderEnabled = value; _settingsService.Save(); }
    partial void OnWatchFolderChanged(string value) { _settingsService.Current.WatchFolder = value; _settingsService.Save(); }
    partial void OnWatchFolderAutoConvertChanged(bool value) { _settingsService.Current.WatchFolderAutoConvert = value; _settingsService.Save(); }
    partial void OnMinimizeToTrayChanged(bool value) { _settingsService.Current.MinimizeToTray = value; _settingsService.Save(); }
    partial void OnAutoConvertIngestsChanged(bool value) { _settingsService.Current.AutoConvertIngests = value; _settingsService.Save(); }
    partial void OnAppendToRunningDocChanged(bool value) { _settingsService.Current.AppendToRunningDoc = value; _settingsService.Save(); }
    partial void OnRunningDocPathChanged(string value) { _settingsService.Current.RunningDocPath = value; _settingsService.Save(); }
    partial void OnShowExtensionTipChanged(bool value) { _settingsService.Current.ShowExtensionTip = value; _settingsService.Save(); }
    partial void OnIncludeTocChanged(bool value) { _settingsService.Current.IncludeToc = value; _settingsService.Save(); }
    partial void OnMermaidDocxModeChanged(int value) { _settingsService.Current.MermaidDocxMode = value; _settingsService.Save(); }
    partial void OnBrandCoverPageChanged(bool value) { _settingsService.Current.BrandCoverPage = value; _settingsService.Save(); }
    partial void OnBrandLogoPathChanged(string value) { _settingsService.Current.BrandLogoPath = value; _settingsService.Save(); }
    partial void OnBrandFontFamilyChanged(string value) { _settingsService.Current.BrandFontFamily = value; _settingsService.Save(); }
    partial void OnShowAttributionChanged(bool value) { _settingsService.Current.ShowAttribution = value; _settingsService.Save(); }
    partial void OnNoEmojiChanged(bool value) { _settingsService.Current.NoEmoji = value; _settingsService.Save(); }
    partial void OnDashModeChanged(int value) { _settingsService.Current.DashMode = value; _settingsService.Save(); }
    partial void OnDashCustomChanged(string value) { _settingsService.Current.DashCustom = value; _settingsService.Save(); }
    partial void OnHeadingShiftChanged(int value) { _settingsService.Current.HeadingShift = value; _settingsService.Save(); }
    partial void OnBoldModeChanged(int value) { _settingsService.Current.BoldMode = value; _settingsService.Save(); }
    partial void OnItalicModeChanged(int value) { _settingsService.Current.ItalicMode = value; _settingsService.Save(); }
    partial void OnAdvancedModeChanged(bool value) { _settingsService.Current.AdvancedMode = value; _settingsService.Save(); }
    partial void OnApiEnabledChanged(bool value) { _settingsService.Current.ApiEnabled = value; _settingsService.Save(); }
    partial void OnApiPortChanged(int value) { _settingsService.Current.ApiPort = value; _settingsService.Save(); }

    public ThemeDefinition CurrentTheme => _themes.GetOrDefault(SelectedThemeName);

    public string BuildPreviewHtml(string markdown) =>
        _markdownHtml.Render(markdown, _settingsService.Current, CurrentTheme, LastClassification);

    // Classification + normalization for content that did NOT arrive through IngestMarkdown —
    // manual paste and browsed/dropped files. Without this, the "Normalize AI formatting quirks"
    // toggle only worked for the automated ingest paths. Safe to call on already-ingested text:
    // normalization is idempotent, and the badge only updates when the new classification says
    // something stronger than what's already displayed (re-running on cleaned text scores lower
    // because most signals were just removed).
    public string PrepareMarkdown(string markdown)
    {
        var classification = App.LlmSource.Classify(markdown);
        if (classification.Source == LlmSource.Generic) return markdown;

        if (NormalizeLlm)
            (markdown, _) = App.LlmSource.Normalize(markdown, classification);

        var better = LastClassification is null
            || classification.Source != LastClassification.Source
            || classification.Confidence > LastClassification.Confidence;
        if (better)
        {
            LastClassification = classification;
            DetectedSourceText = NormalizeLlm
                ? $"{classification.SourceName} · {classification.Confidence}% · {classification.AppliedFixes.Count} fixes"
                : $"{classification.SourceName} · {classification.Confidence}%";
        }
        return markdown;
    }

    // Entry point for all automated ingest paths (clipboard watcher, watched folder, REST API).
    // Classifies the text, optionally normalizes assistant-specific quirks, and loads it into
    // the paste editor so the preview updates immediately.
    public void IngestMarkdown(string text, string origin)
    {
        var classification = App.LlmSource.Classify(text);
        if (NormalizeLlm)
        {
            (text, _) = App.LlmSource.Normalize(text, classification);
        }

        LastClassification = classification;
        DetectedSourceText = classification.Source == LlmSource.Generic
            ? $"Ingested from {origin}"
            : $"{classification.SourceName} · {classification.Confidence}% · {classification.AppliedFixes.Count} fixes";

        PastedMarkdown = text;
        UsePasteSource = true;
        StatusText = classification.Source == LlmSource.Generic
            ? $"Ingested Markdown from {origin}."
            : $"Ingested from {origin} — detected {classification.SourceName} formatting" +
              (classification.AppliedFixes.Count > 0 ? $", applied {classification.AppliedFixes.Count} fixes." : ".");
        StatusSeverity = InfoBarSeverity.Success;
    }

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
            StatusSeverity = InfoBarSeverity.Error;
        }
    }

    [RelayCommand]
    private void LoadRecent(string path)
    {
        InputFilePath = path;
        UsePasteSource = false;
    }

    [RelayCommand]
    private void CancelConversion()
    {
        // Best-effort: CoreWebView2.PrintToPdfAsync has no CancellationToken overload, so this
        // resets the UI immediately rather than truly aborting an in-flight WebView2 call — the
        // file may still be written a moment later. Good enough for "let me try something else
        // without waiting"; a hard-abort would need dropping to the CDP-level printing API.
        _conversionCts?.Cancel();
        StatusText = "Cancelled.";
        StatusSeverity = InfoBarSeverity.Warning;
        IsBusy = false;
    }

    public async Task ConvertToPdfAsync(WebView2 webView)
    {
        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("PDF", async ct =>
        {
            var html = BuildPreviewHtml(markdown);
            var outPath = ResolveOutputPath(sourceLabel, "pdf");
            await _pdfExport.ExportAsync(webView, html, outPath, _settingsService.Current);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("PDF", outPath, markdown);
            StatusText = $"PDF export done: {outPath}";
        });
    }

    public async Task ConvertToDocxAsync()
    {
        if (!App.License.CanExportDocx)
        {
            StatusText = "DOCX export is a Marksmith Pro feature — start your free trial or upgrade in Settings ⚙.";
            StatusSeverity = InfoBarSeverity.Warning;
            return;
        }

        var (markdown, sourceLabel) = ResolveSource();
        if (markdown is null) return;

        await RunConversionAsync("DOCX", async ct =>
        {
            var outPath = ResolveOutputPath(sourceLabel, "docx");
            // Rasterize mermaid diagrams first (Snapshot mode, or ShapeForge's fallback) — the
            // renderer needs the window's WebView2, so it lives on MainWindow.
            List<byte[]?>? mermaidImgs = null;
            if (markdown.Contains("```mermaid", StringComparison.Ordinal) && App.MainAppWindow is MainWindow mw)
                mermaidImgs = await mw.RenderMermaidPngsAsync(markdown, _settingsService.Current);
            // Disclose applied AI-cleanup fixes as a Word comment (paste source is already normalized).
            var fixes = NormalizeLlm && UsePasteSource ? LastClassification?.AppliedFixes : null;
            await _docxExport.ExportAsync(markdown, outPath, _settingsService.Current, mermaidImgs, fixes);
            LastOutputPath = outPath;
            if (!UsePasteSource) TrackRecent(InputFilePath);
            RecordExport("DOCX", outPath, markdown);
            StatusText = $"DOCX export done: {outPath}";
        });
    }

    private async Task RunConversionAsync(string kind, Func<CancellationToken, Task> work)
    {
        _conversionCts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = $"Converting to {kind}...";
        StatusSeverity = InfoBarSeverity.Informational;
        try
        {
            await work(_conversionCts.Token);
            StatusSeverity = InfoBarSeverity.Success;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
            StatusSeverity = InfoBarSeverity.Warning;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            StatusSeverity = InfoBarSeverity.Error;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private (string? Markdown, string SourceLabel) ResolveSource()
    {
        if (UsePasteSource)
        {
            if (string.IsNullOrWhiteSpace(PastedMarkdown))
            {
                StatusText = "Paste area is empty.";
                StatusSeverity = InfoBarSeverity.Warning;
                return (null, string.Empty);
            }
            return (PrepareMarkdown(PastedMarkdown), $"pasted_export_{Guid.NewGuid().ToString()[..8]}");
        }

        if (string.IsNullOrWhiteSpace(InputFilePath) || !File.Exists(InputFilePath))
        {
            StatusText = "Please select a valid Markdown file first.";
            StatusSeverity = InfoBarSeverity.Warning;
            return (null, string.Empty);
        }

        return (PrepareMarkdown(File.ReadAllText(InputFilePath)), Path.GetFileNameWithoutExtension(InputFilePath));
    }

    private string ResolveOutputPath(string sourceLabel, string extension)
    {
        var folder = string.IsNullOrWhiteSpace(OutputFolder)
            ? (UsePasteSource ? _settingsService.Current.OutputFolder : Path.GetDirectoryName(InputFilePath)!)
            : OutputFolder;
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{sourceLabel}.{extension}");
    }

    private void TrackRecent(string path)
    {
        var updated = _recentFilesService.AddToRecent(path);
        RecentFiles.Clear();
        foreach (var f in updated) RecentFiles.Add(f);
    }
}
