namespace MarkSmith.Services;

/// <summary>
/// The icon each command palette entry shows, chosen to match that command's toolbar button or
/// menu item so a person learns one picture per action. Most entries are Segoe Fluent Icons
/// glyphs (one private-use character); a few actions no glyph describes (headings, letter case,
/// numbering) use a short letterform instead, the same one the menus and the editing bar show.
/// Run #47 checked every glyph by rendering it from the installed font, not by name alone.
/// </summary>
public static class CommandIcons
{
    // Glyphs shared with MainWindow.xaml. Keep these in step with the matching menu items.
    public const string Pdf = "\uEA90";
    public const string Word = "\uE8A5";
    public const string PowerPoint = "\uE786";
    public const string Epub = "\uE82D";
    public const string WebPage = "\uE12B";
    public const string Markdown = "M\u2193"; // the Markdown mark: a page glyph looked like Word's
    public const string EmailDraft = "\uE724";
    public const string Email = "\uE715";
    public const string OutlookMessage = "\uE8C3";
    public const string Copy = "\uE8C8";
    public const string ExportAll = "\uE71D";
    public const string Export = "\uEDE1";
    public const string Print = "\uE749";
    public const string RecentExports = "\uE81C";
    public const string Open = "\uE8E5";
    public const string Import = "\uE8B5";
    public const string Save = "\uE74E";
    public const string VersionHistory = "\uE823";
    public const string Find = "\uE721";
    public const string Replace = "\uE8AB";
    public const string Bold = "\uE8DD";
    public const string Italic = "\uE8DB";
    public const string Strikethrough = "\uEDE0";
    public const string BulletList = "\uE8FD";
    public const string TaskList = "\uE73A";
    public const string Blockquote = "\uE9B1";
    public const string Sort = "\uE8CB";
    public const string RemoveDuplicates = "\uECC9";
    public const string Cleanup = "\uEA99";
    public const string Link = "\uE71B";
    public const string Image = "\uEB9F";
    public const string Table = "\uE80A";
    public const string Code = "\uE943";
    public const string Spreadsheet = "\uE9F9";
    public const string Workflow = "\uE9D5";
    public const string Timeline = "\uE7C1";
    public const string SmartArt = "\uF003";
    public const string References = "\uE82D";
    public const string SplitView = "\uE89A";
    public const string PreviewView = "\uE890";
    public const string FocusMode = "\uE740";
    public const string LookingGlass = "\uE7B3";
    public const string Outline = "\uE8A4";
    public const string DiagramStudio = "\uEF90";
    public const string ShapeStudio = "\uE790";
    public const string DocumentGalaxy = "\uEC26";
    public const string SuiteHub = "\uECA5";
    public const string Settings = "\uE713";
    public const string Tour = "\uE897";
    public const string Shortcuts = "\uE765";
    public const string Theme = "\uE790";
    public const string Document = "\uE8A5";

    private static readonly Dictionary<string, string> ByLabel = new(StringComparer.Ordinal)
    {
        ["Export PDF"] = Pdf,
        ["Export Word (.docx)"] = Word,
        ["Export PowerPoint (.pptx)"] = PowerPoint,
        ["Export EPUB"] = Epub,
        ["Export HTML"] = WebPage,
        ["Email draft (open in Outlook)"] = EmailDraft,
        ["Save as email (.eml)"] = Email,
        ["Save as Outlook message (.msg)"] = OutlookMessage,
        ["Copy as email"] = Copy,
        ["Export all formats"] = ExportAll,
        ["Copy the rendered HTML"] = Copy,
        ["Print the rendered document"] = Print,
        ["Recent exports"] = RecentExports,

        ["Open a document"] = Open,
        ["Open an email (.eml or .msg)"] = Email,
        ["Import Word, PDF, HTML or email as a new document"] = Import,
        ["Save (converted files save as a Markdown copy)"] = Save,
        ["Version history"] = VersionHistory,

        ["Find"] = Find,
        ["Find and replace"] = Replace,
        ["Bold"] = Bold,
        ["Italic"] = Italic,
        ["Strikethrough"] = Strikethrough,
        ["Heading 1"] = "H1",
        ["Heading 2"] = "H2",
        ["Heading 3"] = "H3",
        ["Heading 4"] = "H4",
        ["Bullet list"] = BulletList,
        ["Numbered list"] = "1.",
        ["Task list"] = TaskList,
        ["Blockquote"] = Blockquote,
        ["Make selection UPPERCASE"] = "AB",
        ["Make selection lowercase"] = "ab",
        ["Make selection Title Case"] = "Ab",
        ["Sort lines A to Z"] = Sort,
        ["Sort lines Z to A"] = Sort,
        ["Remove duplicate lines"] = RemoveDuplicates,
        ["Clean up document"] = Cleanup,

        ["Insert link"] = Link,
        ["Insert image"] = Image,
        ["Insert table"] = Table,
        ["Insert code block"] = Code,
        ["Insert table from a spreadsheet"] = Spreadsheet,
        ["Insert workflow"] = Workflow,
        ["Insert timeline"] = Timeline,
        ["Insert SmartArt"] = SmartArt,
        ["Insert references"] = References,
        ["Copy a table to Excel"] = Export,

        ["Code view (editor only)"] = Code,
        ["Split view (editor and preview)"] = SplitView,
        ["Preview view (rendered page only)"] = PreviewView,
        ["Toggle focus mode"] = FocusMode,
        ["Toggle Looking Glass portal"] = LookingGlass,
        ["Toggle preview as email"] = Email,
        ["Document outline"] = Outline,

        ["Open Diagram Studio"] = DiagramStudio,
        ["Open Shape Studio"] = ShapeStudio,
        ["Open SmartArt Studio"] = SmartArt,
        ["Open Document Galaxy"] = DocumentGalaxy,
        ["Open Suite Hub"] = SuiteHub,

        ["Open Settings"] = Settings,
        ["Take the welcome tour"] = Tour,
        ["Show keyboard shortcuts"] = Shortcuts,
    };

    private static readonly Dictionary<string, string> ByCategory = new(StringComparer.Ordinal)
    {
        ["Export"] = Export,
        ["File"] = Document,
        ["Edit"] = "\uE70F",
        ["Insert"] = "\uE710",
        ["View"] = PreviewView,
        ["Studio"] = "\uEB3C",
        ["App"] = Settings,
        ["Theme"] = Theme,
        ["Recent"] = Document,
    };

    /// <summary>The palette labels that have their own icon (the rest fall back to their category's).</summary>
    public static IReadOnlyCollection<string> Labels => ByLabel.Keys;

    /// <summary>The icon for a palette entry: its own, else its category's, else a plain document.</summary>
    public static string ForPalette(string label, string category) =>
        ByLabel.TryGetValue(label, out var icon) ? icon
        : ByCategory.TryGetValue(category, out var fallback) ? fallback
        : Document;

    /// <summary>True for a Fluent glyph; false for a letterform such as "H1" (drawn in the text font).</summary>
    public static bool IsGlyph(string icon) =>
        icon.Length == 1 && icon[0] >= '\uE000' && icon[0] <= '\uF8FF';
}
