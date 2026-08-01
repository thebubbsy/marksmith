using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace MdToPdf.Converters;

// Bridges MainViewModel.StatusSeverity (MdToPdf.Models.StatusSeverity, portable — defined in
// MdToPdf.Core so the ViewModel carries no WinUI dependency) to the InfoBar control's own
// severity enum for the status bar binding in MainWindow.xaml.
public sealed class StatusSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is Models.StatusSeverity s ? (InfoBarSeverity)(int)s : InfoBarSeverity.Informational;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
