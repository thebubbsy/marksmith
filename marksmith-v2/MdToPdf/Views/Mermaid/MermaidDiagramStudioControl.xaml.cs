using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        // The Studio window assigns DataContext AFTER LoadFromMarkdown runs, so this fires once
        // the restored palette is known — keeps the preset buttons' active highlight accurate.
        DataContextChanged += (s, e) => HighlightActivePalette();
    }

    private void OnPalettePresetClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { Tag: string name }) return;
        ViewModel.ActivePalette = name;
        HighlightActivePalette();
    }

    // Highlights the active preset button (also reflects state restored from a loaded
    // %%{init}%% directive, not just in-session clicks).
    private void HighlightActivePalette()
    {
        var active = ViewModel?.ActivePalette ?? string.Empty;
        var accent = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x4C, 0xC9, 0xF0));
        var rest = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8D, 0x99, 0xAE));
        foreach (var btn in new[] { PresetCatppuccin, PresetNord, PresetEmerald, PresetMono })
        {
            bool isActive = btn.Tag is string tag && tag == active;
            btn.BorderBrush = isActive ? accent : rest;
            btn.BorderThickness = isActive ? new Thickness(1.5) : new Thickness(1);
        }
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
