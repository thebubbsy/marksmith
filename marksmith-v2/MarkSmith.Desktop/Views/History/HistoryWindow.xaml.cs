using MarkSmith.ViewModels;
using MarkSmith.ViewModels.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Views.History;

public sealed partial class HistoryWindow : Window
{
    private readonly HistoryWindowViewModel _vm;
    private readonly Microsoft.UI.Xaml.Controls.WebView2 _preview;

    public HistoryWindow(MainViewModel mainViewModel, string? initialFilePath = null)
    {
        InitializeComponent();
        Title = "Version History";

        _vm = new HistoryWindowViewModel(
            html => mainViewModel.BuildPreviewHtml(html),
            id => mainViewModel.RestoreVersionAsync(id),
            initialFilePath: initialFilePath);
        RootGrid.DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;

        _preview = PreviewWeb;
        _ = _vm.LoadCommand.ExecuteAsync(null);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryWindowViewModel.PreviewHtml))
            _preview.NavigateToString(_vm.PreviewHtml);
    }

    private void OnFileTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSummaryViewModel file })
            _vm.SelectFileCommand.Execute(file);
    }

    private void OnVersionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _vm.SelectVersionCommand.Execute(item);
    }

    private void OnDiffToggleClick(object sender, RoutedEventArgs e)
    {
        DiffToggle.IsChecked = true;
        PreviewToggle.IsChecked = false;
        _vm.ShowDiffViewCommand.Execute(null);
    }

    private void OnPreviewToggleClick(object sender, RoutedEventArgs e)
    {
        DiffToggle.IsChecked = false;
        PreviewToggle.IsChecked = true;
        _vm.ShowPreviewViewCommand.Execute(null);
    }
}
