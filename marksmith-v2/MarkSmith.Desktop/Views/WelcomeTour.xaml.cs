using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using MarkSmith.Services;

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
    private Storyboard? _transition;

    public WelcomeTour()
    {
        InitializeComponent();
        // Page4 (3 · Preview & Export) runs before Page3 (Diagrams & math) so the numbered
        // pipeline reads 1, 2, 3 in order and the unnumbered extras follow it.
        _pages = new[] { Page0, Page1, Page2, Page4, Page3, Page5, Page6 };
        foreach (var page in _pages) page.RenderTransform = new TranslateTransform();
        Show(0);
        HoverPolish.Track(this);
        // Handled-too: the Back/Next buttons and the PipsPager mark arrow keys handled when they
        // have focus, but Left/Right should page the tour from anywhere inside it.
        AddHandler(KeyDownEvent, new KeyEventHandler(OnTourKeyDown), true);
        Loaded += (_, _) => FitHostToTallestPage();
    }

    // Every page shares one height so the pips and buttons never jump while paging — but that
    // height is the tallest page's, not a guessed constant (a fixed 340 left a big empty band
    // under the shorter pages). Collapsed pages measure as zero, so each is measured visible.
    private void FitHostToTallestPage()
    {
        var width = PagesHost.ActualWidth;
        if (width <= 0) return;
        double tallest = 0;
        foreach (var page in _pages)
        {
            var was = page.Visibility;
            page.Visibility = Visibility.Visible;
            page.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, page.DesiredSize.Height);
            page.Visibility = was;
        }
        PagesHost.Height = Math.Ceiling(tallest);
    }

    private int Last => _pages.Length - 1;

    private void Show(int index)
    {
        var previous = _index;
        _index = Math.Clamp(index, 0, Last);
        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == _index ? Visibility.Visible : Visibility.Collapsed;
        if (Pips.SelectedPageIndex != _index) Pips.SelectedPageIndex = _index;
        BackButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _index >= Last ? "Get started" : "Next";
        SkipButton.Visibility = _index >= Last ? Visibility.Collapsed : Visibility.Visible;
        if (_index != previous) SlideIn(_pages[_index], forward: _index > previous);
    }

    // The incoming page fades in while drifting 24px from the side it came from, so paging reads
    // as moving through a sequence instead of the content snapping. Skipped when Windows'
    // "Animation effects" is off.
    private void SlideIn(StackPanel page, bool forward)
    {
        _transition?.Stop();
        if (!HoverPolish.AnimationsEnabled || page.RenderTransform is not TranslateTransform shift)
        {
            page.Opacity = 1;
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        var slide = new DoubleAnimation { From = forward ? 24 : -24, To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, page);
        Storyboard.SetTargetProperty(fade, nameof(Opacity));
        Storyboard.SetTarget(slide, shift);
        Storyboard.SetTargetProperty(slide, nameof(TranslateTransform.X));
        _transition = new Storyboard();
        _transition.Children.Add(fade);
        _transition.Children.Add(slide);
        _transition.Begin();
    }

    private void OnTourKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Leave arrows alone while the PipsPager has focus — it already moves the selection and
        // reports it through OnPipSelected, so handling them here too would skip a page.
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, Pips)) return;
        if (e.Key == Windows.System.VirtualKey.Right && _index < Last) { Show(_index + 1); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Left && _index > 0) { Show(_index - 1); e.Handled = true; }
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (var d = element; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private void OnPipSelected(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        if (Pips.SelectedPageIndex >= 0 && Pips.SelectedPageIndex != _index) Show(Pips.SelectedPageIndex);
    }

    private void OnStepCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var page)) Show(page);
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
