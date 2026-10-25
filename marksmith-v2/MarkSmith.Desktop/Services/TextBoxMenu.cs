using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.CompilerServices;

namespace MarkSmith.Services;

/// <summary>
/// Gives a TextBox its own options menu without losing the stock Cut / Copy / Paste menu.
/// Shift+F10 / the Menu key reach a TextBox as a context-menu request that opens its ContextFlyout
/// whatever the key handlers do, and a RightTapped handler is mouse only, so the box's ContextFlyout
/// is swapped instead: the options menu while nothing is selected, the stock menu when text is.
/// Right-click and the keyboard share that one path, and the menu is rebuilt each time it opens.
/// </summary>
public static class TextBoxMenu
{
    // Boxes already wired. A box in a DataTemplate is attached from Loaded, which fires again when
    // its container is recycled; a second Attach would capture the options menu as the "text" menu.
    private static readonly ConditionalWeakTable<TextBox, object> Attached = new();

    public static void Attach(TextBox box, Action<MenuFlyout> fill)
    {
        if (box is null || fill is null) return;
        if (Attached.TryGetValue(box, out _)) return;
        Attached.Add(box, fill);
        // The box's own menu, captured before the swap. A TextBox built in code may not have one
        // yet, so fall back to a fresh text menu rather than showing nothing over a selection.
        var textMenu = box.ContextFlyout ?? new TextCommandBarFlyout();
        var optionsMenu = new MenuFlyout();
        optionsMenu.Opening += (_, _) =>
        {
            optionsMenu.Items.Clear();
            fill(optionsMenu);
        };
        void Sync() => box.ContextFlyout = string.IsNullOrEmpty(box.SelectedText) ? optionsMenu : textMenu;
        box.SelectionChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>
    /// The same menu for a control whose text lives in a TextBox inside its template (NumberBox,
    /// AutoSuggestBox). That TextBox only exists once the template is applied, so the menu goes
    /// on it now if it's there and otherwise when the control loads.
    /// </summary>
    public static void AttachInner(Control host, Action<MenuFlyout> fill)
    {
        if (host is null || fill is null) return;
        void TryAttach()
        {
            host.ApplyTemplate();
            if (FindTextBox(host) is { } inner) Attach(inner, fill);
        }
        host.Loaded += (_, _) => TryAttach();
        TryAttach();
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox box) return box;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }
}
