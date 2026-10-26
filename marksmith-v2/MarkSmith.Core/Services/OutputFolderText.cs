namespace MarkSmith.Services;

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
}
