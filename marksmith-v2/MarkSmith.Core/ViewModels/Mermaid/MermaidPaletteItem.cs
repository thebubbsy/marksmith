using CommunityToolkit.Mvvm.ComponentModel;

namespace MarkSmith.ViewModels.Mermaid;

public partial class MermaidPaletteItem : ObservableObject
{
    [ObservableProperty]
    private string _category = "Flowchart";

    [ObservableProperty]
    private string _displayName = "Rectangle";

    [ObservableProperty]
    private string _shapeType = "Rectangle";

    [ObservableProperty]
    private string _iconGlyph = "\uE8A5";

    [ObservableProperty]
    private string _defaultText = "Node";

    [ObservableProperty]
    private string _tooltip = "Add shape to canvas";

    // A ListView announces its items by ToString(): without this, screen readers read every
    // Diagram Studio palette row as "MarkSmith.ViewModels.Mermaid.MermaidPaletteItem".
    public override string ToString() => DisplayName;

    /// <summary>The palette search: name, category, the label a new shape starts with ("Process",
    /// "Decision?", "Start/End") and its shape type ("Rhombus", "Stadium"). It matched only the name and
    /// category, so typing "process" or "terminal" — the words on the shapes themselves — found nothing.</summary>
    public bool Matches(string? query)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return Contains(DisplayName, q) || Contains(Category, q) || Contains(DefaultText, q) || Contains(ShapeType, q);
    }

    /// <summary>The palette header's count: "21 shapes" unfiltered, "3 of 21 shapes" while a pill or
    /// search narrows the list.</summary>
    public static string CountLabel(int shown, int total)
    {
        if (total <= 0) return "";
        var noun = total == 1 ? "shape" : "shapes";
        return shown >= total ? $"{total} {noun}" : $"{shown} of {total} {noun}";
    }

    private static bool Contains(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text.Contains(query, System.StringComparison.OrdinalIgnoreCase);
}
