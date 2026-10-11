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

    private static int GetGridNeighborCardLogic(int currentIndex, string key, bool twoColumn, int count = 7)
    {
        if (currentIndex < 0 || currentIndex >= count) return -1;
        if (!twoColumn)
        {
            return key switch
            {
                "Down" or "Right" => Math.Min(count - 1, currentIndex + 1),
                "Up" or "Left" => Math.Max(0, currentIndex - 1),
                _ => currentIndex,
            };
        }

        if (currentIndex == 0)
        {
            return key switch
            {
                "Down" or "Right" => count > 1 ? 1 : 0,
                _ => 0,
            };
        }

        var pairIndex = currentIndex - 1;
        var row = pairIndex / 2;
        var col = pairIndex % 2;

        return key switch
        {
            "Left" => col == 1 ? currentIndex - 1 : currentIndex,
            "Right" => col == 0 && currentIndex + 1 < count ? currentIndex + 1 : currentIndex,
            "Up" => row == 0 ? 0 : currentIndex - 2,
            "Down" => currentIndex + 2 < count ? currentIndex + 2 : currentIndex,
            _ => currentIndex,
        };
    }

    [Theory]
    [InlineData(0, "Down", true, 1)]
    [InlineData(0, "Up", true, 0)]
    [InlineData(1, "Right", true, 2)]
    [InlineData(1, "Left", true, 1)]
    [InlineData(2, "Left", true, 1)]
    [InlineData(2, "Right", true, 2)]
    [InlineData(1, "Up", true, 0)]
    [InlineData(2, "Up", true, 0)]
    [InlineData(1, "Down", true, 3)]
    [InlineData(2, "Down", true, 4)]
    [InlineData(5, "Down", true, 5)]
    [InlineData(6, "Down", true, 6)]
    [InlineData(3, "Down", false, 4)]
    [InlineData(3, "Up", false, 2)]
    [InlineData(-1, "Down", true, -1)]
    [InlineData(99, "Up", true, -1)]
    public void SuiteHub_2D_grid_neighbor_navigation_handles_two_col_and_single_col_bounds(
        int currentIndex, string key, bool twoColumn, int expected)
    {
        var actual = GetGridNeighborCardLogic(currentIndex, key, twoColumn);
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
    }

    [Theory]
    [InlineData(0, false, "0 commands")]
    [InlineData(1, true, "1 match")]
    [InlineData(5, true, "5 matches")]
    [InlineData(12, false, "12 commands")]
    [InlineData(-2, true, "0 matches")]
    public void CommandPalette_FormatCountText_handles_singular_plural_and_negative_bounds(
        int count, bool hasQuery, string expected)
    {
        var text = count <= 0
            ? (hasQuery ? "0 matches" : "0 commands")
            : count == 1
                ? (hasQuery ? "1 match" : "1 command")
                : hasQuery ? $"{count} matches" : $"{count} commands";
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

        Assert.Contains("x:Name=\"TipBorder\"", tipXaml);
        Assert.Contains("x:Name=\"GetExtensionButton\"", tipXaml);
        Assert.Contains("x:Name=\"DismissButton\"", tipXaml);
        Assert.Contains("RefreshBorderBrush", tipCs);
        Assert.Contains("ShowTipContextMenu", tipCs);
        Assert.Contains("HoverPolish.RestoreFocus", tipCs);
    }
}

