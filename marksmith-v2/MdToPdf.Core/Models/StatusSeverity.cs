namespace MdToPdf.Models;

// Portable stand-in for Microsoft.UI.Xaml.Controls.InfoBarSeverity (values match 1:1) so
// MainViewModel's status property carries no WinUI dependency. Each UI project maps this to its
// own native severity/status-bar type — see MdToPdf/Converters/StatusSeverityConverter.cs for the
// WinUI side.
public enum StatusSeverity
{
    Informational = 0,
    Success = 1,
    Warning = 2,
    Error = 3,
}
