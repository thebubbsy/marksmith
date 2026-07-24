using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MdToPdf.ViewModels.Mermaid;

namespace MdToPdf.Views.Mermaid;

public sealed partial class MermaidDiagramStudioControl : UserControl
{
    public MermaidStudioViewModel? ViewModel => DataContext as MermaidStudioViewModel;

    public UIElement TitleBarElement => AppTitleBar;

    public event EventHandler<string>? SyncToMarkdownRequested;

    public MermaidDiagramStudioControl()
    {
        InitializeComponent();

        PaletteContainer.Child = new NodePaletteControl();
        CanvasContainer.Child = new MermaidCanvasControl();
    }

    private void OnAutoLayoutClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.ApplyAutoLayout(force: true);
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeleteSelected();
    }

    private void OnSyncToMarkdownClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel != null)
        {
            string code = ViewModel.GenerateMermaidCode();
            SyncToMarkdownRequested?.Invoke(this, code);
        }
    }
}
