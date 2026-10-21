using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
        // handledEventsToo: HyperlinkButton marks some keys handled before KeyDown bubbles out.
        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (!IsContextMenuKey(e.Key)) return;
            e.Handled = true;
            IsKeyboardInvocation = true;
            try { handler(element, new RightTappedRoutedEventArgs()); }
            finally { IsKeyboardInvocation = false; }
        }), handledEventsToo: true);
    }

    /// <summary>True while a bridged handler runs from the keyboard, when the synthetic event
    /// args carry no pointer position.</summary>
    public static bool IsKeyboardInvocation { get; private set; }

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
