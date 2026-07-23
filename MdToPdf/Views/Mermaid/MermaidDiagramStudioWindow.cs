using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using MdToPdf.ViewModels.Mermaid;

namespace MdToPdf.Views.Mermaid;

public sealed class MermaidDiagramStudioWindow : Window
{
    private readonly MermaidDiagramStudioControl _studioControl;

    public MermaidStudioViewModel ViewModel => (MermaidStudioViewModel)_studioControl.DataContext!;

    public event EventHandler<string>? SyncToMarkdownRequested
    {
        add => _studioControl.SyncToMarkdownRequested += value;
        remove => _studioControl.SyncToMarkdownRequested -= value;
    }

    public MermaidDiagramStudioWindow(string currentMarkdown, int blockIndex = 0)
    {
        Title = "Mermaid Diagram Studio";

        // Enable WASDK Mica backdrop
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;

        _studioControl = new MermaidDiagramStudioControl();

        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(currentMarkdown, blockIndex);
        _studioControl.DataContext = vm;

        Content = _studioControl;
        
        SetTitleBar(_studioControl.TitleBarElement);
    }
}
