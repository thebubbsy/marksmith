using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace MarkSmith.Services;

/// <summary>
/// Opens a menu wired to <c>RightTapped</c> from the keyboard too. RightTapped is raised only by
/// mouse, pen and touch, so the status-bar pills' "Right-click for options" menus never opened
/// from Shift+F10 or the Menu key. The bridge calls the same handler, so both paths show the
/// same menu anchored to the same element.
/// </summary>
public static class KeyboardContextMenu
{
    public static void Bridge(UIElement element, RightTappedEventHandler handler)
    {
        if (element is null || handler is null) return;
        Bridged.AddOrUpdate(element, Marker);
        // handledEventsToo: HyperlinkButton marks some keys handled before KeyDown bubbles out.
        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!IsContextMenuKey(e.Key)) return;
            e.Handled = true;
            IsKeyboardInvocation = true;
            KeyboardSource = element as FrameworkElement;
            try { handler(element, new RightTappedRoutedEventArgs()); }
            finally { IsKeyboardInvocation = false; KeyboardSource = null; }
        }), handledEventsToo: true);
    }

    /// <summary>Bridges each control to its RightTapped handler (see <see cref="Bridge"/>).</summary>
    public static void BridgeAll(params (UIElement Element, RightTappedEventHandler Handler)[] pairs)
    {
        foreach (var (element, handler) in pairs) Bridge(element, handler);
    }

    private static readonly ConditionalWeakTable<UIElement, object> Bridged = new();
    private static readonly object Marker = new();

    /// <summary>
    /// True when a key event comes from a bridged control (or something inside one). Surface-wide
    /// Shift+F10 handlers (the tour's, Suite Hub's, the palette's) check this first so they don't
    /// open a second menu over the control's own.
    /// </summary>
    public static bool IsFromBridgedControl(KeyRoutedEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && Bridged.TryGetValue(ui, out _)) return true;
        }
        return false;
    }

    /// <summary>True while a bridged handler runs from the keyboard, when the synthetic event
    /// args carry no pointer position.</summary>
    public static bool IsKeyboardInvocation { get; private set; }

    private static FrameworkElement? KeyboardSource;

    /// <summary>
    /// Where a context menu should open, in <paramref name="relativeTo"/>'s coordinates: the
    /// pointer for a right-click, or just under the focused control's left edge for a keyboard
    /// request (synthetic args have no pointer, so <c>GetPosition</c> would put the menu in the
    /// top-left corner of whatever it is relative to — a whole card or bar, not the control).
    /// </summary>
    public static Point PointIn(RightTappedRoutedEventArgs e, UIElement? relativeTo)
    {
        if (!IsKeyboardInvocation) return e.GetPosition(relativeTo);
        var source = KeyboardSource;
        if (source is null || relativeTo is null) return new Point(0, 0);
        try
        {
            return source.TransformToVisual(relativeTo).TransformPoint(new Point(0, source.ActualHeight));
        }
        catch (ArgumentException)
        {
            // Not in the same visual tree (e.g. relative to a popup): anchor at the origin.
            return new Point(0, 0);
        }
    }

    /// <summary>Shows <paramref name="menu"/> at the pointer for a right-click, or anchored to the
    /// element for a keyboard request (where there is no pointer position to use).</summary>
    public static void ShowAt(MenuFlyout menu, FrameworkElement anchor, RightTappedRoutedEventArgs e)
    {
        if (IsKeyboardInvocation) menu.ShowAt(anchor);
        else menu.ShowAt(anchor, e.GetPosition(anchor));
    }

    /// <summary>The Menu (Application) key, or Shift+F10 without Ctrl/Alt.</summary>
    public static bool IsContextMenuKey(VirtualKey key)
    {
        if (key == VirtualKey.Application) return true;
        if (key != VirtualKey.F10) return false;
        return IsDown(VirtualKey.Shift) && !IsDown(VirtualKey.Control) && !IsDown(VirtualKey.Menu);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
