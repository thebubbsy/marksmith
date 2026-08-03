using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters;

/// <summary>
/// Two-way converter between the hex color strings stored on the diagram ViewModels
/// ("#RRGGBB" / "#AARRGGBB") and the <see cref="Color"/> value used by the built-in WinUI
/// ColorPicker. Falls back to transparent when the source is null or malformed so the picker
/// can never throw on an unexpected value.
/// </summary>
public sealed class HexStringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s ? HexParser.Parse(s) : Microsoft.UI.Colors.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Color c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#000000";

}

/// <summary>
/// One-way converter from a hex color string to a <see cref="SolidColorBrush"/>, used to paint
/// the inspector's color swatch buttons from the selected node's Fill/Stroke string properties.
/// </summary>
public sealed class HexStringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(value is string s ? HexParser.Parse(s) : Microsoft.UI.Colors.Transparent);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>Shared, exception-safe hex-string → <see cref="Color"/> parser.</summary>
internal static class HexParser
{
    public static Color Parse(string s)
    {
        try
        {
            string hex = s.Trim().TrimStart('#');
            return hex.Length switch
            {
                6 => Color.FromArgb(255,
                        System.Convert.ToByte(hex[0..2], 16),
                        System.Convert.ToByte(hex[2..4], 16),
                        System.Convert.ToByte(hex[4..6], 16)),
                8 => Color.FromArgb(
                        System.Convert.ToByte(hex[0..2], 16),
                        System.Convert.ToByte(hex[2..4], 16),
                        System.Convert.ToByte(hex[4..6], 16),
                        System.Convert.ToByte(hex[6..8], 16)),
                _ => Microsoft.UI.Colors.Transparent
            };
        }
        catch
        {
            return Microsoft.UI.Colors.Transparent;
        }
    }
}
