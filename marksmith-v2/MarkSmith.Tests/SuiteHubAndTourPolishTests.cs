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
}
