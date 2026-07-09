using System.Globalization;
using Avalonia.Data.Converters;
using FluentAvalonia.UI.Controls;

namespace MdToPdf.Avalonia.Converters;

// Bridges MainViewModel.StatusSeverity (MdToPdf.Models.StatusSeverity, portable) to FluentAvalonia's
// InfoBar severity enum, mirroring the WinUI build's StatusSeverityConverter.
public sealed class StatusSeverityConverter : IValueConverter
{
    public static readonly StatusSeverityConverter Instance = new();

    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        value is MdToPdf.Models.StatusSeverity s ? (FAInfoBarSeverity)(int)s : FAInfoBarSeverity.Informational;

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture) =>
        throw new System.NotSupportedException();
}
