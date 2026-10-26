namespace MarkSmith.Services;

// Wording for the two buttons beside Style & Export's theme picker. Both used to carry one fixed
// tooltip ("Pin/unpin the selected theme…", "Create a theme…") that also became their screen-reader
// name, so the star never said whether the theme was pinned and the palette button kept saying
// "Create" while it opened the selected custom theme for editing.
public static class ThemeButtonText
{
    public static string PinName(string? theme, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(theme)) return "Pin theme";
        return pinned ? $"Unpin {theme}" : $"Pin {theme}";
    }

    public static string PinToolTip(string? theme, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(theme)) return "Pick a theme first to pin it to the top of the list";
        return pinned
            ? $"Unpin “{theme}” from the top of the theme list"
            : $"Pin “{theme}” to the top of the theme list";
    }

    /// <summary>The status line after the star is pressed.</summary>
    public static string PinStatus(string theme, bool pinned) => pinned
        ? $"Pinned “{theme}” to the top of the theme list."
        : $"Unpinned “{theme}”; it's back in alphabetical order.";

    public static string EditName(string? theme, bool isCustom) =>
        isCustom && !string.IsNullOrWhiteSpace(theme) ? $"Edit theme {theme}" : "Create a theme";

    public static string EditToolTip(string? theme, bool isCustom) =>
        isCustom && !string.IsNullOrWhiteSpace(theme)
            ? $"Edit “{theme}”: change its colors, rename it or delete it"
            : string.IsNullOrWhiteSpace(theme)
                ? "Create a theme: pick your own colors for every element"
                : $"Create a theme: pick your own colors for every element (starts from “{theme}”)";
}
