using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        // force: true clears every node's HasCustomPosition flag first - without it the
        // layout engine skips any node that carries saved metadata positions (which is
        // every node after a load), making the button appear dead.
        ViewModel?.SnapshotForUndo();
        ViewModel?.ApplyAutoLayout(force: true);
    }

    private void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.DeleteSelected();
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.Undo();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        ViewModel?.Redo();
    }

    private void OnUndoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel?.Undo();
        args.Handled = true;
    }

    private void OnRedoAcceleratorInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel?.Redo();
        args.Handled = true;
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
