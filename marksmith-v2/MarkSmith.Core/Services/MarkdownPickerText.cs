namespace MarkSmith.Services;

// Step 1's "Recent and found Markdown files" picker. Rescan and pin/unpin rebuild the list, which
// left the picker blank ("Pick a Markdown file from your PC…") with a document open; the window
// reselects the open file's row with IndexOfOpenFile. The right-click menu names that file.
public static class MarkdownPickerText
{
    /// <summary>The row for <paramref name="openPath"/>, or -1 when it isn't in the list.</summary>
    public static int IndexOfOpenFile(IReadOnlyList<MarkdownFileEntry> entries, string? openPath)
    {
        var open = FullPath(openPath);
        if (open is null) return -1;
        for (var i = 0; i < entries.Count; i++)
            if (string.Equals(FullPath(entries[i].Path), open, PathEquality.Comparison)) return i;
        return -1;
    }

    public static string ShowInExplorer(string? path) =>
        FileName(path) is { } name ? $"Show {name} in File Explorer" : "Show selected in File Explorer";

    public static string CopyPath(string? path) =>
        FileName(path) is { } name ? $"Copy path of {name}" : "Copy selected file path";

    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var name = Path.GetFileName(path.Trim().TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()); }
        catch { return null; }
    }
}
