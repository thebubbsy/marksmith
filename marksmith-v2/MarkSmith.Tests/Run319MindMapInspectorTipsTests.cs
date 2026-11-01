using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #319:
// - Mind map: "Add first node", the tour banner's "Import folder", the Map appearance box, the
//   Linked file box, the node Type box and the Notes box say what they do and which keys work;
//   Esc in Notes now returns to the map (the window ignores keys typed in text boxes);
// - Diagram Studio: the connector Label box names Enter/Esc;
// - Shape palette: "Show all shapes" says it clears the search and the category filter.
public class Run319MindMapInspectorTipsTests
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

    private const string MindMap = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml";
    private const string MindMapCode = "MarkSmith.Desktop/Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string Diagram = "MarkSmith.Desktop/Views/Mermaid/MermaidDiagramStudioControl.xaml";
    private const string Palette = "MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml";

    // The opening tag of the element holding `marker`.
    private static string Tag(string xaml, string marker)
    {
        var at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, marker);
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Tip(string tag)
    {
        var m = Regex.Match(tag, "ToolTipService.ToolTip=\"([^\"]*)\"");
        Assert.True(m.Success, tag);
        return m.Groups[1].Value;
    }

    [Theory]
    [InlineData("Command=\"{Binding AddRootNodeCommand}\"", "Tab or N")]
    [InlineData("AutomationProperties.Name=\"Map appearance\"", "Alt+Down")]
    [InlineData("x:Name=\"LinkedFileBox\"", "Enter or Esc returns to the map")]
    [InlineData("x:Name=\"NodeTypeCombo\"", "Alt+Down")]
    [InlineData("x:Name=\"NotesBox\"", "Esc returns to the map")]
    public void MindMapInspectorFieldsSayWhatTheyDo(string marker, string expected)
    {
        Assert.Contains(expected, Tip(Tag(Read(MindMap), marker)));
    }

    [Fact]
    public void EveryImportFolderButtonHasATip()
    {
        var xaml = Read(MindMap);
        var count = 0;
        foreach (Match m in Regex.Matches(xaml, "<Button[^>]*Click=\"OnImportFolderClick\"[^>]*>"))
        {
            count++;
            Assert.Contains("every document in it becomes a node", Tip(m.Value));
        }
        Assert.Equal(3, count);
    }

    [Fact]
    public void NodeTypeTipNamesEveryType()
    {
        var tip = Tip(Tag(Read(MindMap), "x:Name=\"NodeTypeCombo\""));
        foreach (var type in Enum.GetNames<MarkSmith.Models.MindMap.MindMapNodeType>())
        {
            Assert.Contains(type.ToLowerInvariant(), tip);
        }
    }

    [Fact]
    public void AddFirstNodeTipMatchesTheCanvasKeys()
    {
        var code = Read(MindMapCode);
        Assert.Contains("case Windows.System.VirtualKey.Tab when canvasFocused && !ctrl && !alt:\n                case Windows.System.VirtualKey.N when canvasFocused && !ctrl && !alt:\n                    ViewModel.AddChildNodeCommand.Execute(null);", code);
    }

    [Fact]
    public void NotesBoxEscReturnsToTheMapAndLeavesEnterAlone()
    {
        var tag = Tag(Read(MindMap), "x:Name=\"NotesBox\"");
        Assert.Contains("KeyDown=\"OnNotesBoxKeyDown\"", tag);
        Assert.Contains("AcceptsReturn=\"True\"", tag);

        var code = Read(MindMapCode);
        var at = code.IndexOf("private void OnNotesBoxKeyDown(", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = code[at..code.IndexOf("\n        }\n", at, StringComparison.Ordinal)];
        Assert.Contains("if (e.Key != Windows.System.VirtualKey.Escape) return;", body);
        Assert.Contains("GalaxyCanvas.Focus(FocusState.Programmatic);", body);
        Assert.DoesNotContain("Enter", body);
    }

    [Fact]
    public void DiagramConnectorLabelNamesItsKeys()
    {
        var tag = Tag(Read(Diagram), "x:Name=\"ConnectorLabelTextBox\"");
        Assert.Contains("Enter or Esc returns to the canvas", Tip(tag));
        Assert.Contains("KeyDown=\"OnInspectorSingleLineKeyDown\"", tag);
    }

    [Fact]
    public void ShowAllShapesSaysItClearsSearchAndCategory()
    {
        var tip = Tip(Tag(Read(Palette), "x:Name=\"ClearSearchButton\""));
        Assert.Contains("Clear the search", tip);
        Assert.Contains("every category", tip);
        var code = Read("MarkSmith.Desktop/Views/Mermaid/NodePaletteControl.xaml.cs");
        Assert.Contains("SelectPill(CatAll);", code);
    }

    // Every input in the mind map inspector and toolbars has a tooltip or help text.
    [Fact]
    public void EveryMindMapInputHasATipOrHelp()
    {
        var xaml = Read(MindMap);
        foreach (Match m in Regex.Matches(xaml, @"<(Button|ComboBox|TextBox|ToggleSwitch|CheckBox|NumberBox)(\s[^>]*?)?/?>"))
        {
            var tag = m.Value;
            if (tag.Contains("ToolTip") || tag.Contains("HelpText")) continue;
            // Swatches get their colour-name tooltip in OnSwatchLoaded.
            if (tag.Contains("Loaded=\"OnSwatchLoaded\"")) continue;
            var close = xaml.IndexOf($"</{m.Groups[1].Value}>", m.Index, StringComparison.Ordinal);
            if (!tag.EndsWith("/>") && close > 0 && xaml[m.Index..close].Contains("ToolTipService.ToolTip")) continue;
            Assert.Fail(tag);
        }
    }
}
