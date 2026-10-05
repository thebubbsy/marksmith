using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
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
internal static class HoverPolish
{
    private const double HoverScale = 1.035;
    private const double PressScale = 0.97;
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(140);

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "HoverPolishAttached", typeof(bool), typeof(HoverPolish), new PropertyMetadata(false));

    /// <summary>Opt a control out by setting <c>Tag="NoHoverPolish"</c> in XAML.</summary>
    private const string OptOutTag = "NoHoverPolish";

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
        button.PointerPressed += (_, _) => Animate(scale, PressScale);
        button.PointerReleased += (_, _) => Animate(scale, isHovering ? HoverScale : 1.0);
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
