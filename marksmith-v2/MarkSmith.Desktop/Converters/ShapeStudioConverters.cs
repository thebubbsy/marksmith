using System;
using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters
{
    /// <summary>#RRGGBB hex string → SolidColorBrush. Brushes are cached by hex — the lines
    /// list binds one fill per row, and re-allocating a brush per conversion was pure churn
    /// (the palette repeats constantly across a traced image).</summary>
    public class FillBrushConverter : IValueConverter
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SolidColorBrush> Cache = new();
        private static readonly SolidColorBrush Fallback = new(Color.FromArgb(255, 0, 120, 212));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string hex = (value as string ?? "0078D4").Trim().TrimStart('#');
            if (hex.Length != 6) return Fallback;
            return Cache.GetOrAdd(hex, static h =>
            {
                try
                {
                    byte r = byte.Parse(h.Substring(0, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(h.Substring(2, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(h.Substring(4, 2), NumberStyles.HexNumber);
                    return new SolidColorBrush(Color.FromArgb(255, r, g, b));
                }
                catch
                {
                    return Fallback;
                }
            });
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>true → dashed stroke collection; false → null (solid).</summary>
    public class DashConverter : IValueConverter
    {
        private static readonly DoubleCollection Dashed = new() { 6, 4 };

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Dashed : null;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>null → Visible (shows a hint), non-null → Collapsed.</summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }

    /// <summary>Empty/null string → Collapsed (hides the shape label), anything else → Visible.</summary>
    public class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => string.IsNullOrWhiteSpace(value as string)
                ? Microsoft.UI.Xaml.Visibility.Collapsed
                : Microsoft.UI.Xaml.Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
