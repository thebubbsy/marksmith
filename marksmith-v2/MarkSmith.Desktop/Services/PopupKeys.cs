using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

/// <summary>
/// An open menu, flyout or dialog routes its keys through the ancestors of the element it opened
/// from, so a window-level key handler also sees Down, Delete, S or Ctrl+Z pressed inside a menu.
/// Those keys belong to the popup (Down moves through the menu, Esc closes it), so window-level
/// handlers return first when keyboard focus is outside their own root.
/// </summary>
public static class PopupKeys
{
    public static bool FocusIsOutside(UIElement? root)
    {
        if (root?.XamlRoot is not { } xamlRoot) return false;
        if (FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused) return false;
        for (DependencyObject? cur = focused; cur is not null; cur = VisualTreeHelper.GetParent(cur))
            if (ReferenceEquals(cur, root)) return false;
        return true;
    }
}
