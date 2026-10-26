namespace MarkSmith.Services;

using System;

/// <summary>
/// Tooltip and display text for Editor and Workspace input file path.
/// Long file paths in narrow boxes hide the file name; this service extracts
/// the leaf file name, parent directory, display summary, and full informative tooltip.
/// </summary>
public static class InputFilePathText
{
    public const string MenuHint = "Right-click or Shift+F10 for file actions · History button for revisions";
    public const string EmptyHint = "No file selected — open a Markdown file (Ctrl+O) or drop one here";

    public static string ToolTip(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return EmptyHint;
        var trimmed = path.Trim();
        var fileName = FileName(trimmed);
        var folder = Directory(trimmed);

        if (string.IsNullOrEmpty(folder))
            return $"Selected file: {trimmed}\n{MenuHint}";

        return $"Selected file: {trimmed}\nFile name: {fileName}\nFolder: {folder}\n{MenuHint}";
    }

    /// <summary>
    /// Returns the leaf file name (e.g. "document.md" from "C:\Users\Tony\Docs\document.md"),
    /// or empty string if empty.
    /// </summary>
    public static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim().TrimEnd('\\', '/');
        if (trimmed.Length == 0) return string.Empty;
        var idx = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    /// <summary>
    /// Returns the parent directory path, or empty string if none.
    /// </summary>
    public static string Directory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var idx = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return idx >= 0 ? trimmed[..idx] : string.Empty;
    }

    /// <summary>
    /// Returns a short display summary for status lines or window title bar.
    /// </summary>
    public static string DisplaySummary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Unsaved document";
        var name = FileName(path);
        return string.IsNullOrEmpty(name) ? path.Trim() : name;
    }
}
