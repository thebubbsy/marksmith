using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters;

/// <summary>Accent brush when selected, neutral surface otherwise (structure canvas nodes, history
/// hub rows). Pass ConverterParameter="themed" (used by the History window) to resolve theme-aware
/// brushes at runtime ("subtle": transparent when unselected); the dark Figma-style studios keep the
/// fixed palette.</summary>
public class SelectedBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected =
        new(Color.FromArgb(255, 0, 120, 212));

    private static readonly SolidColorBrush Transparent = new(Microsoft.UI.Colors.Transparent);

    private static readonly SolidColorBrush Normal =
        new(Color.FromArgb(255, 38, 48, 60));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSelected = value is true;
        if (parameter is string s && string.Equals(s, "themed", StringComparison.OrdinalIgnoreCase))
            return isSelected ? ThemedSelection() : Themed("CardBackgroundFillColorSecondaryBrush");
        // "subtle": unselected rows have no fill at all (outline editors, where a filled box per
        // row reads as a wall of buttons).
        if (parameter is string t && string.Equals(t, "subtle", StringComparison.OrdinalIgnoreCase))
            return isSelected ? ThemedSelection() : Transparent;
        return isSelected ? Selected : Normal;
    }

    // SystemAccentColor is a Color resource, not a Brush — looking it up through Themed() always
    // missed and every "selected" History row fell back to plain grey. A translucent accent wash
    // reads as selected in both themes while keeping the row's primary text legible.
    private static Brush ThemedSelection()
    {
        if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color accent)
            return new SolidColorBrush(accent) { Opacity = 0.24 };
        return Selected;
    }

    private static Brush Themed(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush)
            return brush;
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
