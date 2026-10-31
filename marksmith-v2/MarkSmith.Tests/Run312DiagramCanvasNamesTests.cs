using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.ViewModels.Mermaid;
using Xunit;

namespace MarkSmith.Tests;

// Run #312:
// - Diagram Studio canvas nodes have an accessible name ("Check input, Diamond, selected"); they
//   were unnamed boxes, so a screen reader only heard their quick-add groups;
// - connectors are named "Connector from A to B, labelled X" (only their label text was exposed);
// - the canvas viewport, where focus sits while shapes are edited, is named with key help;
// - SelectorBar tab bars (editor view, Shape Studio tabs, SmartArt editor tabs) name their inner list;
// - the main window's "⋯ menu" teaching tip carries its title as its accessible name.
public class Run312DiagramCanvasNamesTests
{
    private const string CanvasXaml = "MarkSmith.Desktop/Views/Mermaid/MermaidCanvasControl.xaml";
    private const string CanvasCode = "MarkSmith.Desktop/Views/Mermaid/MermaidCanvasControl.xaml.cs";
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string Helper = "MarkSmith.Desktop/Services/SelectorBarNames.cs";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static XElement Named(string relative, string name) =>
        XDocument.Parse(Read(relative)).Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static MermaidStudioViewModel FlowchartVm()
    {
        var vm = new MermaidStudioViewModel();
        vm.LoadFromMarkdown("```mermaid\nflowchart TD\n    A[Start] -->|go| B{Check input}\n```", 0);
        return vm;
    }

    [Theory]
    [InlineData("RoundedRectangle", "Rounded rectangle")]
    [InlineData("Rectangle", "Rectangle")]
    [InlineData("NormalState", "Normal state")]
    [InlineData("", "Shape")]
    [InlineData(null, "Shape")]
    public void FriendlyShapeName_SplitsPascalCase(string? shape, string expected)
    {
        Assert.Equal(expected, DiagramNodeViewModel.FriendlyShapeName(shape));
    }

    [Fact]
    public void NodeName_SaysLabelShapeAndSelection()
    {
        var node = new DiagramNodeViewModel { Id = "n1", LabelText = "Check input\rsecond line", Shape = "Rhombus" };
        Assert.Equal("Check input, Rhombus", node.AccessibleName);
        node.IsSelected = true;
        Assert.Equal("Check input, Rhombus, selected", node.AccessibleName);
    }

    // A WinUI TextBox stores a typed line break as a bare '\r'; the status line ("Selected 'Check
    // input' (2 of 3)") used to run the two lines together as "Check inputsecond line".
    [Theory]
    [InlineData("Check input\rsecond line")]
    [InlineData("Check input\nsecond line")]
    [InlineData("Check input\r\nsecond line")]
    public void DisplayName_StopsAtTheFirstLineBreak_WhicheverKind(string label)
    {
        var node = new DiagramNodeViewModel { Id = "n1", LabelText = label };
        Assert.Equal("Check input", MermaidStudioViewModel.DisplayName(node));
        Assert.StartsWith("Check input, ", node.AccessibleName);
    }

    [Fact]
    public void NodeName_FallsBackToId_AndNamesPseudoStates()
    {
        Assert.Equal("n7, Rectangle", new DiagramNodeViewModel { Id = "n7", LabelText = "  " }.AccessibleName);
        Assert.Equal("Start point", new DiagramNodeViewModel { Shape = "Start", LabelText = "" }.AccessibleName);
        Assert.Equal("End point, selected", new DiagramNodeViewModel { Shape = "End", IsSelected = true }.AccessibleName);
    }

    [Theory]
    [InlineData(nameof(DiagramNodeViewModel.LabelText))]
    [InlineData(nameof(DiagramNodeViewModel.Shape))]
    [InlineData(nameof(DiagramNodeViewModel.IsSelected))]
    public void NodeName_IsRaisedWhenItsPartsChange(string property)
    {
        var node = new DiagramNodeViewModel { LabelText = "A", Shape = "Rectangle" };
        var raised = new List<string?>();
        node.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        switch (property)
        {
            case nameof(DiagramNodeViewModel.LabelText): node.LabelText = "B"; break;
            case nameof(DiagramNodeViewModel.Shape): node.Shape = "Hexagon"; break;
            default: node.IsSelected = true; break;
        }
        Assert.Contains(nameof(DiagramNodeViewModel.AccessibleName), raised);
    }

    [Fact]
    public void ConnectorName_SaysEndsLabelAndSelection()
    {
        var vm = FlowchartVm();
        var conn = vm.Connectors.Single();
        Assert.Equal("Connector from Start to Check input, labelled go", vm.ConnectorAccessibleName(conn));
        conn.Label = "line\rbreak";
        conn.IsSelected = true;
        Assert.Equal("Connector from Start to Check input, labelled line break, selected", vm.ConnectorAccessibleName(conn));
        conn.Label = null;
        conn.IsSelected = false;
        Assert.Equal("Connector from Start to Check input", vm.ConnectorAccessibleName(conn));
    }

