using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Services;

/// <summary>
/// A <see cref="SelectorBar"/> lays its items out in an inner <see cref="ItemsView"/>
/// (PART_ItemsView) that has no accessible name, so a screen reader moving into the editor-view
/// or studio tab bars heard an unnamed "list" inside the named bar. Names it after the bar once
/// the template is applied.
/// </summary>
public static class SelectorBarNames
{
    public static void NameInnerList(SelectorBar bar)
    {
        bar.Loaded -= OnLoaded;
        bar.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SelectorBar bar) return;
        var name = AutomationProperties.GetName(bar);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (FindItemsView(bar) is { } list && string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)))
            AutomationProperties.SetName(list, name);
    }

    private static ItemsView? FindItemsView(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ItemsView view) return view;
            if (FindItemsView(child) is { } found) return found;
        }
        return null;
    }
}
