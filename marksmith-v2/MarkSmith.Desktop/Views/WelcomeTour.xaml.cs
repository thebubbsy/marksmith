using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkSmith.Views;

// The first-run guided tour: 7 visibility-switched pages (NOT a FlipView — see the note in the
// XAML: FlipView inside a detached UserControl fails Application.LoadComponent on WASDK 1.6)
// walking through Source, Style, diagrams/math, Export, and automation/Pro. Shown once
// automatically and replayable from the title bar. Raises Completed when the user finishes or
// skips; the hosting ContentDialog closes on that.
public sealed partial class WelcomeTour : UserControl
{
    public event EventHandler? Completed;

    // What the user opted into on the final page. Only honored when they finish with
    // "Get started" — Skip means "leave me alone", so it stays false on that path.
    public bool LoadSampleRequested { get; private set; }

    private StackPanel[] _pages = Array.Empty<StackPanel>();
    private int _index;

    public WelcomeTour()
    {
        InitializeComponent();
        _pages = new[] { Page0, Page1, Page2, Page3, Page4, Page5, Page6 };
        Show(0);
    }

    private int Last => _pages.Length - 1;

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, Last);
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
        if (Pips.SelectedPageIndex != _index) Pips.SelectedPageIndex = _index;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _index >= Last ? "Get started" : "Next";
        SkipButton.Visibility = _index >= Last ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPipSelected(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (Pips.SelectedPageIndex >= 0 && Pips.SelectedPageIndex != _index) Show(Pips.SelectedPageIndex);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Show(_index - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_index < Last)
        {
            Show(_index + 1);
        }
        else
        {
            LoadSampleRequested = LoadSampleCheck.IsChecked == true;
            Completed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Completed?.Invoke(this, EventArgs.Empty);
}
