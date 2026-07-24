using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.Services;

public sealed class WinUiDialogService : IUiPrompts
{
    private readonly Window _window;

    public WinUiDialogService(Window window)
    {
        _window = window;
    }

    public async Task<int> AskOversizedDiagramModeAsync()
    {
        var remember = new CheckBox { Content = "Remember my choice (change later in Settings)", Margin = new Thickness(0, 14, 0, 0) };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "This document has a large diagram that won't fit a printed page. How should MarkSmith put it into Word?"
        });

        var rbGroup = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var rbExact = new RadioButton { Content = "Keep exact layout (Opens in Web Layout view)", IsChecked = true, Tag = 1 };
        var rbReflow = new RadioButton { Content = "Reflow to fit page (Uniform scale)", Tag = 2 };
        var rbCompactSpace = new RadioButton { Content = "Compact spacing (Shrink gaps first)", Tag = 6 };
        var rbCompactShapes = new RadioButton { Content = "Compact shapes (Shrink shapes first)", Tag = 7 };
        var rbUltraCompact = new RadioButton { Content = "Ultra compact (Shrink both equally)", Tag = 8 };

        rbGroup.Children.Add(rbExact);
        rbGroup.Children.Add(rbReflow);
        rbGroup.Children.Add(rbCompactSpace);
        rbGroup.Children.Add(rbCompactShapes);
        rbGroup.Children.Add(rbUltraCompact);

        body.Children.Add(rbGroup);
        body.Children.Add(remember);

        var dialog = new ContentDialog
        {
            XamlRoot = _window.Content.XamlRoot,
            Title = "Large diagram",
            Content = body,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return 1; // default to exact on cancel

        int mode = 1;
        if (rbReflow.IsChecked == true) mode = 2;
        else if (rbCompactSpace.IsChecked == true) mode = 6;
        else if (rbCompactShapes.IsChecked == true) mode = 7;
        else if (rbUltraCompact.IsChecked == true) mode = 8;

        if (remember.IsChecked == true)
        {
            App.Settings.Current.OversizedDiagramMode = mode;
            App.Settings.Save();
            App.ViewModel.OversizedDiagramMode = mode; // keep the Settings UI in sync
        }
        return mode;
    }

    public Task<MdToPdf.Models.RenderOption?> ShowAmbiguityResolverDialogAsync(MdToPdf.Models.AmbiguityCase ambiguity)
    {
        return Task.FromResult<MdToPdf.Models.RenderOption?>(null);
    }
}
