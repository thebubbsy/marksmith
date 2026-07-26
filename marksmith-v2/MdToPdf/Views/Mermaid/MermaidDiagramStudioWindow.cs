using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using MdToPdf.ViewModels.Mermaid;

namespace MdToPdf.Views.Mermaid;

public sealed class MermaidDiagramStudioWindow : Window
{
    private readonly MermaidDiagramStudioControl _studioControl;

    // Close-confirmation state: set once the user opts to discard edits so the re-raised Closing
    // event passes through; _confirmingClose guards against stacking dialogs if the X is clicked
    // again while the prompt is already open.
    private bool _allowClose;
    private bool _confirmingClose;

    public MermaidStudioViewModel ViewModel => (MermaidStudioViewModel)_studioControl.DataContext!;

    public event EventHandler<string>? SyncToMarkdownRequested
    {
        add => _studioControl.SyncToMarkdownRequested += value;
        remove => _studioControl.SyncToMarkdownRequested -= value;
    }

    public MermaidDiagramStudioWindow(string currentMarkdown, int blockIndex = 0)
    {
        Title = "Mermaid Diagram Studio";

        // Set official Marksmith taskbar & titlebar icon
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

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

        // Guard against silently throwing away edits: if the diagram has unsynced changes, ask
        // before closing instead of just discarding them.
        AppWindow.Closing += (sender, e) =>
        {
            if (_allowClose) return;
            if (!ViewModel.HasUnsavedChanges) return; // clean canvas — close freely

            e.Cancel = true;
            if (_confirmingClose) return; // prompt already on screen
            _confirmingClose = true;
            _ = ConfirmDiscardAndCloseAsync();
        };
    }

    private async Task ConfirmDiscardAndCloseAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Unsaved diagram changes",
                Content = "This diagram has edits that haven't been synced back to the document. Close anyway and discard them?",
                PrimaryButtonText = "Discard & close",
                SecondaryButtonText = "Keep editing",
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = _studioControl.XamlRoot,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _allowClose = true;
                Close();
            }
        }
        finally
        {
            _confirmingClose = false;
        }
    }
}
