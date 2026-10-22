using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #229: one action per click across History, Suite Hub and the editor status bar. Buttons
// that already act on Click no longer repeat the action on DoubleTapped; Suite Hub's two count
// pills are keyboard-reachable buttons; History's menus advertise the shortcuts that actually work.
public class Run229OneActionPerClickTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

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

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static XDocument Xaml(params string[] parts) => XDocument.Load(Path.Combine(new[] { DesktopDir() }.Concat(parts).ToArray()));

    private static string HistoryCode => Read("Views", "History", "HistoryWindow.xaml.cs");
    private static XDocument HistoryXaml => Xaml("Views", "History", "HistoryWindow.xaml");
    private static string SuiteCode => Read("Views", "SuiteHubView.xaml.cs");
    private static XDocument SuiteXaml => Xaml("Views", "SuiteHubView.xaml");

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    /// <summary>The text of a method, from its signature to the matching closing brace.</summary>
    private static string Method(string code, string name)
    {
        var start = code.IndexOf($"void {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} not found");
        var open = code.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code.Substring(start, i - start + 1);
        }
        throw new InvalidOperationException($"{name} has no closing brace");
    }

    private static string BridgeTable(string code)
    {
        var start = code.IndexOf("KeyboardContextMenu.BridgeAll(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return code.Substring(start, code.IndexOf(");", start, StringComparison.Ordinal) - start);
    }

    [Theory]
    [InlineData("TakeCheckpointButton", "OnTakeCheckpointButtonDoubleTapped")]
    [InlineData("RestoreButton", "OnRestoreButtonDoubleTapped")]
    public void History_ActionButtons_DoNotRepeatTheirClickOnDoubleTap(string button, string removedHandler)
    {
        var el = Named(HistoryXaml, button);
        Assert.NotNull(el.Attribute("Click"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.DoesNotContain(removedHandler, HistoryCode);
        Assert.DoesNotContain("Double-click", (string?)el.Attribute(Ui + "ToolTipService.ToolTip") ?? (string?)el.Attribute("ToolTipService.ToolTip") ?? "");
        // Still reachable from Shift+F10.
        Assert.Contains($"({button}, On{button}RightTapped)", BridgeTable(HistoryCode));
    }

    [Fact]
    public void History_Menus_AdvertiseTheShortcutsThatActuallyWork()
    {
        var code = HistoryCode;
        var keys = Method(code, "OnRootKeyDown");
        // The truth the menus must match: Ctrl+Shift+S takes a checkpoint, Ctrl+Shift+C copies the diff patch.
        Assert.Matches(@"isCtrl && isShift && e\.Key == VirtualKey\.S\)\s*\{[^}]*OnTakeSnapshotClick", keys);
        Assert.Matches(@"isCtrl && isShift && e\.Key == VirtualKey\.C && [^\n]*\)\s*\{[^}]*CopyDiffPatchAsync", keys);

        Assert.DoesNotContain("Take checkpoint (Ctrl", code);
        Assert.DoesNotContain("Show starred versions only (Ctrl", code);
        Assert.DoesNotContain("Copy full file path (Ctrl", code);
        var checkpointItems = Regex.Matches(code, @"Text = ""Take checkpoint"",\s*Icon = new FontIcon \{ Glyph = ""\\uE74E"" \},\s*KeyboardAcceleratorTextOverride = ""Ctrl\+Shift\+S""");
        Assert.Equal(3, checkpointItems.Count);
    }

    [Fact]
    public void History_VersionTitleCopy_IsInTheSummaryMenu()
    {
        var code = HistoryCode;
        var menu = Method(code, "ShowDiffAreaContextMenu");
        Assert.Contains("\"Copy version title\"", menu);
        Assert.Contains("CopyDiffTitle()", menu);
        Assert.Contains("CopyDiffTitle();", Method(code, "OnDiffSummaryDoubleTapped"));
        // The keyboard path to that menu: Shift+F10 on the version-ID button.
        Assert.Contains("(CommitShaCopyPill, OnDiffSummaryRightTapped)", BridgeTable(code));
        var tip = (string?)Named(HistoryXaml, "DiffSummaryPanel").Attribute("ToolTipService.ToolTip");
        Assert.Contains("Shift+F10 on the version ID", tip);
    }

    [Fact]
    public void History_DiffSearchBox_KeepsItsCompactHeight()
    {
        // TextBox's default MinHeight (32) silently overrode Height="26", so the box stood taller than the pills beside it.
        var box = Named(HistoryXaml, "DiffSearchBox");
        Assert.Equal("26", (string?)box.Attribute("Height"));
        Assert.Equal("0", (string?)box.Attribute("MinHeight"));
    }

    [Fact]
    public void StatusBar_CursorPosition_OpensGoToLineOnceAndKeepsAlignInTheMenu()
    {
        var el = Named(Xaml("MainWindow.xaml"), "CursorPosButton");
        Assert.Equal("OnCursorPosButtonClick", (string?)el.Attribute("Click"));
        Assert.Null(el.Attribute("DoubleTapped"));
        var code = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("void OnCursorPosTextDoubleTapped", code);
        Assert.Contains("Align Preview to cursor position", Method(code, "OnCursorPosButtonRightTapped"));
    }

    [Fact]
    public void SuiteHub_RefreshRecentDocs_RefreshesOncePerClick()
    {
        var el = Named(SuiteXaml, "RefreshRecentDocsButton");
        Assert.Equal("OnRefreshRecentDocsClick", (string?)el.Attribute("Click"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)el.Attribute("ToolTipService.ToolTip"));
        Assert.DoesNotContain("OnRefreshRecentDocsButtonDoubleTapped", SuiteCode);
    }

    [Fact]
    public void SuiteHub_RecentDocsCountPill_IsAKeyboardButtonWithOneAction()
    {
        var el = Named(SuiteXaml, "RecentDocsCountPill");
        Assert.Equal("Button", el.Name.LocalName);
        Assert.Equal("OnRecentDocsCountPillClick", (string?)el.Attribute("Click"));
        Assert.Null(el.Attribute("Tapped"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.Equal("Copy recent document paths", (string?)el.Attribute("AutomationProperties.Name"));

        var code = SuiteCode;
        Assert.Contains("(RecentDocsCountPill, OnRecentDocsCountPillRightTapped)", BridgeTable(code));
        Assert.DoesNotContain("OnRecentDocsCountPillDoubleTapped", code);
        // The tick goes back to the count instead of sticking until the next refresh.
        var click = Method(code, "OnRecentDocsCountPillClick");
        Assert.Contains("Task.Delay", click);
        Assert.Contains("if (RecentDocsCountText.Text == copied) RecentDocsCountText.Text = before;", click);
        // It collapses with the filter box, so it hands focus off like the box does.
        Assert.Contains("ReferenceEquals(d, RecentDocsCountPill)", Method(code, "SetRecentFilterChromeVisible"));
    }

    [Fact]
    public void SuiteHub_TemplateCountPill_IsAKeyboardButtonWithOneAction()
    {
        var el = Named(SuiteXaml, "TemplateCountPill");
        Assert.Equal("Button", el.Name.LocalName);
        Assert.Equal("OnTemplateCountPillClick", (string?)el.Attribute("Click"));
        Assert.Null(el.Attribute("Tapped"));
        Assert.Null(el.Attribute("DoubleTapped"));

        var code = SuiteCode;
        Assert.Contains("(TemplateCountPill, OnTemplateCountPillRightTapped)", BridgeTable(code));
        Assert.DoesNotContain("OnTemplateCountPillDoubleTapped", code);
        Assert.DoesNotContain("OnTemplateCountPillTapped", code);
        var click = Method(code, "OnTemplateCountPillClick");
        Assert.Contains("FilterTemplates(\"\")", click);
        Assert.Contains("TemplateSearchBox.Focus(", click);

        var menu = Method(code, "OnTemplateCountPillRightTapped");
        Assert.DoesNotContain("(10)", menu);
        Assert.DoesNotContain("Ctrl+F", menu);
        Assert.Contains("KeyboardContextMenu.PointIn(e, TemplateCountPill)", menu);

        // The spoken name follows the filter, like the tooltip.
        Assert.Contains("AutomationProperties.SetName(TemplateCountPill", Method(code, "FilterTemplates"));
    }

    [Fact]
    public void SuiteHub_PlanningPreset_FindsBothTemplatesItNames()
    {
        // The menu says "Product & Planning (PRD, Meeting Notes)" and filters on "planning".
        Assert.Contains("Product & Planning (PRD, Meeting Notes)", SuiteCode);
        Assert.Contains("FilterTemplates(\"planning\")", SuiteCode);
        var tags = Named(SuiteXaml, "StarterTemplatesPanel").Descendants()
            .Where(e => e.Name.LocalName == "Button")
            .Select(e => (string?)e.Attribute("Tag") ?? "")
            .ToList();
        Assert.Contains(tags, t => t.StartsWith("meeting notes") && t.Contains("planning"));
        Assert.Contains(tags, t => t.StartsWith("prd ") && t.Contains("planning"));
    }
}
