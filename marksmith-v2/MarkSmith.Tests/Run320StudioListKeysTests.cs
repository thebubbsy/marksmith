using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #320:
// - Shape Studio's colour-scheme flyout and SmartArt Studio's palette flyout: an arrow key no longer
//   applies the next palette and closes the flyout (selecting applies and hides it, and selection
//   followed focus), so the lists can be browsed from the keyboard; Enter or Space applies;
// - the lists with their own keys (Shape Studio's Shapes on canvas, SmartArt's Layouts and quick
//   layout carousel, the Diagram Studio shape palette) say which keys work.
public class Run320StudioListKeysTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private const string Shape = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string ShapeCode = "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string SmartArt = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string SmartArtCode = "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string Palette = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml";
    private const string PaletteCode = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs";

    // The opening tag of the element holding `marker`.
    private static string Tag(string xaml, string marker)
    {
        var at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, marker);
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Help(string tag)
    {
        var m = Regex.Match(tag, "AutomationProperties.HelpText=\"([^\"]*)\"");
        Assert.True(m.Success, tag);
        return m.Groups[1].Value;
    }

    // The body of a method, up to the next method at the same indentation.
    private static string Method(string code, string name)
    {
        var at = code.IndexOf($"void {name}(", StringComparison.Ordinal);
        Assert.True(at >= 0, name);
        var next = Regex.Match(code[(at + 10)..], @"\n\s+(private|public|internal|protected) ");
        return next.Success ? code.Substring(at, next.Index + 10) : code[at..];
    }

    [Theory]
    [InlineData(Shape, ShapeCode)]
    [InlineData(SmartArt, SmartArtCode)]
    public void PaletteFlyoutListsDoNotApplyOnArrowKeys(string xamlPath, string codePath)
    {
        var tag = Tag(Read(xamlPath), "x:Name=\"PaletteList\"");
        Assert.Contains("SingleSelectionFollowsFocus=\"False\"", tag);
        Assert.Contains("Enter or Space applies", Help(tag));
        // The reason for the setting: selecting a palette applies it and closes the flyout.
        var handler = Method(Read(codePath), "OnPaletteListSelectionChanged");
        Assert.Contains("PaletteFlyout", handler);
        Assert.Contains(".Hide()", handler);
    }

    [Fact]
    public void ShapesListNamesItsKeys()
    {
        var help = Help(Tag(Read(Shape), "x:Name=\"ShapesList\""));
        var keys = Method(Read(ShapeCode), "OnShapesListKeyDown");
        Assert.Contains("Delete removes", help);
        Assert.Contains("VirtualKey.Delete", keys);
        Assert.Contains("Ctrl+D duplicates", help);
        Assert.Contains("VirtualKey.D", keys);
        Assert.Contains("F2 edits its label", help);
        Assert.Contains("FocusLabelEditor", keys);
        Assert.Contains("Shift+F10", help);
        Assert.Contains("VirtualKey.F10", keys);
    }

    [Fact]
    public void SmartArtLayoutListNamesItsKeys()
    {
        var help = Help(Tag(Read(SmartArt), "x:Name=\"LayoutList\""));
        var keys = Method(Read(SmartArtCode), "OnLayoutListKeyDown");
        Assert.Contains("Enter edits the outline", help);
        Assert.Contains("OutlineScroll.Focus", keys);
        Assert.Contains("Esc returns to search", help);
        Assert.Contains("LayoutSearch.Focus", keys);
        Assert.Contains("Ctrl+C copies the layout name", help);
        Assert.Contains("Shift+F10", help);
        Assert.Contains("double-click inserts", help);
        Assert.Contains("InsertIntoDocumentCommand", Method(Read(SmartArtCode), "OnLayoutListDoubleTapped"));
    }

    [Fact]
    public void QuickLayoutCarouselSaysArrowsSwitchLayout()
    {
        var xaml = Read(SmartArt);
        var tag = Tag(xaml, "x:Name=\"QuickLayoutCarouselList\"");
        Assert.Contains("Left and Right arrows switch layout", Help(tag));
        // Horizontal, and its selection is the studio's layout, so arrowing really switches it.
        Assert.Contains("SelectedItem=\"{Binding SelectedLayout, Mode=TwoWay}\"", tag);
        var after = xaml[xaml.IndexOf("x:Name=\"QuickLayoutCarouselList\"", StringComparison.Ordinal)..];
        Assert.Contains("<ItemsStackPanel Orientation=\"Horizontal\" />", after[..after.IndexOf("</ListView.ItemsPanel>", StringComparison.Ordinal)]);
    }

    [Fact]
    public void ShapePaletteNamesItsKeys()
    {
        var help = Help(Tag(Read(Palette), "x:Name=\"PaletteListView\""));
        var keys = Method(Read(PaletteCode), "OnPaletteListViewKeyDown");
        Assert.Contains("Enter or Space adds the shape", help);
        Assert.Contains("VirtualKey.Space", keys);
        Assert.Contains("drag it onto the canvas", help);
        Assert.Contains("Ctrl+C copies its name", help);
        Assert.Contains("Shift+F10", help);
        Assert.Contains("Esc returns to search", help);
        Assert.Contains("SearchTextBox.Focus", keys);
    }

    // Guard: a studio list with keys of its own says which keys work.
    [Fact]
    public void EveryStudioListWithItsOwnKeysHasHelpText()
    {
        var files = new[] { Shape, SmartArt, Palette,
            "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml",
            "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml" };
        var checkedLists = 0;
        foreach (var file in files)
        {
            foreach (Match m in Regex.Matches(Read(file), "<(ListView|GridView)\\s[^>]*KeyDown=\"[^\"]+\"[^>]*>"))
            {
                checkedLists++;
                Assert.True(m.Value.Contains("AutomationProperties.HelpText=", StringComparison.Ordinal), $"{file}: {m.Value}");
            }
        }
        Assert.True(checkedLists >= 3);
    }
}
