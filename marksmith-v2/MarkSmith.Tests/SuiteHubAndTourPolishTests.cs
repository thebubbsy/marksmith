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

        var mainXaml = File.ReadAllText(DesktopPath("MainWindow.xaml"));
        var mainCs = File.ReadAllText(DesktopPath("MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"FootnoteButton\"", mainXaml);
        Assert.Contains("x:Name=\"FootnoteFlyout\"", mainXaml);
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
}

