using MarkSmith.ViewModels.History;
using MarkSmith.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Views.History;

public sealed partial class HistoryWindow : Window
{
    private readonly HistoryWindowViewModel _vm;
    private readonly Microsoft.UI.Xaml.Controls.WebView2 _preview;

    public HistoryWindow(string filePath, MainViewModel mainViewModel)
    {
        InitializeComponent();
        Title = "Version History — " + System.IO.Path.GetFileName(filePath);

        _vm = new HistoryWindowViewModel(
            filePath,
            html => mainViewModel.BuildPreviewHtml(html),
            id => mainViewModel.RestoreVersionAsync(id));
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

    private void OnVersionTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VersionItemViewModel item })
            _vm.SelectVersionCommand.Execute(item);
    }
}
