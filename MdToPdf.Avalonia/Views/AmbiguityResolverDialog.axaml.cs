using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MdToPdf.Models;
using System.Linq;
using System.Threading.Tasks;

namespace MdToPdf.Avalonia.Views;

public partial class AmbiguityResolverDialog : Window
{
    private AmbiguityCase? _caseData;

    public AmbiguityResolverDialog()
    {
        InitializeComponent();
    }

    private AmbiguityResolverDialog(AmbiguityCase ambiguity) : this()
    {
        _caseData = ambiguity;
        
        var descriptionText = this.FindControl<TextBlock>("DescriptionText");
        if (descriptionText != null)
        {
            descriptionText.Text = ambiguity.Description;
        }

        var optionsList = this.FindControl<ItemsControl>("OptionsList");
        if (optionsList != null)
        {
            optionsList.ItemsSource = ambiguity.Options;
        }
    }

    public static Task<RenderOption?> ShowAsync(Visual parent, AmbiguityCase ambiguity)
    {
        var dialog = new AmbiguityResolverDialog(ambiguity);
        
        if (TopLevel.GetTopLevel(parent) is Window window)
        {
            return dialog.ShowDialog<RenderOption?>(window);
        }
        
        return Task.FromResult<RenderOption?>(null);
    }

    private void OnOptionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RenderOption option)
        {
            Close(option);
        }
    }

    private void OnDefaultClick(object? sender, RoutedEventArgs e)
    {
        var defaultOption = _caseData?.Options.OrderBy(o => o.Priority).FirstOrDefault();
        Close(defaultOption);
    }
}
