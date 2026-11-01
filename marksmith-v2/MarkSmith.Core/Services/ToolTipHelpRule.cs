using System;

namespace MarkSmith.Services;

// Desktop's ToolTipHelp copies a control's tooltip into its help text; this is its rule, kept here
// so it can be tested without a XAML tree.
public static class ToolTipHelpRule
{
    /// <summary>The help text a control should get from its tooltip, "" to clear the copy made
    /// earlier, or null to leave the help text alone: no tooltip and nothing copied, a tooltip that
    /// only repeats the name, or help text that was set by someone else.</summary>
    /// <param name="copied">The help text copied from the tooltip last time, or null.</param>
    public static string? HelpFor(string? tip, string? name, string? help, string? copied)
    {
        if (!string.IsNullOrEmpty(help) && !string.Equals(help, copied, StringComparison.Ordinal)) return null;
        if (string.IsNullOrWhiteSpace(tip)
            || string.Equals(tip.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase))
            return copied is null ? null : "";
        return string.Equals(tip, help, StringComparison.Ordinal) ? null : tip;
    }
}
