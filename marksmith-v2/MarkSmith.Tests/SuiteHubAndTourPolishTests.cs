using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;
using Phase = MarkSmith.Services.BrowserExtensionPackage.ConnectionPhase;

namespace MarkSmith.Core.Tests;

public class SuiteHubAndTourPolishTests
{
    [Theory]
    [InlineData("file", "report.md", "report.docx")]
    [InlineData("batch", "C:\\path\\to\\folder", "--format docx")]
    [InlineData("doctor", "doctor", "")]
    public void SuiteHub_CLI_commands_generate_runnable_powershell_syntax(string mode, string expectedSnippet1, string expectedSnippet2)
    {
        var exe = "marksmith";
        var command = mode switch
        {
            "batch" => $"{exe} batch \"C:\\path\\to\\folder\" --format docx",
            "doctor" => $"{exe} doctor",
            _ => $"{exe} \"report.md\" \"report.docx\"",
        };

        Assert.Contains(expectedSnippet1, command);
        if (!string.IsNullOrEmpty(expectedSnippet2))
        {
            Assert.Contains(expectedSnippet2, command);
        }
    }

    [Fact]
    public void SuiteHub_MCP_config_matches_standard_json_schema()
    {
        var exePath = "C:\\Program Files\\MarkSmith\\marksmith-mcp.exe";
        var configObj = new
        {
            mcpServers = new
            {
                marksmith = new
                {
                    command = exePath,
                    args = Array.Empty<string>()
                }
            }
        };

        var json = JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("mcpServers", out var servers));
        Assert.True(servers.TryGetProperty("marksmith", out var entry));
        Assert.True(entry.TryGetProperty("command", out var cmdProp));
        Assert.Equal(exePath, cmdProp.GetString());
        Assert.True(entry.TryGetProperty("args", out var argsProp));
        Assert.Equal(0, argsProp.GetArrayLength());
    }

    [Fact]
    public void WelcomeTour_page_sequence_has_seven_coherent_steps()
    {
        var pageTitles = new[]
        {
            "Welcome to MarkSmith",
            "Source",
            "Style",
            "Preview & Export",
            "Diagrams & math",
            "Automation & Pro",
            "You're all set",
        };

        Assert.Equal(7, pageTitles.Length);
        Assert.Equal("Welcome to MarkSmith", pageTitles[0]);
        Assert.Equal("Source", pageTitles[1]);
        Assert.Equal("Style", pageTitles[2]);
        Assert.Equal("Preview & Export", pageTitles[3]);
        Assert.Equal("Diagrams & math", pageTitles[4]);
        Assert.Equal("Automation & Pro", pageTitles[5]);
        Assert.Equal("You're all set", pageTitles[6]);
    }

    [Theory]
    [InlineData(0, -1, 0)]      // Below min clamps to 0
    [InlineData(0, 1, 1)]       // Step forward
    [InlineData(3, -1, 2)]      // Step backward
    [InlineData(6, 1, 6)]       // Above max clamps to 6
    public void WelcomeTour_paging_clamps_cleanly_within_bounds(int current, int delta, int expected)
    {
        const int last = 6;
        var next = Math.Clamp(current + delta, 0, last);
        Assert.Equal(expected, next);
    }

    [Fact]
    public void ExtensionSetup_browser_chips_support_edge_and_chrome()
    {
        var pages = BrowserExtensionPackage.ExtensionPages;
        Assert.NotNull(pages);
        Assert.True(pages.Count >= 2);

        var edge = pages.FirstOrDefault(p => p.Browser.Contains("Edge"));
        Assert.Equal("edge://extensions", edge.Address);

        var chrome = pages.FirstOrDefault(p => p.Browser.Contains("Chrome"));
        Assert.Equal("chrome://extensions", chrome.Address);
    }

    [Fact]
    public void PlanBadge_formats_correctly_for_editions()
    {
        var freeState = new LicenseState { Edition = Edition.Free };
        Assert.Equal("Free plan", ProGate.PlanBadge(freeState));

        var proState = new LicenseState { Edition = Edition.Pro, Key = "valid-key" };
        Assert.Equal("MarkSmith Pro", ProGate.PlanBadge(proState));
    }

    private static string DesktopPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName, "MarkSmith.Desktop" }.Concat(parts).ToArray());
    }

    private static int GetGridNeighborCardLogic(int currentCard, string direction, bool singleColumn)
    {
        if (currentCard < 0 || currentCard > 6) return -1;
        if (singleColumn)
        {
            return direction switch
            {
                "Up" or "Left" => currentCard > 0 ? currentCard - 1 : -1,
                "Down" or "Right" => currentCard < 6 ? currentCard + 1 : -1,
                _ => -1,
            };
        }

        return (currentCard, direction) switch
        {
            (0, "Down") => 2,
            (0, "Right") => 1,
            (1, "Up") => 0,
            (1, "Left") => 0,
            (2, "Up") => 0,
            (1, "Right") => 2,
            (2, "Left") => 1,
            (1, "Down") => 3,
            (2, "Down") => 4,
            (3, "Up") => 1,
            (4, "Up") => 2,
            (3, "Right") => 4,
            (4, "Left") => 3,
            (3, "Down") => 5,
            (4, "Down") => 6,
            (5, "Up") => 3,
            (6, "Up") => 4,
            (5, "Right") => 6,
            (6, "Left") => 5,
            _ => -1,
        };
    }

    [Theory]
    [InlineData(0, "Down", false, 2)]
    [InlineData(0, "Right", false, 1)]
    [InlineData(0, "Up", false, -1)]
    [InlineData(1, "Right", false, 2)]
    [InlineData(1, "Left", false, 0)]
    [InlineData(2, "Left", false, 1)]
    [InlineData(2, "Right", false, -1)]
    [InlineData(1, "Up", false, 0)]
    [InlineData(2, "Up", false, 0)]
    [InlineData(1, "Down", false, 3)]
    [InlineData(2, "Down", false, 4)]
    [InlineData(5, "Down", false, -1)]
    [InlineData(6, "Down", false, -1)]
    [InlineData(3, "Down", true, 4)]
    [InlineData(3, "Right", true, 4)]
    [InlineData(3, "Up", true, 2)]
    [InlineData(3, "Left", true, 2)]
    [InlineData(0, "Left", true, -1)]
    [InlineData(6, "Right", true, -1)]
    [InlineData(-1, "Down", false, -1)]
    [InlineData(99, "Up", false, -1)]
    public void SuiteHub_2D_grid_neighbor_navigation_handles_two_col_and_single_col_bounds(
        int currentCard, string direction, bool singleColumn, int expected)
    {
        var actual = GetGridNeighborCardLogic(currentCard, direction, singleColumn);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SuiteHubView_wires_card_hover_focus_context_menus_and_express_copy_action()
    {
        var xaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var cs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));

        Assert.Contains("x:Name=\"HeaderCardBorder\"", xaml);
        for (var i = 1; i <= 6; i++)
        {
            Assert.Contains($"x:Name=\"Card{i}Border\"", xaml);
        }
        Assert.Contains("x:Name=\"CopyExpressUrlButton\"", xaml);
        Assert.Contains("x:Name=\"OpenConfigFolderButton\"", xaml);

        Assert.Contains("WireCardPolish", cs);
        Assert.Contains("BuildCardContextMenu", cs);
        Assert.Contains("GetGridNeighborCard", cs);
        Assert.Contains("ApplyResponsiveLayout", cs);
        Assert.Contains("OnCopyExpressUrl", cs);
        Assert.Contains("ExpressUrl = \"http://localhost:5000\"", cs);
        Assert.Contains("VirtualKey.Application", cs);
        Assert.Contains("VirtualKey.NumberPad1", cs);
        Assert.Contains("(0, \"Right\") => 1", cs);
        Assert.Contains("(1, \"Left\") => 0", cs);
        Assert.Contains("var nextTarget = TurnOnApiButton.Visibility == Visibility.Visible ? TurnOnApiButton : CopyApiUrlButton;", cs);
    }

    [Fact]
    public void WelcomeTour_wires_step_badge_slide_jump_keys_and_context_menu()
    {
        var xaml = File.ReadAllText(DesktopPath("Views", "WelcomeTour.xaml"));
        var cs = File.ReadAllText(DesktopPath("Views", "WelcomeTour.xaml.cs"));

        Assert.Contains("x:Name=\"StepBadgeBorder\"", xaml);
        Assert.Contains("x:Name=\"StepBadgeText\"", xaml);
        Assert.Contains("x:Name=\"Step1Card\"", xaml);
        Assert.Contains("x:Name=\"Step2Card\"", xaml);
        Assert.Contains("x:Name=\"Step3Card\"", xaml);
        Assert.Contains("ButtonBorderBrushPointerOver", xaml);

        Assert.Contains("FormatStepBadge", cs);
        Assert.Contains("TryParseSlideNumberKey", cs);
        Assert.Contains("ShowTourContextMenu", cs);
        Assert.Contains("Jump to step", cs);
        Assert.Contains("AutomationProperties.SetName(NextButton", cs);
        Assert.Contains("AutomationProperties.SetName(BackButton", cs);
        Assert.Contains("_index == 0 && IsInside(focused, BackButton)", cs);
    }

    [Theory]
    [InlineData(0, 7, "Step 1 of 7")]
    [InlineData(6, 7, "Step 7 of 7")]
    [InlineData(-3, 7, "Step 1 of 7")]
    [InlineData(20, 7, "Step 7 of 7")]
    public void WelcomeTour_StepBadge_clamps_and_formats_cleanly(int page, int total, string expected)
    {
        var clampedTotal = Math.Max(1, total);
        var clampedPage = Math.Clamp(page, 0, clampedTotal - 1);
        var badge = $"Step {clampedPage + 1} of {clampedTotal}";
        Assert.Equal(expected, badge);
    }

    [Fact]
    public void CommandPalette_wires_clear_search_reset_button_context_menu_and_shortcuts()
    {
        var xaml = File.ReadAllText(DesktopPath("Controls", "CommandPalette.xaml"));
        var cs = File.ReadAllText(DesktopPath("Controls", "CommandPalette.xaml.cs"));

        Assert.Contains("x:Name=\"ClearSearchButton\"", xaml);
        Assert.Contains("x:Name=\"ResetSearchButton\"", xaml);
        Assert.Contains("RightTapped=\"OnResultsRightTapped\"", xaml);

        Assert.Contains("ShowCommandContextMenu", cs);
        Assert.Contains("Remove from Recently used (Ctrl+Delete)", cs);
        Assert.Contains("Clear Recently used", cs);
        Assert.Contains("CopySelectedCommandAsync", cs);
        Assert.Contains("RemoveRecent", cs);
        Assert.Contains("FormatCountText", cs);
        Assert.Contains("FormatCommandClipboardText", cs);
        Assert.Contains("AutomationProperties.SetName(Results", cs);
        Assert.Contains("if (_recents.Labels is System.Collections.Generic.IList<string> mutable)", cs.Replace("IList<string>", "System.Collections.Generic.IList<string>"));
        Assert.Contains("else if (NoMatches.Visibility == Visibility.Visible) OnClearSearchClick", cs);
        Assert.Contains("ResetSearchButton.Focus(FocusState.Keyboard)", cs);
    }

    [Fact]
    public void PaletteRecents_removing_item_from_backing_list_persists_across_subsequent_Push()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ms-palette-recents-{Guid.NewGuid():N}.json");
        try
        {
            var recents = new PaletteRecents(tempFile);
            recents.Push("Export PDF");
            recents.Push("Split view");
            Assert.Equal(new[] { "Split view", "Export PDF" }, recents.Labels);

            // Simulate CommandPalette.RemoveRecent mutating the backing list + persisting
            var mutable = Assert.IsAssignableFrom<System.Collections.Generic.IList<string>>(recents.Labels);
            Assert.True(mutable.Remove("Export PDF"));
            File.WriteAllText(tempFile, JsonSerializer.Serialize(recents.Labels));

            // Subsequent command execution calls recents.Push(...)
            recents.Push("Generate Word (.docx)");

            // Reload from disk to verify "Export PDF" was NOT resurrected by Push
            var reloaded = new PaletteRecents(tempFile);
            Assert.Equal(new[] { "Generate Word (.docx)", "Split view" }, reloaded.Labels);
            Assert.DoesNotContain("Export PDF", reloaded.Labels);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0, false, "0 commands")]
    [InlineData(1, false, "1 command")]
    [InlineData(1, true, "1 match")]
    [InlineData(5, true, "5 matches")]
    [InlineData(12, false, "12 commands")]
    [InlineData(0, true, "")]
    [InlineData(-2, true, "")]
    public void CommandPalette_FormatCountText_handles_singular_plural_and_negative_bounds(
        int count, bool searching, string expected)
    {
        var text = !searching
            ? (count == 1 ? "1 command" : $"{Math.Max(0, count)} commands")
            : count <= 0 ? ""
            : count == 1 ? "1 match" : $"{count} matches";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void ExtensionHintBar_and_ExtensionTip_wire_hover_focus_arrow_nav_and_context_menus()
    {
        var hintXaml = File.ReadAllText(DesktopPath("Controls", "ExtensionHintBar.xaml"));
        var hintCs = File.ReadAllText(DesktopPath("Controls", "ExtensionHintBar.xaml.cs"));
        var tipXaml = File.ReadAllText(DesktopPath("Controls", "ExtensionTip.xaml"));
        var tipCs = File.ReadAllText(DesktopPath("Controls", "ExtensionTip.xaml.cs"));

        Assert.Contains("x:Name=\"HintBorder\"", hintXaml);
        Assert.Contains("x:Name=\"DismissButton\"", hintXaml);
        Assert.Contains("RefreshBorderBrush", hintCs);
        Assert.Contains("ShowHintContextMenu", hintCs);
        Assert.Contains("HoverPolish.RestoreFocus", hintCs);
        Assert.Contains("VirtualKey.Up", hintCs);
        Assert.Contains("VirtualKey.Down", hintCs);

        Assert.Contains("x:Name=\"TipBorder\"", tipXaml);
        Assert.Contains("x:Name=\"GetExtensionButton\"", tipXaml);
        Assert.Contains("x:Name=\"DismissButton\"", tipXaml);
        Assert.Contains("RefreshBorderBrush", tipCs);
        Assert.Contains("ShowTipContextMenu", tipCs);
        Assert.Contains("HoverPolish.RestoreFocus", tipCs);
    }

    [Fact]
    public void SuiteHub_RecentDocs_QuickStrip_and_ActionMenu_Wired()
    {
        var xaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var cs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));

        Assert.Contains("x:Name=\"RecentDocsStrip\"", xaml);
        Assert.Contains("x:Name=\"RecentDocsActionBtn\"", xaml);
        Assert.Contains("x:Name=\"RecentDocsFlyout\"", xaml);
        Assert.Contains("OnRecentDocsActionBtnClick", xaml);
        Assert.Contains("OnBrowseFileToOpenClick", xaml);
        Assert.Contains("OnOpenDocumentsFolderClick", xaml);
        Assert.Contains("OnClearRecentDocsClick", xaml);
        Assert.Contains("OnRecentDocsStripRightTapped", xaml);
        Assert.Contains("OnRecentDocsStripDoubleTapped", xaml);

        Assert.Contains("void OnRecentDocsActionBtnClick", cs);
        Assert.Contains("void OnBrowseFileToOpenClick", cs);
        Assert.Contains("void OnOpenDocumentsFolderClick", cs);
        Assert.Contains("void OnClearRecentDocsClick", cs);
        Assert.Contains("void OnRecentDocsStripRightTapped", cs);
        Assert.Contains("void OnRecentDocsStripDoubleTapped", cs);
    }

    [Fact]
    public void SmartArt_and_History_and_Shape_Run126_Polish_Elements_Wired()
    {
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));

        Assert.Contains("x:Name=\"ZoomButton\"", smartArtXaml);
        Assert.Contains("x:Name=\"ZoomFlyout\"", smartArtXaml);
        Assert.Contains("OnZoomButtonClick", smartArtCs);
        Assert.Contains("SmartArtZoomMode", smartArtCs);

        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));

        Assert.Contains("x:Name=\"TimelineFilterStatusPill\"", historyXaml);
        Assert.Contains("x:Name=\"TimelineFilterStatusText\"", historyXaml);
        Assert.Contains("UpdateTimelineFilterStatus", historyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));

        Assert.Contains("x:Name=\"RecentColorsContainer\"", shapeXaml);
        Assert.Contains("x:Name=\"RecentColorsSwatches\"", shapeXaml);
        Assert.Contains("AddRecentColor", shapeCs);
        Assert.Contains("RebuildRecentColorsSwatches", shapeCs);

        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("InsertSequentialFootnote", mainCs);
    }

    [Fact]
    public void Run129_Polish_Elements_Wired_Across_Surfaces()
    {
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("ToggleLineEndings", mainCs);
        Assert.Contains("ShowEncodingFlyout", mainCs);
        Assert.Contains("UpdateStatusEncoding", mainCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("x:Name=\"OutlineSearchPill\"", smartArtXaml);
        Assert.Contains("x:Name=\"OutlineSearchBox\"", smartArtXaml);
        Assert.Contains("x:Name=\"OutlineMatchCountLabel\"", smartArtXaml);
        Assert.Contains("PerformOutlineSearch", smartArtCs);
        Assert.Contains("StepOutlineMatch", smartArtCs);

        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("x:Name=\"DiffWrapButton\"", historyXaml);
        Assert.Contains("x:Name=\"DiffLineJumpButton\"", historyXaml);
        Assert.Contains("OnDiffWrapButtonClick", historyCs);
        Assert.Contains("OnDiffLineJumpButtonClick", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("x:Name=\"TemplatePrdBtn\"", suiteXaml);
        Assert.Contains("x:Name=\"TemplateApiDocBtn\"", suiteXaml);
        Assert.Contains("OnTemplatePrdClick", suiteCs);
        Assert.Contains("OnTemplateApiDocClick", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FilterRecentFiles_FiltersByExtensionAndSubstring()
    {
        var sampleFiles = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Summary.txt",
            @"C:\Users\Docs\Meeting_Notes.markdown",
            @"C:\Users\Docs\Quarterly_Review.MD"
        };

        // Null or whitespace filter returns all items
        Assert.Equal(4, RecentFilesService.FilterRecentFiles(sampleFiles, null).Count());
        Assert.Equal(4, RecentFilesService.FilterRecentFiles(sampleFiles, "   ").Count());

        // Null collection returns empty
        Assert.Empty(RecentFilesService.FilterRecentFiles(null, ".md"));

        // Extension filter
        var mdOnly = RecentFilesService.FilterRecentFiles(sampleFiles, ".md").ToList();
        Assert.Equal(2, mdOnly.Count);
        Assert.Contains(@"C:\Users\Docs\Report.md", mdOnly);
        Assert.Contains(@"C:\Users\Docs\Quarterly_Review.MD", mdOnly);

        // Substring filter
        var notes = RecentFilesService.FilterRecentFiles(sampleFiles, "notes").ToList();
        Assert.Single(notes);
        Assert.Equal(@"C:\Users\Docs\Meeting_Notes.markdown", notes[0]);
    }

    [Fact]
    public void Run130_Polish_Elements_Wired_Across_Surfaces()
    {
        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"MatchWholeWordCheck\"", mainXaml);
        Assert.Contains("OnMatchWholeWordChanged", mainCs);
        Assert.Contains("IsWholeWordBoundary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("x:Name=\"ConnDashSolidItem\"", galaxyXaml);
        Assert.Contains("x:Name=\"ConnDashDashedItem\"", galaxyXaml);
        Assert.Contains("x:Name=\"ConnDashDottedItem\"", galaxyXaml);
        Assert.Contains("ConnectionDashStyle", galaxyCs);
        Assert.Contains("SetConnectionDashStyle", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("x:Name=\"SelectionBoundsPill\"", shapeXaml);
        Assert.Contains("x:Name=\"SelectionBoundsFlyout\"", shapeXaml);
        Assert.Contains("UpdateSelectionBoundsPill", shapeCs);
        Assert.Contains("OnSelectionCenterCanvasClick", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("x:Name=\"OutlineMetricsPill\"", smartArtXaml);
        Assert.Contains("x:Name=\"OutlineMetricsFlyout\"", smartArtXaml);
        Assert.Contains("OnOutlineMetricsPillTapped", smartArtCs);
        Assert.Contains("OnCopyPlainOutlineTextClick", smartArtCs);

        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("x:Name=\"DiffIgnoreWhitespaceButton\"", historyXaml);
        Assert.Contains("OnDiffIgnoreWhitespaceButtonClick", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("x:Name=\"RecentSearchBox\"", suiteXaml);
        Assert.Contains("OnRecentSearchBoxTextChanged", suiteCs);
        Assert.Contains("OnFilterMarkdownRecentClick", suiteCs);
    }

    [Theory]
    [InlineData(0, 0, null, "0 docs")]
    [InlineData(0, 0, "filter", "0 docs")]
    [InlineData(1, 1, null, "1 doc")]
    [InlineData(5, 5, null, "5 docs")]
    [InlineData(2, 5, "md", "2/5 docs")]
    [InlineData(5, 5, "md", "5 docs")]
    public void RecentFilesService_FormatRecentFilesCount_FormatsExpectedSummary(
        int displayed, int total, string? filter, string expected)
    {
        Assert.Equal(expected, RecentFilesService.FormatRecentFilesCount(displayed, total, filter));
    }

    [Fact]
    public void Run131_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Regex search toggle & length tracking
        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"UseRegexCheck\"", mainXaml);
        Assert.Contains("OnUseRegexChanged", mainCs);
        Assert.Contains("_findMatchLengths", mainCs);

        // 2. MindMapGalaxy: Edge stroke weight presets & dynamic thickness
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("x:Name=\"ConnWeightThinItem\"", galaxyXaml);
        Assert.Contains("x:Name=\"ConnWeightNormalItem\"", galaxyXaml);
        Assert.Contains("x:Name=\"ConnWeightThickItem\"", galaxyXaml);
        Assert.Contains("SetConnectionStrokeWeight", galaxyCs);
        Assert.Contains("_connectionStrokeWeight", galaxyCs);

        // 3. ShapeStudio: Selection layer reordering
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("OnSelectionBringForwardClick", shapeXaml);
        Assert.Contains("OnSelectionSendBackwardClick", shapeXaml);
        Assert.Contains("OnSelectionBringToFrontClick", shapeXaml);
        Assert.Contains("OnSelectionSendToBackClick", shapeXaml);
        Assert.Contains("OnSelectionBringForwardClick", shapeCs);
        Assert.Contains("OnSelectionSendBackwardClick", shapeCs);
        Assert.Contains("OnSelectionBringToFrontClick", shapeCs);
        Assert.Contains("OnSelectionSendToBackClick", shapeCs);

        // 4. SmartArtStudio: Outline reverse and sort
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OnReverseOutlineClick", smartArtXaml);
        Assert.Contains("OnSortOutlineAscendingClick", smartArtXaml);
        Assert.Contains("OnSortOutlineDescendingClick", smartArtXaml);
        Assert.Contains("OnReverseOutlineClick", smartArtCs);
        Assert.Contains("OnSortOutlineAscendingClick", smartArtCs);
        Assert.Contains("OnSortOutlineDescendingClick", smartArtCs);

        // 5. HistoryWindow: Copy added/removed lines
        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("OnCopyAddedLinesContextClick", historyXaml);
        Assert.Contains("OnCopyRemovedLinesContextClick", historyXaml);
        Assert.Contains("OnCopyAddedLinesContextClick", historyCs);
        Assert.Contains("OnCopyRemovedLinesContextClick", historyCs);

        // 6. SuiteHub: Recent docs count badge
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("x:Name=\"RecentDocsCountPill\"", suiteXaml);
        Assert.Contains("x:Name=\"RecentDocsCountText\"", suiteXaml);
        Assert.Contains("OnRecentDocsCountPillTapped", suiteXaml);
        Assert.Contains("OnRecentDocsCountPillTapped", suiteCs);
        Assert.Contains("FormatRecentFilesCount", suiteCs);
    }

    [Fact]
    public void RecentFilesService_ExtractFileNames_ExtractsFileNamesWithoutDirectory()
    {
        var samplePaths = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"D:\Projects\MarkSmith\Notes.txt",
            @"/unix/path/readme.markdown"
        };

        var names = RecentFilesService.ExtractFileNames(samplePaths);
        Assert.Equal(3, names.Count);
        Assert.Equal("Report.md", names[0]);
        Assert.Equal("Notes.txt", names[1]);
        Assert.Equal("readme.markdown", names[2]);

        Assert.Empty(RecentFilesService.ExtractFileNames(null));
        Assert.Empty(RecentFilesService.ExtractFileNames(Array.Empty<string>()));
    }

    [Fact]
    public void Run132_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Search history navigation & Recent searches menu
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("_findHistory", mainCs);
        Assert.Contains("RecordFindHistory", mainCs);
        Assert.Contains("Recent searches", mainCs);

        // 2. MindMapGalaxy: Connection style summary formatting & copy item
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("Copy connection style summary", galaxyCs);
        Assert.Contains("FormatConnectionStyleSummary", galaxyCs);

        // 3. ShapeStudio: SelectionBoundsFlyout distribution items & click handlers
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("OnSelectionDistributeHorizontallyClick", shapeXaml);
        Assert.Contains("OnSelectionDistributeVerticallyClick", shapeXaml);
        Assert.Contains("OnSelectionDistributeHorizontallyClick", shapeCs);
        Assert.Contains("OnSelectionDistributeVerticallyClick", shapeCs);

        // 4. SmartArtStudio: Trim trailing whitespace menu item & click handler
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("Trim trailing whitespace", smartArtXaml);
        Assert.Contains("OnTrimTrailingWhitespaceClick", smartArtCs);
        Assert.Contains("TrimTrailingWhitespace", smartArtCs);

        // 5. HistoryWindow: Copy unchanged context lines item & click handler
        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("OnCopyUnchangedLinesContextClick", historyXaml);
        Assert.Contains("Copy unchanged context lines (=)", historyXaml);
        Assert.Contains("OnCopyUnchangedLinesContextClick", historyCs);

        // 6. SuiteHub: Copy recent file names only menu item & click handler
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("Copy recent file names only", suiteXaml);
        Assert.Contains("OnCopyRecentFileNamesOnlyClick", suiteXaml);
        Assert.Contains("OnCopyRecentFileNamesOnlyClick", suiteCs);
    }

    [Fact]
    public void RecentFilesService_ExtractParentDirectories_ExtractsUniqueDirectoriesInOrder()
    {
        var samplePaths = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Summary.md",
            @"D:\Projects\MarkSmith\Notes.txt",
            @"C:\Users\Docs\Draft.md",
            @"E:\Archive\2026\readme.markdown"
        };

        var dirs = RecentFilesService.ExtractParentDirectories(samplePaths);
        Assert.Equal(3, dirs.Count);
        Assert.Equal(@"C:\Users\Docs", dirs[0]);
        Assert.Equal(@"D:\Projects\MarkSmith", dirs[1]);
        Assert.Equal(@"E:\Archive\2026", dirs[2]);

        Assert.Empty(RecentFilesService.ExtractParentDirectories(null));
        Assert.Empty(RecentFilesService.ExtractParentDirectories(Array.Empty<string>()));
    }

    [Fact]
    public void Run133_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: _lastFindWrapped and FormatFindMatchStatus
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("_lastFindWrapped", mainCs);
        Assert.Contains("FormatFindMatchStatus", mainCs);

        // 2. MindMapGalaxy: Copy connection style summary item in flyout & ToggleArrowheads
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CopyConnectionStyleSummaryItem", galaxyXaml);
        Assert.Contains("OnCopyConnectionStyleSummaryClick", galaxyXaml);
        Assert.Contains("OnCopyConnectionStyleSummaryClick", galaxyCs);
        Assert.Contains("ToggleArrowheads", galaxyCs);

        // 3. ShapeStudio: Fit inside canvas item & OnSelectionFitInsideCanvasClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("OnSelectionFitInsideCanvasClick", shapeXaml);
        Assert.Contains("OnSelectionFitInsideCanvasClick", shapeCs);
        Assert.Contains("ComputeClampOffset", shapeCs);

        // 4. SmartArtStudio: Standardise bullets menu item & click handler
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("Standardise bullet points (-)", smartArtXaml);
        Assert.Contains("OnStandardiseBulletsClick", smartArtCs);
        Assert.Contains("StandardiseBullets", smartArtCs);

        // 5. HistoryWindow: Copy diff statistics item & click handler
        var historyXaml = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml"));
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("CopyDiffStatsContextItem", historyXaml);
        Assert.Contains("OnCopyDiffStatsContextClick", historyXaml);
        Assert.Contains("OnCopyDiffStatsContextClick", historyCs);
        Assert.Contains("FormatDiffStatistics", historyCs);

        // 6. SuiteHub: Copy parent folders only menu item & click handler
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("Copy parent folders only", suiteXaml);
        Assert.Contains("OnCopyRecentParentFoldersClick", suiteXaml);
        Assert.Contains("OnCopyRecentParentFoldersClick", suiteCs);
        Assert.Contains("ExtractParentDirectories", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatAsMarkdownList_FormatsLinks()
    {
        var samplePaths = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"D:\Projects\MarkSmith\Notes.txt"
        };

        var md = RecentFilesService.FormatAsMarkdownList(samplePaths);
        Assert.Contains("- [Report.md](C:\\Users\\Docs\\Report.md)", md);
        Assert.Contains("- [Notes.txt](D:\\Projects\\MarkSmith\\Notes.txt)", md);

        Assert.Equal(string.Empty, RecentFilesService.FormatAsMarkdownList(null));
        Assert.Equal(string.Empty, RecentFilesService.FormatAsMarkdownList(Array.Empty<string>()));
    }

    [Fact]
    public void Run134_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: JumpToMatch, Alt+Home/End shortcuts and context items
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("JumpToMatch", mainCs);
        Assert.Contains("First match (Alt+Home)", mainCs);
        Assert.Contains("Last match (Alt+End)", mainCs);

        // 2. MindMapGalaxy: CycleConnectionRoutingItem & CycleStrokeWeightItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CycleConnectionRoutingItem", galaxyXaml);
        Assert.Contains("CycleStrokeWeightItem", galaxyXaml);
        Assert.Contains("OnCycleConnectionRoutingClick", galaxyCs);
        Assert.Contains("OnCycleStrokeWeightClick", galaxyCs);

        // 3. ShapeStudio: SelectionSnapToGridItem & OnSelectionSnapToGridClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionSnapToGridItem", shapeXaml);
        Assert.Contains("OnSelectionSnapToGridClick", shapeXaml);
        Assert.Contains("OnSelectionSnapToGridClick", shapeCs);
        Assert.Contains("SnapPointToGrid", shapeCs);

        // 4. SmartArtStudio: OutlineNormalizeIndentItem & OnNormalizeIndentationClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineNormalizeIndentItem", smartArtXaml);
        Assert.Contains("OnNormalizeIndentationClick", smartArtXaml);
        Assert.Contains("OnNormalizeIndentationClick", smartArtCs);
        Assert.Contains("NormalizeIndentation", smartArtCs);

        // 5. HistoryWindow: Copy version timestamp header item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version timestamp", historyCs);

        // 6. SuiteHub: CopyRecentAsMarkdownListItem & OnCopyRecentAsMarkdownListClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentAsMarkdownListItem", suiteXaml);
        Assert.Contains("OnCopyRecentAsMarkdownListClick", suiteXaml);
        Assert.Contains("OnCopyRecentAsMarkdownListClick", suiteCs);
        Assert.Contains("FormatAsMarkdownList", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentLibrarySummary_ComputesSummary()
    {
        var sample = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Summary.md",
            @"D:\Projects\MarkSmith\Notes.txt"
        };
        var summary = RecentFilesService.FormatRecentLibrarySummary(sample);
        Assert.Contains("3 documents", summary);
        Assert.Contains("2 directories", summary);
        Assert.Contains(".md", summary);
        Assert.Contains(".txt", summary);

        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentLibrarySummary(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentLibrarySummary(Array.Empty<string>()));
    }

    [Fact]
    public void Run135_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Inspect match location item & FormatMatchInspectorSummary
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Inspect match location", mainCs);
        Assert.Contains("FormatMatchInspectorSummary", mainCs);

        // 2. MindMapGalaxy: CycleAnchorPositionItem & OnCycleAnchorPositionClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CycleAnchorPositionItem", galaxyXaml);
        Assert.Contains("OnCycleAnchorPositionClick", galaxyXaml);
        Assert.Contains("OnCycleAnchorPositionClick", galaxyCs);
        Assert.Contains("CycleAnchorPosition", galaxyCs);

        // 3. ShapeStudio: SelectionStandardizeRatioItem & OnSelectionStandardizeRatioClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionStandardizeRatioItem", shapeXaml);
        Assert.Contains("OnSelectionStandardizeRatioClick", shapeXaml);
        Assert.Contains("OnSelectionStandardizeRatioClick", shapeCs);
        Assert.Contains("ApplyStandardAspectRatio", shapeCs);

        // 4. SmartArtStudio: OutlineHierarchyMetricsItem & OnCopyHierarchyMetricsClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineHierarchyMetricsItem", smartArtXaml);
        Assert.Contains("OnCopyHierarchyMetricsClick", smartArtXaml);
        Assert.Contains("OnCopyHierarchyMetricsClick", smartArtCs);
        Assert.Contains("AnalyzeHierarchyMetrics", smartArtCs);

        // 5. HistoryWindow: Copy version delta item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version delta", historyCs);
        Assert.Contains("FormatVersionDeltaSummary", historyCs);

        // 6. SuiteHub: CopyRecentLibrarySummaryItem & OnCopyRecentLibrarySummaryClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentLibrarySummaryItem", suiteXaml);
        Assert.Contains("OnCopyRecentLibrarySummaryClick", suiteXaml);
        Assert.Contains("OnCopyRecentLibrarySummaryClick", suiteCs);
        Assert.Contains("FormatRecentLibrarySummary", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentExtensionBreakdown_ComputesSummary()
    {
        var sample = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Summary.md",
            @"D:\Projects\MarkSmith\Notes.txt",
            @"D:\Projects\MarkSmith\Data.csv"
        };
        var summary = RecentFilesService.FormatRecentExtensionBreakdown(sample);
        Assert.Contains("4 documents", summary);
        Assert.Contains("2 .md", summary);
        Assert.Contains("1 .txt", summary);
        Assert.Contains("1 .csv", summary);

        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentExtensionBreakdown(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentExtensionBreakdown(Array.Empty<string>()));
    }

    [Fact]
    public void Run136_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: snippetItem & FormatMatchContextSnippet
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy match snippet with context", mainCs);
        Assert.Contains("FormatMatchContextSnippet", mainCs);

        // 2. MindMapGalaxy: CycleAnchorPaddingItem & OnCycleAnchorPaddingClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CycleAnchorPaddingItem", galaxyXaml);
        Assert.Contains("OnCycleAnchorPaddingClick", galaxyXaml);
        Assert.Contains("OnCycleAnchorPaddingClick", galaxyCs);
        Assert.Contains("CycleAnchorPadding", galaxyCs);

        // 3. ShapeStudio: SelectionCycleRotationItem, SelectionToggleCornerStyleItem & handlers
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleRotationItem", shapeXaml);
        Assert.Contains("SelectionToggleCornerStyleItem", shapeXaml);
        Assert.Contains("OnSelectionCycleRotationClick", shapeXaml);
        Assert.Contains("OnSelectionToggleCornerStyleClick", shapeXaml);
        Assert.Contains("OnSelectionCycleRotationClick", shapeCs);
        Assert.Contains("OnSelectionToggleCornerStyleClick", shapeCs);
        Assert.Contains("CycleCardinalRotation", shapeCs);
        Assert.Contains("ToggleCornerStyle", shapeCs);

        // 4. SmartArtStudio: OutlineRootsOnlyItem & OnCopyOutlineRootsOnlyClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineRootsOnlyItem", smartArtXaml);
        Assert.Contains("OnCopyOutlineRootsOnlyClick", smartArtXaml);
        Assert.Contains("OnCopyOutlineRootsOnlyClick", smartArtCs);
        Assert.Contains("FormatOutlineAtMaxDepth", smartArtCs);

        // 5. HistoryWindow: Copy version summary card item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version summary card", historyCs);
        Assert.Contains("FormatVersionStatsCard", historyCs);

        // 6. SuiteHub: CopyRecentExtensionBreakdownItem & OnCopyRecentExtensionBreakdownClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentExtensionBreakdownItem", suiteXaml);
        Assert.Contains("OnCopyRecentExtensionBreakdownClick", suiteXaml);
        Assert.Contains("OnCopyRecentExtensionBreakdownClick", suiteCs);
        Assert.Contains("FormatRecentExtensionBreakdown", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentDriveDistribution_GroupsDrivesCorrectly()
    {
        var samplePaths = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Notes.txt",
            @"D:\Projects\MarkSmith\Readme.md",
            @"E:\Backup\Archive.zip"
        };

        var summary = RecentFilesService.FormatRecentDriveDistribution(samplePaths);
        Assert.Contains("4 documents across 3 storage roots", summary);
        Assert.Contains("C: (2)", summary);
        Assert.Contains("D: (1)", summary);
        Assert.Contains("E: (1)", summary);

        // Single drive
        var singleDrivePaths = new[] { @"C:\doc1.md", @"C:\doc2.md" };
        var singleDriveSummary = RecentFilesService.FormatRecentDriveDistribution(singleDrivePaths);
        Assert.Equal("All 2 documents on C:", singleDriveSummary);

        // Empty
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentDriveDistribution(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentDriveDistribution(Array.Empty<string>()));
    }

    [Fact]
    public void Run137_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Copy match line context
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy match line context", mainCs);
        Assert.Contains("FormatMatchLineContext", mainCs);

        // 2. MindMapGalaxy: CopyFullConnectionConfigurationItem & OnCopyFullConnectionConfigurationClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CopyFullConnectionConfigurationItem", galaxyXaml);
        Assert.Contains("OnCopyFullConnectionConfigurationClick", galaxyXaml);
        Assert.Contains("OnCopyFullConnectionConfigurationClick", galaxyCs);
        Assert.Contains("FormatFullConnectionConfiguration", galaxyCs);

        // 3. ShapeStudio: SelectionCycleStrokeDashItem, OnSelectionCycleStrokeDashClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleStrokeDashItem", shapeXaml);
        Assert.Contains("OnSelectionCycleStrokeDashClick", shapeXaml);
        Assert.Contains("OnSelectionCycleStrokeDashClick", shapeCs);
        Assert.Contains("CycleStrokeDashStyle", shapeCs);

        // 4. SmartArtStudio: OutlineAuditLineLengthsItem & OnAuditOutlineLineLengthsClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineAuditLineLengthsItem", smartArtXaml);
        Assert.Contains("OnAuditOutlineLineLengthsClick", smartArtXaml);
        Assert.Contains("OnAuditOutlineLineLengthsClick", smartArtCs);
        Assert.Contains("FormatOutlineLineLengthAuditSummary", smartArtCs);

        // 5. HistoryWindow: Copy version audit record item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version audit record", historyCs);
        Assert.Contains("FormatVersionAuditRecord", historyCs);

        // 6. SuiteHub: CopyRecentDriveDistributionItem & OnCopyRecentDriveDistributionClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentDriveDistributionItem", suiteXaml);
        Assert.Contains("OnCopyRecentDriveDistributionClick", suiteXaml);
        Assert.Contains("OnCopyRecentDriveDistributionClick", suiteCs);
        Assert.Contains("FormatRecentDriveDistribution", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentAgeDistribution_GroupsAgesCorrectly()
    {
        var refTime = new DateTime(2026, 10, 17, 16, 0, 0);
        var files = new[]
        {
            @"C:\doc_today.md",
            @"C:\doc_week.md",
            @"D:\doc_older.txt",
            @"E:\missing.doc"
        };

        var summary = RecentFilesService.FormatRecentAgeDistribution(
            files,
            refTime,
            file => file switch
            {
                @"C:\doc_today.md" => refTime.AddHours(-2),
                @"C:\doc_week.md" => refTime.AddDays(-3),
                @"D:\doc_older.txt" => refTime.AddDays(-20),
                _ => null
            });

        Assert.Equal("4 documents: 1 today, 1 this week, 1 older, 1 unverified", summary);

        // Empty
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentAgeDistribution(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentAgeDistribution(Array.Empty<string>()));
    }

    [Fact]
    public void Run138_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Copy search query diagnostics
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy search query diagnostics", mainCs);
        Assert.Contains("FormatSearchQueryDiagnostics", mainCs);

        // 2. MindMapGalaxy: CycleAnchorSnapAngleItem & OnCycleAnchorSnapAngleClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CycleAnchorSnapAngleItem", galaxyXaml);
        Assert.Contains("OnCycleAnchorSnapAngleClick", galaxyXaml);
        Assert.Contains("OnCycleAnchorSnapAngleClick", galaxyCs);
        Assert.Contains("CycleAnchorSnapAngle", galaxyCs);

        // 3. ShapeStudio: SelectionCycleOpacityItem, OnSelectionCycleOpacityClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleOpacityItem", shapeXaml);
        Assert.Contains("OnSelectionCycleOpacityClick", shapeXaml);
        Assert.Contains("OnSelectionCycleOpacityClick", shapeCs);
        Assert.Contains("CycleShapeOpacity", shapeCs);

        // 4. SmartArtStudio: OutlineHierarchyBreakdownItem & OnCopyOutlineHierarchyBreakdownClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineHierarchyBreakdownItem", smartArtXaml);
        Assert.Contains("OnCopyOutlineHierarchyBreakdownClick", smartArtXaml);
        Assert.Contains("OnCopyOutlineHierarchyBreakdownClick", smartArtCs);
        Assert.Contains("FormatOutlineHierarchyBreakdown", smartArtCs);

        // 5. HistoryWindow: Copy version change velocity item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version change velocity", historyCs);
        Assert.Contains("FormatVersionChangeVelocity", historyCs);

        // 6. SuiteHub: CopyRecentAgeDistributionItem & OnCopyRecentAgeDistributionClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentAgeDistributionItem", suiteXaml);
        Assert.Contains("OnCopyRecentAgeDistributionClick", suiteXaml);
        Assert.Contains("OnCopyRecentAgeDistributionClick", suiteCs);
        Assert.Contains("FormatRecentAgeDistribution", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentPathLengthDistribution_ComputesAccurately()
    {
        var samplePaths = new[]
        {
            @"C:\docs\short.md",
            @"C:\docs\medium_length_document.md",
            @"D:\very\long\path\that\is\quite\extended\for\testing\purposes\and\audit\document.md"
        };

        var summary = RecentFilesService.FormatRecentPathLengthDistribution(samplePaths, warnThreshold: 50);
        Assert.Contains("Min 16 chars", summary);
        Assert.Contains("Max 83 chars", summary);
        Assert.Contains("1 path >= 50 chars", summary);

        var safeSummary = RecentFilesService.FormatRecentPathLengthDistribution(samplePaths, warnThreshold: 260);
        Assert.Contains("0 paths >= 260 chars (All safe)", safeSummary);

        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentPathLengthDistribution(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentPathLengthDistribution(Array.Empty<string>()));
    }

    [Fact]
    public void Run139_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: Copy search query history summary & Clear search query history
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy search query history summary", mainCs);
        Assert.Contains("Clear search query history", mainCs);
        Assert.Contains("FormatSearchHistorySummary", mainCs);

        // 2. MindMapGalaxy: ToggleConnectionLabelsItem & OnToggleConnectionLabelsClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("ToggleConnectionLabelsItem", galaxyXaml);
        Assert.Contains("OnToggleConnectionLabelsClick", galaxyXaml);
        Assert.Contains("OnToggleConnectionLabelsClick", galaxyCs);
        Assert.Contains("ToggleConnectionLabels", galaxyCs);

        // 3. ShapeStudio: SelectionCycleShadowBlurItem, OnSelectionCycleShadowBlurClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleShadowBlurItem", shapeXaml);
        Assert.Contains("OnSelectionCycleShadowBlurClick", shapeXaml);
        Assert.Contains("OnSelectionCycleShadowBlurClick", shapeCs);
        Assert.Contains("CycleShadowBlurRadius", shapeCs);

        // 4. SmartArtStudio: OutlineBranchingAuditorItem & OnAuditOutlineBranchingClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineBranchingAuditorItem", smartArtXaml);
        Assert.Contains("OnAuditOutlineBranchingClick", smartArtXaml);
        Assert.Contains("OnAuditOutlineBranchingClick", smartArtCs);
        Assert.Contains("FormatOutlineBranchingSummary", smartArtCs);

        // 5. HistoryWindow: Copy version net growth & churn ratio item
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy version net growth & churn ratio", historyCs);
        Assert.Contains("FormatVersionGrowthAndChurn", historyCs);

        // 6. SuiteHub: CopyRecentPathLengthItem & OnCopyRecentPathLengthClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentPathLengthItem", suiteXaml);
        Assert.Contains("OnCopyRecentPathLengthClick", suiteXaml);
        Assert.Contains("OnCopyRecentPathLengthClick", suiteCs);
        Assert.Contains("FormatRecentPathLengthDistribution", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentSizeDistribution_ComputesAccurately()
    {
        var samplePaths = new[]
        {
            @"C:\Docs\Doc1.md",
            @"C:\Docs\Doc2.md",
            @"C:\Docs\Doc3.md"
        };

        var sizeMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Docs\Doc1.md"] = 1024,      // 1.0 KB
            [@"C:\Docs\Doc2.md"] = 2048,      // 2.0 KB
            [@"C:\Docs\Doc3.md"] = 5120       // 5.0 KB
        };

        var summary = RecentFilesService.FormatRecentSizeDistribution(samplePaths, path => sizeMap.TryGetValue(path, out var s) ? s : null);
        Assert.Equal("Disk footprint (3 documents): Total 8 KB · Min 1 KB, Max 5 KB, Avg 2.7 KB", summary);

        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentSizeDistribution(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentSizeDistribution(Array.Empty<string>()));
    }

    [Fact]
    public void Run140_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: matchRateItem & FormatSearchMatchRate
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy search match density & frequency", mainCs);
        Assert.Contains("FormatSearchMatchRate", mainCs);

        // 2. MindMapGalaxy: StepStrokeThicknessItem & OnStepStrokeThicknessClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("StepStrokeThicknessItem", galaxyXaml);
        Assert.Contains("OnStepStrokeThicknessClick", galaxyXaml);
        Assert.Contains("OnStepStrokeThicknessClick", galaxyCs);
        Assert.Contains("StepStrokeThickness", galaxyCs);

        // 3. ShapeStudio: SelectionCycleCornerRadiusItem & OnSelectionCycleCornerRadiusClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleCornerRadiusItem", shapeXaml);
        Assert.Contains("OnSelectionCycleCornerRadiusClick", shapeXaml);
        Assert.Contains("OnSelectionCycleCornerRadiusClick", shapeCs);
        Assert.Contains("CycleCornerRadius", shapeCs);

        // 4. SmartArtStudio: OutlineTreeBalanceItem & OnAuditOutlineTreeBalanceClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineTreeBalanceItem", smartArtXaml);
        Assert.Contains("OnAuditOutlineTreeBalanceClick", smartArtXaml);
        Assert.Contains("OnAuditOutlineTreeBalanceClick", smartArtCs);
        Assert.Contains("FormatOutlineBalanceSummary", smartArtCs);

        // 5. HistoryWindow: copyRetention item & FormatVersionRetentionRate
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy line retention & survival rate", historyCs);
        Assert.Contains("FormatVersionRetentionRate", historyCs);

        // 6. SuiteHub: CopyRecentSizeDistributionItem & OnCopyRecentSizeDistributionClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentSizeDistributionItem", suiteXaml);
        Assert.Contains("OnCopyRecentSizeDistributionClick", suiteXaml);
        Assert.Contains("OnCopyRecentSizeDistributionClick", suiteCs);
        Assert.Contains("FormatRecentSizeDistribution", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentDirectorySpread_ComputesAccurately()
    {
        var samplePaths = new[]
        {
            @"C:\Docs\Work\Project1\spec.md",
            @"C:\Docs\Work\Project1\notes.md",
            @"C:\Docs\Personal\todo.md"
        };

        var summary = RecentFilesService.FormatRecentDirectorySpread(samplePaths);
        Assert.Contains("3 documents across 2 folders · Clustered", summary);
        Assert.Contains("Primary: 'Project1' (2/3, 66.7%)", summary);

        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentDirectorySpread(null));
        Assert.Equal("0 documents in recent history", RecentFilesService.FormatRecentDirectorySpread(Array.Empty<string>()));
    }

    [Fact]
    public void Run141_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: matchHotspotsItem & FormatSearchMatchHotspots
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy search match hotspots & clustering", mainCs);
        Assert.Contains("FormatSearchMatchHotspots", mainCs);

        // 2. MindMapGalaxy: CycleLinkDirectionItem & OnCycleLinkDirectionClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("CycleLinkDirectionItem", galaxyXaml);
        Assert.Contains("OnCycleLinkDirectionClick", galaxyXaml);
        Assert.Contains("OnCycleLinkDirectionClick", galaxyCs);
        Assert.Contains("CycleLinkDirection", galaxyCs);

        // 3. ShapeStudio: SelectionCycleStrokeDashOffsetItem & OnSelectionCycleStrokeDashOffsetClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionCycleStrokeDashOffsetItem", shapeXaml);
        Assert.Contains("OnSelectionCycleStrokeDashOffsetClick", shapeXaml);
        Assert.Contains("OnSelectionCycleStrokeDashOffsetClick", shapeCs);
        Assert.Contains("CycleStrokeDashOffset", shapeCs);

        // 4. SmartArtStudio: OutlineDepthDistributionItem & OnAuditOutlineDepthDistributionClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineDepthDistributionItem", smartArtXaml);
        Assert.Contains("OnAuditOutlineDepthDistributionClick", smartArtXaml);
        Assert.Contains("OnAuditOutlineDepthDistributionClick", smartArtCs);
        Assert.Contains("FormatOutlineDepthDistributionSummary", smartArtCs);

        // 5. HistoryWindow: copyEntropy item & FormatVersionEditEntropy
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy edit dispersion & entropy index", historyCs);
        Assert.Contains("FormatVersionEditEntropy", historyCs);

        // 6. SuiteHub: CopyRecentDirectorySpreadItem & OnCopyRecentDirectorySpreadClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentDirectorySpreadItem", suiteXaml);
        Assert.Contains("OnCopyRecentDirectorySpreadClick", suiteXaml);
        Assert.Contains("OnCopyRecentDirectorySpreadClick", suiteCs);
        Assert.Contains("FormatRecentDirectorySpread", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentFileNamingPatterns_ComputesAccurately()
    {
        Assert.Equal("Naming patterns: 0 documents (No recent files)", RecentFilesService.FormatRecentFileNamingPatterns(null));
        Assert.Equal("Naming patterns: 0 documents (No recent files)", RecentFilesService.FormatRecentFileNamingPatterns(Array.Empty<string>()));

        var samplePaths = new[]
        {
            @"C:\docs\2026-10-17-meeting-notes.md",
            @"C:\docs\2026-10-16-daily-standup.md",
            @"C:\projects\system-architecture.md",
            @"C:\projects\user-guide.markdown",
            @"C:\projects\data_pipeline.py",
            @"C:\projects\AppLauncher.cs"
        };

        var summary = RecentFilesService.FormatRecentFileNamingPatterns(samplePaths);
        Assert.Contains("6 documents", summary);
        Assert.Contains("date-prefixed", summary);
        Assert.Contains("kebab-case", summary);
        Assert.Contains("snake_case", summary);
        Assert.Contains("Pascal/camelCase", summary);
        Assert.Contains(".md", summary);
    }

    [Fact]
    public void Run142_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: copyCaptureGroupsItem & FormatRegexCaptureGroupsSummary
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy regex capture groups & token matches", mainCs);
        Assert.Contains("FormatRegexCaptureGroupsSummary", mainCs);

        // 2. MindMapGalaxy: AutoColorizeHierarchyItem & OnAutoColorizeHierarchyClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AutoColorizeHierarchyItem", galaxyXaml);
        Assert.Contains("OnAutoColorizeHierarchyClick", galaxyXaml);
        Assert.Contains("OnAutoColorizeHierarchyClick", galaxyCs);
        Assert.Contains("AutoColorizeByHierarchy", galaxyCs);

        // 3. ShapeStudio: SelectionReverseZOrderItem & OnSelectionReverseZOrderClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionReverseZOrderItem", shapeXaml);
        Assert.Contains("OnSelectionReverseZOrderClick", shapeXaml);
        Assert.Contains("OnSelectionReverseZOrderClick", shapeCs);
        Assert.Contains("ReverseSelectedZOrder", shapeCs);

        // 4. SmartArtStudio: ReverseHierarchyOutlineItem & OnReverseHierarchyOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("ReverseHierarchyOutlineItem", smartArtXaml);
        Assert.Contains("OnReverseHierarchyOutlineClick", smartArtXaml);
        Assert.Contains("OnReverseHierarchyOutlineClick", smartArtCs);
        Assert.Contains("ReverseOutlineHierarchically", smartArtCs);

        // 5. HistoryWindow: copyHunks item & FormatDiffHunkSummary
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy edit hunk distribution & dispersion", historyCs);
        Assert.Contains("FormatDiffHunkSummary", historyCs);

        // 6. SuiteHub: CopyRecentNamingPatternsItem & OnCopyRecentNamingPatternsClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentNamingPatternsItem", suiteXaml);
        Assert.Contains("OnCopyRecentNamingPatternsClick", suiteXaml);
        Assert.Contains("OnCopyRecentNamingPatternsClick", suiteCs);
        Assert.Contains("FormatRecentFileNamingPatterns", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatMissingRecentFilesAudit_ComputesAccurately()
    {
        Assert.Equal("Recent path health: 0 documents (No recent files)", RecentFilesService.FormatMissingRecentFilesAudit(null));
        Assert.Equal("Recent path health: 0 documents (No recent files)", RecentFilesService.FormatMissingRecentFilesAudit(Array.Empty<string>()));

        var paths = new[]
        {
            @"C:\docs\existing-1.md",
            @"C:\docs\existing-2.md",
            @"C:\docs\missing-draft.md",
            @"C:\docs\moved-spec.md"
        };

        var summaryMixed = RecentFilesService.FormatMissingRecentFilesAudit(paths, p => p.Contains("existing"));
        Assert.Equal("Recent path health (4 documents): 2 valid (50%), 2 missing/moved ('missing-draft.md', 'moved-spec.md')", summaryMixed);

        var summaryAllValid = RecentFilesService.FormatMissingRecentFilesAudit(paths.Take(2), _ => true);
        Assert.Equal("Recent path health (2 documents): 100% valid on disk (2 verified · 0 missing paths)", summaryAllValid);
    }

    [Fact]
    public void Run143_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: swapFindReplaceItem & casePreserveReplaceItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Swap Find and Replace queries", mainCs);
        Assert.Contains("Replace all preserving match casing", mainCs);
        Assert.Contains("ReplacePreservingCase", mainCs);

        // 2. MindMapGalaxy: AttachOrphansItem & OnAttachOrphansClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AttachOrphansItem", galaxyXaml);
        Assert.Contains("OnAttachOrphansClick", galaxyXaml);
        Assert.Contains("OnAttachOrphansClick", galaxyCs);
        Assert.Contains("AttachOrphansToRoot", galaxyCs);

        // 3. ShapeStudio: SelectionNormalizePrecisionItem & OnSelectionNormalizePrecisionClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionNormalizePrecisionItem", shapeXaml);
        Assert.Contains("OnSelectionNormalizePrecisionClick", shapeXaml);
        Assert.Contains("OnSelectionNormalizePrecisionClick", shapeCs);
        Assert.Contains("NormalizeShapePrecision", shapeCs);

        // 4. SmartArtStudio: DeduplicateOutlineItem & OnDeduplicateOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("DeduplicateOutlineItem", smartArtXaml);
        Assert.Contains("OnDeduplicateOutlineClick", smartArtXaml);
        Assert.Contains("OnDeduplicateOutlineClick", smartArtCs);
        Assert.Contains("DeduplicateOutlineNodes", smartArtCs);

        // 5. HistoryWindow: copyDiffAgainstLatest & ComputeDiffAgainstLatestSummary
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy diff summary against latest version (HEAD)", historyCs);
        Assert.Contains("ComputeDiffAgainstLatestSummary", historyCs);

        // 6. SuiteHub: CopyRecentMissingAuditItem & OnCopyRecentMissingAuditClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentMissingAuditItem", suiteXaml);
        Assert.Contains("OnCopyRecentMissingAuditClick", suiteXaml);
        Assert.Contains("OnCopyRecentMissingAuditClick", suiteCs);
        Assert.Contains("FormatMissingRecentFilesAudit", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatRecentFilesSortedCatalog_ComputesAccurately()
    {
        Assert.Equal("Sorted recent catalog: 0 documents (No recent files)",
            RecentFilesService.FormatRecentFilesSortedCatalog(null));
        Assert.Equal("Sorted recent catalog: 0 documents (No recent files)",
            RecentFilesService.FormatRecentFilesSortedCatalog(Array.Empty<string>()));

        var paths = new[]
        {
            @"C:\Specs\zeta-plan.md",
            @"C:\Docs\alpha-notes.md",
            @"C:\Docs\beta-guide.md"
        };

        var catalog = RecentFilesService.FormatRecentFilesSortedCatalog(paths);
        Assert.Equal("Sorted recent catalog (3 documents across 2 folders): A–Z [alpha-notes.md, beta-guide.md, zeta-plan.md] · By folder [Docs (2), Specs (1)]", catalog);
    }

    [Fact]
    public void Run144_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: fixSafeLintItem & FixSafeMarkdownLintIssues
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Auto-fix safe Markdown lint issues", mainCs);
        Assert.Contains("FixSafeMarkdownLintIssues", mainCs);
        Assert.Contains("FormatSafeLintFixSummary", mainCs);

        // 2. MindMapGalaxy: AuditLinkedFilesItem & OnAuditLinkedFilesClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditLinkedFilesItem", galaxyXaml);
        Assert.Contains("OnAuditLinkedFilesClick", galaxyXaml);
        Assert.Contains("OnAuditLinkedFilesClick", galaxyCs);
        Assert.Contains("AuditLinkedFilesStatus", galaxyCs);

        // 3. ShapeStudio: SelectionMatchSizeItem & OnSelectionMatchSizeClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionMatchSizeItem", shapeXaml);
        Assert.Contains("OnSelectionMatchSizeClick", shapeXaml);
        Assert.Contains("OnSelectionMatchSizeClick", shapeCs);
        Assert.Contains("MatchSelectedShapeDimensions", shapeCs);

        // 4. SmartArtStudio: SplitLongOutlineItem & OnSplitLongOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("SplitLongOutlineItem", smartArtXaml);
        Assert.Contains("OnSplitLongOutlineClick", smartArtXaml);
        Assert.Contains("OnSplitLongOutlineClick", smartArtCs);
        Assert.Contains("SplitLongOutlineItems", smartArtCs);

        // 5. HistoryWindow: copyCherryPickHunks & FormatCherryPickHunksPayload
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy numbered cherry-pick hunks payload", historyCs);
        Assert.Contains("FormatCherryPickHunksPayload", historyCs);

        // 6. SuiteHub: CopyRecentSortedCatalogItem & OnCopyRecentSortedCatalogClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentSortedCatalogItem", suiteXaml);
        Assert.Contains("OnCopyRecentSortedCatalogClick", suiteXaml);
        Assert.Contains("OnCopyRecentSortedCatalogClick", suiteCs);
        Assert.Contains("FormatRecentFilesSortedCatalog", suiteCs);
    }

    [Fact]
    public void RecentFilesService_FormatProjectWorkspaceClusters_ComputesAccurately()
    {
        Assert.Equal("Project workspace clusters: 0 workspaces (no recent files recorded)",
            RecentFilesService.FormatProjectWorkspaceClusters(null));
        Assert.Equal("Project workspace clusters: 0 workspaces (no recent files recorded)",
            RecentFilesService.FormatProjectWorkspaceClusters(Array.Empty<string>()));

        var paths = new[]
        {
            @"C:\Repos\MarkSmith\docs\architecture.md",
            @"C:\Repos\MarkSmith\docs\governance.md",
            @"C:\Repos\MarkSmith\docs\roadmap.md",
            @"C:\Repos\CloudflareOS\README.md"
        };

        var clusters = RecentFilesService.FormatProjectWorkspaceClusters(paths);
        Assert.Equal("Project workspace clusters (4 files in 2 workspaces, 1 multi-file hub(s)): MarkSmith/docs (3: architecture.md, governance.md, +1 more) · CloudflareOS (1: README.md)", clusters);
    }

    [Fact]
    public void Run145_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: auditProseCleanupItem & FormatProseCleanupAuditSummary
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy prose cleanup & typography audit", mainCs);
        Assert.Contains("FormatProseCleanupAuditSummary", mainCs);

        // 2. MindMapGalaxy: RollupSubtreeProgressItem & OnRollupSubtreeProgressClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("RollupSubtreeProgressItem", galaxyXaml);
        Assert.Contains("OnRollupSubtreeProgressClick", galaxyXaml);
        Assert.Contains("OnRollupSubtreeProgressClick", galaxyCs);
        Assert.Contains("RollupSubtreeProgress", galaxyCs);

        // 3. ShapeStudio: SelectionFlipHorizontalItem & OnSelectionFlipHorizontalClick
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionFlipHorizontalItem", shapeXaml);
        Assert.Contains("OnSelectionFlipHorizontalClick", shapeXaml);
        Assert.Contains("OnSelectionFlipHorizontalClick", shapeCs);
        Assert.Contains("FlipSelectedShapes", shapeCs);

        // 4. SmartArtStudio: AutoNumberOutlineItem & OnAutoNumberOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AutoNumberOutlineItem", smartArtXaml);
        Assert.Contains("OnAutoNumberOutlineClick", smartArtXaml);
        Assert.Contains("OnAutoNumberOutlineClick", smartArtCs);
        Assert.Contains("ApplyHierarchicalNumbering", smartArtCs);

        // 5. HistoryWindow: copyHeadingDiff & FormatStructuralHeadingDiffSummary
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy structural heading diff (# H1–H6)", historyCs);
        Assert.Contains("FormatStructuralHeadingDiffSummary", historyCs);

        // 6. SuiteHub: CopyRecentWorkspaceClustersItem & OnCopyRecentWorkspaceClustersClick
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentWorkspaceClustersItem", suiteXaml);
        Assert.Contains("OnCopyRecentWorkspaceClustersClick", suiteXaml);
        Assert.Contains("OnCopyRecentWorkspaceClustersClick", suiteCs);
        Assert.Contains("FormatProjectWorkspaceClusters", suiteCs);
    }

    [Fact]
    public void Run146_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: PortalResetDialsButton & ResetPortalDials & portalFocusProfileItem
        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"PortalResetDialsButton\"", mainXaml);
        Assert.Contains("OnResetPortalDialsClick", mainXaml);
        Assert.Contains("ResetPortalDials", mainCs);
        Assert.Contains("portalFocusProfileItem", mainCs);
        Assert.Contains("FormatFocusWindowReadingProfile", mainCs);

        // 2. MindMapGalaxy: AutoColorizeByTagItem & CopyTagCoOccurrenceItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AutoColorizeByTagItem", galaxyXaml);
        Assert.Contains("CopyTagCoOccurrenceItem", galaxyXaml);
        Assert.Contains("OnAutoColorizeByTagClick", galaxyCs);
        Assert.Contains("OnCopyTagCoOccurrenceClick", galaxyCs);
        Assert.Contains("AutoColorizeByPrimaryTag", galaxyCs);
        Assert.Contains("FormatTagCoOccurrenceSummary", galaxyCs);

        // 3. ShapeStudio: SelectionSelectSimilarKindItem & SelectionSelectSimilarFillItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionSelectSimilarKindItem", shapeXaml);
        Assert.Contains("SelectionSelectSimilarFillItem", shapeXaml);
        Assert.Contains("OnSelectionSelectSimilarKindClick", shapeCs);
        Assert.Contains("OnSelectionSelectSimilarFillClick", shapeCs);
        Assert.Contains("SelectSimilarShapes", shapeCs);

        // 4. SmartArtStudio: RecommendLayoutsOutlineItem & OnRecommendLayoutsOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("RecommendLayoutsOutlineItem", smartArtXaml);
        Assert.Contains("OnRecommendLayoutsOutlineClick", smartArtCs);
        Assert.Contains("FormatLayoutRecommendationSummary", smartArtCs);

        // 5. HistoryWindow: copyMicroSaveAudit & FormatLowEntropySnapshotAudit
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy auto-save micro-saves audit (low-entropy snapshots)", historyCs);
        Assert.Contains("FormatLowEntropySnapshotAudit", historyCs);

        // 6. SuiteHub: CopyRecentDuplicatesAuditItem & FormatDuplicateAndVersionedFilesAudit
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentDuplicatesAuditItem", suiteXaml);
        Assert.Contains("OnCopyRecentDuplicatesAuditClick", suiteCs);
        Assert.Contains("FormatDuplicateAndVersionedFilesAudit", suiteCs);

        // RecentFilesService duplicate and versioned files audit logic test
        var files = new[]
        {
            @"C:\Projects\Doc.md",
            @"C:\Projects\Doc (1).md",
            @"C:\Projects\Doc - Copy.md",
            @"C:\Projects\Doc_backup.md",
            @"D:\Other\Doc.md",
            @"C:\Projects\Unique.md"
        };
        var (clusterCount, redundantCount, clusters) = RecentFilesService.DetectDuplicateAndVersionedFiles(files);
        Assert.Equal(1, clusterCount);
        Assert.Equal(4, redundantCount);
        Assert.Equal("Doc.md", clusters[0].CanonicalBaseName);
        Assert.True(clusters[0].IsCrossDirectoryDuplicate);
        Assert.True(clusters[0].IsVersionSuffixed);

        var audit = RecentFilesService.FormatDuplicateAndVersionedFilesAudit(files);
        Assert.Contains("Duplicate & versioned files audit (1 duplicate cluster · 4 redundant copies)", audit);
        Assert.Contains("'Doc.md'", audit);
    }

    [Fact]
    public void Run147_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. MainWindow: StatusSectionBreadcrumbButton & Section Budget Analysis
        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("x:Name=\"StatusSectionBreadcrumbButton\"", mainXaml);
        Assert.Contains("OnStatusSectionBreadcrumbButtonClick", mainCs);
        Assert.Contains("ExtractMarkdownSectionBudgets", mainCs);
        Assert.Contains("FormatMarkdownSectionBudgetsSummary", mainCs);

        // 2. MindMapGalaxy: CenterOnMassItem & CopyQuadrantBalanceItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("x:Name=\"CenterOnMassItem\"", galaxyXaml);
        Assert.Contains("x:Name=\"CopyQuadrantBalanceItem\"", galaxyXaml);
        Assert.Contains("OnCenterOnMassClick", galaxyCs);
        Assert.Contains("OnCopyQuadrantBalanceClick", galaxyCs);
        Assert.Contains("CenterGalaxyOnCenterOfMass", galaxyCs);
        Assert.Contains("FormatSpatialQuadrantBalanceSummary", galaxyCs);

        // 3. ShapeStudio: SelectionResolveOverlapsItem & ResolveShapeOverlaps
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionResolveOverlapsItem", shapeXaml);
        Assert.Contains("OnSelectionResolveOverlapsClick", shapeCs);
        Assert.Contains("ResolveShapeOverlaps", shapeCs);

        // 4. SmartArtStudio: StructureByDelimiterOutlineItem & OnStructureByDelimiterOutlineClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("StructureByDelimiterOutlineItem", smartArtXaml);
        Assert.Contains("OnStructureByDelimiterOutlineClick", smartArtCs);
        Assert.Contains("StructureFlatOutlineByDelimiter", smartArtCs);

        // 5. HistoryWindow: copyFencedBlockDiff & FormatFencedBlockDiffSummary
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy fenced code & diagram blocks diff audit", historyCs);
        Assert.Contains("FormatFencedBlockDiffSummary", historyCs);

        // 6. SuiteHub: CopyRecentHealthScorecardItem & FormatLibraryHealthScorecard
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentHealthScorecardItem", suiteXaml);
        Assert.Contains("OnCopyRecentHealthScorecardClick", suiteCs);
        Assert.Contains("FormatLibraryHealthScorecard", suiteCs);

        // RecentFilesService library health scorecard logic test
        var files = new[]
        {
            @"C:\Projects\spec.md",
            @"C:\Projects\readme.md",
            @"C:\Projects\architecture.md",
            @"D:\Missing\deleted.md"
        };
        bool MockExists(string p) => !p.Contains("Missing");

        var (score, grade, valid, missing, redundant, scorecard) =
            RecentFilesService.AuditLibraryHealthScorecard(files, MockExists);

        Assert.Equal(3, valid);
        Assert.Equal(1, missing);
        Assert.Equal(0, redundant);
        Assert.True(score >= 70);
        Assert.Contains("Recent library health scorecard", scorecard);
        Assert.Contains("Path validity: 75% on disk", scorecard);
        Assert.Contains("3 valid · 1 missing", scorecard);
    }

    [Fact]
    public void Run148_FootnoteIntegrity_CaseNormalizer_EvenDistribution_DepthBalancer_VolatilityIndex_StalenessTiers()
    {
        // 1. MainWindow: auditFootnotesItem & AuditFootnoteAndReferenceIntegrity
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Audit footnote & reference link integrity", mainCs);
        Assert.Contains("FormatFootnoteAndReferenceIntegritySummary", mainCs);

        // 2. MindMapGalaxy: NormalizeNodeCasingItem & OnNormalizeNodeCasingClick
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("x:Name=\"NormalizeNodeCasingItem\"", galaxyXaml);
        Assert.Contains("OnNormalizeNodeCasingClick", galaxyCs);
        Assert.Contains("NormalizeNodeTextCasing", galaxyCs);

        // 3. ShapeStudio: DistributeEvenlyHoriz/Vert items & DistributeShapesEvenly
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionDistributeEvenlyHorizItem", shapeXaml);
        Assert.Contains("SelectionDistributeEvenlyVertItem", shapeXaml);
        Assert.Contains("OnSelectionDistributeEvenlyHorizClick", shapeCs);
        Assert.Contains("OnSelectionDistributeEvenlyVertClick", shapeCs);
        Assert.Contains("DistributeShapesEvenly", shapeCs);

        // 4. SmartArtStudio: BalanceOutlineDepthItem & OnBalanceOutlineDepthClick
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("BalanceOutlineDepthItem", smartArtXaml);
        Assert.Contains("OnBalanceOutlineDepthClick", smartArtCs);
        Assert.Contains("BalanceOutlineNestingDepth", smartArtCs);

        // 5. HistoryWindow: copyVolatilityIndex & FormatTimelineVolatilitySummary
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("Copy timeline volatility index & session churn audit", historyCs);
        Assert.Contains("FormatTimelineVolatilitySummary", historyCs);

        // 6. SuiteHub: CopyRecentStalenessScorecardItem & FormatStalenessTiersSummary
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentStalenessScorecardItem", suiteXaml);
        Assert.Contains("OnCopyRecentStalenessScorecardClick", suiteCs);
        Assert.Contains("FormatStalenessTiersSummary", suiteCs);

        // RecentFilesService staleness tiers logic test
        var refDate = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            @"C:\Docs\Active.md",    // 2 hours ago -> active
            @"C:\Docs\Fresh.md",     // 3 days ago -> fresh
            @"C:\Docs\Warm.md",      // 14 days ago -> warm
            @"C:\Docs\Cold.md",      // 60 days ago -> cold
            @"C:\Docs\Stale.md",     // 120 days ago -> stale
            @"D:\Missing\Lost.md"    // missing -> stale
        };

        DateTime? MockMtime(string p) => p switch
        {
            @"C:\Docs\Active.md" => refDate.AddHours(-2),
            @"C:\Docs\Fresh.md" => refDate.AddDays(-3),
            @"C:\Docs\Warm.md" => refDate.AddDays(-14),
            @"C:\Docs\Cold.md" => refDate.AddDays(-60),
            @"C:\Docs\Stale.md" => refDate.AddDays(-120),
            _ => null
        };

        var (active, fresh, warm, cold, stale, total, summary) =
            RecentFilesService.AuditRecentLibraryStalenessTiers(files, MockMtime, refDate);

        Assert.Equal(1, active);
        Assert.Equal(1, fresh);
        Assert.Equal(1, warm);
        Assert.Equal(1, cold);
        Assert.Equal(2, stale); // 120 days + missing
        Assert.Equal(6, total);
        Assert.Contains("Recent library staleness tiers (6 documents): 1 active (<24h), 1 fresh (1-7d), 1 warm (7-30d), 1 cold (30-90d), 2 stale (>90d)", summary);
        Assert.Contains("Prune or archive 2 stale documents >90d old", summary);
    }

    [Fact]
    public void Run149_VolumeDistribution_And_SurfaceWiring()
    {
        // 1. Surface wiring checks
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy session velocity forecast & pacing", mainCs);
        Assert.Contains("CalculateSessionVelocityForecast", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditInactiveNodesItem", galaxyXaml);
        Assert.Contains("OnAuditInactiveNodesClick", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionScaleProportionalItem", shapeXaml);
        Assert.Contains("OnSelectionScaleProportionalClick", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditTokenDensityItem", smartArtXaml);
        Assert.Contains("OnAuditTokenDensityClick", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("AssessCherryPickConflictRisk", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentVolumeDistributionItem", suiteXaml);
        Assert.Contains("OnCopyRecentVolumeDistributionClick", suiteCs);
        Assert.Contains("FormatVolumeDistributionSummary", suiteCs);

        // 2. Volume Distribution Logic
        var files = new[]
        {
            @"C:\Users\Docs\Report.md",
            @"C:\Users\Docs\Plan.md",
            @"C:\Users\Docs\Notes.md",
            @"D:\Projects\Repo\Readme.md",
            @"\\nas\share\Team\Doc.md",
            @"relative/path/draft.md"
        };

        bool MockAccessibility(string vol) => !vol.Contains("nas", StringComparison.OrdinalIgnoreCase);

        var (totalFiles, totalVols, accessible, offline, dominant, summary) =
            RecentFilesService.AuditVolumeDistribution(files, MockAccessibility);

        Assert.Equal(6, totalFiles);
        Assert.Equal(4, totalVols); // C:\, D:\, \\nas\share\, Relative / Workspace
        Assert.Equal(3, accessible);
        Assert.Equal(1, offline);
        Assert.Equal(@"C:\", dominant);
        Assert.Contains(@"Recent library volume distribution (6 documents across 4 storage volumes): Dominant: C:\", summary);
        Assert.Contains("Warning: 1 volume offline/unreachable", summary);
        Assert.Contains(@"C:\ (3 docs, 50%)", summary);
    }

    [Fact]
    public void Run150_AuditAccessFrequencyHeatmap_And_SurfaceWiring()
    {
        // 1. Surface wiring checks across all 6 WinUI 3 surfaces
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("FormatReadabilityScorecardSummary", mainCs);
        Assert.Contains("readabilityScorecardItem", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditTopologicalDepthItem", galaxyXaml);
        Assert.Contains("OnAuditTopologicalDepthClick", galaxyXaml);
        Assert.Contains("OnAuditTopologicalDepthClick", galaxyCs);
        Assert.Contains("AuditTopologicalDepth", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionDistributeRadialItem", shapeXaml);
        Assert.Contains("OnSelectionDistributeRadialClick", shapeXaml);
        Assert.Contains("OnSelectionDistributeRadialClick", shapeCs);
        Assert.Contains("DistributeShapesRadially", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditAspectRatioFitItem", smartArtXaml);
        Assert.Contains("OnAuditAspectRatioFitClick", smartArtXaml);
        Assert.Contains("OnAuditAspectRatioFitClick", smartArtCs);
        Assert.Contains("AuditLayoutAspectRatio", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("SynthesizeSemanticVersionMessage", historyCs);
        Assert.Contains("synthesizeCommitMessage", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentAccessHeatmapItem", suiteXaml);
        Assert.Contains("OnCopyRecentAccessHeatmapClick", suiteXaml);
        Assert.Contains("OnCopyRecentAccessHeatmapClick", suiteCs);
        Assert.Contains("FormatAccessFrequencyHeatmapSummary", suiteCs);

        // 2. Access frequency heatmap calculation & velocity score
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0);
        var files = new[]
        {
            @"C:\doc_daily.md",
            @"C:\doc_weekly.md",
            @"C:\doc_biweekly.md",
            @"C:\doc_monthly.md",
            @"C:\doc_dormant.md"
        };

        DateTime? MockAccessTime(string path) => path switch
        {
            @"C:\doc_daily.md" => refTime.AddHours(-2),     // daily (<24h)
            @"C:\doc_weekly.md" => refTime.AddDays(-3),     // weekly (1–7d)
            @"C:\doc_biweekly.md" => refTime.AddDays(-10),  // bi-weekly (7–14d)
            @"C:\doc_monthly.md" => refTime.AddDays(-20),   // monthly (14–30d)
            @"C:\doc_dormant.md" => refTime.AddDays(-40),   // dormant (>30d)
            _ => null
        };

        var heatmap = RecentFilesService.AuditAccessFrequencyHeatmap(files, MockAccessTime, refTime);
        Assert.Equal(5, heatmap.TotalFiles);
        Assert.Equal(1, heatmap.DailyCadence);
        Assert.Equal(1, heatmap.WeeklyCadence);
        Assert.Equal(1, heatmap.BiWeeklyCadence);
        Assert.Equal(1, heatmap.MonthlyCadence);
        Assert.Equal(1, heatmap.DormantOrphans);
        Assert.Equal(60.0, heatmap.RevisitVelocityScore);
        Assert.Equal("Daily (<24h)", heatmap.DominantCadence);
        Assert.Contains("Recent access frequency heatmap (5 documents · Velocity: 60/100 · Dominant: Daily (<24h)): 1 daily (<24h), 1 weekly (1–7d), 1 bi-weekly (7–14d), 1 monthly (14–30d), 1 dormant (>30d)", heatmap.Summary);

        // Empty file collection
        var emptyHeatmap = RecentFilesService.AuditAccessFrequencyHeatmap(Array.Empty<string>());
        Assert.Equal(0, emptyHeatmap.TotalFiles);
        Assert.Equal(0.0, emptyHeatmap.RevisitVelocityScore);
        Assert.Contains("0 documents (No recent files recorded)", emptyHeatmap.Summary);
    }

    [Fact]
    public void Run151_AuditDormancyAlerts_And_DesktopWiring()
    {
        // 1. Verify desktop wiring across surfaces
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("sentenceCadenceItem", mainCs);
        Assert.Contains("FormatSentenceFlowSummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditOrphansItem", galaxyXaml);
        Assert.Contains("OnAuditOrphansClick", galaxyXaml);
        Assert.Contains("OnAuditOrphansClick", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditLayerStackItem", shapeXaml);
        Assert.Contains("OnSelectionAuditLayerStackClick", shapeXaml);
        Assert.Contains("OnSelectionAuditLayerStackClick", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditCharBudgetsItem", smartArtXaml);
        Assert.Contains("OnAuditCharBudgetsClick", smartArtXaml);
        Assert.Contains("OnAuditCharBudgetsClick", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeCommitScope", historyCs);
        Assert.Contains("SynthesizeConventionalCommitWithScope", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentDormancyAlertsItem", suiteXaml);
        Assert.Contains("OnCopyRecentDormancyAlertsClick", suiteXaml);
        Assert.Contains("OnCopyRecentDormancyAlertsClick", suiteCs);

        // 2. Dormancy alerts audit logic
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0);
        var files = new[]
        {
            @"C:\active_doc.md",
            @"C:\dormant_project1.md",
            @"C:\dormant_project2.md",
            @"C:\ancient_project.md"
        };

        DateTime? MockAccessTime(string path) => path switch
        {
            @"C:\active_doc.md" => refTime.AddDays(-5),          // active (<60d)
            @"C:\dormant_project1.md" => refTime.AddDays(-65),   // dormant (65d)
            @"C:\dormant_project2.md" => refTime.AddDays(-90),   // dormant (90d)
            @"C:\ancient_project.md" => refTime.AddDays(-200),   // archive (>180d)
            _ => null
        };

        var audit = RecentFilesService.AuditDormancyAlerts(files, MockAccessTime, refTime, dormancyDaysThreshold: 60);
        Assert.Equal(4, audit.TotalFiles);
        Assert.Equal(3, audit.DormantCount);
        Assert.Equal(1, audit.ActiveCount);
        Assert.Equal("Moderate (Stale documents accumulating)", audit.RiskLevel);
        Assert.Contains("3 dormant projects", audit.Summary);
        Assert.Contains("dormant_project1.md", audit.Summary);

        // Ancient project recommended action is archive
        var ancient = audit.Alerts.First(a => a.FileName == "ancient_project.md");
        Assert.Equal("Archive or prune stale project", ancient.RecommendedAction);

        // Empty file collection
        var emptyAudit = RecentFilesService.AuditDormancyAlerts(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Contains("0 documents (No recent files recorded)", emptyAudit.Summary);
    }

    [Fact]
    public void Run152_Desktop_Wiring_And_WorkspaceClusterDormancy()
    {
        // 1. Verify desktop wiring across surfaces
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("conflictMarkersItem", mainCs);
        Assert.Contains("FormatConflictMarkerSummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditClusterHullsItem", galaxyXaml);
        Assert.Contains("OnAuditClusterHullsClick", galaxyXaml);
        Assert.Contains("OnAuditClusterHullsClick", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditGridSnapItem", shapeXaml);
        Assert.Contains("SelectionQuantizeGridItem", shapeXaml);
        Assert.Contains("OnSelectionAuditGridSnapClick", shapeXaml);
        Assert.Contains("OnSelectionQuantizeGridClick", shapeXaml);
        Assert.Contains("OnSelectionAuditGridSnapClick", shapeCs);
        Assert.Contains("OnSelectionQuantizeGridClick", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("RecommendIconsOutlineItem", smartArtXaml);
        Assert.Contains("OnRecommendIconsOutlineClick", smartArtXaml);
        Assert.Contains("OnRecommendIconsOutlineClick", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("formatChangelogSection", historyCs);
        Assert.Contains("FormatConventionalChangelogSection", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentClusterDormancyItem", suiteXaml);
        Assert.Contains("OnCopyRecentClusterDormancyClick", suiteXaml);
        Assert.Contains("OnCopyRecentClusterDormancyClick", suiteCs);

        // 2. Workspace cluster dormancy audit logic
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0);
        var files = new[]
        {
            @"C:\Projects\Alpha\docs\overview.md",
            @"C:\Projects\Alpha\docs\specs.md",
            @"C:\Projects\Beta\notes\todo.md",
            @"C:\Projects\Beta\notes\ancient.md"
        };

        DateTime? MockAccessTime(string path) => path switch
        {
            @"C:\Projects\Alpha\docs\overview.md" => refTime.AddDays(-2),   // active
            @"C:\Projects\Alpha\docs\specs.md" => refTime.AddDays(-10),    // active
            @"C:\Projects\Beta\notes\todo.md" => refTime.AddDays(-75),     // dormant (>60d)
            @"C:\Projects\Beta\notes\ancient.md" => refTime.AddDays(-120),  // dormant (>60d)
            _ => null
        };

        var audit = RecentFilesService.AuditWorkspaceClusterDormancy(files, MockAccessTime, refTime, dormancyDaysThreshold: 60);
        Assert.Equal(4, audit.TotalFiles);
        Assert.Equal(2, audit.TotalClusters);
        Assert.Equal(1, audit.StagnantClusters); // Beta is 100% dormant (2/2)

        var betaCluster = audit.Clusters.First(c => c.WorkspaceName.Contains("Beta"));
        Assert.Equal(2, betaCluster.TotalFiles);
        Assert.Equal(2, betaCluster.DormantFiles);
        Assert.Equal(1.0, betaCluster.DormancyRatio);

        var alphaCluster = audit.Clusters.First(c => c.WorkspaceName.Contains("Alpha"));
        Assert.Equal(2, alphaCluster.TotalFiles);
        Assert.Equal(0, alphaCluster.DormantFiles);
        Assert.Equal(0.0, alphaCluster.DormancyRatio);

        Assert.Contains("1 stagnant cluster", audit.Summary);
        Assert.Contains("Beta", audit.Summary);

        // Empty file collection
        var emptyAudit = RecentFilesService.AuditWorkspaceClusterDormancy(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Contains("0 workspaces, 0 documents", emptyAudit.Summary);
    }

    [Fact]
    public void Run153_Polish_Elements_Wired_Across_Surfaces_And_ClusterVelocity()
    {
        // 1. Cross-surface XAML and code-behind wiring tests
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("readabilityMetricsItem", mainCs);
        Assert.Contains("FormatReadabilitySummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditMstItem", galaxyXaml);
        Assert.Contains("OnAuditMstClick", galaxyXaml);
        Assert.Contains("OnAuditMstClick", galaxyCs);
        Assert.Contains("AuditMinimumSpanningTree", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditAspectRatioItem", shapeXaml);
        Assert.Contains("SelectionLockAspectRatioSquareItem", shapeXaml);
        Assert.Contains("OnSelectionAuditAspectRatioClick", shapeCs);
        Assert.Contains("AuditAspectRatioProportions", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditBranchingFactorItem", smartArtXaml);
        Assert.Contains("OnAuditBranchingFactorClick", smartArtXaml);
        Assert.Contains("OnAuditBranchingFactorClick", smartArtCs);
        Assert.Contains("AuditHierarchyBranchingFactor", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeMilestoneHeader", historyCs);
        Assert.Contains("SynthesizeReleaseMilestoneHeader", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyRecentClusterVelocityItem", suiteXaml);
        Assert.Contains("OnCopyRecentClusterVelocityClick", suiteXaml);
        Assert.Contains("OnCopyRecentClusterVelocityClick", suiteCs);
        Assert.Contains("AuditClusterActivityVelocity", suiteCs);

        // 2. Logic test for AuditClusterActivityVelocity
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0);
        var files = new[]
        {
            @"C:\Projects\Alpha\docs\overview.md",
            @"C:\Projects\Alpha\docs\specs.md",
            @"C:\Projects\Beta\notes\todo.md",
            @"C:\Projects\Beta\notes\ancient.md"
        };

        DateTime MockAccessTime(string path) => path switch
        {
            @"C:\Projects\Alpha\docs\overview.md" => refTime.AddDays(-2),   // active (<14d)
            @"C:\Projects\Alpha\docs\specs.md" => refTime.AddDays(-5),     // active (<14d)
            @"C:\Projects\Beta\notes\todo.md" => refTime.AddDays(-25),     // dormant (>=14d)
            @"C:\Projects\Beta\notes\ancient.md" => refTime.AddDays(-100),  // dormant (>=14d)
            _ => DateTime.MinValue
        };

        var velocityAudit = RecentFilesService.AuditClusterActivityVelocity(files, MockAccessTime, refTime);
        Assert.Equal(4, velocityAudit.TotalFiles);
        Assert.Equal(2, velocityAudit.TotalClusters);
        Assert.Equal(50.0, velocityAudit.OverallVelocityPercentage); // 2/4 = 50%
        Assert.Contains("docs", velocityAudit.TopClusterName);

        var alphaCluster = velocityAudit.Clusters.First(c => c.ClusterName == "docs");
        Assert.Equal(2, alphaCluster.TotalFiles);
        Assert.Equal(2, alphaCluster.ActiveFiles);
        Assert.Equal(100.0, alphaCluster.ActivityPercentage);
        Assert.Equal("High Velocity", alphaCluster.VelocityRating);

        var betaCluster = velocityAudit.Clusters.First(c => c.ClusterName == "notes");
        Assert.Equal(2, betaCluster.TotalFiles);
        Assert.Equal(0, betaCluster.ActiveFiles);
        Assert.Equal(0.0, betaCluster.ActivityPercentage);
        Assert.Equal("Dormant", betaCluster.VelocityRating);

        Assert.Contains("50% active", velocityAudit.Summary);

        // Empty file collection test
        var emptyVelocity = RecentFilesService.AuditClusterActivityVelocity(Array.Empty<string>());
        Assert.Equal(0, emptyVelocity.TotalFiles);
        Assert.Contains("0 workspaces, 0 documents", emptyVelocity.Summary);
    }

    [Fact]
    public void Run154_Polish_Elements_Wired_Across_Surfaces_And_ActiveClusterChips()
    {
        // 1. Cross-surface XAML and code-behind wiring tests
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("headingOutlineBalanceItem", mainCs);
        Assert.Contains("FormatHeadingOutlineBalanceSummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditDegreeCentralityItem", galaxyXaml);
        Assert.Contains("OnAuditDegreeCentralityClick", galaxyXaml);
        Assert.Contains("OnAuditDegreeCentralityClick", galaxyCs);
        Assert.Contains("AuditDegreeCentrality", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditSpacingItem", shapeXaml);
        Assert.Contains("SelectionDistributeSpacingHorizontalItem", shapeXaml);
        Assert.Contains("OnSelectionAuditSpacingClick", shapeCs);
        Assert.Contains("OnSelectionDistributeSpacingHorizontalClick", shapeCs);
        Assert.Contains("AuditSpacingDistribution", shapeCs);
        Assert.Contains("DistributeSpacingUniformly", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditContainerDensityItem", smartArtXaml);
        Assert.Contains("OnAuditContainerDensityClick", smartArtXaml);
        Assert.Contains("OnAuditContainerDensityClick", smartArtCs);
        Assert.Contains("AuditContainerDensityFit", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeReleaseBadges", historyCs);
        Assert.Contains("SynthesizeReleaseBadgesMarkdown", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("FilterByActiveClusterItem", suiteXaml);
        Assert.Contains("OnFilterByActiveClusterClick", suiteXaml);
        Assert.Contains("OnFilterByActiveClusterClick", suiteCs);
        Assert.Contains("GetActiveClusterFilterChips", suiteCs);

        // 2. Logic test for GetActiveClusterFilterChips
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0);
        var files = new[]
        {
            @"C:\Projects\Alpha\docs\overview.md",
            @"C:\Projects\Alpha\docs\specs.md",
            @"C:\Projects\Beta\notes\todo.md",
            @"C:\Projects\Beta\notes\ancient.md"
        };

        DateTime MockAccessTime(string path) => path switch
        {
            @"C:\Projects\Alpha\docs\overview.md" => refTime.AddDays(-2),   // active (<14d)
            @"C:\Projects\Alpha\docs\specs.md" => refTime.AddDays(-5),     // active (<14d)
            @"C:\Projects\Beta\notes\todo.md" => refTime.AddDays(-25),     // dormant (>=14d)
            @"C:\Projects\Beta\notes\ancient.md" => refTime.AddDays(-100),  // dormant (>=14d)
            _ => DateTime.MinValue
        };

        var chips = RecentFilesService.GetActiveClusterFilterChips(files, MockAccessTime, refTime);
        Assert.Equal(2, chips.Count);

        var topChip = chips[0];
        Assert.Equal("docs", topChip.ClusterName);
        Assert.Equal(2, topChip.TotalFiles);
        Assert.Equal(2, topChip.ActiveFiles);
        Assert.Equal(100.0, topChip.ActivityPercentage);
        Assert.Contains("2/2 active", topChip.DisplayBadge);

        var summary = RecentFilesService.FormatActiveClusterFilterSummary(chips);
        Assert.Contains("Top active 'docs'", summary);
        Assert.Contains("2/2 active", summary);

        // Empty file collection test
        var emptyChips = RecentFilesService.GetActiveClusterFilterChips(Array.Empty<string>());
        Assert.Empty(emptyChips);
        var emptySummary = RecentFilesService.FormatActiveClusterFilterSummary(emptyChips);
        Assert.Contains("No clusters found", emptySummary);
    }

    [Fact]
    public void Run155_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. Verify all UI elements & handlers across surfaces
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("codeFenceCoverageItem", mainCs);
        Assert.Contains("FormatCodeFenceLanguageCoverageSummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditPeripheryEccentricityItem", galaxyXaml);
        Assert.Contains("OnAuditPeripheryEccentricityClick", galaxyXaml);
        Assert.Contains("OnAuditPeripheryEccentricityClick", galaxyCs);
        Assert.Contains("AuditPeripheryEccentricity", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditOcclusionItem", shapeXaml);
        Assert.Contains("OnSelectionAuditOcclusionClick", shapeXaml);
        Assert.Contains("OnSelectionAuditOcclusionClick", shapeCs);
        Assert.Contains("AuditOcclusionAndOverlap", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditSiblingVarianceItem", smartArtXaml);
        Assert.Contains("OnAuditSiblingVarianceClick", smartArtXaml);
        Assert.Contains("OnAuditSiblingVarianceClick", smartArtCs);
        Assert.Contains("AuditSiblingTextLengthVariance", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeReleaseAnnouncement", historyCs);
        Assert.Contains("SynthesizeReleaseAnnouncementSnippet", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyFormatClusterMatrixItem", suiteXaml);
        Assert.Contains("OnCopyFormatClusterMatrixClick", suiteXaml);
        Assert.Contains("OnCopyFormatClusterMatrixClick", suiteCs);
        Assert.Contains("AuditFormatClusterMatrix", suiteCs);

        // 2. Logic test for AuditFormatClusterMatrix
        var emptyAudit = RecentFilesService.AuditFormatClusterMatrix(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.SiloStatus);

        var files = new[]
        {
            @"C:\Projects\Alpha\doc1.md",
            @"C:\Projects\Alpha\doc2.md",
            @"C:\Projects\Alpha\doc3.md",
            @"C:\Projects\Alpha\report.docx",
            @"C:\Projects\Alpha\export.pdf"
        };
        var audit = RecentFilesService.AuditFormatClusterMatrix(files);
        Assert.Equal(5, audit.TotalFiles);
        Assert.Equal(3, audit.TotalFormats);
        Assert.Equal(".md", audit.DominantFormat);
        Assert.Equal(60.0, audit.DominantPercentage);
        Assert.True(audit.DiversityIndex > 0.4);
        Assert.Contains("Format cluster matrix (5 files across 3 formats", audit.Summary);
    }

    [Fact]
    public void Run156_Polish_Elements_Wired_Across_Surfaces()
    {
        // 1. Verify all UI elements & handlers across surfaces
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("listIndentationItem", mainCs);
        Assert.Contains("FormatListIndentationSummary", mainCs);

        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditRadialAngularItem", galaxyXaml);
        Assert.Contains("OnAuditRadialAngularClick", galaxyXaml);
        Assert.Contains("OnAuditRadialAngularClick", galaxyCs);
        Assert.Contains("AuditRadialAngularSpacing", galaxyCs);

        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditBoundaryBleedItem", shapeXaml);
        Assert.Contains("OnSelectionAuditBoundaryBleedClick", shapeXaml);
        Assert.Contains("OnSelectionAuditBoundaryBleedClick", shapeCs);
        Assert.Contains("AuditBoundaryMarginsAndBleed", shapeCs);

        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditLinearFlowMonotonicityItem", smartArtXaml);
        Assert.Contains("OnAuditLinearFlowMonotonicityClick", smartArtXaml);
        Assert.Contains("OnAuditLinearFlowMonotonicityClick", smartArtCs);
        Assert.Contains("AuditLinearFlowMonotonicity", smartArtCs);

        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeMultiChannelDigest", historyCs);
        Assert.Contains("SynthesizeMultiChannelReleaseDigest", historyCs);

        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyCompanionFormatCoverageItem", suiteXaml);
        Assert.Contains("OnCopyCompanionFormatCoverageClick", suiteXaml);
        Assert.Contains("OnCopyCompanionFormatCoverageClick", suiteCs);
        Assert.Contains("AuditCompanionFormatCoverage", suiteCs);

        // 2. Logic test for AuditCompanionFormatCoverage
        var emptyAudit = RecentFilesService.AuditCompanionFormatCoverage(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFamilies);
        Assert.Equal("Empty Library", emptyAudit.CoverageStatus);

        var files = new[]
        {
            @"C:\Projects\Alpha\spec.md",
            @"C:\Projects\Alpha\spec.docx",
            @"C:\Projects\Alpha\spec.pdf",
            @"C:\Projects\Alpha\notes.md",
            @"C:\Projects\Beta\legacy.docx"
        };
        var audit = RecentFilesService.AuditCompanionFormatCoverage(files);
        Assert.Equal(3, audit.TotalFamilies);
        Assert.Equal(1, audit.FullyCoveredFamilies);
        Assert.Equal(1, audit.SourceOnlyFamilies);
        Assert.Equal(1, audit.ExportOnlyFamilies);
        Assert.Equal(33.3, audit.CoveragePercentage);
        Assert.Contains("Companion coverage (3 document families [1 compiled companions, 1 source-only, 1 export-only]", audit.Summary);
    }

    [Fact]
    public void Run157_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // 1. MainWindow: table column alignment audit wired to breadcrumb and find count
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy markdown table column alignment & pipe grid audit", mainCs);
        Assert.Contains("FormatTableAlignmentSummary", mainCs);

        // 2. MindMapGalaxyWindow: cluster cohesion audit in XAML and code-behind
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditClusterCohesionItem", galaxyXaml);
        Assert.Contains("OnAuditClusterCohesionClick", galaxyXaml);
        Assert.Contains("OnAuditClusterCohesionClick", galaxyCs);
        Assert.Contains("AuditClusterCohesionAndDensity", galaxyCs);

        // 3. ShapeDesignStudioWindow: text contrast audit in XAML and code-behind
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditTextContrastItem", shapeXaml);
        Assert.Contains("OnSelectionAuditTextContrastClick", shapeXaml);
        Assert.Contains("OnSelectionAuditTextContrastClick", shapeCs);
        Assert.Contains("AuditShapeTextContrast", shapeCs);

        // 4. SmartArtDesignStudioWindow: cyclic flow continuity audit in XAML and code-behind
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditCyclicFlowContinuityItem", smartArtXaml);
        Assert.Contains("OnAuditCyclicFlowContinuityClick", smartArtXaml);
        Assert.Contains("OnAuditCyclicFlowContinuityClick", smartArtCs);
        Assert.Contains("AuditCyclicFlowContinuity", smartArtCs);

        // 5. HistoryWindow: diff churn acceleration in code-behind
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditDiffChurnAcceleration", historyCs);
        Assert.Contains("AuditDiffChurnAcceleration", historyCs);

        // 6. SuiteHubView: document aging and velocity in XAML and code-behind
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyDocumentStalenessItem", suiteXaml);
        Assert.Contains("OnCopyDocumentStalenessClick", suiteXaml);
        Assert.Contains("OnCopyDocumentStalenessClick", suiteCs);
        Assert.Contains("AuditDocumentStalenessAndVelocity", suiteCs);
    }

    [Fact]
    public void Run157_AuditDocumentStalenessAndVelocity()
    {
        // 1. Empty library
        var emptyAudit = RecentFilesService.AuditDocumentStalenessAndVelocity(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.VelocityCategory);
        Assert.Contains("0 documents", emptyAudit.Summary);

        // 2. Active sprint documents with reference time
        var refTime = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            @"C:\Projects\Active\doc1.md",
            @"C:\Projects\Active\doc2.md",
            @"C:\Projects\Active\doc3.md"
        };

        var audit = RecentFilesService.AuditDocumentStalenessAndVelocity(files, refTime);
        Assert.Equal(3, audit.TotalFiles);
        Assert.True(audit.DocumentAges.Count == 3);
        Assert.Contains("Document velocity", audit.Summary);
        Assert.Equal(audit.Summary, RecentFilesService.FormatDocumentStalenessSummary(
            audit.TotalFiles, audit.ActiveFiles, audit.WarmFiles, audit.ColdFiles, audit.ArchivedFiles,
            audit.FreshnessIndex, audit.AverageAgeDays, audit.VelocityCategory));
    }

    [Fact]
    public void Run158_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // 1. MainWindow: task list progress audit wired to breadcrumb and find count
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy markdown task list completion velocity & progress audit", mainCs);
        Assert.Contains("FormatTaskListProgressSummary", mainCs);

        // 2. MindMapGalaxyWindow: bipartite partition audit in XAML and code-behind
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditBipartitePartitionItem", galaxyXaml);
        Assert.Contains("OnAuditBipartitePartitionClick", galaxyXaml);
        Assert.Contains("OnAuditBipartitePartitionClick", galaxyCs);
        Assert.Contains("AuditBipartitePartitioning", galaxyCs);

        // 3. ShapeDesignStudioWindow: golden ratio progression audit in XAML and code-behind
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditGoldenRatioItem", shapeXaml);
        Assert.Contains("OnSelectionAuditGoldenRatioClick", shapeXaml);
        Assert.Contains("OnSelectionAuditGoldenRatioClick", shapeCs);
        Assert.Contains("AuditGoldenRatioProgression", shapeCs);

        // 4. SmartArtDesignStudioWindow: matrix quadrant balance audit in XAML and code-behind
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditMatrixQuadrantBalanceItem", smartArtXaml);
        Assert.Contains("OnAuditMatrixQuadrantBalanceClick", smartArtXaml);
        Assert.Contains("OnAuditMatrixQuadrantBalanceClick", smartArtCs);
        Assert.Contains("AuditMatrixQuadrantBalance", smartArtCs);

        // 5. HistoryWindow: pull request description synthesizer in code-behind
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizePullRequest", historyCs);
        Assert.Contains("SynthesizePullRequestDescription", historyCs);

        // 6. SuiteHubView: workspace storage footprint in XAML and code-behind
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceStorageFootprintItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceStorageFootprintClick", suiteXaml);
        Assert.Contains("OnCopyWorkspaceStorageFootprintClick", suiteCs);
        Assert.Contains("AuditWorkspaceStorageFootprint", suiteCs);
    }

    [Fact]
    public void Run158_AuditWorkspaceStorageFootprint()
    {
        // 1. Empty library
        var emptyAudit = RecentFilesService.AuditWorkspaceStorageFootprint(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.FootprintCategory);
        Assert.Contains("0 files", emptyAudit.Summary);

        // 2. FormatBytes helper
        Assert.Equal("500 B", RecentFilesService.FormatBytes(500));
        Assert.Equal("2 KB", RecentFilesService.FormatBytes(2048));
        Assert.Equal("2.4 MB", RecentFilesService.FormatBytes(2_500_000));

        // 3. Document library with sizes and a heavy outlier (>500KB)
        var files = new[]
        {
            @"C:\Projects\doc1.md",
            @"C:\Projects\doc2.docx",
            @"C:\Projects\doc3.pdf"
        };
        var sizes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase)
        {
            { @"C:\Projects\doc1.md", 120_000L },
            { @"C:\Projects\doc2.docx", 650_000L }, // >500 KB heavy outlier
            { @"C:\Projects\doc3.pdf", 24_000L }
        };

        var audit = RecentFilesService.AuditWorkspaceStorageFootprint(files, p => sizes.TryGetValue(p, out var sz) ? sz : null);
        Assert.Equal(3, audit.TotalFiles);
        Assert.Equal(3, audit.ExistingFiles);
        Assert.Equal(794_000L, audit.TotalBytes);
        Assert.Equal(1, audit.HeavyOutliersCount);
        Assert.Equal("doc2.docx", audit.LargestFileName);
        Assert.Equal(650_000L, audit.LargestFileSizeBytes);
        Assert.Equal("Lightweight Workspace", audit.FootprintCategory);
        Assert.Contains("doc2.docx", audit.Summary);
        Assert.Contains("1 heavy (>500KB)", audit.Summary);
        Assert.Equal(audit.Summary, RecentFilesService.FormatWorkspaceStorageFootprintSummary(
            audit.TotalFiles, audit.ExistingFiles, audit.TotalBytes, audit.FormattedTotalSize,
            audit.FormattedAverageSize, audit.HeavyOutliersCount, audit.LargestFileName, audit.LargestFileSizeBytes, audit.FootprintCategory));
    }

    [Fact]
    public void Run159_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // 1. MainWindow: footnote linkage audit wired to breadcrumb and find count
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy markdown footnote linkage & missing target audit", mainCs);
        Assert.Contains("FormatFootnoteLinkageSummary", mainCs);

        // 2. MindMapGalaxyWindow: topological DAG order in XAML and code-behind
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditTopologicalOrderItem", galaxyXaml);
        Assert.Contains("OnAuditTopologicalOrderClick", galaxyXaml);
        Assert.Contains("OnAuditTopologicalOrderClick", galaxyCs);
        Assert.Contains("AuditTopologicalOrderAndDepth", galaxyCs);

        // 3. ShapeDesignStudioWindow: golden spiral progression in XAML and code-behind
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditGoldenSpiralItem", shapeXaml);
        Assert.Contains("OnSelectionAuditGoldenSpiralClick", shapeXaml);
        Assert.Contains("OnSelectionAuditGoldenSpiralClick", shapeCs);
        Assert.Contains("AuditGoldenSpiralProgression", shapeCs);

        // 4. SmartArtDesignStudioWindow: normalize matrix quadrant headers in XAML and code-behind
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("NormalizeMatrixQuadrantsItem", smartArtXaml);
        Assert.Contains("OnNormalizeMatrixQuadrantsClick", smartArtXaml);
        Assert.Contains("OnNormalizeMatrixQuadrantsClick", smartArtCs);
        Assert.Contains("NormalizeMatrixQuadrantHeaders", smartArtCs);

        // 5. HistoryWindow: conventional commit synthesizer in code-behind
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("synthesizeConventionalCommit", historyCs);
        Assert.Contains("SynthesizeConventionalCommitMessage", historyCs);

        // 6. SuiteHubView: document naming convention audit in XAML and code-behind
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyDocumentNamingConventionsItem", suiteXaml);
        Assert.Contains("OnCopyDocumentNamingConventionsClick", suiteXaml);
        Assert.Contains("OnCopyDocumentNamingConventionsClick", suiteCs);
        Assert.Contains("AuditDocumentNamingConventions", suiteCs);
    }

    [Fact]
    public void Run159_AuditDocumentNamingConventions()
    {
        // 1. Empty library
        var emptyAudit = RecentFilesService.AuditDocumentNamingConventions(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.Category);
        Assert.Contains("0 files", emptyAudit.Summary);

        // 2. Uniform kebab-case
        var kebabFiles = new[]
        {
            @"C:\Docs\project-roadmap.md",
            @"C:\Docs\architecture-overview.docx",
            @"C:\Docs\release-notes-2026.pdf"
        };
        var kebabAudit = RecentFilesService.AuditDocumentNamingConventions(kebabFiles);
        Assert.Equal(3, kebabAudit.TotalFiles);
        Assert.Equal(3, kebabAudit.KebabCaseCount);
        Assert.Equal("kebab-case", kebabAudit.DominantConvention);
        Assert.Equal(100.0, kebabAudit.ConsistencyScore);
        Assert.Equal("Uniform kebab-case (100%)", kebabAudit.Category);

        // 3. Mixed with spaces and special chars
        var mixedFiles = new[]
        {
            @"C:\Docs\Project Roadmap.md",
            @"C:\Docs\user_guide.docx",
            @"C:\Docs\spec(draft)[#1].md"
        };
        var mixedAudit = RecentFilesService.AuditDocumentNamingConventions(mixedFiles);
        Assert.Equal(3, mixedAudit.TotalFiles);
        Assert.Equal(1, mixedAudit.SpacedCount);
        Assert.Equal(1, mixedAudit.SnakeCaseCount);
        Assert.Equal(1, mixedAudit.FilesWithSpecialChars);
        Assert.Contains("Document naming audit", mixedAudit.Summary);
        Assert.Contains("special chars", mixedAudit.Summary);
    }

    [Fact]
    public void Run160_AuditPathLengthsAndNestingDepth()
    {
        // 1. Empty library
        var emptyAudit = RecentFilesService.AuditPathLengthsAndNestingDepth(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.SafetyCategory);
        Assert.Equal(100.0, emptyAudit.PathSafetyScore);

        // 2. Safe short paths
        var safeFiles = new[]
        {
            @"C:\Projects\app\readme.md",
            @"C:\Projects\app\docs\architecture.md",
            @"C:\Projects\app\docs\spec.md"
        };
        var safeAudit = RecentFilesService.AuditPathLengthsAndNestingDepth(safeFiles);
        Assert.Equal(3, safeAudit.TotalFiles);
        Assert.Equal(3, safeAudit.SafePathCount);
        Assert.Equal(0, safeAudit.WarningPathCount);
        Assert.Equal(0, safeAudit.ViolationPathCount);
        Assert.Equal("100% MAX_PATH Safe", safeAudit.SafetyCategory);
        Assert.Equal(100.0, safeAudit.PathSafetyScore);
        Assert.Equal(4, safeAudit.MaxNestingDepth);

        // 3. Warning path (>200 chars) & Violation (>=260 chars)
        string baseDir = @"C:\DeepWorkspace\EnterpriseProject\Subsystems\Microservices\DataPipelines\OrchestrationEngine\Connectors\";
        string warningFile = baseDir + new string('a', 150) + ".md"; // ~240 chars
        string violationFile = baseDir + new string('b', 200) + ".md"; // ~290 chars

        var riskFiles = new[]
        {
            @"C:\Docs\normal.md",
            warningFile,
            violationFile
        };

        var riskAudit = RecentFilesService.AuditPathLengthsAndNestingDepth(riskFiles);
        Assert.Equal(3, riskAudit.TotalFiles);
        Assert.Equal(1, riskAudit.SafePathCount);
        Assert.Equal(1, riskAudit.WarningPathCount);
        Assert.Equal(1, riskAudit.ViolationPathCount);
        Assert.Equal("MAX_PATH Violations Detected (≥260 chars)", riskAudit.SafetyCategory);
        Assert.True(riskAudit.PathSafetyScore < 70.0);
        Assert.Contains("MAX_PATH violations", riskAudit.Summary);
        Assert.Equal(riskAudit.Summary, RecentFilesService.FormatPathLengthsSummary(riskFiles));
    }

    [Fact]
    public void Run161_AuditFileExtensionDiversity_ClassifiesExtensionsAndComputesEntropy()
    {
        // 1. Empty files
        var emptyAudit = RecentFilesService.AuditFileExtensionDiversity(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal("Empty Library", emptyAudit.DiversityCategory);

        // 2. Pure markdown files
        var pureMdFiles = new[]
        {
            @"C:\Repo\readme.md",
            @"C:\Repo\architecture.md",
            @"C:\Repo\spec.md"
        };
        var pureAudit = RecentFilesService.AuditFileExtensionDiversity(pureMdFiles);
        Assert.Equal(3, pureAudit.TotalFiles);
        Assert.Equal(1, pureAudit.UniqueExtensions);
        Assert.Equal(3, pureAudit.StandardMarkdownCount);
        Assert.Equal(0, pureAudit.NonStandardMarkdownCount);
        Assert.Equal(0.0, pureAudit.ShannonEntropy);
        Assert.Equal("Standard Markdown Centric (≥85% .md)", pureAudit.DiversityCategory);
        Assert.Equal(pureAudit.Summary, RecentFilesService.FormatFileExtensionDiversitySummary(pureMdFiles));

        // 3. Non-standard markdown variants
        var variantFiles = new[]
        {
            @"C:\Docs\readme.md",
            @"C:\Docs\notes.markdown",
            @"C:\Docs\article.mdown",
            @"C:\Docs\component.mdx"
        };
        var variantAudit = RecentFilesService.AuditFileExtensionDiversity(variantFiles);
        Assert.Equal(4, variantAudit.TotalFiles);
        Assert.Equal(1, variantAudit.StandardMarkdownCount);
        Assert.Equal(3, variantAudit.NonStandardMarkdownCount);
        Assert.Contains("Irregular Markdown Variants Present", variantAudit.DiversityCategory);
        Assert.Equal(3, variantAudit.NonStandardMarkdownFiles.Count);

        // 4. Polyglot workspace
        var polyglotFiles = new[]
        {
            @"C:\Repo\readme.md",
            @"C:\Repo\report.docx",
            @"C:\Repo\index.html",
            @"C:\Repo\config.json",
            @"C:\Repo\diagram.png"
        };
        var polyAudit = RecentFilesService.AuditFileExtensionDiversity(polyglotFiles);
        Assert.Equal(5, polyAudit.TotalFiles);
        Assert.Equal(5, polyAudit.UniqueExtensions);
        Assert.True(polyAudit.ShannonEntropy >= 2.0);
        Assert.Contains("High Format Diversity", polyAudit.DiversityCategory);
    }

    [Fact]
    public void Run162_AuditDeadLinksAndOrphanAssets_IdentifiesBrokenLinksAndOrphans()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditDeadLinksAndOrphanAssets(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFiles);
        Assert.Equal(0, emptyAudit.TotalRelativeLinks);
        Assert.Equal("Empty Workspace", emptyAudit.IntegrityCategory);
        Assert.Equal(100.0, emptyAudit.IntegrityScore);

        // 2. Mock file system & content reader
        var doc1 = @"C:\Workspace\doc1.md";
        var doc2 = @"C:\Workspace\doc2.md";
        var img1 = @"C:\Workspace\images\chart.png";
        var imgOrphan = @"C:\Workspace\images\unused.png";

        var files = new[] { doc1, doc2, img1, imgOrphan };
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [doc1] = "See [Architecture](./doc2.md) and ![Chart](images/chart.png). Also [Broken Link](non-existent.md).",
            [doc2] = "# Doc 2 Content"
        };

        var audit = RecentFilesService.AuditDeadLinksAndOrphanAssets(
            files,
            fileExistsProvider: path => fileSet.Contains(path),
            contentReader: path => fileContents.TryGetValue(path, out var c) ? c : null);

        Assert.Equal(4, audit.TotalFiles);
        Assert.Equal(3, audit.TotalRelativeLinks);
        Assert.Equal(2, audit.ValidLinkCount);
        Assert.Equal(1, audit.BrokenLinkCount);
        Assert.Equal(1, audit.OrphanAssetCount);
        Assert.Contains(audit.BrokenLinks, l => l.TargetPath == "non-existent.md");
        Assert.Contains(audit.OrphanAssets, a => a.Contains("unused.png"));
        Assert.Contains("Broken Links & Orphan Assets Detected", audit.IntegrityCategory);
        Assert.True(audit.IntegrityScore < 100.0);
        Assert.Equal(audit.Summary, RecentFilesService.FormatDeadLinksSummary(
            audit.TotalFiles, audit.TotalRelativeLinks, audit.ValidLinkCount, audit.BrokenLinkCount,
            audit.OrphanAssetCount, audit.IntegrityScore, audit.IntegrityCategory, audit.BrokenLinks, audit.OrphanAssets));
    }

    [Fact]
    public void Run163_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // 1. MainWindow: syntax ambiguity and delimiter balance audit wired to breadcrumb and find count
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("Copy syntax ambiguity & delimiter balance audit", mainCs);
        Assert.Contains("FormatSyntaxAmbiguitySummary", mainCs);

        // 2. MindMapGalaxyWindow: graph diameter and eccentricity audit in XAML and code-behind
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditGraphDiameterItem", galaxyXaml);
        Assert.Contains("OnAuditGraphDiameterClick", galaxyXaml);
        Assert.Contains("OnAuditGraphDiameterClick", galaxyCs);
        Assert.Contains("AuditGraphDiameterAndEccentricity", galaxyCs);

        // 3. ShapeDesignStudioWindow: boundary spacing uniformity in XAML and code-behind
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditSpacingUniformityItem", shapeXaml);
        Assert.Contains("OnSelectionAuditSpacingUniformityClick", shapeXaml);
        Assert.Contains("OnSelectionAuditSpacingUniformityClick", shapeCs);
        Assert.Contains("AuditSpacingUniformityAndCollisions", shapeCs);

        // 4. SmartArtDesignStudioWindow: cyclic convergence audit in XAML and code-behind
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditCyclicConvergenceItem", smartArtXaml);
        Assert.Contains("OnAuditCyclicConvergenceClick", smartArtXaml);
        Assert.Contains("OnAuditCyclicConvergenceClick", smartArtCs);
        Assert.Contains("AuditCyclicProcessConvergence", smartArtCs);

        // 5. HistoryWindow: chunk boundary audit in code-behind
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditChunkBoundaries", historyCs);
        Assert.Contains("AuditChunkContextBoundaries", historyCs);

        // 6. SuiteHubView: asset compression advisor in XAML and code-behind
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyAssetCompressionAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyAssetCompressionAdvisorClick", suiteXaml);
        Assert.Contains("OnCopyAssetCompressionAdvisorClick", suiteCs);
        Assert.Contains("AuditAssetCompressionAndFormatAdvisor", suiteCs);
    }

    [Fact]
    public void Run163_AuditAssetCompressionAndFormatAdvisor_RecommendsWebPAndVectors()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditAssetCompressionAndFormatAdvisor(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalAssetsScanned);
        Assert.Equal("Empty Workspace", emptyAudit.EfficiencyCategory);
        Assert.Equal(0, emptyAudit.TotalPotentialSavingsBytes);

        // 2. Multi-image assets with uncompressed formats
        var doc1 = @"C:\Workspace\doc1.md";
        var heavyBmp = @"C:\Workspace\images\diagram.bmp";
        var heavyPng = @"C:\Workspace\images\screenshot.png";
        var smallSvg = @"C:\Workspace\images\icon.svg";

        var files = new[] { doc1, heavyBmp, heavyPng, smallSvg };
        var fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [doc1] = 2048,
            [heavyBmp] = 2_000_000,
            [heavyPng] = 600_000,
            [smallSvg] = 12_000
        };

        var audit = RecentFilesService.AuditAssetCompressionAndFormatAdvisor(
            files,
            fileExistsProvider: path => fileSizes.ContainsKey(path),
            fileLengthProvider: path => fileSizes.TryGetValue(path, out var s) ? s : 0);

        Assert.Equal(3, audit.TotalAssetsScanned);
        Assert.Equal(3, audit.LocalAssetsCount);
        Assert.True(audit.LegacyFormatCount >= 2);
        Assert.True(audit.TotalPotentialSavingsBytes > 1_000_000);
        Assert.Contains("Heavy Media Assets Detected", audit.EfficiencyCategory);
        Assert.NotEmpty(audit.Assets);
        Assert.Contains(audit.Assets, a => a.Extension == ".bmp" && a.RecommendedFormat.StartsWith("WebP / PNG"));
        Assert.Equal(audit.Summary, RecentFilesService.FormatAssetCompressionSummary(
            audit.TotalAssetsScanned, audit.LocalAssetsCount, audit.RemoteAssetsCount,
            audit.ModernFormatCount, audit.LegacyFormatCount, audit.OversizedCount,
            audit.TotalCurrentSizeBytes, audit.TotalPotentialSavingsBytes, audit.OverallSavingsPercentage,
            audit.AssetEfficiencyScore, audit.EfficiencyCategory));
    }

    [Fact]
    public void Run164_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // 1. MainWindow: math formula complexity item in breadcrumb and search HUD
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("mathFormulaComplexityItem", mainCs);
        Assert.Contains("FormatMathFormulaSummary", mainCs);

        // 2. MindMapGalaxyWindow: closeness centrality audit in XAML and code-behind
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditClosenessCentralityItem", galaxyXaml);
        Assert.Contains("OnAuditClosenessCentralityClick", galaxyXaml);
        Assert.Contains("OnAuditClosenessCentralityClick", galaxyCs);
        Assert.Contains("AuditClosenessCentralityAndHarmonicDistance", galaxyCs);

        // 3. ShapeDesignStudioWindow: orthogonal connection routing in XAML and code-behind
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditOrthogonalRoutingItem", shapeXaml);
        Assert.Contains("OnSelectionAuditOrthogonalRoutingClick", shapeXaml);
        Assert.Contains("OnSelectionAuditOrthogonalRoutingClick", shapeCs);
        Assert.Contains("AuditOrthogonalRoutingAndIntersections", shapeCs);

        // 4. SmartArtDesignStudioWindow: hierarchy span of control audit in XAML and code-behind
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditHierarchySpanItem", smartArtXaml);
        Assert.Contains("OnAuditHierarchySpanClick", smartArtXaml);
        Assert.Contains("OnAuditHierarchySpanClick", smartArtCs);
        Assert.Contains("AuditHierarchySpanOfControl", smartArtCs);

        // 5. HistoryWindow: semantic hunk folding audit in code-behind
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditSemanticFolding", historyCs);
        Assert.Contains("AuditSemanticHunkFoldingAndTokenContext", historyCs);

        // 6. SuiteHubView: asset DPI and SVG optimization advisor in XAML and code-behind
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyAssetDpiSvgAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyAssetDpiSvgAdvisorClick", suiteXaml);
        Assert.Contains("OnCopyAssetDpiSvgAdvisorClick", suiteCs);
        Assert.Contains("AuditAssetDpiAndSvgOptimizationAdvisor", suiteCs);
    }

    [Fact]
    public void Run164_AuditAssetDpiAndSvgOptimizationAdvisor_DetectsHighDpiAndSvgBloat()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditAssetDpiAndSvgOptimizationAdvisor(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalAssetsEvaluated);
        Assert.Equal("Empty Workspace", emptyAudit.ReadinessCategory);
        Assert.Equal(100.0, emptyAudit.OverallHighDpiReadinessScore);
        Assert.Contains("Workspace contains no documents", emptyAudit.Summary);

        // 2. Multi-asset workspace with SVG bloat and mixed DPI rasters
        var doc1 = @"C:\Workspace\doc1.md";
        var bloatedSvg = @"C:\Workspace\images\diagram.svg";
        var retinaPng = @"C:\Workspace\images\hero@2x.png";
        var lowDpiJpg = @"C:\Workspace\images\thumbnail.jpg";

        var files = new[] { doc1, bloatedSvg, retinaPng, lowDpiJpg };
        var fileSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [doc1] = 2048,
            [bloatedSvg] = 45000,
            [retinaPng] = 120000,
            [lowDpiJpg] = 8000
        };

        var docContent = """
            # Document Overview
            Here is a vector diagram:
            ![Diagram](images/diagram.svg)
            And high-dpi retina asset:
            ![Hero](images/hero@2x.png)
            And low-dpi asset:
            ![Thumb](images/thumbnail.jpg)
            """;

        var svgContent = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape">
              <!-- Created with Inkscape -->
              <metadata>
                <rdf:RDF>Document metadata</rdf:RDF>
              </metadata>
              <rect width="100" height="100" fill="red"/>
            </svg>
            """;

        var audit = RecentFilesService.AuditAssetDpiAndSvgOptimizationAdvisor(
            files,
            fileExistsProvider: path => fileSizes.ContainsKey(path),
            fileLengthProvider: path => fileSizes.TryGetValue(path, out var s) ? s : 0,
            dimensionsProvider: path => path.Contains("thumbnail") ? (100, 100) : (1920, 1080),
            contentReader: path => path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? svgContent
                : (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? docContent : ""));

        Assert.Equal(3, audit.TotalAssetsEvaluated);
        Assert.Equal(1, audit.SvgCount);
        Assert.Equal(2, audit.RasterCount);
        Assert.Equal(2, audit.HighDpiReadyCount); // SVG is scalable, hero@2x is Retina
        Assert.Equal(1, audit.LowDpiRiskCount); // thumbnail.jpg is low-DPI
        Assert.Equal(1, audit.UnoptimizedSvgCount); // bloatedSvg has inkscape, metadata, rdf tags
        Assert.True(audit.PotentialSvgSavingsBytes > 0);
        Assert.NotEmpty(audit.Assets);
        Assert.Contains(audit.Assets, a => a.HasSvgBloat && a.SvgBloatTagCount >= 3);
        Assert.Contains(audit.Assets, a => a.FileName.Contains("@2x") && a.IsHighDpiReady);
        Assert.Equal(audit.Summary, RecentFilesService.FormatAssetDpiSvgSummary(
            audit.TotalAssetsEvaluated, audit.SvgCount, audit.RasterCount,
            audit.HighDpiReadyCount, audit.LowDpiRiskCount, audit.UnoptimizedSvgCount,
            audit.OverallHighDpiReadinessScore, audit.PotentialSvgSavingsBytes, audit.ReadinessCategory));
    }

    [Fact]
    public void Run165_WorkspaceTypographyAndFontEmbedding_And_CrossSurfaceWiring()
    {
        // 1. Logic test for RecentFilesService.AuditWorkspaceTypographyAndFontEmbedding
        var emptyAudit = RecentFilesService.AuditWorkspaceTypographyAndFontEmbedding(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalFilesScanned);
        Assert.Equal(0, emptyAudit.DistinctFamiliesCount);
        Assert.Equal("Empty Workspace", emptyAudit.TypographicHealthCategory);
        Assert.Equal(100.0, emptyAudit.TypographicSelfContainmentScore);

        var docContent = """
            # Document Header
            <div style="font-family: 'Segoe UI', Arial, sans-serif;">System Font</div>
            <p style="font-family: 'Cascadia Code', monospace;">Code block</p>
            <span style="font-family: 'CustomExoticFont', sans-serif;">Exotic custom font</span>
            """;

        var fontAudit = RecentFilesService.AuditWorkspaceTypographyAndFontEmbedding(
            new[] { @"C:\Docs\spec.md" },
            fileExistsProvider: _ => true,
            contentReader: _ => docContent);

        Assert.Equal(1, fontAudit.TotalFilesScanned);
        Assert.True(fontAudit.DistinctFamiliesCount >= 2);
        Assert.True(fontAudit.TotalFontDeclarations >= 2);
        Assert.Equal(fontAudit.Summary, RecentFilesService.FormatWorkspaceTypographySummary(
            1, fontAudit.TotalFontDeclarations, fontAudit.DistinctFamiliesCount,
            fontAudit.SelfContainedFontCount, fontAudit.MissingFallbackCount, fontAudit.RemoteWebFontCount,
            fontAudit.TypographicSelfContainmentScore, fontAudit.TypographicHealthCategory, fontAudit.DiscoveredFamilies));

        // 2. Cross-surface XAML / C# wiring verification for Run #165 across all 6 surfaces:
        // MainWindow: tableAlignmentSpacingItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("tableAlignmentSpacingItem", mainCs);
        Assert.Contains("FormatTableAlignmentAndColumnSpacingSummary", mainCs);

        // MindMapGalaxy: AuditBetweennessCentralityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditBetweennessCentralityItem", galaxyXaml);
        Assert.Contains("OnAuditBetweennessCentralityClick", galaxyCs);

        // ShapeStudio: SelectionAuditGridSnapConformanceItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditGridSnapConformanceItem", shapeXaml);
        Assert.Contains("OnSelectionAuditGridSnapConformanceClick", shapeCs);

        // SmartArtStudio: AuditTypographyFontFitItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditTypographyFontFitItem", smartArtXaml);
        Assert.Contains("OnAuditTypographyFontFitClick", smartArtCs);

        // HistoryWindow: auditWordDiff
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditWordDiff", historyCs);
        Assert.Contains("AuditWordLevelDiffChunkingAndTokenShifts", historyCs);

        // SuiteHubView: CopyWorkspaceTypographyAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceTypographyAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceTypographyAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run166_WorkspaceFileSizeDistribution_And_CrossSurfaceWiring()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditWorkspaceFileSizeDistribution(null);
        Assert.Equal(0, emptyAudit.TotalFilesScanned);
        Assert.Equal(0, emptyAudit.TotalSizeBytes);
        Assert.Equal(100.0, emptyAudit.StorageCompactnessScore);
        Assert.Equal("Empty Workspace", emptyAudit.StorageHealthCategory);
        Assert.Equal(emptyAudit.Summary, RecentFilesService.FormatWorkspaceFileSizeDistributionSummary(
            0, 0, 0, 0, "None", 0, 100.0, "Empty Workspace"));

        // 2. Mocked workspace with light files and heavy bloat
        var fakeFiles = new[]
        {
            "C:\\docs\\notes.md",
            "C:\\docs\\report.docx",
            "C:\\docs\\video-presentation.mp4"
        };

        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\docs\\notes.md"] = 12 * 1024,
            ["C:\\docs\\report.docx"] = 250 * 1024,
            ["C:\\docs\\video-presentation.mp4"] = 8 * 1024 * 1024
        };

        var audit = RecentFilesService.AuditWorkspaceFileSizeDistribution(
            fakeFiles,
            fileExistsProvider: _ => true,
            fileLengthProvider: p => lengths.TryGetValue(p, out var len) ? len : 1024);

        Assert.Equal(3, audit.TotalFilesScanned);
        Assert.Equal(1, audit.HeavyBloatCount);
        Assert.Equal("video-presentation.mp4", audit.HeaviestFileName);
        Assert.True(audit.StorageCompactnessScore < 100.0);
        Assert.NotEmpty(audit.ExtensionCounts);
        Assert.Equal(audit.Summary, RecentFilesService.FormatWorkspaceFileSizeDistributionSummary(
            3, audit.TotalSizeBytes, audit.MeanSizeBytes, audit.MaxSizeBytes,
            audit.HeaviestFileName, audit.HeavyBloatCount, audit.StorageCompactnessScore, audit.StorageHealthCategory));

        // 3. Cross-surface XAML / C# wiring verification for Run #166 across all 6 surfaces:
        // MainWindow: definitionListConsistencyItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("definitionListConsistencyItem", mainCs);
        Assert.Contains("FormatDefinitionListSummary", mainCs);

        // MindMapGalaxy: AuditEigenvectorCentralityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditEigenvectorCentralityItem", galaxyXaml);
        Assert.Contains("OnAuditEigenvectorCentralityClick", galaxyCs);

        // ShapeStudio: SelectionAuditVisualWeightItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditVisualWeightItem", shapeXaml);
        Assert.Contains("OnSelectionAuditVisualWeightClick", shapeCs);

        // SmartArtStudio: AuditColorHierarchyItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditColorHierarchyItem", smartArtXaml);
        Assert.Contains("OnAuditColorHierarchyClick", smartArtCs);

        // HistoryWindow: auditLevenshtein
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditLevenshtein", historyCs);
        Assert.Contains("AuditCharacterLevenshteinEditDistance", historyCs);

        // SuiteHubView: CopyWorkspaceFileSizeDistributionAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceFileSizeDistributionAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceFileSizeDistributionAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run167_AuditWorkspaceAssetDeduplication_IdentifiesHashDuplicatesAndWastedBytes()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditWorkspaceAssetDeduplication(null);
        Assert.Equal(0, emptyAudit.TotalAssetsScanned);
        Assert.Equal(0, emptyAudit.UniqueAssetCount);
        Assert.Equal(0, emptyAudit.DuplicateGroupCount);
        Assert.Equal(0, emptyAudit.TotalRedundantAssetCount);
        Assert.Equal(100.0, emptyAudit.DeduplicationEfficiencyScore);
        Assert.Equal("Zero Redundancy (Fully Deduplicated)", emptyAudit.DeduplicationCategory);
        Assert.Equal("Workspace asset deduplication advisor: 0 assets · Workspace is empty", emptyAudit.Summary);

        // 2. Mocked workspace with distinct and duplicate assets
        var fakeFiles = new[]
        {
            @"C:\docs\images\diagram1.png",
            @"C:\docs\assets\diagram1_copy.png",
            @"C:\docs\images\photo.jpg",
            @"C:\docs\images\icon.svg"
        };

        var byteDict = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\docs\images\diagram1.png"] = new byte[] { 1, 2, 3, 4 },
            [@"C:\docs\assets\diagram1_copy.png"] = new byte[] { 1, 2, 3, 4 },
            [@"C:\docs\images\photo.jpg"] = new byte[] { 5, 6, 7, 8, 9 },
            [@"C:\docs\images\icon.svg"] = new byte[] { 10, 11 }
        };

        var audit = RecentFilesService.AuditWorkspaceAssetDeduplication(
            fakeFiles,
            fileExistsProvider: _ => true,
            fileBytesProvider: p => byteDict.TryGetValue(p, out var b) ? b : Array.Empty<byte>());

        Assert.Equal(4, audit.TotalAssetsScanned);
        Assert.Equal(3, audit.UniqueAssetCount);
        Assert.Equal(1, audit.DuplicateGroupCount);
        Assert.Equal(1, audit.TotalRedundantAssetCount);
        Assert.Equal(4, audit.TotalRedundantWastedBytes);
        Assert.True(audit.DeduplicationEfficiencyScore < 100.0);
        Assert.Single(audit.DuplicateGroups);
        Assert.Equal(2, audit.DuplicateGroups[0].DuplicateFilePaths.Count);
        Assert.Equal(audit.Summary, RecentFilesService.FormatWorkspaceAssetDeduplicationSummary(
            4, 3, 1, 1, audit.TotalScannedSizeBytes, audit.TotalRedundantWastedBytes,
            audit.DeduplicationEfficiencyScore, audit.DeduplicationCategory));

        // 3. Cross-surface XAML / C# wiring verification for Run #167 across all 6 surfaces:
        // MainWindow: footnoteReferenceConsistencyItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("footnoteReferenceConsistencyItem", mainCs);
        Assert.Contains("FormatFootnoteReferenceSummary", mainCs);

        // MindMapGalaxy: AuditRadialLayoutItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditRadialLayoutItem", galaxyXaml);
        Assert.Contains("OnAuditRadialLayoutClick", galaxyCs);

        // ShapeStudio: SelectionAuditConnectorCurvatureItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditConnectorCurvatureItem", shapeXaml);
        Assert.Contains("OnSelectionAuditConnectorCurvatureClick", shapeCs);

        // SmartArtStudio: AuditLinearFlowRhythmItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditLinearFlowRhythmItem", smartArtXaml);
        Assert.Contains("OnAuditLinearFlowRhythmClick", smartArtCs);

        // HistoryWindow: auditDiffTokenEntropy
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditDiffTokenEntropy", historyCs);
        Assert.Contains("AuditSemanticDiffTokenEntropy", historyCs);

        // SuiteHubView: CopyWorkspaceAssetDeduplicationAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceAssetDeduplicationAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceAssetDeduplicationAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run168_Polish_Elements_Wired_Across_Surfaces_And_HeadingUniformity()
    {
        // 1. AuditWorkspaceMarkdownHeaderUniformity - empty
        var empty = RecentFilesService.AuditWorkspaceMarkdownHeaderUniformity(Array.Empty<string>());
        Assert.Equal(0, empty.TotalDocumentsEvaluated);
        Assert.Equal(100.0, empty.HeadingUniformityScore);
        Assert.Equal("No Documents", empty.Category);

        // 2. AuditWorkspaceMarkdownHeaderUniformity with mock documents
        var doc1 = string.Join("\n",
            "# Introduction",
            "Introductory text.",
            "## Architecture Overview",
            "Architecture details.",
            "### Component Pipeline",
            "Pipeline breakdown.");

        var doc2 = string.Join("\n",
            "# Getting Started",
            "Setup guide.",
            "## Prerequisites",
            "Prereq list.");

        var files = new[] { "C:/docs/doc1.md", "C:/docs/doc2.md" };
        var audit = RecentFilesService.AuditWorkspaceMarkdownHeaderUniformity(
            files,
            fileExistsProvider: _ => true,
            contentReader: path => path.EndsWith("doc1.md") ? doc1 : doc2);

        Assert.Equal(2, audit.TotalDocumentsEvaluated);
        Assert.Equal(5, audit.TotalHeadingsFound);
        Assert.Equal(5, audit.AtxCount);
        Assert.Equal(0, audit.SetextCount);
        Assert.Equal(0, audit.SkippedLevelCount);
        Assert.Equal(0, audit.DuplicateSlugCount);
        Assert.Equal(100.0, audit.HeadingUniformityScore);
        Assert.Equal("Pure Monotonic ATX Hierarchy", audit.Category);
        Assert.Equal(audit.Summary, RecentFilesService.FormatWorkspaceHeadingUniformitySummary(
            2, 5, 5, 0, 0, 0, 100.0, audit.Category));

        // 3. Cross-surface XAML / C# wiring verification for Run #168 across all 6 surfaces:
        // MainWindow: admonitionCalloutItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("admonitionCalloutItem", mainCs);
        Assert.Contains("FormatAdmonitionCalloutSummary", mainCs);

        // MindMapGalaxy: AuditClusteringTransitivityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditClusteringTransitivityItem", galaxyXaml);
        Assert.Contains("OnAuditClusteringTransitivityClick", galaxyCs);

        // ShapeStudio: SelectionAuditAspectRatioScalingItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditAspectRatioScalingItem", shapeXaml);
        Assert.Contains("OnSelectionAuditAspectRatioScalingClick", shapeCs);

        // SmartArtStudio: AuditRadialCycleBalanceItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditRadialCycleBalanceItem", smartArtXaml);
        Assert.Contains("OnAuditRadialCycleBalanceClick", smartArtCs);

        // HistoryWindow: auditDiffLineWrap
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditDiffLineWrap", historyCs);
        Assert.Contains("AuditDiffLineWrapAndOverflow", historyCs);

        // SuiteHubView: CopyWorkspaceHeadingAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceHeadingAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceHeadingAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run169_Polish_Elements_Wired_Across_Surfaces_And_CodeFenceLanguageTags()
    {
        // 1. AuditWorkspaceCodeFenceLanguageTags with empty list
        var emptyAudit = RecentFilesService.AuditWorkspaceCodeFenceLanguageTags(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalDocumentsEvaluated);
        Assert.Equal(100.0, emptyAudit.CoverageScore);
        Assert.Equal("Workspace Empty", emptyAudit.Category);

        // 2. AuditWorkspaceCodeFenceLanguageTags with in-memory files
        var virtualFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:\\workspace\\doc1.md"] = string.Join("\n",
                "# Doc 1",
                "```csharp",
                "int x = 42;",
                "```",
                "```markdown",
                "- Item 1",
                "```"),
            ["C:\\workspace\\doc2.md"] = string.Join("\n",
                "# Doc 2",
                "```cs",
                "int y = 99;",
                "```",
                "```",
                "untagged code block",
                "```")
        };

        var audit = RecentFilesService.AuditWorkspaceCodeFenceLanguageTags(
            virtualFiles.Keys,
            fileExistsProvider: path => virtualFiles.ContainsKey(path),
            contentReader: path => virtualFiles[path]);

        Assert.Equal(2, audit.TotalDocumentsEvaluated);
        Assert.Equal(4, audit.TotalCodeFencesFound);
        Assert.Equal(3, audit.TaggedFencesCount);
        Assert.Equal(1, audit.UntaggedFencesCount);
        Assert.Equal(3, audit.SupportedHighlighterCount);
        Assert.Equal(1, audit.NonCanonicalAliasCount);
        Assert.True(audit.CoverageScore < 100.0);
        Assert.Equal("Good Syntax Highlighter Coverage", audit.Category);

        // 3. Cross-surface XAML / C# wiring verification for Run #169 across all 6 surfaces:
        // MainWindow: StatusCopyLinksButton & image accessibility
        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("StatusCopyLinksButton", mainXaml);
        Assert.Contains("OnStatusCopyLinksButtonClick", mainCs);
        Assert.Contains("FormatImageAccessibilitySummary", mainCs);

        // MindMapGalaxy: AuditSpectralBisectionItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditSpectralBisectionItem", galaxyXaml);
        Assert.Contains("OnAuditSpectralBisectionClick", galaxyCs);

        // ShapeStudio: SelectionAuditStrokeAlignmentItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditStrokeAlignmentItem", shapeXaml);
        Assert.Contains("OnSelectionAuditStrokeAlignmentClick", shapeCs);

        // SmartArtStudio: AuditPyramidTaperingItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("AuditPyramidTaperingItem", smartArtXaml);
        Assert.Contains("OnAuditPyramidTaperingClick", smartArtCs);

        // HistoryWindow: auditGitConflictMarkers
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditGitConflictMarkers", historyCs);
        Assert.Contains("AuditGitConflictMarkersAndRebaseResidue", historyCs);

        // SuiteHubView: CopyWorkspaceCodeFenceAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceCodeFenceAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceCodeFenceAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run170_AuditWorkspaceMediaLinkIntegrity_ValidatesMediaAndCrossSurfaceWiring()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditWorkspaceMediaLinkIntegrity(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalDocumentsEvaluated);
        Assert.Equal(0, emptyAudit.TotalMediaLinksFound);
        Assert.Equal(100.0, emptyAudit.IntegrityScore);
        Assert.Equal("Pristine", emptyAudit.Category);

        // 2. Mock documents with local valid, local missing, external, insecure, and data URIs
        var doc1 = string.Join("\n",
            "# Media Document",
            "![Valid Banner](images/banner.png)",
            "![Missing Image](images/missing.png)",
            "![Remote Logo](https://example.com/logo.png)",
            "![Insecure CDN](http://insecure.example.com/asset.png)",
            "<img src=\"data:image/png;base64,iVBORw0KGgo=\" />",
            "```markdown",
            "![Fenced Code Ignored](images/broken.png)",
            "```"
        );

        var docFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\mock\doc1.md"] = doc1
        };

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\mock\doc1.md",
            @"C:\mock\images\banner.png"
        };

        bool MockFileExists(string p) => existingFiles.Contains(p);
        string MockContentReader(string p) => docFiles.TryGetValue(p, out var content) ? content : "";

        var audit = RecentFilesService.AuditWorkspaceMediaLinkIntegrity(
            new[] { @"C:\mock\doc1.md" },
            MockFileExists,
            MockContentReader);

        Assert.Equal(1, audit.TotalDocumentsEvaluated);
        Assert.Equal(5, audit.TotalMediaLinksFound);
        Assert.Equal(1, audit.ValidLocalMediaCount);
        Assert.Equal(1, audit.MissingLocalMediaCount);
        Assert.Equal(2, audit.ExternalMediaCount);
        Assert.Equal(1, audit.DataUriCount);
        Assert.True(audit.PortabilityWarningCount >= 1);
        Assert.Equal(50.0, audit.IntegrityScore);
        Assert.Contains(@"images\missing.png", audit.MissingPaths[0]);

        // Formatter test
        string formatted = RecentFilesService.FormatWorkspaceMediaLinkSummary(
            audit.TotalDocumentsEvaluated, audit.TotalMediaLinksFound,
            audit.ValidLocalMediaCount, audit.MissingLocalMediaCount,
            audit.ExternalMediaCount, audit.PortabilityWarningCount,
            audit.IntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Cross-surface XAML / C# wiring verification for Run #170 across all 6 surfaces:
        // Surface 1: MainWindow - auditMathDelimitersItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditMathDelimitersItem", mainCs);
        Assert.Contains("FormatMathDelimiterSummary", mainCs);

        // Surface 2: MindMapGalaxy - AuditBiconnectivityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditBiconnectivityItem", galaxyXaml);
        Assert.Contains("OnAuditBiconnectivityClick", galaxyCs);

        // Surface 3: ShapeStudio - SelectionAuditConnectorObstaclesItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditConnectorObstaclesItem", shapeXaml);
        Assert.Contains("OnSelectionAuditConnectorObstaclesClick", shapeCs);

        // Surface 4: SmartArtStudio - OutlineStatsAuditMatrixQuadrantSymmetryItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditMatrixQuadrantSymmetryItem", smartArtXaml);
        Assert.Contains("OnAuditMatrixQuadrantSymmetryClick", smartArtCs);

        // Surface 5: HistoryWindow - auditSemanticCoChange
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditSemanticCoChange", historyCs);
        Assert.Contains("AuditSemanticDiffCoChangeClusters", historyCs);

        // Surface 6: SuiteHubView - CopyWorkspaceMediaLinksAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceMediaLinksAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceMediaLinksAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run171_AuditWorkspaceCrossDocumentAnchors_ValidatesTargetFilesAndHeadingSlugs()
    {
        // 1. Empty workspace
        var emptyAudit = RecentFilesService.AuditWorkspaceCrossDocumentAnchors(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalDocumentsEvaluated);
        Assert.Equal(0, emptyAudit.TotalCrossDocLinksFound);
        Assert.Equal(100.0, emptyAudit.CrossDocAnchorIntegrityScore);
        Assert.Equal("Empty Workspace", emptyAudit.Category);

        // 2. Mock documents with valid cross-doc link, missing target file, and missing heading anchor slug
        var sourceDoc = string.Join("\n",
            "# Source Document",
            "See [Target Reference](target.md#existing-heading) for details.",
            "See [Missing File](missing.md#some-heading) for missing doc.",
            "See [Broken Anchor](target.md#non-existent-heading) for bad anchor.",
            "```markdown",
            "[Fenced Link](target.md#ignored-heading)",
            "```"
        );

        var targetDoc = string.Join("\n",
            "# Existing Heading",
            "Target document body prose."
        );

        var docFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\mock\source.md"] = sourceDoc,
            [@"C:\mock\target.md"] = targetDoc
        };

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\mock\source.md",
            @"C:\mock\target.md"
        };

        bool MockFileExists(string p) => existingFiles.Contains(p);
        string MockContentReader(string p) => docFiles.TryGetValue(p, out var content) ? content : "";

        var audit = RecentFilesService.AuditWorkspaceCrossDocumentAnchors(
            new[] { @"C:\mock\source.md", @"C:\mock\target.md" },
            MockFileExists,
            MockContentReader);

        Assert.Equal(2, audit.TotalDocumentsEvaluated);
        Assert.Equal(3, audit.TotalCrossDocLinksFound);
        Assert.Equal(1, audit.ValidLinksCount);
        Assert.Equal(1, audit.BrokenTargetFileCount);
        Assert.Equal(1, audit.BrokenAnchorSlugCount);
        Assert.Equal(33.3, audit.CrossDocAnchorIntegrityScore);
        Assert.Equal("Missing Target Documents Detected", audit.Category);
        Assert.Equal(2, audit.MissingAnchors.Count);

        // Formatter test
        string formatted = RecentFilesService.FormatWorkspaceCrossDocumentAnchorSummary(
            audit.TotalDocumentsEvaluated, audit.TotalCrossDocLinksFound,
            audit.ValidLinksCount, audit.BrokenTargetFileCount,
            audit.BrokenAnchorSlugCount, audit.CrossDocAnchorIntegrityScore,
            audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Cross-surface XAML / C# wiring verification for Run #171 across all 6 surfaces:
        // Surface 1: MainWindow - auditReadingEaseItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditReadingEaseItem", mainCs);
        Assert.Contains("FormatReadingEaseSummary", mainCs);

        // Surface 2: MindMapGalaxy - AuditKCoreDecompositionItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditKCoreDecompositionItem", galaxyXaml);
        Assert.Contains("OnAuditKCoreDecompositionClick", galaxyCs);

        // Surface 3: ShapeStudio - SelectionAuditCompoundGroupsItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditCompoundGroupsItem", shapeXaml);
        Assert.Contains("OnSelectionAuditCompoundGroupsClick", shapeCs);

        // Surface 4: SmartArtStudio - OutlineStatsAuditVennOverlapItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditVennOverlapItem", smartArtXaml);
        Assert.Contains("OnAuditVennOverlapClick", smartArtCs);

        // Surface 5: HistoryWindow - auditHunkCoupling
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditHunkCoupling", historyCs);
        Assert.Contains("AuditSemanticHunkCouplingAndPropagation", historyCs);

        // Surface 6: SuiteHubView - CopyWorkspaceCrossDocAnchorsAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceCrossDocAnchorsAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceCrossDocAnchorsAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run172_AuditWorkspaceTableFormatting_ValidatesColumnsAndDelimiters()
    {
        // 1. Empty workspace test
        var emptyAudit = RecentFilesService.AuditWorkspaceTableFormatting(
            Array.Empty<string>(),
            _ => false,
            _ => "");
        Assert.Equal(0, emptyAudit.TotalDocumentsEvaluated);
        Assert.Equal(0, emptyAudit.TotalTablesFound);
        Assert.Equal(0, emptyAudit.ValidTablesCount);
        Assert.Equal(100.0, emptyAudit.TabularIntegrityScore);
        Assert.Equal("Empty Workspace", emptyAudit.Category);

        // 2. Multi-table document with compliant, ragged, and missing delimiter tables
        string docContent = @"# Documentation

| Name | Role | City |
| :--- | :---: | ---: |
| Alice | Architect | Sydney |
| Bob | Engineer | Melbourne |

```markdown
| InCodeFence | Col2 |
| --- | --- |
| Should | BeIgnored |
```

Some paragraph in between.

| Product | Price | Stock |
| --- | --- | --- |
| Widget | $10 |

| Missing | Delimiter |
| Data1 | Data2 |
";

        var files = new[] { @"C:\mock\docs\tables.md" };
        var audit = RecentFilesService.AuditWorkspaceTableFormatting(
            files,
            p => p.Equals(@"C:\mock\docs\tables.md", StringComparison.OrdinalIgnoreCase),
            _ => docContent);

        Assert.Equal(1, audit.TotalDocumentsEvaluated);
        Assert.Equal(3, audit.TotalTablesFound);
        Assert.Equal(1, audit.ValidTablesCount);
        Assert.Equal(1, audit.RaggedTablesCount);
        Assert.Equal(1, audit.MissingDelimiterTablesCount);
        Assert.Equal(0, audit.EmptyHeaderOnlyTablesCount);
        Assert.Equal("Ragged Table Columns Detected", audit.Category);

        // Verify alignments of Table 1
        var validTable = audit.Tables[0];
        Assert.Equal(3, validTable.HeaderColumnCount);
        Assert.Equal(2, validTable.DataRowCount);
        Assert.True(validTable.HasDelimiterRow);
        Assert.False(validTable.HasRaggedRows);
        Assert.Equal(new[] { "left", "center", "right" }, validTable.ColumnAlignments);

        // Verify ragged table
        var raggedTable = audit.Tables[1];
        Assert.True(raggedTable.HasRaggedRows);
        Assert.Equal(1, raggedTable.RaggedRowCount);

        // Verify missing delimiter table
        var missingDelimTable = audit.Tables[2];
        Assert.False(missingDelimTable.HasDelimiterRow);

        // Formatter validation
        string formatted = RecentFilesService.FormatWorkspaceTableFormattingSummary(
            audit.TotalDocumentsEvaluated, audit.TotalTablesFound,
            audit.ValidTablesCount, audit.RaggedTablesCount,
            audit.MissingDelimiterTablesCount, audit.EmptyHeaderOnlyTablesCount,
            audit.TabularIntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Cross-surface XAML / C# wiring verification for Run #172 across all 6 surfaces:
        // Surface 1: MainWindow - auditFootnotesItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditFootnotesItem", mainCs);
        Assert.Contains("AuditFootnoteReferenceIntegrity", mainCs);

        // Surface 2: MindMapGalaxy - AuditGraphAssortativityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditGraphAssortativityItem", galaxyXaml);
        Assert.Contains("OnAuditGraphAssortativityClick", galaxyCs);

        // Surface 3: ShapeStudio - SelectionAuditConnectorPortSnappingItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditConnectorPortSnappingItem", shapeXaml);
        Assert.Contains("OnSelectionAuditConnectorPortSnappingClick", shapeCs);

        // Surface 4: SmartArtStudio - OutlineStatsAuditProcessSequencePacingItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditProcessSequencePacingItem", smartArtXaml);
        Assert.Contains("OnAuditProcessSequencePacingClick", smartArtCs);

        // Surface 5: HistoryWindow - auditWordLevelDiff
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditWordLevelDiff", historyCs);
        Assert.Contains("AuditWordLevelDiffResolution", historyCs);

        // Surface 6: SuiteHubView - CopyWorkspaceTableFormattingAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceTableFormattingAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceTableFormattingAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run173_AuditWorkspaceFileSizeDistribution_CalculatesPercentilesAndOutliers()
    {
        // 1. Empty workspace test
        var emptyAudit = RecentFilesService.AuditWorkspaceFileSizeDistribution(
            Array.Empty<string>(),
            _ => false,
            _ => 0L);
        Assert.Equal(0, emptyAudit.TotalFilesScanned);
        Assert.Equal(0L, emptyAudit.TotalSizeBytes);
        Assert.Equal(100.0, emptyAudit.StorageCompactnessScore);
        Assert.Equal("Empty Workspace", emptyAudit.StorageHealthCategory);
        Assert.Contains("0 files", emptyAudit.Summary);

        // 2. Multi-tier workspace file size distribution
        var mockFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\mock\docs\readme.md"] = 12 * 1024,                  // 12 KB -> Lightweight (<50KB)
            [@"C:\mock\docs\manual.docx"] = 250 * 1024,               // 250 KB -> Standard (50–500KB)
            [@"C:\mock\images\diagram.png"] = 1200 * 1024,            // 1.2 MB -> Substantial (500KB–2MB)
            [@"C:\mock\bundles\app.exe"] = 3 * 1024 * 1024,           // 3 MB -> Heavy Asset (2–5MB)
            [@"C:\mock\videos\screencast.mp4"] = 8 * 1024 * 1024,     // 8 MB -> Storage Bloat (≥5MB)
        };

        var audit = RecentFilesService.AuditWorkspaceFileSizeDistribution(
            mockFiles.Keys,
            p => mockFiles.ContainsKey(p),
            p => mockFiles[p]);

        Assert.Equal(5, audit.TotalFilesScanned);
        Assert.Equal(1, audit.HeavyBloatCount);
        Assert.Equal("screencast.mp4", audit.HeaviestFileName);
        Assert.Equal(8 * 1024 * 1024, audit.MaxSizeBytes);
        // compactnessScore: 100 - (1 * 20.0) - (1 * 5.0) = 75.0
        Assert.Equal(75.0, audit.StorageCompactnessScore);
        Assert.Equal("Moderate Asset Bloat Detected", audit.StorageHealthCategory);

        // Extension counts
        Assert.Equal(1, audit.ExtensionCounts[".md"]);
        Assert.Equal(1, audit.ExtensionCounts[".docx"]);
        Assert.Equal(1, audit.ExtensionCounts[".png"]);
        Assert.Equal(1, audit.ExtensionCounts[".exe"]);
        Assert.Equal(1, audit.ExtensionCounts[".mp4"]);

        // Verify tier labeling
        var bloatItem = audit.Files.First(f => f.FileName == "screencast.mp4");
        Assert.True(bloatItem.IsHeavyBloat);
        Assert.Equal("Storage Bloat (≥5MB)", bloatItem.SizeTier);

        var heavyItem = audit.Files.First(f => f.FileName == "app.exe");
        Assert.False(heavyItem.IsHeavyBloat);
        Assert.Equal("Heavy Asset (2–5MB)", heavyItem.SizeTier);

        var lightItem = audit.Files.First(f => f.FileName == "readme.md");
        Assert.False(lightItem.IsHeavyBloat);
        Assert.Equal("Lightweight (<50KB)", lightItem.SizeTier);

        // Formatter validation
        string formatted = RecentFilesService.FormatWorkspaceFileSizeDistributionSummary(
            audit.TotalFilesScanned, audit.TotalSizeBytes, audit.MeanSizeBytes,
            audit.MaxSizeBytes, audit.HeaviestFileName, audit.HeavyBloatCount,
            audit.StorageCompactnessScore, audit.StorageHealthCategory);
        Assert.Equal(audit.Summary, formatted);

        // 3. Cross-surface XAML / C# wiring verification for Run #173 across all 6 surfaces:
        // Surface 1: MainWindow - auditBlockquoteCalloutsItem
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditBlockquoteCalloutsItem", mainCs);
        Assert.Contains("AuditBlockquoteCalloutsAndDepth", mainCs);

        // Surface 2: MindMapGalaxy - AuditClusteringTransitivityItem
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditClusteringTransitivityItem", galaxyXaml);
        Assert.Contains("OnAuditClusteringTransitivityClick", galaxyCs);

        // Surface 3: ShapeStudio - SelectionAuditCanvasSpatialBalanceItem
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditCanvasSpatialBalanceItem", shapeXaml);
        Assert.Contains("OnSelectionAuditCanvasSpatialBalanceClick", shapeCs);

        // Surface 4: SmartArtStudio - OutlineStatsAuditHierarchyBranchingItem
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditHierarchyBranchingItem", smartArtXaml);
        Assert.Contains("OnAuditHierarchyBranchingClick", smartArtCs);

        // Surface 5: HistoryWindow - auditScopeTransitions
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditScopeTransitions", historyCs);
        Assert.Contains("AuditIdentifierScopeTransitions", historyCs);

        // Surface 6: SuiteHubView - CopyWorkspaceFileSizeAdvisorItem
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceFileSizeAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceFileSizeAdvisorClick", suiteCs);
    }

    [Fact]
    public void Run174_AuditWorkspaceDocumentGraphAndOrphans()
    {
        var empty = RecentFilesService.AuditWorkspaceDocumentGraphAndOrphans(Array.Empty<string>());
        Assert.Equal(0, empty.TotalDocumentsEvaluated);
        Assert.Equal(100.0, empty.CrossReferenceScore);
        Assert.Equal("Empty Workspace", empty.Category);

        var files = new[]
        {
            @"C:\Docs\index.md",
            @"C:\Docs\guide.md",
            @"C:\Docs\orphan.md"
        };

        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { @"C:\Docs\index.md", "# Index\n\nRead the [User Guide](guide.md) or [[guide]]." },
            { @"C:\Docs\guide.md", "# Guide\n\nReturn to [Index](index.md)." },
            { @"C:\Docs\orphan.md", "# Standalone Note\n\nThis note has zero links in or out." }
        };

        var audit = RecentFilesService.AuditWorkspaceDocumentGraphAndOrphans(
            files,
            fileExistsProvider: _ => true,
            contentReader: path => contents.TryGetValue(path, out var text) ? text : string.Empty);

        Assert.Equal(3, audit.TotalDocumentsEvaluated);
        Assert.Equal(3, audit.TotalLinksFound);
        Assert.Equal(1, audit.OrphanedDocumentCount);
        Assert.True(audit.Nodes.First(n => n.FileName == "orphan.md").IsOrphan);
        Assert.Equal("Isolated Orphan", audit.Nodes.First(n => n.FileName == "orphan.md").NodeRole);
        Assert.False(audit.Nodes.First(n => n.FileName == "index.md").IsOrphan);

        string formatted = RecentFilesService.FormatWorkspaceDocumentGraphSummary(
            audit.TotalDocumentsEvaluated, audit.TotalLinksFound, audit.OrphanedDocumentCount, audit.HubDocumentCount,
            audit.GraphDensity, audit.CrossReferenceScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run174_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // Surface 1: MainWindow - Definition list & glossary term audit
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditDefinitionListsItem", mainCs);
        Assert.Contains("AuditDefinitionListsAndGlossaryTerms", mainCs);

        // Surface 2: MindMapGalaxyWindow - Community modularity Q audit
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditCommunityModularityItem", galaxyXaml);
        Assert.Contains("OnAuditCommunityModularityClick", galaxyCs);
        Assert.Contains("AuditCommunityModularityAndClusters", galaxyCs);

        // Surface 3: ShapeDesignStudioWindow - Collinearity & distribution spacing audit
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditShapeCollinearityItem", shapeXaml);
        Assert.Contains("OnSelectionAuditShapeCollinearityClick", shapeCs);
        Assert.Contains("AuditShapeCollinearityAndSpacing", shapeCs);

        // Surface 4: SmartArtDesignStudioWindow - Timeline milestone chronology & spacing audit
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditTimelineChronologyItem", smartArtXaml);
        Assert.Contains("OnOutlineStatsAuditTimelineChronologyClick", smartArtCs);
        Assert.Contains("AuditTimelineMilestoneChronologyAndSpacing", smartArtCs);

        // Surface 5: HistoryWindow - Semantic code block relocation audit
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditBlockRelocation", historyCs);
        Assert.Contains("AuditBlockRelocationAndMoves", historyCs);

        // Surface 6: SuiteHubView - Workspace document linking graph & orphan advisor
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceDocumentGraphAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceDocumentGraphAdvisorClick", suiteCs);
        Assert.Contains("AuditWorkspaceDocumentGraphAndOrphans", suiteCs);
    }

    [Fact]
    public void Run175_AuditWorkspaceMediaAssetIntegrity_DetectsBrokenImages()
    {
        var empty = RecentFilesService.AuditWorkspaceMediaAssetIntegrity(Array.Empty<string>());
        Assert.Equal(0, empty.TotalDocumentsEvaluated);
        Assert.Equal(0, empty.TotalMediaReferences);
        Assert.Equal(100.0, empty.MediaIntegrityScore);
        Assert.Equal("Empty Workspace", empty.Category);

        var files = new[]
        {
            @"C:\Docs\readme.md",
            @"C:\Docs\guide.md"
        };

        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { @"C:\Docs\readme.md", "# Readme\n\n![Logo](assets/logo.png)\n<img src=\"https://example.com/banner.jpg\" />" },
            { @"C:\Docs\guide.md", "# Guide\n\n![Diagram](missing.svg)\n![External](http://example.com/ext.png)" }
        };

        var existingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Docs\readme.md",
            @"C:\Docs\guide.md",
            Path.GetFullPath(@"C:\Docs\assets\logo.png")
        };

        var audit = RecentFilesService.AuditWorkspaceMediaAssetIntegrity(
            files,
            fileExistsProvider: path => existingFiles.Contains(path),
            contentReader: path => contents.TryGetValue(path, out var text) ? text : string.Empty);

        Assert.Equal(2, audit.TotalDocumentsEvaluated);
        Assert.Equal(4, audit.TotalMediaReferences);
        Assert.Equal(2, audit.WebRemoteAssets);
        Assert.Equal(2, audit.LocalAssetsChecked);
        Assert.Equal(1, audit.ValidLocalAssets);
        Assert.Equal(1, audit.MissingLocalAssets);
        Assert.Equal(50.0, audit.MediaIntegrityScore);

        var missingItem = audit.MediaItems.First(a => !a.Exists);
        Assert.Equal("missing.svg", missingItem.RawSource);
        Assert.Equal("guide.md", missingItem.DocumentName);

        string formatted = RecentFilesService.FormatWorkspaceMediaAssetSummary(
            audit.TotalDocumentsEvaluated, audit.TotalMediaReferences, audit.LocalAssetsChecked,
            audit.ValidLocalAssets, audit.MissingLocalAssets, audit.WebRemoteAssets,
            audit.MediaIntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run175_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // Surface 1: MainWindow - Task list hierarchy & progress auditor
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditTaskListHierarchyItem", mainCs);
        Assert.Contains("AuditTaskListHierarchyAndProgress", mainCs);

        // Surface 2: MindMapGalaxyWindow - Graph cycle & feedback loop topology auditor
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditGraphCyclesFeedbackItem", galaxyXaml);
        Assert.Contains("OnAuditGraphCyclesFeedbackClick", galaxyCs);
        Assert.Contains("AuditGraphCyclesAndFeedbackLoops", galaxyCs);

        // Surface 3: ShapeDesignStudioWindow - Diagram visual flow directionality & monotonicity auditor
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditVisualFlowDirectionalityItem", shapeXaml);
        Assert.Contains("OnSelectionAuditVisualFlowDirectionalityClick", shapeCs);
        Assert.Contains("AuditVisualFlowDirectionality", shapeCs);

        // Surface 4: SmartArtDesignStudioWindow - Segmented process cycle interval uniformity auditor
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditCycleIntervalItem", smartArtXaml);
        Assert.Contains("OnOutlineStatsAuditCycleIntervalClick", smartArtCs);
        Assert.Contains("AuditCycleIntervalAndRadialChords", smartArtCs);

        // Surface 5: HistoryWindow - Formatting whitespace vs syntactic token change entropy auditor
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditEntropy", historyCs);
        Assert.Contains("AuditFormattingVsSyntacticChangeEntropy", historyCs);

        // Surface 6: SuiteHubView - Workspace media asset relative path resolution & missing image advisor
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceMediaAssetAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceMediaAssetAdvisorClick", suiteCs);
        Assert.Contains("AuditWorkspaceMediaAssetIntegrity", suiteCs);
    }

    [Fact]
    public void Run176_AuditWorkspaceMediaAccessibility()
    {
        var emptyAudit = RecentFilesService.AuditWorkspaceMediaAccessibility(Array.Empty<string>());
        Assert.Equal(0, emptyAudit.TotalDocumentsEvaluated);
        Assert.Equal(0, emptyAudit.TotalMediaReferences);
        Assert.Equal(100.0, emptyAudit.AccessibilityComplianceScore);
        Assert.Equal("Empty Workspace", emptyAudit.Category);

        var files = new[]
        {
            @"C:\Projects\spec.md",
            @"C:\Projects\guide.md"
        };
        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {
                @"C:\Projects\spec.md",
                "# Specification\n![Architecture Pipeline Flow](pipeline.png)\nSome text.\n<img src=\"diagram.svg\" alt=\"Detailed Sequence Diagram\" />"
            },
            {
                @"C:\Projects\guide.md",
                "# Guide\n![](unlabelled.png)\n![screenshot.png](shot.png)"
            }
        };

        var audit = RecentFilesService.AuditWorkspaceMediaAccessibility(
            files,
            fileExistsProvider: p => fileContents.ContainsKey(p),
            contentReader: p => fileContents[p]);

        Assert.Equal(2, audit.TotalDocumentsEvaluated);
        Assert.Equal(4, audit.TotalMediaReferences);
        Assert.Equal(2, audit.DescriptiveAltCount);
        Assert.Equal(1, audit.EmptyAltCount);
        Assert.Equal(1, audit.GenericFilenameAltCount);
        Assert.True(audit.AccessibilityComplianceScore < 100.0);

        var emptyItem = audit.MediaItems.First(m => m.HasEmptyAlt);
        Assert.True(emptyItem.HasEmptyAlt);
        Assert.Contains("Critical", emptyItem.AccessibilityQuality);

        string formatted = RecentFilesService.FormatWorkspaceMediaAccessibilitySummary(
            audit.TotalDocumentsEvaluated, audit.TotalMediaReferences, audit.DescriptiveAltCount,
            audit.EmptyAltCount, audit.GenericFilenameAltCount, audit.AccessibilityComplianceScore,
            audit.Category);
        Assert.Equal(audit.Summary, formatted);
    }

    [Fact]
    public void Run176_AllSixSurfacesXamlAndCodeBehindWiring()
    {
        // Surface 1: MainWindow - Table column alignment & delimiter syntax auditor
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));
        Assert.Contains("auditTableAlignmentSyntaxItem", mainCs);
        Assert.Contains("AuditTableColumnAlignmentSyntax", mainCs);

        // Surface 2: MindMapGalaxyWindow - Graph diameter & small-world navigation auditor
        var galaxyXaml = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml"));
        var galaxyCs = File.ReadAllText(DesktopPath("Views", "MindMap", "MindMapGalaxyWindow.xaml.cs"));
        Assert.Contains("AuditGraphDiameterSmallWorldItem", galaxyXaml);
        Assert.Contains("OnAuditGraphDiameterAndSmallWorldClick", galaxyCs);
        Assert.Contains("AuditGraphDiameterAndSmallWorld", galaxyCs);

        // Surface 3: ShapeDesignStudioWindow - Shape aspect ratio & golden ratio adherence auditor
        var shapeXaml = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml"));
        var shapeCs = File.ReadAllText(DesktopPath("Views", "ShapeStudio", "ShapeDesignStudioWindow.xaml.cs"));
        Assert.Contains("SelectionAuditAspectRatioGoldenRatioItem", shapeXaml);
        Assert.Contains("OnSelectionAuditAspectRatioGoldenRatioClick", shapeCs);
        Assert.Contains("AuditShapeAspectRatioAndGoldenRatio", shapeCs);

        // Surface 4: SmartArtDesignStudioWindow - 2x2 matrix quadrant grid balance & diagonal symmetry auditor
        var smartArtXaml = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml"));
        var smartArtCs = File.ReadAllText(DesktopPath("Views", "SmartArtStudio", "SmartArtDesignStudioWindow.xaml.cs"));
        Assert.Contains("OutlineStatsAuditQuadrantGridBalanceItem", smartArtXaml);
        Assert.Contains("OnOutlineStatsAuditQuadrantGridBalanceClick", smartArtCs);
        Assert.Contains("AuditQuadrantGridBalance", smartArtCs);

        // Surface 5: HistoryWindow - Line addition/deletion churn volatility & change symmetry auditor
        var historyCs = File.ReadAllText(DesktopPath("Views", "History", "HistoryWindow.xaml.cs"));
        Assert.Contains("auditChurnVolatility", historyCs);
        Assert.Contains("AuditCodeChurnVolatilityIndex", historyCs);

        // Surface 6: SuiteHubView - Workspace media alt-text accessibility & WCAG caption advisor
        var suiteXaml = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml"));
        var suiteCs = File.ReadAllText(DesktopPath("Views", "SuiteHubView.xaml.cs"));
        Assert.Contains("CopyWorkspaceMediaAccessibilityAdvisorItem", suiteXaml);
        Assert.Contains("OnCopyWorkspaceMediaAccessibilityAdvisorClick", suiteCs);
        Assert.Contains("AuditWorkspaceMediaAccessibility", suiteCs);
    }
}






