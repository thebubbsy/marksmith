using System;

namespace MarkSmith.Services;

// Desktop's HoverPolish gives a few template buttons a tooltip of their own; this is its rule, kept
// here so it can be tested without a XAML tree.
public static class TemplatePartTipRule
{
    public const string InfoBarClose = "Dismiss this message";

    /// <summary>Whether a template button's current tooltip may be replaced: it has none, or the one
    /// it has only repeats its name (InfoBar's own "Close").</summary>
    public static bool CanReplace(bool hasTip, string? tipText, string? name) =>
        !hasTip || string.Equals(tipText?.Trim(), name?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The tooltip for template part <paramref name="part"/> of a control of kind
    /// <paramref name="owner"/> ("Expander", "InfoBar" or "ContentDialog"), or null for none.</summary>
    /// <param name="ownerHelp">The owner's help text: what an Expander's header says it holds.</param>
    /// <param name="text">The button's own text: a dialog's "Close" or "Cancel".</param>
    public static string? For(string part, string? owner, string? ownerHelp, string? text)
    {
        var tip = (part, owner) switch
        {
            ("ExpanderHeader", "Expander") => ownerHelp,
            ("CloseButton", "InfoBar") => InfoBarClose,
            ("CloseButton", "ContentDialog") when !string.IsNullOrWhiteSpace(text) => $"{text.Trim()} (Esc)",
            _ => null,
        };
        return string.IsNullOrWhiteSpace(tip) ? null : tip;
    }
}
