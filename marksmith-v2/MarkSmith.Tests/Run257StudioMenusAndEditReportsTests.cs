using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #257, the studios' long menus (run #256's Next up item 1):
// - Shape Studio's selection menu (the bounds pill) had 35 rows at the top level; three of its "distribute"
//   entries read alike but did different things, and a star / palette glyph stood in for unrelated actions;
// - the Diagnostics submenus of Shape Studio, SmartArt Studio and the Mind Map Galaxy used the picture glyph
//   (E8B9) on ~140 entries and the palette glyph (E790) on non-colour ones, with stray Title Case labels;
// - 19 edit actions in Shape Studio and the Galaxy copied their result summary, replacing the clipboard;
// - Suite Hub let a second file rewrite start while the first was still writing (run #256's Noticed list).
public class Run257StudioMenusAndEditReportsTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private const string ShapeXaml = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml";
    private const string SmartArtXaml = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string GalaxyXaml = "Views/MindMap/MindMapGalaxyWindow.xaml";

    private static string DesktopDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "MarkSmith.Desktop");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private static XDocument Load(string relative) => XDocument.Load(Path.Combine(DesktopDir(), relative));

    private static XElement SelectionMenu() =>
        Load(ShapeXaml).Descendants(Ui + "MenuFlyout").Single(m => (string?)m.Attribute(X + "Name") == "SelectionBoundsFlyout");

    // The menu that holds an item with this Click handler.
    private static XElement MenuHolding(string relative, string click) =>
        Load(relative).Descendants(Ui + "MenuFlyout").First(m => m.Descendants().Any(d => (string?)d.Attribute("Click") == click));

    private static XElement Diagnostics(XElement menu) =>
        menu.Elements(Ui + "MenuFlyoutSubItem").Single(s => (string?)s.Attribute("Text") == "Diagnostics");

    private static IEnumerable<XElement> Items(XElement parent) =>
        parent.Descendants().Where(e => e.Name.LocalName is "MenuFlyoutItem" or "ToggleMenuFlyoutItem");

    private static string Glyph(XElement item) =>
        (string)item.Descendants(Ui + "FontIcon").First().Attribute("Glyph")!;

    private static string Text(XElement item) => (string)item.Attribute("Text")!;

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n        }\n", start, StringComparison.Ordinal);
        return code[start..end];
    }

    public static IEnumerable<object[]> DiagnosticsMenus() => new[]
    {
        new object[] { ShapeXaml, "OnSelectionCenterCanvasClick" },
        new object[] { SmartArtXaml, "OnSelectRootNodeClick" },
        new object[] { GalaxyXaml, "OnLayoutTreeClick" },
    };

    [Fact]
    public void ShapeSelectionMenu_TopLevelIsShort_AndGroupsActionsIntoNamedSubmenus()
    {
        var menu = SelectionMenu();
        var top = menu.Elements().Where(e => e.Name.LocalName is "MenuFlyoutItem" or "MenuFlyoutSubItem").ToList();
        Assert.InRange(top.Count, 8, 12);

        var subs = menu.Elements(Ui + "MenuFlyoutSubItem").ToList();
        Assert.Equal(new[] { "Arrange", "Align & distribute", "Size & snap", "Rotate & flip", "Style", "Select similar", "Diagnostics" },
                     subs.Select(s => (string)s.Attribute("Text")!));
        foreach (var sub in subs.Where(s => (string?)s.Attribute("Text") != "Diagnostics"))
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)sub.Attribute(XName.Get("ToolTipService.ToolTip"))), (string)sub.Attribute("Text")!);
            Assert.NotEmpty(sub.Descendants(Ui + "FontIcon"));
        }

        // Shortcut hints travel with the items into the Arrange submenu.
        var arrange = subs.First();
        Assert.Equal(new[] { "Ctrl+]", "Ctrl+[", "Ctrl+Shift+]", "Ctrl+Shift+[" },
                     Items(arrange).Select(i => (string?)i.Attribute("KeyboardAcceleratorTextOverride")).Where(k => k != null));
    }

    [Fact]
    public void ShapeSelectionMenu_KeepsEveryAction_ExactlyOnce()
    {
        var clicks = Items(SelectionMenu()).Select(i => (string)i.Attribute("Click")!).ToList();
        Assert.Equal(clicks.Count, clicks.Distinct().Count());
        foreach (var c in new[] { "OnSelectionCenterCanvasClick", "OnSelectionBringForwardClick", "OnSelectionSendToBackClick",
                                  "OnSelectionDistributeHorizontallyClick", "OnSelectionDistributeEvenlyVertClick",
                                  "OnSelectionDistributeSpacingHorizontalClick", "OnSelectionFitInsideCanvasClick",
                                  "OnSelectionSnapToGridClick", "OnSelectionCycleShadowBlurClick", "OnSelectionSelectSimilarFillClick",
                                  "OnSelectionAuditOcclusionClick" })
            Assert.Contains(c, clicks);
        Assert.True(clicks.Count >= 100, $"only {clicks.Count} actions left");
    }

    [Fact]
    public void ShapeSelectionMenu_DistributeEntriesSayWhatEachDoes()
    {
        var sub = SelectionMenu().Elements(Ui + "MenuFlyoutSubItem").Single(s => (string?)s.Attribute("Text") == "Align & distribute");
        var labels = Items(sub).Select(Text).ToList();
        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("Space left edges evenly (horizontal)", labels);
        Assert.Contains("Space top edges evenly (vertical)", labels);
        Assert.Contains("Equal gaps between shapes (horizontal)", labels);
        Assert.Contains("Equal gaps between shapes (vertical)", labels);
        Assert.Contains("Equal gaps in whole pixels (horizontal)", labels);
        Assert.DoesNotContain(labels, l => l.Contains("(equal spacing)"));
    }

    [Fact]
    public void ShapeSelectionMenu_TopLevelGlyphsFitTheirActions()
    {
        var menu = SelectionMenu();
        foreach (var item in Items(menu).Where(i => !i.Ancestors(Ui + "MenuFlyoutSubItem").Any(a => (string?)a.Attribute("Text") == "Diagnostics")))
        {
            var glyph = Glyph(item);
            Assert.NotEqual("", glyph); // the star
            Assert.NotEqual("", glyph); // the picture
            if (glyph == "") Assert.Matches("(?i)colou?r", Text(item));
        }
    }

    [Theory]
    [MemberData(nameof(DiagnosticsMenus))]
    public void Diagnostics_UseFittingGlyphs_NotThePictureOrPaletteForEverything(string xaml, string anyClickInMenu)
    {
        var items = Items(Diagnostics(MenuHolding(xaml, anyClickInMenu))).ToList();
        Assert.True(items.Count > 20);
        Assert.DoesNotContain(items, i => Glyph(i) == "");
        Assert.All(items.Where(i => Glyph(i) == ""), i => Assert.Matches("(?i)colou?r|chromat|palette|fill", Text(i)));
        // No single glyph covers more than a third of the submenu.
        var most = items.GroupBy(Glyph).Max(g => g.Count());
        Assert.True(most * 3 <= items.Count, $"{most} of {items.Count} share one glyph in {xaml}");
    }

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "SmartArt", "Venn", "Bezier", "Tarjan", "Kuratowski", "Golden", "Ratio", "Spiral", "Minimum", "Spanning", "Tree",
    };

    [Theory]
    [MemberData(nameof(DiagnosticsMenus))]
    public void Diagnostics_LabelsAreSentenceCase(string xaml, string anyClickInMenu)
    {
        foreach (var item in Items(Diagnostics(MenuHolding(xaml, anyClickInMenu))))
        {
            var words = Text(item).Split(' ').Skip(1)
                .Select(w => w.Trim('(', ')', ',', '.', ':'))
                .Where(w => w.Length > 1 && char.IsUpper(w[0]) && !w.Skip(1).All(c => char.IsUpper(c) || char.IsDigit(c) || c is '/' or '-')
                            && !w.StartsWith("Z-", StringComparison.Ordinal) && !Acronyms.Contains(w))
                .ToList();
            Assert.True(words.Count < 2, $"Title Case label in {xaml}: \"{Text(item)}\"");
        }
    }

    [Theory]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionDistributeEvenlyHorizClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionDistributeEvenlyVertClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionDistributeRadialClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionDistributeSpacingHorizontalClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionFlipHorizontalClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionLockAspectRatioSquareClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionMatchSizeClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionNormalizePrecisionClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionQuantizeGridClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionResolveOverlapsClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionReverseZOrderClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionScaleProportionalClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionSelectSimilarFillClick")]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs", "OnSelectionSelectSimilarKindClick")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnAutoColorizeHierarchyClick")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnAttachOrphansClick")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnRollupSubtreeProgressClick")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnAutoColorizeByTagClick")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs", "OnNormalizeNodeCasingClick")]
    public void EditActions_ReportOnTheStatusLine_WithoutTouchingTheClipboard(string file, string handler)
    {
        var body = Method(Read(file), $"void {handler}(");
        Assert.Contains("ReportEdit(", body);
        Assert.DoesNotContain("CopyAndReport(", body);
        Assert.DoesNotContain("Clipboard", body);
    }

    [Theory]
    [InlineData("Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs")]
    [InlineData("Views/MindMap/MindMapGalaxyWindow.xaml.cs")]
    public void ReportEdit_SetsOnlyTheStatusLine_FirstLineOfTheSummary(string file)
    {
        var body = Method(Read(file), "private void ReportEdit(string? summary)");
        Assert.DoesNotContain("Clipboard", body);
        Assert.Contains("Split('\\n')[0]", body);
        Assert.Contains("ViewModel.StatusMessage = line;", body);
    }

    [Theory]
    [InlineData("OnApplyWorkspaceDialectNormalizationClick", "ApplyWorkspaceDialectNormalization(")]
    [InlineData("OnApplyWorkspaceMarkdownTableAutoAlignClick", "ApplyWorkspaceTableAutoAlignmentFixes(")]
    [InlineData("OnRestoreWorkspaceTableBackupsClick", "RestoreWorkspaceTableAutoAlignmentBackups(")]
    public void SuiteHub_FileRewrites_RunOneAtATime(string handler, string serviceCall)
    {
        var code = Read("Views/SuiteHubView.xaml.cs");
        var start = code.IndexOf($"private async void {handler}(", StringComparison.Ordinal);
        Assert.True(start >= 0, handler);
        var body = code[start..code.IndexOf("\n    private ", start + 10, StringComparison.Ordinal)];

        var firstGuard = body.IndexOf("if (RecentDocsRewriteIsRunning()) return;", StringComparison.Ordinal);
        var ask = body.IndexOf("ConfirmRecentDocsActionAsync(", StringComparison.Ordinal);
        var secondGuard = body.IndexOf("if (RecentDocsRewriteIsRunning()) return;", ask, StringComparison.Ordinal);
        var mark = body.IndexOf("_recentDocsRewriteRunning = true;", StringComparison.Ordinal);
        var run = body.IndexOf("Task.Run(() => RecentFilesService." + serviceCall, StringComparison.Ordinal);
        var clear = body.IndexOf("finally", run, StringComparison.Ordinal);
        Assert.True(firstGuard >= 0 && firstGuard < ask, "refuses before asking");
        Assert.True(secondGuard > ask && secondGuard < mark, "re-checks after the question");
        Assert.True(mark < run, "marks running before the work");
        Assert.True(clear > run && body.IndexOf("_recentDocsRewriteRunning = false;", clear, StringComparison.Ordinal) > clear, "clears in finally");

        var helper = code[code.IndexOf("private bool RecentDocsRewriteIsRunning()", StringComparison.Ordinal)..];
        helper = helper[..helper.IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Contains("SetNotification(", helper);
        Assert.Contains("success: false", helper);
    }
}
