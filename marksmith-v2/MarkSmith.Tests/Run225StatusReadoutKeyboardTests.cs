using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #225: the last mouse-only readouts on the main window (detected-source badge, status
// message, find-bar match count, preview width) are reachable by keyboard, History's delta badge
// action is in the version menu, and the statistics card, tooltip and copy menu agree on lines.
public class Run225StatusReadoutKeyboardTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative));

    private static XDocument MainXaml() => XDocument.Load(Path.Combine(DesktopDir(), "MainWindow.xaml"));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    [Theory]
    [InlineData("DetectedBadge", "Button", "OnDetectedBadgeClick", "OnDetectedBadgeRightTapped")]
    [InlineData("FindCountButton", "HyperlinkButton", "OnFindCountButtonClick", "OnFindCountTextRightTapped")]
    [InlineData("PreviewWidthBadge", "HyperlinkButton", "OnPreviewWidthBadgeClick", "OnPreviewWidthBadgeRightTapped")]
    public void Readouts_AreButtons_BridgedForShiftF10(string name, string type, string click, string rightTapped)
    {
        var el = Named(MainXaml(), name);
        Assert.Equal(type, el.Name.LocalName);
        Assert.Equal(click, (string?)el.Attribute("Click"));
        Assert.Equal(rightTapped, (string?)el.Attribute("RightTapped"));
        Assert.NotNull(el.Attribute("AutomationProperties.HelpText"));
        // Click replaces the old Tapped/DoubleTapped pair; both would fire on one click.
        Assert.Null(el.Attribute("Tapped"));
        Assert.Null(el.Attribute("DoubleTapped"));

        var code = Read("MainWindow.xaml.cs");
        Assert.Contains($"({name}, {rightTapped}),", code);
        Assert.Contains($"private void {click}(object sender, RoutedEventArgs e)", code);
    }

    [Fact]
    public void OldPointerOnlyHandlers_AreGone()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.DoesNotContain("OnDetectedBadgeTapped", code);
        Assert.DoesNotContain("OnFindCountTextTapped", code);
        Assert.DoesNotContain("OnPreviewWidthBadgeTapped", code);
        Assert.DoesNotContain("OnPreviewWidthBadgeDoubleTapped", code);
    }

    [Fact]
    public void DetectedBadge_ToolTipNamesShiftF10_AndHasAName()
    {
        var badge = Named(MainXaml(), "DetectedBadge");
        Assert.Contains("Shift+F10", (string?)badge.Attribute("ToolTipService.ToolTip"));
        Assert.Equal("{Binding DetectedSourceText}", (string?)badge.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void DetectedBadge_Clear_MovesFocusBeforeCollapsing()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("private void MoveFocusOffDetectedBadge()", code);

        // From the menu: remembered at open time, because focus is in the menu when Clear runs.
        var show = code.IndexOf("private void ShowDetectedBadgeContextMenu(", StringComparison.Ordinal);
        var had = code.IndexOf("var badgeHadFocus = DetectedBadge.FocusState != FocusState.Unfocused;", show, StringComparison.Ordinal);
        var move = code.IndexOf("if (badgeHadFocus) MoveFocusOffDetectedBadge();", show, StringComparison.Ordinal);
        var collapse = code.IndexOf("DetectedBadge.Visibility = Visibility.Collapsed;", show, StringComparison.Ordinal);
        Assert.True(show > 0 && had > show && move > had && collapse > move);

        // From anywhere else that clears the source text.
        var changed = code.IndexOf("if (e.PropertyName == nameof(ViewModels.MainViewModel.DetectedSourceText))", StringComparison.Ordinal);
        var guard = code.IndexOf("MoveFocusOffDetectedBadge();", changed, StringComparison.Ordinal);
        var vis = code.IndexOf("DetectedBadge.Visibility = string.IsNullOrEmpty", changed, StringComparison.Ordinal);
        Assert.True(changed > 0 && guard > changed && vis > guard);
    }

    [Fact]
    public void StatusMessage_HasAFocusableHost_ThatOpensItsMenu()
    {
        var doc = MainXaml();
        var text = Named(doc, "StatusMsgText");
        var host = text.Parent!;
        Assert.Equal("ContentControl", host.Name.LocalName);
        Assert.Equal("StatusMsgHost", (string?)host.Attribute(X + "Name"));
        Assert.Equal("False", (string?)host.Attribute("IsTabStop"));
        Assert.Equal("True", (string?)host.Attribute("UseSystemFocusVisuals"));
        // Clicking the message must not pull focus out of the editor.
        Assert.Equal("False", (string?)host.Attribute("AllowFocusOnInteraction"));
        Assert.Equal("OnStatusMsgHostKeyDown", (string?)host.Attribute("KeyDown"));

        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("(StatusMsgHost, OnStatusMsgTextRightTapped),", code);
        // Enter/Space go through the same keyboard path as the bridge, so the menu anchors under the host.
        Assert.Contains("Services.KeyboardContextMenu.Invoke(StatusMsgHost, OnStatusMsgTextRightTapped);", code);
        // A tab stop only while there is something to copy.
        var update = code.IndexOf("private void UpdateStatusMsgHost()", StringComparison.Ordinal);
        var move = code.IndexOf("if (!hasContent && StatusMsgHost.FocusState != FocusState.Unfocused)", update, StringComparison.Ordinal);
        var tab = code.IndexOf("StatusMsgHost.IsTabStop = hasContent;", update, StringComparison.Ordinal);
        Assert.True(update > 0 && move > update && tab > move);
    }

    [Fact]
    public void StatusMessageKeys_DoNotSwallowArrowsOrTyping()
    {
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf("private void OnStatusMsgHostKeyDown(", StringComparison.Ordinal);
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        var body = code[start..end];
        Assert.Contains("if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;", body);
        Assert.True(body.IndexOf("return;", StringComparison.Ordinal) < body.IndexOf("e.Handled = true;", StringComparison.Ordinal));
    }

    [Fact]
    public void KeyboardContextMenu_Invoke_IsWhatTheBridgeUses()
    {
        var src = Read(Path.Combine("Services", "KeyboardContextMenu.cs"));
        Assert.Contains("public static void Invoke(UIElement element, RightTappedEventHandler handler)", src);
        var bridge = src.IndexOf("public static void Bridge(", StringComparison.Ordinal);
        var invoke = src.IndexOf("Invoke(element, handler);", bridge, StringComparison.Ordinal);
        Assert.True(bridge > 0 && invoke > bridge);
        // The flags are reset even if the handler throws.
        Assert.Contains("finally { IsKeyboardInvocation = false; KeyboardSource = null; }", src);
    }

    [Fact]
    public void FindCount_IsATabStopOnlyWithACount_AndHandsFocusBack()
    {
        Assert.Equal("False", (string?)Named(MainXaml(), "FindCountButton").Attribute("IsTabStop"));
        var code = Read("MainWindow.xaml.cs");
        var update = code.IndexOf("private void UpdateFindCount()", StringComparison.Ordinal);
        var focus = code.IndexOf("if (!hasCount && FindCountButton.FocusState != FocusState.Unfocused)", update, StringComparison.Ordinal);
        var tab = code.IndexOf("FindCountButton.IsTabStop = hasCount;", update, StringComparison.Ordinal);
        Assert.True(update > 0 && focus > update && tab > focus);
        Assert.Contains("AutomationProperties.SetName(FindCountButton, hasCount ? FindCountText.Text : \"Search match count\");", code);
        // The tooltip is set on the button (the old one on the text told users to right-click only).
        Assert.Contains("ToolTipService.SetToolTip(FindCountButton, _lastFindWrapped", code);
        Assert.Contains("Click to copy · Alt+Home/End to jump · Right-click or Shift+F10 for options", code);
        Assert.DoesNotContain("ToolTipService.SetToolTip(FindCountText", code);
    }

    [Fact]
    public void HistoryVersionMenu_OffersTheDeltaCopy()
    {
        var code = Read(Path.Combine("Views", "History", "HistoryWindow.xaml.cs"));
        var build = code.IndexOf("private MenuFlyout BuildVersionMenu(VersionItemViewModel item)", StringComparison.Ordinal);
        var end = code.IndexOf("return menu;", build, StringComparison.Ordinal);
        var body = code[build..end];
        Assert.Contains("if (item.HasDelta)", body);
        Assert.Contains("Copy line delta ({item.DeltaBadge})", body);
        Assert.Contains("CopyVersionDelta(item)", body);
        // One copy routine for the badge's double-click, its menu, and the version menu.
        Assert.Equal(1, code.Split("dp.SetText(item.DeltaBadge);").Length - 1);
    }

    [Theory]
    [InlineData("", 1, "1 line")]
    [InlineData("   \n\n", 1, "1 line")]
    [InlineData("hello", 1, "1 line")]
    [InlineData("hello\n", 1, "1 line")]
    [InlineData("a\nb", 2, "2 lines")]
    [InlineData("a\n\n", 2, "2 lines")]
    public void DocumentStats_LinesText_FloorsAtOneLine(string markdown, int display, string text)
    {
        var stats = DocumentStatsService.Analyze(markdown);
        Assert.Equal(display, stats.DisplayLines);
        Assert.Equal(text, stats.LinesText);
        Assert.Contains($"{text} · Estimated reading time:", stats.DetailText);
    }

    [Fact]
    public void DocumentStats_LinesText_GroupsThousands()
    {
        var md = string.Join("\n", Enumerable.Range(1, 1204).Select(i => $"line {i}"));
        Assert.Equal($"{1204:N0} lines", DocumentStatsService.Analyze(md).LinesText);
    }

    [Fact]
    public void MetricsCard_UsesDisplayLines()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("StatPill(\"Lines / Para\", $\"{stats.DisplayLines:N0} / {stats.Paragraphs:N0}\"", code);
    }
}
