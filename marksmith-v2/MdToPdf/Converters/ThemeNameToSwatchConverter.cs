using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MdToPdf.Converters;

// Turns a theme *name* (the ComboBox items are plain strings) into a small accent swatch brush so
// the theme dropdown can show a tiny color preview next to each entry. The swatch uses the theme's
// Heading color — its most distinctive accent — falling back to a neutral grey if the name is not a
// known theme or its color can't be parsed.
public sealed class ThemeNameToSwatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string name && !string.IsNullOrWhiteSpace(name))
        {
            var theme = AppServices.Themes.GetOrDefault(name);
            if (TryParseHex(theme.Heading, out var color))
                return new SolidColorBrush(color);
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    private static bool TryParseHex(string? hex, out Windows.UI.Color color)
    {
        color = Microsoft.UI.Colors.Gray;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6) return false;
        try
        {
            color = Windows.UI.Color.FromArgb(
                255,
                System.Convert.ToByte(s.Substring(0, 2), 16),
                System.Convert.ToByte(s.Substring(2, 2), 16),
                System.Convert.ToByte(s.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }
}
