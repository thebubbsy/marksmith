using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MarkSmith.Converters
{
    /// <summary>Accent brush when selected, neutral surface otherwise (structure canvas nodes).</summary>
    public class SelectedBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Selected =
            new(Color.FromArgb(255, 0, 120, 212));

        private static readonly SolidColorBrush Normal =
            new(Color.FromArgb(255, 38, 48, 60));

        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Selected : Normal;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
