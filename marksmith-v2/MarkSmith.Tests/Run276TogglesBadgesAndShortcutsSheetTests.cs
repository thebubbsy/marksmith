using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #276: toggles, badges and the shortcuts sheet.
// - SmartArt Studio: the harmony mode buttons (Mono / Analogous / Triadic) only said which mode
//   was on in a label beside them; they are toggles now, the active one stays pressed and named,
//   and the palette flyout's buttons get the hover lift (the flyout lives in the popup layer);
// - Shape Studio: the cursor HUD, rotate-angle and resize-size badges were a fixed near-black
//   chip with white text in both themes; they follow the studio's theme now;
// - Keyboard shortcuts (F1): the sheet scrolls, but focus sat on Close, so keyboard users could
//   not reach the rows below the fold; the sheet is a focused, named tab stop now;
// - Counts read right at one: SmartArt "(1 step)", outline metrics "1 word · 1 char", the outline
//   summary "1 node", History "net +1 word", Shape Studio "1 shape selected", Suite Hub "1 file".
public class Run276TogglesBadgesAndShortcutsSheetTests
{
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

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"missing {signature}");
        int lineStart = source.LastIndexOf('\n', start) + 1;
        int indent = start - lineStart - (source.Substring(lineStart, start - lineStart).TrimStart().Length);
        var m = Regex.Match(source.Substring(start + signature.Length),
            @"\n {" + indent + @"}(?! )(private|public|internal|protected|//|\[|///)");
        return m.Success ? source.Substring(start, signature.Length + m.Index) : source.Substring(start);
    }

    private const string SmartArt = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs";
    private const string SmartArtXaml = "Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml";
    private const string ShapeStudio = "Views/ShapeStudio/ShapeDesignStudioWindow.xaml.cs";
    private const string Main = "MainWindow.xaml.cs";

    // ---- SmartArt harmony modes ----

    [Theory]
    [InlineData("HarmonyMonoBtn", "Monochromatic harmony")]
    [InlineData("HarmonyAnalogousBtn", "Analogous harmony")]
    [InlineData("HarmonyTriadicBtn", "Triadic harmony")]
    public void HarmonyModes_AreNamedToggleButtons(string name, string accessibleName)
    {
        var xaml = Read(SmartArtXaml);
        var m = Regex.Match(xaml, $"<(\\w+) x:Name=\"{name}\"[^>]*>");
        Assert.True(m.Success, $"missing {name}");
        Assert.Equal("ToggleButton", m.Groups[1].Value);
        Assert.Contains($"AutomationProperties.Name=\"{accessibleName}\"", m.Value);
        Assert.DoesNotContain($"<Button x:Name=\"{name}\"", xaml);
    }

    [Fact]
    public void HarmonyModes_MonoStartsChecked()
    {
        var m = Regex.Match(Read(SmartArtXaml), "<ToggleButton x:Name=\"HarmonyMonoBtn\"[^>]*>");
        Assert.Contains("IsChecked=\"True\"", m.Value);
    }

    [Fact]
    public void GenerateHarmonySwatches_ChecksOnlyTheActiveMode()
    {
        var body = Body(Read(SmartArt), "private void GenerateHarmonySwatches(string mode)");
        Assert.Contains("HarmonyMonoBtn.IsChecked = mode is not (\"analogous\" or \"triadic\");", body);
        Assert.Contains("AutomationProperties.SetItemStatus(HarmonyMonoBtn, HarmonyMonoBtn.IsChecked == true ? \"Active\" : \"\");", body);
        Assert.Contains("HarmonyAnalogousBtn.IsChecked = mode == \"analogous\";", body);
        Assert.Contains("AutomationProperties.SetItemStatus(HarmonyAnalogousBtn, HarmonyAnalogousBtn.IsChecked == true ? \"Active\" : \"\");", body);
        Assert.Contains("HarmonyTriadicBtn.IsChecked = mode == \"triadic\";", body);
        Assert.Contains("AutomationProperties.SetItemStatus(HarmonyTriadicBtn, HarmonyTriadicBtn.IsChecked == true ? \"Active\" : \"\");", body);
    }

    [Fact]
    public void PaletteFlyout_GetsHoverPolishWhenOpened()
    {
        Assert.Contains("HoverPolish.AttachOnOpen(PaletteFlyout);", Read(SmartArt));
        Assert.Contains("x:Name=\"PaletteFlyout\"", Read(SmartArtXaml));
    }

    // ---- Shape Studio badges ----

    [Fact]
    public void ShapeStudioBadges_FollowTheTheme()
    {
        var src = Read(ShapeStudio);
        Assert.DoesNotContain("FromArgb(0xEE, 0x14, 0x1A, 0x24)", src);
        Assert.DoesNotContain("FromArgb(0xEE, 0x18, 0x20, 0x2C)", src);
        foreach (var (badge, text) in new[]
                 {
                     ("_cursorHudBadge", "_cursorHudText"),
                     ("_rotateAngleBadge", "_rotateAngleBadgeText"),
                     ("_resizeDimensionBadge", "_resizeDimensionBadgeText"),
                 })
        {
            Assert.Contains($"ThemeBrush.Set({badge}, Border.BackgroundProperty, \"SolidBackgroundFillColorTertiaryBrush\");", src);
            Assert.Contains($"ThemeBrush.Set({text}, TextBlock.ForegroundProperty, \"TextFillColorPrimaryBrush\");", src);
        }
    }

    [Fact]
    public void ShapeStudioBadgeText_HasNoFixedWhiteForeground()
    {
        var src = Read(ShapeStudio);
        foreach (var text in new[] { "_cursorHudText = new TextBlock", "_rotateAngleBadgeText = new TextBlock", "_resizeDimensionBadgeText = new TextBlock" })
        {
            int at = src.IndexOf(text, StringComparison.Ordinal);
            Assert.True(at >= 0, $"missing {text}");
            int end = src.IndexOf("};", at, StringComparison.Ordinal);
            Assert.DoesNotContain("Colors.White", src.Substring(at, end - at));
        }
    }

    // ---- Keyboard shortcuts sheet ----

    [Fact]
    public void ShortcutsSheet_IsAFocusedNamedTabStop()
    {
        var body = Body(Read(Main), "private async Task ShowShortcutsCheatsheetAsync()");
        Assert.Contains("IsTabStop = true,", body);
        Assert.Contains("AutomationProperties.SetName(sheet, \"Keyboard shortcuts list\");", body);
        Assert.Contains("dialog.Opened += (_, _) => sheet.Focus(FocusState.Programmatic);", body);
        Assert.Contains("Content = sheet,", body);
        // Focus must be set before the dialog is awaited, or it would run after it closed.
        Assert.True(body.IndexOf("dialog.Opened +=", StringComparison.Ordinal)
                    < body.IndexOf("ShowPolishedAsync(dialog)", StringComparison.Ordinal));
    }

    // ---- Counts at one ----

    [Fact]
    public void SmartArtStepCounts_UseCountText()
    {
        var src = Read(SmartArt);
        Assert.DoesNotContain("steps)", src);
        Assert.Equal(9, Regex.Matches(src, Regex.Escape("CountText.Of(") + "(count|c), \"step\"\\)").Count);
    }

    [Fact]
    public void SmartArtOutlineMetrics_UseCountText()
    {
        var src = Read(SmartArt);
        var stats = Body(src, "private void UpdateOutlineStats()");
        Assert.DoesNotContain("{totalWords} words", stats);
        Assert.DoesNotContain("{totalChars} char", stats);
        Assert.DoesNotContain("{count} items", stats);
        Assert.Contains("CountText.Of(totalWords, \"word\")", stats);
        Assert.Contains("CountText.Of(totalChars, \"char\")", stats);
        Assert.Contains("CountText.Of(count, \"item\")", stats);
        var summary = Body(src, "private void CopyOutlineSummary()");
        Assert.Contains("CountText.Of(count, \"node\")", summary);
    }

    [Fact]
    public void OtherCounts_UseCountText()
    {
        var shape = Read(ShapeStudio);
        Assert.Contains("SelectionBoundsText.Text = $\"{CountText.Of(count, \"shape\")} selected\";", shape);
        Assert.Contains("SelectionBoundsText.Text = $\"{CountText.Of(sel.Count, \"shape\")} · {bw}×{bh} bounds\";", shape);
        var main = Read(Main);
        Assert.Contains("CopyAndAnnounce(selected, $\"Copied selection ({CountText.Of(selected.Length, \"char\")}) to clipboard\");", main);
        var history = Read("Views/History/HistoryWindow.xaml.cs");
        Assert.DoesNotContain("{netSign} words", history);
        Assert.Equal(2, Regex.Matches(history, Regex.Escape("{netSign} {CountText.Noun(Math.Abs(net), \"word\")}")).Count);
        var hub = Read("Views/SuiteHubView.xaml.cs");
        Assert.DoesNotContain("{cacheFileCount} files", hub);
    }

    [Theory]
    [InlineData(1, "step", "1 step")]
    [InlineData(3, "step", "3 steps")]
    [InlineData(1, "char", "1 char")]
    [InlineData(1, "file", "1 file")]
    [InlineData(0, "item", "0 items")]
    public void CountText_ReadsRight(int n, string noun, string expected) =>
        Assert.Equal(expected, CountText.Of(n, noun));

    [Theory]
    [InlineData(1, "word")]
    [InlineData(-1, "word")]
    [InlineData(0, "words")]
    [InlineData(-5, "words")]
    public void NetWordNoun_UsesTheMagnitude(int net, string expected) =>
        Assert.Equal(expected, CountText.Noun(Math.Abs(net), "word"));
}
