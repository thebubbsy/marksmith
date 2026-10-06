using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace MarkSmith.Services;

/// <summary>
/// Gives every button-like control a consistent "lift" on hover/press — a small scale animation
/// layered on top of WinUI's default PointerOver/Pressed background — instead of each view
/// re-templating buttons individually. Call <see cref="Apply"/> once from a view's Loaded handler;
/// it walks the visual tree under that root and wires up any <see cref="ButtonBase"/> it finds
/// (Button, ToggleButton, RepeatButton, HyperlinkButton, …), skipping ones already wired so it is
/// safe to call again if the same instance is reloaded into the tree.
/// </summary>
public static class HoverPolish
{
    private const double HoverScale = 1.035;
    private const double PressScale = 0.97;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(140);

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "HoverPolishAttached", typeof(bool), typeof(HoverPolish), new PropertyMetadata(false));

    /// <summary>
    /// Set <c>services:HoverPolish.ApplyOnLoad="True"</c> on the root element of a
    /// <c>DataTemplate</c> whose buttons should get the lift. Template content is realized per item
    /// long after a view's constructor-time <see cref="Apply"/> has run, so those buttons would
    /// otherwise silently miss the polish.
    /// </summary>
    public static readonly DependencyProperty ApplyOnLoadProperty = DependencyProperty.RegisterAttached(
        "ApplyOnLoad", typeof(bool), typeof(HoverPolish), new PropertyMetadata(false, OnApplyOnLoadChanged));

    public static bool GetApplyOnLoad(DependencyObject element) => (bool)element.GetValue(ApplyOnLoadProperty);

    public static void SetApplyOnLoad(DependencyObject element, bool value) => element.SetValue(ApplyOnLoadProperty, value);

    private static void OnApplyOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += (sender, _) => Apply((DependencyObject)sender);
        }
    }

    /// <summary>Opt a control out by setting <c>Tag="NoHoverPolish"</c> in XAML.</summary>
    private const string OptOutTag = "NoHoverPolish";

    /// <summary>
    /// Shows a <see cref="ContentDialog"/> with its Primary/Secondary/Close buttons (and any custom
    /// buttons in its Content) getting the same hover lift as the rest of the app. The dialog's
    /// button row only joins the visual tree once the dialog opens, so this hooks <c>Opened</c>
    /// rather than applying immediately. Use in place of <c>dialog.ShowAsync()</c>.
    /// </summary>
    public static IAsyncOperation<ContentDialogResult> ShowPolishedAsync(this ContentDialog dialog)
    {
        dialog.Opened += (sender, _) => Apply((DependencyObject)sender);
        return dialog.ShowAsync();
    }

    /// <summary>
    /// Wires a <see cref="Flyout"/> (not <see cref="MenuFlyout"/> — its items intentionally keep
    /// their stock list-style highlight rather than a scale lift) so any real buttons inside its
    /// content get polished the first time it opens. Call once, e.g. from a view's constructor.
    /// </summary>
    public static void AttachOnOpen(Flyout flyout)
    {
        flyout.Opened += (_, _) =>
        {
            if (flyout.Content is DependencyObject content)
            {
                Apply(content);
            }
        };
    }

    public static void Apply(DependencyObject root)
    {
        if (root is ButtonBase button)
        {
            AttachTo(button);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            Apply(VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void AttachTo(ButtonBase button)
    {
        if (Equals(button.Tag, OptOutTag)) return;
        if ((bool)button.GetValue(AttachedProperty)) return;
        button.SetValue(AttachedProperty, true);

        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        button.RenderTransform = scale;

        var isHovering = false;

        button.PointerEntered += (_, _) =>
        {
            isHovering = true;
            Animate(scale, HoverScale);
        };
        button.PointerExited += (_, _) =>
        {
            isHovering = false;
            Animate(scale, 1.0);
        };
        button.PointerCanceled += (_, _) =>
        {
            isHovering = false;
            Animate(scale, 1.0);
        };
        // ButtonBase marks PointerPressed/PointerReleased as handled for its own click logic, so a
        // plain += subscription never fires — listen with handledEventsToo instead.
        button.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => Animate(scale, PressScale)), true);
        button.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, _) => Animate(scale, isHovering ? HoverScale : 1.0)), true);
    }

    private static void Animate(ScaleTransform scale, double to)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var animationX = new DoubleAnimation { To = to, Duration = AnimationDuration, EasingFunction = easing };
        var animationY = new DoubleAnimation { To = to, Duration = AnimationDuration, EasingFunction = easing };
        Storyboard.SetTarget(animationX, scale);
        Storyboard.SetTargetProperty(animationX, nameof(ScaleTransform.ScaleX));
        Storyboard.SetTarget(animationY, scale);
        Storyboard.SetTargetProperty(animationY, nameof(ScaleTransform.ScaleY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animationX);
        storyboard.Children.Add(animationY);
        storyboard.Begin();
    }
}
