using System;
using Microsoft.UI.Xaml.Data;

namespace MarkSmith.Converters;

public sealed class OversizedDiagramModeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int mode)
        {
            // Mode 1 = Exact / Web Layout (do not fit to page)
            // Mode 4 = Aggressive Shrink (fit to single page width)
            return mode != 1;
        }
        return true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isChecked)
        {
            return isChecked ? 4 : 1;
        }
        return 4;
    }
}
