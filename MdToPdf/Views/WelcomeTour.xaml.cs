using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdToPdf.Views;

// The first-run guided tour: a 7-page FlipView walking through Source, Style, diagrams/math,
// Export, and automation/Pro. Shown once automatically and replayable from the title bar. Raises
// Completed when the user finishes or skips; the hosting ContentDialog closes on that.
public sealed partial class WelcomeTour : UserControl
{
    public event EventHandler? Completed;

    public WelcomeTour()
    {
        InitializeComponent();
        UpdateButtons();
    }

    private int Last => Pages.Items.Count - 1;

    private void OnPageChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var i = Pages.SelectedIndex;
        BackButton.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = i >= Last ? "Get started" : "Next";
        SkipButton.Visibility = i >= Last ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex > 0) Pages.SelectedIndex--;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (Pages.SelectedIndex < Last) Pages.SelectedIndex++;
        else Completed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Completed?.Invoke(this, EventArgs.Empty);
}
