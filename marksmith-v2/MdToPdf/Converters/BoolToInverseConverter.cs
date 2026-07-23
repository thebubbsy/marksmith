using Microsoft.UI.Xaml.Data;
using System;

namespace MdToPdf.Converters
{
    public sealed class BoolToInverseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}
