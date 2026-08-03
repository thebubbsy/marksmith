using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MarkSmith.Converters;

// Maps MainViewModel.StatusSeverity (portable enum, defined in MarkSmith.Core) to a themed
// foreground brush for the persistent status bar in MainWindow.xaml. Success reads green,
// warning amber, error red, and plain information stays on the default secondary text color so
// the bar doesn't shout when nothing noteworthy happened.
public sealed class StatusSeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Models.StatusSeverity s ? s switch
        {
            Models.StatusSeverity.Success => "SystemFillColorSuccessBrush",
            Models.StatusSeverity.Warning => "SystemFillColorWarningBrush",
            Models.StatusSeverity.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        } : "TextFillColorSecondaryBrush";

        return Application.Current.Resources.TryGetValue(key, out var brush) && brush is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
