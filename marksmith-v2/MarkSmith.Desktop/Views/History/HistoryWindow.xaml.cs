using System;
using System.Threading.Tasks;
using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace MarkSmith.Views.History;

public sealed partial class HistoryWindow : Window
{
    private readonly HistoryWindowViewModel _vm;
    private readonly Microsoft.UI.Xaml.Controls.WebView2 _preview;
    private bool _webViewReady;

    public HistoryWindow(MainViewModel? mainViewModel = null, string? initialFilePath = null)
    {
        InitializeComponent();
        Title = "Document Time Machine — Version History";

        _vm = new HistoryWindowViewModel(
            html => mainViewModel != null ? mainViewModel.BuildPreviewHtml(html) : AppServices.MarkdownHtml.Render(html, AppServices.Settings.Current, AppServices.Themes.GetOrDefault(AppServices.Settings.Current.Theme)),
            id => mainViewModel != null ? mainViewModel.RestoreVersionAsync(id) : Task.FromResult(false),
            initialFilePath: initialFilePath);
        RootGrid.DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _preview = PreviewWeb;
        _ = InitializeWebViewAsync();
        _ = _vm.LoadCommand.ExecuteAsync(null);

        RootGrid.KeyDown += OnRootKeyDown;
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            await _preview.EnsureCoreWebView2Async();
            _webViewReady = true;
            if (!string.IsNullOrEmpty(_vm.PreviewHtml))
                _preview.NavigateToString(_vm.PreviewHtml);
        }
        catch { /* best-effort WebView2 initialization */ }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryWindowViewModel.PreviewHtml) && _webViewReady)
        {
            try
            {
                if (!string.IsNullOrEmpty(_vm.PreviewHtml))
                    _preview.NavigateToString(_vm.PreviewHtml);
            }
            catch { /* best effort */ }
        }
    }

    private void OnFileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSummaryViewModel file })
            _ = _vm.SelectFileCommand.ExecuteAsync(file);
    }

    private void OnVersionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _vm.SelectVersionCommand.Execute(item);
    }

    private void OnStarTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _ = _vm.ToggleStarCommand.ExecuteAsync(item);
    }

    private void OnUnifiedToggleClick(object sender, RoutedEventArgs e)
    {
        UnifiedToggle.IsChecked = true;
        SplitToggle.IsChecked = false;
        PreviewToggle.IsChecked = false;
        _vm.SetDiffMode(HistoryDiffMode.Unified);
    }

    private void OnSplitToggleClick(object sender, RoutedEventArgs e)
    {
        UnifiedToggle.IsChecked = false;
        SplitToggle.IsChecked = true;
        PreviewToggle.IsChecked = false;
        _vm.SetDiffMode(HistoryDiffMode.Split);
    }

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
    {
        UnifiedToggle.IsChecked = false;
        SplitToggle.IsChecked = false;
        PreviewToggle.IsChecked = true;
        _vm.SetDiffMode(HistoryDiffMode.Preview);
    }

    private async void OnTakeSnapshotClick(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "e.g. Cleaned up Mermaid diagram", Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Take Manual Checkpoint",
            Content = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Save a named milestone snapshot to the time machine:" },
                    input
                }
            },
            PrimaryButtonText = "Save Checkpoint",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var label = string.IsNullOrWhiteSpace(input.Text) ? "Manual Checkpoint" : input.Text.Trim();
            await _vm.TakeSnapshotCommand.ExecuteAsync(label);
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (isCtrl && e.Key == VirtualKey.F)
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
