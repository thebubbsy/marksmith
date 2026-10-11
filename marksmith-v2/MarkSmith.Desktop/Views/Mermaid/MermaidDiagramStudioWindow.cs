using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using MarkSmith.ViewModels.Mermaid;

namespace MarkSmith.Views.Mermaid;

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
        Title = "Diagram Studio — MarkSmith";

        // Set official Marksmith taskbar & titlebar icon
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (System.IO.File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }
        SizeForDisplay();

        // Enable WASDK Mica backdrop
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        // The Studio is always dark (RequestedTheme="Dark"), but caption-button glyphs follow the
        // OS theme — on a Light OS they'd be black-on-#1E1E2E and effectively invisible. Pin them.
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 0xED, 0xF2, 0xF4);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 0x8D, 0x99, 0xAE);
        AppWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
        AppWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

        _studioControl = new MermaidDiagramStudioControl();

        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown(currentMarkdown, blockIndex);
        _studioControl.DataContext = vm;

        Content = _studioControl;
        
        SetTitleBar(_studioControl.TitleBarElement);
        if (_studioControl.TitleBarElement is Grid titleBarGrid)
            MarkSmith.Services.TitleBarInsets.Reserve(this, titleBarGrid);

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

    private void SizeForDisplay()
    {
        try
        {
            var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            if (scale <= 0) scale = 1;
            var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
                Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                Math.Min((int)(1380 * scale), work.Width), Math.Min((int)(860 * scale), work.Height)));
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = Math.Min((int)(1080 * scale), work.Width);
                presenter.PreferredMinimumHeight = Math.Min((int)(600 * scale), work.Height);
            }
        }
        catch { /* best effort: the default size still works */ }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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

            if (await MarkSmith.Services.HoverPolish.ShowPolishedAsync(dialog) == ContentDialogResult.Primary)
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
