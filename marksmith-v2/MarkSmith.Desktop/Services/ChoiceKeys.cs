using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

/// <summary>
/// A studio's Delete / Backspace / Ctrl+A shortcuts act on the canvas selection, but they also
/// reached the window while focus sat in a gallery, palette, combo box, slider or pill group.
/// Backspace on a layout in SmartArt's gallery deleted the selected outline item, and Delete on a
/// preset in Shape Studio deleted the selected shapes behind it. Window-level handlers return first
/// when focus is in one of those choice controls.
/// </summary>
public static class ChoiceKeys
{
    /// <summary>True when keyboard focus is inside a list, combo box, slider, toggle switch or an
    /// arrow-navigated pill group. The walk stops (false) at <paramref name="selectionHost"/>: a list
    /// that lists the canvas items themselves (Shape Studio's Shapes list) keeps Delete.</summary>
    public static bool FocusIsInChoiceControl(XamlRoot? xamlRoot, DependencyObject? selectionHost = null)
    {
        if (xamlRoot is null) return false;
        if (FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused) return false;
        for (var cur = focused; cur is not null; cur = cur is UIElement ? VisualTreeHelper.GetParent(cur) : null)
        {
            if (selectionHost is not null && ReferenceEquals(cur, selectionHost)) return false;
            if (cur is ListViewBase or SelectorItem or ComboBox or Slider or ToggleSwitch) return true;
            if (cur is UIElement { XYFocusKeyboardNavigation: XYFocusKeyboardNavigationMode.Enabled }) return true;
        }
        return false;
    }
}
