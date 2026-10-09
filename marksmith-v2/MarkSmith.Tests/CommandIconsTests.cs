using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #47 rendered every icon in the app from the installed Segoe Fluent Icons font. It found
// Export PDF drawn as a printer, the Diagram Studio as "{ }", "Clean up" as a paintbrush, menus
// where half the items had icons, and a command palette with none. These tests keep the menus
// complete and the palette's pictures in step with the menu items they stand for.
public class CommandIconsTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string DesktopDir([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "MarkSmith.Desktop");

    private static string IconOf(XElement item) =>
        item.Elements().Where(e => e.Name.LocalName.EndsWith(".Icon", StringComparison.Ordinal))
            .SelectMany(e => e.Descendants(Ns + "FontIcon"))
            .Select(i => (string?)i.Attribute("Glyph") ?? "")
            .FirstOrDefault() ?? "";

    private static bool HasIcon(XElement item) =>
        item.Attribute("Icon") is not null
        || item.Elements().Any(e => e.Name.LocalName.EndsWith(".Icon", StringComparison.Ordinal));

    [Fact]
    public void Every_menu_gives_all_its_items_an_icon_or_none()
    {
        var itemNames = new[] { "MenuFlyoutItem", "ToggleMenuFlyoutItem", "MenuFlyoutSubItem", "RadioMenuFlyoutItem" };
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(DesktopDir(), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var doc = XDocument.Load(file);
            foreach (var menu in doc.Descendants().Where(e => e.Name == Ns + "MenuFlyout" || e.Name == Ns + "MenuFlyoutSubItem"))
            {
                var items = menu.Elements().Where(e => itemNames.Contains(e.Name.LocalName)).ToList();
                var bare = items.Where(i => !HasIcon(i)).ToList();
                if (bare.Count > 0 && bare.Count < items.Count)
                {
                    problems.Add($"{Path.GetFileName(file)}: {string.Join(", ", bare.Select(b => (string?)b.Attribute("Text")))}");
                }
            }
        }
        Assert.Empty(problems);
    }

    // Menu item text in MainWindow.xaml → the palette label for the same action.
    public static TheoryData<string, string> MenuToPalette => new()
    {
        { "Export as Word (.docx)", "Export Word (.docx)" },
        { "Export as PDF (.pdf)", "Export PDF" },
        { "Export as PowerPoint (.pptx)", "Export PowerPoint (.pptx)" },
        { "Export as EPUB (.epub)", "Export EPUB" },
        { "Export as web page (.html)", "Export HTML" },
        { "Email draft (opens in Outlook)", "Email draft (open in Outlook)" },
        { "Save as email (.eml)", "Save as email (.eml)" },
        { "Save as Outlook message (.msg)", "Save as Outlook message (.msg)" },
        { "Copy as email", "Copy as email" },
        { "Export all licensed formats", "Export all formats" },
        { "Bold", "Bold" },
        { "Italic", "Italic" },
        { "Strikethrough", "Strikethrough" },
        { "Heading 1", "Heading 1" },
        { "Heading 4", "Heading 4" },
        { "Bullet list", "Bullet list" },
        { "Task list", "Task list" },
        { "Blockquote", "Blockquote" },
        { "Link", "Insert link" },
        { "Image", "Insert image" },
        { "Table", "Insert table" },
        { "Code block", "Insert code block" },
        { "Table from a spreadsheet…", "Insert table from a spreadsheet" },
        { "Workflow", "Insert workflow" },
        { "Timeline", "Insert timeline" },
        { "SmartArt", "Insert SmartArt" },
        { "References", "Insert references" },
        { "Find", "Find" },
        { "Find and replace", "Find and replace" },
        { "UPPERCASE", "Make selection UPPERCASE" },
        { "lowercase", "Make selection lowercase" },
        { "Title Case", "Make selection Title Case" },
        { "Sort lines A to Z", "Sort lines A to Z" },
        { "Remove duplicate lines", "Remove duplicate lines" },
        { "Copy a table to Excel…", "Copy a table to Excel" },
        { "Clean up document", "Clean up document" },
        { "Document Galaxy…", "Open Document Galaxy" },
        { "Shape Studio…", "Open Shape Studio" },
        { "SmartArt Studio…", "Open SmartArt Studio" },
        { "Diagram Studio…", "Open Diagram Studio" },
        { "Version history…", "Version history" },
        { "Recent exports", "Recent exports" },
        { "Take a quick tour", "Take the welcome tour" },
        { "Keyboard shortcuts", "Show keyboard shortcuts" },
        { "Settings", "Open Settings" },
    };

    [Theory]
    [MemberData(nameof(MenuToPalette))]
    public void Palette_icon_matches_the_menu_item(string menuText, string paletteLabel)
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir(), "MainWindow.xaml"));
        var item = doc.Descendants(Ns + "MenuFlyoutItem").Single(i => (string?)i.Attribute("Text") == menuText);
        Assert.Contains(paletteLabel, CommandIcons.Labels);
        Assert.Equal(IconOf(item), CommandIcons.ForPalette(paletteLabel, ""));
    }

    [Fact]
    public void Every_palette_command_has_its_own_icon()
    {
        var source = File.ReadAllText(Path.Combine(DesktopDir(), "MainWindow.xaml.cs"));
        var start = source.IndexOf("private List<PaletteCommand> BuildPaletteCommands()", StringComparison.Ordinal);
        var end = source.IndexOf("foreach (var theme in App.Themes.All)", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "BuildPaletteCommands moved; update this test.");
        var labels = Regex.Matches(source[start..end], "\\b(?:Do|DoAsync|Edit|Insert)\\(\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.True(labels.Count > 50, $"Only found {labels.Count} palette commands.");
        Assert.DoesNotContain(labels, l => !CommandIcons.Labels.Contains(l));
        // And nothing in the table is stale.
        Assert.DoesNotContain(CommandIcons.Labels, l => !labels.Contains(l));
    }

    [Fact]
    public void Icons_are_a_fluent_glyph_or_a_short_letterform()
    {
        foreach (var label in CommandIcons.Labels)
        {
            var icon = CommandIcons.ForPalette(label, "");
            Assert.True(CommandIcons.IsGlyph(icon) || (icon.Length is 2 && !CommandIcons.IsGlyph(icon[..1])), $"{label}: '{icon}'");
        }
        foreach (var category in new[] { "Export", "File", "Edit", "Insert", "View", "Studio", "App", "Theme", "Recent", "Unknown" })
        {
            Assert.True(CommandIcons.IsGlyph(CommandIcons.ForPalette("Switch theme: Nord", category)), category);
        }
    }
}
