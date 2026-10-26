using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #277: readable ink, map palettes and a keyboard copy for the shortcut sheet.
// - OCR Inspection Studio: the page badges were white text on the kind colour (about 2:1 on amber,
//   teal, green and cyan) and the inspector card titles were the raw kind colour (amber on the light
//   pane was about 2:1); both now pick a readable ink, and the titles re-tone on a theme switch;
// - Document Galaxy: the snap guides, marquee, lasso, multi-select frame and badge, and the
//   breadcrumb bar kept the colours of the theme the window opened with (the badge stayed a
//   near-black chip with white text on Clean White); they follow the map's palette now;
// - Keyboard shortcuts (F1): rows copy one chord from a right-click only; the focused sheet copies
//   the whole list as Markdown from Ctrl+C or its own Shift+F10 menu;
// - Counts read right at one: "1 query", "1 level in", Suite Hub "1 replacement across 1 file".
public class Run277InkPalettesAndShortcutCopyTests
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

    private const string Ocr = "Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string Galaxy = "Views/MindMap/MindMapGalaxyWindow.xaml.cs";
    private const string Main = "MainWindow.xaml.cs";

    // ---- ReadableInk (Core) ----

    // The OCR studio's kind colours: amber, teal, green, cyan, blue, purple, indigo, slate.
    public static TheoryData<byte, byte, byte> KindColours => new()
    {
        { 245, 158, 11 }, { 20, 184, 166 }, { 16, 185, 129 }, { 6, 182, 212 },
        { 59, 130, 246 }, { 168, 85, 247 }, { 99, 102, 241 }, { 100, 116, 139 },
    };

    private static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    [Theory]
    [MemberData(nameof(KindColours))]
    public void BadgeInk_IsTheHigherContrastOfBlackAndWhite(byte r, byte g, byte b)
    {
        double onBlack = ContrastGuard.GetContrastRatio(Hex(r, g, b), "#000000");
        double onWhite = ContrastGuard.GetContrastRatio(Hex(r, g, b), "#FFFFFF");
        Assert.Equal(onBlack > onWhite, ReadableInk.PrefersDarkInk(r, g, b));
        Assert.True(Math.Max(onBlack, onWhite) >= 4.0, $"{Hex(r, g, b)} best ink is only {Math.Max(onBlack, onWhite):F2}:1");
    }

    [Fact]
    public void BadgeInk_AmberAndTealTakeBlack_SlateKeepsWhite()
    {
        Assert.True(ReadableInk.PrefersDarkInk(245, 158, 11));
        Assert.True(ReadableInk.PrefersDarkInk(20, 184, 166));
        Assert.False(ReadableInk.PrefersDarkInk(100, 116, 139));
        Assert.False(ReadableInk.PrefersDarkInk(0, 0, 0));
        Assert.True(ReadableInk.PrefersDarkInk(255, 255, 255));
    }

    [Theory]
    [MemberData(nameof(KindColours))]
    public void TitleInk_ReadsOnTheLightPane(byte r, byte g, byte b)
    {
        var (tr, tg, tb) = ReadableInk.AsTextOn(r, g, b, lightSurface: true);
        double ratio = ContrastGuard.GetContrastRatio(Hex(tr, tg, tb), "#FFFFFF");
        Assert.True(ratio >= 4.5, $"{Hex(r, g, b)} → {Hex(tr, tg, tb)} is {ratio:F2}:1 on white");
    }

    [Theory]
    [MemberData(nameof(KindColours))]
    public void TitleInk_ReadsOnTheDarkPane(byte r, byte g, byte b)
    {
        var (tr, tg, tb) = ReadableInk.AsTextOn(r, g, b, lightSurface: false);
        // #2B2B2B is the dark theme's card fill (Layer / ControlFill over Mica).
        double ratio = ContrastGuard.GetContrastRatio(Hex(tr, tg, tb), "#2B2B2B");
        Assert.True(ratio >= 4.5, $"{Hex(r, g, b)} → {Hex(tr, tg, tb)} is {ratio:F2}:1 on #2B2B2B");
    }

    [Fact]
    public void TitleInk_KeepsTheHue()
    {
        var (r, g, b) = ReadableInk.AsTextOn(245, 158, 11, lightSurface: true);
        Assert.True(r > g && g > b, "darkened amber should still be amber");
        var (lr, lg, lb) = ReadableInk.AsTextOn(99, 102, 241, lightSurface: false);
        Assert.True(lb > lr && lb > lg, "lightened indigo should still be indigo");
    }

    // ---- OCR Inspection Studio ----

    [Fact]
    public void OcrCanvasBadge_UsesReadableInk_NotFixedWhite()
    {
        var body = Body(Read(Ocr), "private Border CreateCanvasElementFrame(");
        Assert.DoesNotContain("Colors.White", body);
        Assert.Equal(2, Regex.Matches(body, @"Foreground = BadgeInkFor\(strokeColor\)").Count);
    }

    [Fact]
    public void OcrReclassify_RetonesTheBadgeInk()
    {
        var body = Body(Read(Ocr), "private void ReclassifyElement(");
        Assert.Contains("badgeIcon.Foreground = BadgeInkFor(sc);", body);
        Assert.Contains("badgeLabel.Foreground = BadgeInkFor(sc);", body);
        Assert.Contains("titleText.Foreground = KindTitleBrush(strokeColor);", body);
        Assert.DoesNotContain("titleText.Foreground = new SolidColorBrush(strokeColor)", body);
    }

    [Fact]
    public void OcrInspectorCardTitle_IsTonedForThePane_AndFollowsThemeChanges()
    {
        var src = Read(Ocr);
        Assert.Contains("Foreground = KindTitleBrush(strokeColor),", Body(src, "private Border CreateInspectorCard("));
        Assert.Contains("ReadableInk.AsTextOn(", Body(src, "private SolidColorBrush KindTitleBrush("));
        Assert.Contains("RootGrid.ActualThemeChanged += (_, _) => RetintInspectorCardTitles();", src);
        Assert.Contains("KindTitleBrush(", Body(src, "private void RetintInspectorCardTitles("));
    }

    // ---- Document Galaxy ----

    [Fact]
    public void GalaxyAdornments_DropTheFixedDarkChipAndWindowsBlue()
    {
        var src = Read(Galaxy);
        Assert.DoesNotContain("FromArgb(0xEE, 0x18, 0x20, 0x2C)", src);
        Assert.DoesNotContain("FromArgb(0x40, 0x00, 0x78, 0xD7)", src);
        Assert.DoesNotContain("FromArgb(0x28, 0x00, 0x78, 0xD7)", src);
        var init = Body(src, "private void InitializeSnappingGuides(");
        Assert.DoesNotContain("Colors.White", init);
        Assert.Contains("Foreground = new SolidColorBrush(_palette.Text)", init);
    }

    [Fact]
    public void GalaxyApplyPalette_RetintsEveryCanvasAdornment()
    {
        var src = Read(Galaxy);
        Assert.Contains("RetintCanvasAdornments();", Body(src, "private void ApplyPalette("));
        var retint = Body(src, "private void RetintCanvasAdornments(");
        foreach (var field in new[] { "_snapGuideX", "_snapGuideY", "_lassoLine", "_multiSelectFrame", "_marqueeBox", "_multiSelectBadge", "_multiSelectBadgeText" })
        {
            Assert.Contains(field, retint);
        }
        Assert.Contains("_palette.CardBackground", retint);
        Assert.Contains("_palette.Text", retint);
    }

    // ---- Keyboard shortcuts sheet ----

    [Fact]
    public void SheetText_IsMarkdownWithEveryVisibleRow()
    {
        var text = KeyboardShortcuts.SheetText();
        Assert.StartsWith("# Marksmith keyboard shortcuts\n", text);
        foreach (var (section, rows) in KeyboardShortcuts.Sheet())
        {
            if (rows.Count == 0) continue;
            Assert.Contains($"\n## {section}\n", text);
            foreach (var row in rows) Assert.Contains($": {row.Action}\n", text);
        }
        int bullets = text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal(KeyboardShortcuts.Sheet().Sum(s => s.Rows.Count), bullets);
    }

    [Fact]
    public void SheetText_JoinsAlternativeChords_AndLeavesHiddenRowsOut()
    {
        var text = KeyboardShortcuts.SheetText();
        Assert.Contains("- F1 or Ctrl+/: Show this list of shortcuts\n", text);
        Assert.DoesNotContain("(developer)", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void ShortcutsSheet_CopiesTheWholeListFromTheKeyboard()
    {
        var body = Body(Read(Main), "private async Task ShowShortcutsCheatsheetAsync(");
        Assert.Contains("Shortcuts.SheetText()", body);
        Assert.Contains("sheet.ContextFlyout = sheetMenu;", body);
        Assert.Contains("KeyboardAcceleratorTextOverride = \"Ctrl+C\"", body);
        Assert.Contains("sheet.KeyDown +=", body);
        Assert.Contains("ke.Key != Windows.System.VirtualKey.C", body);
        Assert.Contains("ke.Handled = true;", body);
        Assert.Contains("CountText.Of(visibleRows, \"shortcut\")", body);
        Assert.Contains("AutomationProperties.SetHelpText(sheet,", body);
        Assert.Contains("Ctrl+C copies the whole list", body);
    }

    // ---- Counts ----

    [Theory]
    [InlineData(Main, "CountText.Of(count, \"query\", \"queries\")", "({count} queries)")]
    [InlineData("Views/SmartArtStudio/SmartArtDesignStudioWindow.xaml.cs", "({CountText.Of(count, \"level\")} in)", "({count} levels in)")]
    [InlineData("Views/SuiteHubView.xaml.cs", "{CountText.Of(result.TotalReplacementCount, \"replacement\")} across {CountText.Of(result.FilesRequiringNormalization, \"file\")}", "{result.TotalReplacementCount} replacements")]
    [InlineData("Views/SuiteHubView.xaml.cs", "({CountText.Of(result.BackupsCreatedCount, \"backup\")})", "{result.BackupsCreatedCount} backups")]
    public void Counts_ReadRightAtOne(string file, string expected, string old)
    {
        var src = Read(file);
        Assert.Contains(expected, src);
        Assert.DoesNotContain(old, src);
    }

    [Fact]
    public void CountText_IrregularPluralAndSingular()
    {
        Assert.Equal("1 query", CountText.Of(1, "query", "queries"));
        Assert.Equal("3 queries", CountText.Of(3, "query", "queries"));
        Assert.Equal("1 level", CountText.Of(1, "level"));
        Assert.Equal("1 replacement", CountText.Of(1, "replacement"));
    }
}
