namespace MarkSmith.Services;

/// <summary>
/// Canonical tooltips and accessible descriptions for status bar copy pills.
/// Each description documents both the primary left-click action and the right-click / Shift+F10 context menu.
/// </summary>
public static class StatusCopyPillTooltipText
{
    public const string MarkdownTooltip =
        "Copy Markdown (or active selection) to clipboard · Right-click or Shift+F10 for rich email & metrics copy options";

    public const string TextTooltip =
        "Copy plain text document (Markdown stripped) to clipboard · Right-click or Shift+F10 for single-line or text metrics";

    public const string TocTooltip =
        "Copy Markdown Table of Contents to clipboard · Right-click or Shift+F10 for plain outline or insert at cursor";

    public const string LinksTooltip =
        "Copy all document links to clipboard · Right-click or Shift+F10 for raw URLs only or insert Link Index at cursor";

    public const string CodeTooltip =
        "Copy all fenced code blocks to clipboard · Right-click or Shift+F10 for language breakdown table or insert at cursor";

    public const string TablesTooltip =
        "Copy all Markdown tables to clipboard · Right-click or Shift+F10 for Excel TSV format or Table Inventory Table";

    public const string TasksTooltip =
        "Copy all task list items to clipboard · Right-click or Shift+F10 for pending/completed filters or Task Progress Table";

    public const string MathTooltip =
        "Copy all LaTeX math expressions to clipboard · Right-click or Shift+F10 for block/inline filters or Math Inventory Table";

    public const string CalloutsTooltip =
        "Copy all admonition callouts & blockquotes to clipboard · Right-click or Shift+F10 for alert filters or Callout Inventory Table";

    public const string ImagesTooltip =
        "Copy all Markdown image tags to clipboard · Right-click or Shift+F10 for URL filters or Image Asset Inventory Table";

    public const string FootnotesTooltip =
        "Copy all footnote definitions to clipboard · Right-click or Shift+F10 for orphan audits or Footnote Inventory Table";

    public const string DiagramsTooltip =
        "Copy all Mermaid diagrams and :::smartart / :::chart / :::shapes visual blocks to clipboard · Right-click or Shift+F10 for filters or Diagram Inventory Table";

    public const string FrontmatterTooltip =
        "Copy YAML frontmatter block (--- ... ---) to clipboard · Right-click or Shift+F10 for property key-values, schema audit, or properties table";

    public const string HtmlTooltip =
        "Copy rendered HTML document to clipboard · Right-click or Shift+F10 for standalone inlined HTML";

    public const string MoreCopyTooltip =
        "More copy actions: text, code, tables, tasks, math, callouts, images, footnotes, diagrams, frontmatter and HTML · Click, Right-click or Shift+F10 for menu";
}
