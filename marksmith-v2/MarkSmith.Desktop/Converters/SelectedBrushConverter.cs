using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters;

/// <summary>Accent brush when selected, neutral surface otherwise (structure canvas nodes, history
/// hub rows). Pass ConverterParameter="themed" (used by the History window) to resolve theme-aware
/// brushes at runtime; the dark Figma-style studios keep the fixed palette.</summary>
public class SelectedBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected =
        new(Color.FromArgb(255, 0, 120, 212));

    private static readonly SolidColorBrush Normal =
        new(Color.FromArgb(255, 38, 48, 60));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isSelected = value is true;
        if (parameter is string s && string.Equals(s, "themed", StringComparison.OrdinalIgnoreCase))
            return isSelected ? Themed("SystemAccentColor") : Themed("CardBackgroundFillColorSecondaryBrush");
        return isSelected ? Selected : Normal;
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
