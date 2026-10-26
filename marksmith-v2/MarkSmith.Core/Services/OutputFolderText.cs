namespace MarkSmith.Services;

using System;

// Tooltip for Style & Export's output-folder box. The box is about 200 px wide at the default
// window size, so it shows the start of the path ("C:\Users\Tony\OneDrive\Doc") and hides the
// folder that matters; the tooltip names the whole path first and keeps the menu hint after it.
public static class OutputFolderText
{
    public const string MenuHint = "Right-click or Shift+F10 to open it in File Explorer, copy or change it";

    public static string ToolTip(string? folder) =>
        string.IsNullOrWhiteSpace(folder)
            ? $"Output folder: next to the input file (none chosen)\n{MenuHint}"
            : $"Output folder: {folder.Trim()}\n{MenuHint}";

    /// <summary>
    /// Returns the leaf directory name (e.g. "Exports" from "C:\Users\Tony\Documents\Exports"),
    /// or empty string if empty or root.
    /// </summary>
    public static string LeafFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
        var trimmed = folder.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':')) return string.Empty;
        var idx = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// Returns a short display summary for the output folder in status lines or badges.
    /// </summary>
    public static string DisplaySummary(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "next to input file";
        var leaf = LeafFolder(folder);
        return string.IsNullOrEmpty(leaf) ? folder.Trim() : leaf;
    }
}
