namespace MarkSmith.Converters;

// Maps MainViewModel.StatusSeverity (portable enum, defined in MarkSmith.Core) to the theme brush
// for the persistent status bar in MainWindow. Success reads green, warning amber, error red, and
// plain information stays on the default secondary text color so the bar doesn't shout when
// nothing noteworthy happened. MainWindow applies the key with ThemeBrush (a converter's brush is
// fixed at conversion time, so it kept the old theme's colour after a Windows theme switch).
public static class StatusSeverityBrushes
{
    public static string KeyFor(Models.StatusSeverity severity) => severity switch
    {
        Models.StatusSeverity.Success => "SystemFillColorSuccessBrush",
        // WinUI names its amber "Caution"; there is no SystemFillColorWarningBrush, so this key
        // used to miss and every warning status fell back to plain grey.
        Models.StatusSeverity.Warning => "SystemFillColorCautionBrush",
        Models.StatusSeverity.Error => "SystemFillColorCriticalBrush",
        _ => "TextFillColorSecondaryBrush",
    };
}