    [Fact]
    public void Canvas_IsNamed_WithKeyHelp()
    {
        var viewer = Named(CanvasXaml, "CanvasScrollViewer");
        Assert.Equal("Diagram canvas", (string?)viewer.Attribute("AutomationProperties.Name"));
        var help = (string?)viewer.Attribute("AutomationProperties.HelpText");
        Assert.NotNull(help);
        foreach (var key in new[] { "Tab", "Arrow keys", "F2", "Delete", "Shift+F10" })
            Assert.Contains(key, help);
    }

    [Fact]
    public void CanvasTemplates_PushNamesFromTheViewModels()
    {
        var xaml = Read(CanvasXaml);
        Assert.Contains("Loaded=\"OnConnectorTemplateLoaded\" Unloaded=\"OnConnectorTemplateUnloaded\"", xaml);
        Assert.DoesNotContain("x:Bind AccessibleName", xaml);

        var code = Read(CanvasCode);
        Assert.Contains("TrackNodeName(root, node);", code);
        Assert.Contains("AutomationProperties.SetName(root, node.AccessibleName)", code);
        Assert.Contains("vm.ConnectorAccessibleName(conn)", code);
        // Handlers come off when the element unloads, and a re-run Loaded replaces rather than stacks them.
        Assert.Contains("hook.Source.PropertyChanged -= hook.Handler;", code);
        int hook = code.IndexOf("private static void HookName(", StringComparison.Ordinal);
        Assert.True(hook > 0);
        Assert.True(code.IndexOf("UnhookName(root);", hook, StringComparison.Ordinal) < code.IndexOf("source.PropertyChanged += handler;", hook, StringComparison.Ordinal));
        // Renaming a shape renames the connectors that name it.
        Assert.Contains("conn.SourceNodeId == node.Id || conn.TargetNodeId == node.Id", code);
    }

    [Theory]
    [InlineData("MarkSmith.Desktop/MainWindow.xaml", "MarkSmith.Desktop/MainWindow.xaml.cs", "CenterViewSelector")]
    [InlineData("MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml", "MarkSmith.Desktop/Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "LeftTabs")]
    [InlineData("MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml", "MarkSmith.Desktop/Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "EditorTabs")]
    public void SelectorBars_NameTheirInnerList(string xaml, string code, string bar)
    {
        var el = Named(xaml, bar);
        Assert.Equal("SelectorBar", el.Name.LocalName);
        Assert.False(string.IsNullOrWhiteSpace((string?)el.Attribute("AutomationProperties.Name")));
        Assert.Contains($"SelectorBarNames.NameInnerList({bar});", Read(code));
    }

    [Fact]
    public void EverySelectorBar_IsCovered()
    {
        var desktop = Path.Combine(RepoDir(), "MarkSmith.Desktop");
        var bars = Directory.EnumerateFiles(desktop, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => XDocument.Parse(File.ReadAllText(f)).Descendants().Where(e => e.Name.LocalName == "SelectorBar")
                .Select(e => (File: f, Name: (string?)e.Attribute(X + "Name"))))
            .ToList();
        Assert.NotEmpty(bars);
        foreach (var (file, name) in bars)
        {
            Assert.False(string.IsNullOrEmpty(name), $"unnamed SelectorBar in {file}");
            Assert.Contains($"SelectorBarNames.NameInnerList({name});", File.ReadAllText(file + ".cs"));
        }
    }

    [Fact]
    public void SelectorBarHelper_NamesTheItemsViewAfterTheBar_OnlyWhenUnnamed()
    {
        var code = Read(Helper);
        Assert.Contains("AutomationProperties.GetName(bar)", code);
        Assert.Contains("is ItemsView", code);
        Assert.Contains("string.IsNullOrWhiteSpace(AutomationProperties.GetName(list))", code);
        Assert.Contains("bar.Loaded -= OnLoaded;", code);
    }

    [Fact]
    public void MoreMenuTip_IsNamedAfterItsTitle()
    {
        var code = Read(Main);
        int a = code.IndexOf("private void ShowMoreMenuTip(", StringComparison.Ordinal);
        int b = code.IndexOf("MoreMenuTip.IsOpen = true;", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        var body = code.Substring(a, b - a);
        Assert.Contains("AutomationProperties.SetName(MoreMenuTip, title);", body);
        Assert.Contains("AutomationProperties.SetHelpText(MoreMenuTip, subtitle);", body);
    }
}
