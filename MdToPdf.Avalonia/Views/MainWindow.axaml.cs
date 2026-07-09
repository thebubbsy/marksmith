using System.Linq;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Media;
using global::Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using MdToPdf.Services;
using MdToPdf.ViewModels;

namespace MdToPdf.Avalonia.Views;

public partial class MainWindow : Window, IWebRenderHost, IUiPrompts
{
    private MainViewModel ViewModel => (MainViewModel)DataContext!;

    private static readonly Uri PreviewBaseUri = new("https://marksmith.local/");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            await LoadMarkdownFilesAsync();
            ViewModel.LoadPresets();
            UpdateLicenseBanner();
            await RefreshPreviewAsync();
        };
    }

    // ---- IWebRenderHost: drives PDF export + mermaid harvesting via NativeWebView instead of
    // WebView2, mirroring MdToPdf/MainWindow.xaml.cs on the WinUI side. ----
    //
    // KNOWN GAP: Avalonia.Controls.WebView 12.0.1's WebViewPrintSettings exposes only Orientation
    // and integer margins — no page width/height and no "print backgrounds" flag. That means the
    // WinUI build's exact A4-lock / single-continuous-page / forced-background print geometry
    // (PdfPageSetup) cannot be reproduced here; PDFs print at the engine's default page size. Not
    // silently glossed over — flagged for the user in the shipping notes.

    public Task<bool> EnsureReadyAsync() => Task.FromResult(true);

    public Task NavigateToStringAsync(string html)
    {
        PreviewWebView.NavigateToString(html, PreviewBaseUri);
        return Task.CompletedTask;
    }

    public async Task<string?> ExecuteScriptAsync(string javaScript) =>
        await PreviewWebView.InvokeScript(javaScript);

    public async Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
    {
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
        var result = await dialog.ShowAsync(this);
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.PastedMarkdown) or nameof(MainViewModel.InputFilePath)
            or nameof(MainViewModel.UsePasteSource) or nameof(MainViewModel.SelectedThemeName)
            or nameof(MainViewModel.ContentWidth) or nameof(MainViewModel.A4FixedWidth)
            or nameof(MainViewModel.UnlimitedHeight) or nameof(MainViewModel.IncludeToc)
            or nameof(MainViewModel.ShowAttribution) or nameof(MainViewModel.NoEmoji))
        {
            _ = RefreshPreviewAsync();
        }
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
        if (await dialog.ShowAsync(this) == FAContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
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
        await dialog.ShowAsync(this);

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
