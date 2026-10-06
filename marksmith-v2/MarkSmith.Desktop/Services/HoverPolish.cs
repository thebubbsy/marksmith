using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    /// <para>
    /// WinUI allows only one open ContentDialog per XamlRoot and throws if a second one is shown
    /// (e.g. pressing F1 or Ctrl+K while Settings is already up). Every dialog in the app goes
    /// through here, so a request that would collide is dropped and reported as
    /// <see cref="ContentDialogResult.None"/> — exactly what callers already get for a dismissed
    /// dialog — instead of crashing the app.
    /// </para>
    /// </summary>
    public static IAsyncOperation<ContentDialogResult> ShowPolishedAsync(this ContentDialog dialog)
    {
        if (IsContentDialogOpen(dialog.XamlRoot))
        {
            return Task.FromResult(ContentDialogResult.None).AsAsyncOperation();
        }

        // Track rather than a one-off Apply: dialog content like Settings realises each Pivot tab
        // only when it's first selected.
        dialog.Opened += (sender, _) => Track((FrameworkElement)sender);
        return dialog.ShowAsync();
    }

    /// <summary>True when a ContentDialog is already showing on <paramref name="xamlRoot"/>.</summary>
    public static bool IsContentDialogOpen(XamlRoot? xamlRoot) =>
        xamlRoot is not null &&
        VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Any(p => p.Child is ContentDialog);

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

    private static readonly DependencyProperty TrackedProperty = DependencyProperty.RegisterAttached(
        "HoverPolishTracked", typeof(bool), typeof(HoverPolish), new PropertyMetadata(false));

    /// <summary>
    /// <see cref="Apply"/> now, then again (throttled) whenever layout changes while
    /// <paramref name="root"/> is loaded. A one-shot Apply at construction only reaches what is
    /// already in the visual tree, so buttons realised later — an unselected Pivot tab, collapsed
    /// expander content, controls inside templates that hadn't been applied yet — silently missed
    /// both the hover lift and their accessible name. Apply is idempotent, so re-walking is safe;
    /// the walk (a few ms) runs at most every 300 ms, and only while layout is changing.
    /// </summary>
    public static void Track(FrameworkElement root)
    {
        Apply(root);
        if ((bool)root.GetValue(TrackedProperty)) return;
        root.SetValue(TrackedProperty, true);

        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = null;
        var subscribed = false;

        void OnLayoutUpdated(object? sender, object e)
        {
            if (timer is null)
            {
                var queue = root.DispatcherQueue;
                if (queue is null) return;
                timer = queue.CreateTimer();
                timer.Interval = TimeSpan.FromMilliseconds(300);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => Apply(root);
            }
            // Capped, not trailing: some views re-layout continuously enough that a
            // "wait for quiet" debounce never fired. A walk costs a few ms and only runs while
            // layout is actually changing (none at idle).
            if (!timer.IsRunning) timer.Start();
        }

        void Subscribe()
        {
            if (subscribed) return;
            root.LayoutUpdated += OnLayoutUpdated;
            subscribed = true;
        }

        // Unhook when the root leaves the tree (window closed, dialog dismissed) so closed views
        // don't keep reacting to every layout pass in the app.
        void Unsubscribe()
        {
            if (!subscribed) return;
            root.LayoutUpdated -= OnLayoutUpdated;
            subscribed = false;
            timer?.Stop();
        }

        Subscribe();
        root.Loaded += (_, _) => Subscribe();
        // WinUI can raise a reparented element's Unloaded *after* its new Loaded (e.g. content
        // moving into a ContentDialog's popup), which would switch tracking off for good. So only
        // unhook once the element has genuinely left the tree.
        root.Unloaded += (_, _) => root.DispatcherQueue?.TryEnqueue(() =>
        {
            if (!root.IsLoaded) Unsubscribe();
        });
    }

    public static void Apply(DependencyObject root)
    {
        if (root is ButtonBase button)
        {
            EnsureAccessibleName(button);
            AttachTo(button);
        }
        else if (root is Expander expander
                 && string.IsNullOrWhiteSpace(AutomationProperties.GetName(expander))
                 && expander.Header is not string)
        {
            // Same gap as icon+label buttons: a composite header leaves the expander unnamed.
            var parts = new List<string>();
            CollectText(expander.Header, parts, 0);
            if (parts.Count > 0) AutomationProperties.SetName(expander, string.Join(" ", parts));
        }
        else if (root is Control control && string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)))
        {
            // Compact, headerless inputs (toolbar ComboBoxes, snap ToggleSwitch, …) describe
            // themselves only through a tooltip; use it as the name. A Header already names them.
            var header = control switch
            {
                ToggleSwitch t => t.Header,
                ComboBox c => c.Header,
                TextBox t => t.Header,
                PasswordBox pw => pw.Header,
                NumberBox n => n.Header,
                Slider sl => sl.Header,
                _ => "skip",
            };
            if (header is null && ToolTipService.GetToolTip(control) is string { Length: > 0 } tip)
            {
                AutomationProperties.SetName(control, tip);
            }
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

        // Bound text (and template content) may not have resolved yet at construction time.
        button.Loaded += (_, _) => EnsureAccessibleName(button);

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

        // A click that opens a flyout/dialog or moves focus steals pointer capture, and the
        // matching PointerExited often never arrives — the button stayed stuck "lifted" behind the
        // popup. Settle back to the hover state the pointer is actually in.
        button.PointerCaptureLost += (_, _) => Animate(scale, isHovering ? HoverScale : 1.0);

        // Commands commonly disable their button mid-hover (e.g. Delete after the last shape
        // goes); disabled controls get no pointer events, so reset rather than freeze at 1.035.
        button.IsEnabledChanged += (_, _) =>
        {
            if (!button.IsEnabled)
            {
                isHovering = false;
                Animate(scale, 1.0);
            }
        };

        // Template-recycled buttons (ListView items) come back with whatever scale they left with.
        button.Unloaded += (_, _) =>
        {
            isHovering = false;
            Animate(scale, 1.0, instant: true);
        };

        // Keyboard users get the same press feedback as the mouse: Space/Enter/gamepad A dip the
        // button while held. ButtonBase handles these keys itself, so listen with handledEventsToo.
        button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (IsActivationKey(e.Key)) Animate(scale, PressScale);
        }), true);
        button.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, e) =>
        {
            if (IsActivationKey(e.Key)) Animate(scale, isHovering ? HoverScale : 1.0);
        }), true);
        button.LostFocus += (_, _) =>
        {
            if (!isHovering) Animate(scale, 1.0);
        };
    }

    private static bool IsActivationKey(Windows.System.VirtualKey key) =>
        key is Windows.System.VirtualKey.Space or Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.GamepadA;

    // Windows Settings › Accessibility › Visual effects › "Animation effects". When it's off the
    // lift is skipped entirely (values snap) — the stock Fluent hover colours still give feedback.
    private static readonly Windows.UI.ViewManagement.UISettings SystemUiSettings = new();

    internal static bool AnimationsEnabled
    {
        get
        {
            try { return SystemUiSettings.AnimationsEnabled; }
            catch { return true; }
        }
    }

    /// <summary>
    /// Buttons whose content is an icon + label StackPanel, or an icon alone, expose no UI
    /// Automation name, so Narrator announced most of the app as just "button". Fill in
    /// AutomationProperties.Name from the visible label text, falling back to the tooltip.
    /// An explicitly set name, or plain string content (which already names itself), wins.
    /// </summary>
    private static void EnsureAccessibleName(ButtonBase button)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button))) return;
        if (button.Content is string { Length: > 0 }) return;

        var parts = new List<string>();
        CollectText(button.Content, parts, 0);
        var name = string.Join(" ", parts).Trim();
        var tooltip = ToolTipService.GetToolTip(button) switch
        {
            string tip => tip,
            ToolTip { Content: string tip } => tip,
            _ => "",
        };

        // Glyph-like labels ("B", "I", "•", "A+", "H1", "Img") make poor spoken names; the tooltip
        // ("Bold", "Heading 1", …) says what the button does.
        if (name.Count(char.IsLetter) < 4 && tooltip.Length > 0)
        {
            name = tooltip;
        }

        if (name.Length > 0)
        {
            AutomationProperties.SetName(button, name);
        }
    }

    private static void CollectText(object? node, List<string> parts, int depth)
    {
        if (node is null || depth > 6) return;
        switch (node)
        {
            case string text when !string.IsNullOrWhiteSpace(text):
                parts.Add(text);
                break;
            case TextBlock { Visibility: Visibility.Visible } textBlock when !string.IsNullOrWhiteSpace(textBlock.Text):
                parts.Add(textBlock.Text);
                break;
            case Panel panel:
                foreach (var child in panel.Children) CollectText(child, parts, depth + 1);
                break;
            case Border border:
                CollectText(border.Child, parts, depth + 1);
                break;
            case Viewbox viewbox:
                CollectText(viewbox.Child, parts, depth + 1);
                break;
            case ContentControl contentControl:
                CollectText(contentControl.Content, parts, depth + 1);
                break;
        }
    }

    private static void Animate(ScaleTransform scale, double to, bool instant = false)
    {
        // Reduced motion: no scale changes at all (a press dip would still be motion). Values go
        // through a zero-length storyboard rather than a local set, because a finished storyboard
        // holds its end value over any local value.
        if (!AnimationsEnabled)
        {
            to = 1.0;
            instant = true;
        }

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = instant ? TimeSpan.Zero : AnimationDuration;

        var animationX = new DoubleAnimation { To = to, Duration = duration, EasingFunction = easing };
        var animationY = new DoubleAnimation { To = to, Duration = duration, EasingFunction = easing };
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
