using Microsoft.UI.Xaml.Controls;
using System;

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
    public static void Attach(TextBox box, Action<MenuFlyout> fill)
    {
        if (box is null || fill is null) return;
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
}
