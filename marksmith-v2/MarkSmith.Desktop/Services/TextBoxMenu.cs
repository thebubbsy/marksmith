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
        // Tabbing into a box selects its text, so Shift+F10 shows the text menu. That menu carries a
        // "More options" entry, or keyboard users could only reach the options by pressing End first.
        if (textMenu is CommandBarFlyout bar)
            bar.Opening += (_, _) => AddMoreOptions(bar, box, optionsMenu);
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

    private const string MoreOptionsTag = "TextBoxMenu.MoreOptions";

    private static void AddMoreOptions(CommandBarFlyout bar, TextBox box, MenuFlyout optionsMenu)
    {
        // The text menu rebuilds its commands as it opens. If one menu ever serves several boxes,
        // only the box it is opening on adds the entry.
        if (!ReferenceEquals(bar.Target, box)) return;
        foreach (var command in bar.SecondaryCommands)
            if (command is FrameworkElement { Tag: MoreOptionsTag }) return;
        var more = new AppBarButton { Label = "More options", Icon = new SymbolIcon(Symbol.More), Tag = MoreOptionsTag };
        ToolTipService.SetToolTip(more, "This box's own options (the menu it shows with nothing selected)");
        more.Click += (_, _) =>
        {
            bar.Hide();
            // After the text menu has closed, so the two never share the screen.
            box.DispatcherQueue.TryEnqueue(() => optionsMenu.ShowAt(box));
        };
        bar.SecondaryCommands.Add(more);
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
