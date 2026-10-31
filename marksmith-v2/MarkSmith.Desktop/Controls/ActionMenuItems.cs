using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Controls;

/// <summary>
/// The "run this row's button" items on <see cref="SettingsCard"/> and <see cref="OptionRow"/>
/// right-click menus. The menus used to call the button's Command only, so a button wired with
/// Click (Settings ▸ Company template's "Import .dotx…") showed a menu item that did nothing, and a
/// row whose buttons sat in a panel (Google account, the licence buttons, a plugin's Install /
/// Remove) offered none of them.
/// </summary>
internal static class ActionMenuItems
{
    /// <summary>One menu item per visible, enabled <see cref="Button"/> in <paramref name="action"/>:
    /// the action itself, or the buttons inside it when it is a panel (a cluster of buttons).</summary>
    public static IEnumerable<MenuFlyoutItem> For(object? action)
    {
        foreach (var button in Buttons(action))
        {
            var item = new MenuFlyoutItem
            {
                Text = Label(button),
                Icon = new FontIcon { Glyph = Glyph(button) ?? "" },
            };
            item.Click += (_, _) => Invoke(button);
            yield return item;
        }
    }

    internal static IEnumerable<Button> Buttons(object? action)
    {
        if (action is Button { Visibility: Visibility.Visible, IsEnabled: true } button)
        {
            yield return button;
        }
        else if (action is Panel { Visibility: Visibility.Visible } panel)
        {
            foreach (var child in panel.Children)
                foreach (var inner in Buttons(child))
                    yield return inner;
        }
    }

    // The accessible name first ("Import company template", "Sign in with Google"), then the text
    // the button shows.
    internal static string Label(Button button)
    {
        var name = AutomationProperties.GetName(button);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        if (button.Content is string s && !string.IsNullOrWhiteSpace(s)) return s;
        if (FirstOf<TextBlock>(button.Content as DependencyObject) is { Text: { Length: > 0 } text }) return text;
        return "Run action";
    }

    private static string? Glyph(Button button) =>
        FirstOf<FontIcon>(button.Content as DependencyObject)?.Glyph is { Length: > 0 } glyph ? glyph : null;

    // Through the automation peer, as a click would: it raises Click and runs Command, whichever the
    // button uses. The button may have been disabled or hidden while the menu was open.
    private static void Invoke(Button button)
    {
        if (!button.IsEnabled || button.Visibility != Visibility.Visible) return;
        (FrameworkElementAutomationPeer.CreatePeerForElement(button) as IInvokeProvider)?.Invoke();
    }

    private static T? FirstOf<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match) return match;
        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                if (FirstOf<T>(child) is { } found) return found;
            return null;
        }
        if (root is Border border)
        {
            return FirstOf<T>(border.Child);
        }
        // Before the button is loaded its content has no visual children yet, so walk the logical
        // shape above; after that, the visual tree covers templated content too.
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FirstOf<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
}
