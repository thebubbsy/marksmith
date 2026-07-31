using Avalonia.Data.Converters;
using System;
using System.Globalization;
using MdToPdf.Avalonia.Controls;

namespace MdToPdf.Avalonia.Converters
{
    public class StatusSeverityConverter : IValueConverter
    {
        public static readonly StatusSeverityConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                if (status.StartsWith("Success"))
                    return InfoBarSeverity.Success;
                if (status.StartsWith("Error"))
                    return InfoBarSeverity.Error;
            }
            return InfoBarSeverity.Informational;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
