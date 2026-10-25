using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #254, found with the UIA clip sweep over OCR Studio (seeded with a sample PDF).
// - At its default 1220 px the Live Markdown header read "LIVE MARI": the scope box and Copy sat in Auto
//   columns and took the title's width. Below ~1000 px Copy had Empty bounds and the scope box ran past
//   the window edge.
// - The window had no minimum size, and at narrow widths the centred Previous / Page / Next controls
//   were squeezed out of the title bar entirely (Empty bounds).
// - The status banner sat in a horizontal StackPanel (unlimited width), so it was cut mid-word instead
//   of trimming; the footer status had no trimming either.
// - The narrow layout's "QUICK RECLASSIFY SELECTED" caption overlapped its "Keys 1–8 · R reset" hint.
// - The Live Markdown box asked for TextWrapping="Wrap" inside a horizontally scrolling ScrollViewer,
//   so it never wrapped and long lines ran off the pane edge.
// - Diagram Studio: the code pane's one-line status had no wrapping, so a clipboard error (which carries
//   the exception text) ran past its 260 px pane. The syntax-error line just fits at the default width.
public class Run254OcrStudioNarrowLayoutTests
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

    private const string OcrXaml = "Views/Ocr/OcrInspectionStudioWindow.xaml";
    private const string OcrCode = "Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string MermaidXaml = "Views/Mermaid/MermaidDiagramStudioControl.xaml";

    // The opening tag of the element named `name`.
    private static string Tag(string xaml, string name)
    {
        var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{name} not found");
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var next = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? code[start..] : code[start..next];
    }

    [Fact]
    public void MarkdownHeader_IsAWrapRow_WithTitleFirstAndScopePlusCopyGroupedTogether()
    {
        var xaml = Read(OcrXaml);
        Assert.Contains("xmlns:ui=\"using:MarkSmith.Controls\"", xaml);

        var start = xaml.IndexOf("<ui:WrapRow x:Name=\"MarkdownPaneHeader\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the Live Markdown header is not a WrapRow");
        var end = xaml.IndexOf("</ui:WrapRow>", start, StringComparison.Ordinal);
        var header = xaml[start..end];

        var title = header.IndexOf("LIVE MARKDOWN PREVIEW", StringComparison.Ordinal);
        var actions = header.IndexOf("x:Name=\"MarkdownPaneActions\"", StringComparison.Ordinal);
        var scope = header.IndexOf("x:Name=\"MarkdownScopeComboBox\"", StringComparison.Ordinal);
        var copy = header.IndexOf("x:Name=\"CopyMarkdownButton\"", StringComparison.Ordinal);
        Assert.True(title >= 0 && actions > title && scope > actions && copy > scope,
            "header order must be title, then one actions panel holding the scope box and Copy");

        // No leftover Grid.Column on the moved controls (a WrapRow ignores it; a stale one misleads).
        Assert.DoesNotContain("Grid.Column", Tag(xaml, "MarkdownScopeComboBox"));
        Assert.DoesNotContain("Grid.Column", Tag(xaml, "CopyMarkdownButton"));
    }

    [Fact]
    public void ScopeItems_AreShort_SoTheyFitTheNarrowPane()
    {
        var xaml = Read(OcrXaml);
        Assert.Contains("<ComboBoxItem Content=\"Current page\" />", xaml);
        Assert.Contains("<ComboBoxItem Content=\"Full document\" />", xaml);
        Assert.DoesNotContain("Current Page Markdown", xaml);
        Assert.DoesNotContain("Full Document Markdown", xaml);
    }

    [Fact]
    public void Window_HasAMinimumSize_MatchingItsSizingFloor()
    {
        var code = Read(OcrCode);
        var sizing = Method(code, "private void SizeForDisplay()");
        Assert.Contains("presenter.PreferredMinimumWidth = Math.Min((int)(MinimumWidthDips * scale), workArea.Width);", sizing);
        Assert.Contains("presenter.PreferredMinimumHeight = Math.Min((int)(MinimumHeightDips * scale), workArea.Height);", sizing);
        Assert.Contains("private const int MinimumWidthDips = 980;", code);
        // SizeForDisplay never opens the window narrower than this, so the minimum can't fight it.
        Assert.Contains("Math.Max(980, width)", sizing);
    }

    [Fact]
    public void NarrowTitleBar_GoesIconOnly_AndKeepsAccessibleNames()
    {
        var code = Read(OcrCode);
        var resize = Method(code, "private void OnRootSizeChanged(");
        Assert.Contains("bool compact = w > 0 && w < 1120;", resize);
        Assert.Contains("PrevPageLabelText.Visibility = labels;", resize);
        Assert.Contains("NextPageLabelText.Visibility = labels;", resize);
        Assert.Contains("EngineBadgeBorder.MaxWidth = compact ? 130 : 190;", resize);
        // Only label TextBlocks collapse, never the (possibly focused) buttons themselves.
        Assert.DoesNotContain("PrevPageButton.Visibility", resize);
        Assert.DoesNotContain("NextPageButton.Visibility", resize);

        var xaml = Read(OcrXaml);
        Assert.Contains("AutomationProperties.Name=\"Previous page\"", Tag(xaml, "PrevPageButton"));
        Assert.Contains("AutomationProperties.Name=\"Next page\"", Tag(xaml, "NextPageButton"));
    }

    [Fact]
    public void NarrowColumns_LeaveTheMarkdownPaneRoomForScopeAndCopy()
    {
        var resize = Method(Read(OcrCode), "private void OnRootSizeChanged(");
        var m = Regex.Match(resize, @"if \(compact\)\s*\{\s*CanvasPaneColumn\.Width = new GridLength\((\d+)\);\s*InspectorPaneColumn\.Width = new GridLength\((\d+)\);");
        Assert.True(m.Success, "compact column widths not found");
        int canvas = int.Parse(m.Groups[1].Value), inspector = int.Parse(m.Groups[2].Value);
        // 980 window − 28 stage padding − 24 column spacing − 26 pane padding/border.
        double markdownInner = 980 - 28 - 24 - canvas - inspector - 26;
        Assert.True(markdownInner >= 260, $"Markdown pane would be {markdownInner} px; scope + Copy need ~250");
    }

    [Theory]
    [InlineData("StatusBannerText")]
    [InlineData("FooterStatusText")]
    public void StatusLines_TrimWithEllipsis(string name)
    {
        var tag = Tag(Read(OcrXaml), name);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", tag);
        Assert.Contains("TextWrapping=\"NoWrap\"", tag);
    }

    [Fact]
    public void StatusBanner_IsNotInAHorizontalStackPanel()
    {
        var xaml = Read(OcrXaml);
        var at = xaml.IndexOf("x:Name=\"StatusBannerText\"", StringComparison.Ordinal);
        var parentStart = xaml.LastIndexOf("<Grid ColumnDefinitions=\"Auto,*\"", at, StringComparison.Ordinal);
        var stackStart = xaml.LastIndexOf("<StackPanel Orientation=\"Horizontal\"", at, StringComparison.Ordinal);
        Assert.True(parentStart > stackStart, "the banner must sit in an Auto,* Grid so it can trim");
    }

    [Fact]
    public void TrimmedReadouts_GetFullTextToolTips()
    {
        var code = Read(OcrCode);
        Assert.Contains("TrimmedTextToolTip.Attach(StatusBannerText, StatusSubtext, FooterStatusText,\n            WordStreamText, SelectedElementBadgeText);", code);
    }

    [Fact]
    public void QuickReclassifyCaption_LeavesRoomForTheKeyHint()
    {
        var xaml = Read(OcrXaml);
        Assert.DoesNotContain("Text=\"QUICK RECLASSIFY SELECTED\"", xaml);
        var at = xaml.IndexOf("Text=\"QUICK RECLASSIFY\"", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var grid = xaml[xaml.LastIndexOf("<Grid", at, StringComparison.Ordinal)..at];
        Assert.Contains("ColumnSpacing=\"8\"", grid);
        var caption = xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)];
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", caption);
    }

    [Fact]
    public void LiveMarkdownBox_WrapsInsteadOfScrollingSideways()
    {
        var xaml = Read(OcrXaml);
        var at = xaml.IndexOf("x:Name=\"MarkdownPreviewTextBox\"", StringComparison.Ordinal);
        var scroller = xaml[xaml.LastIndexOf("<ScrollViewer", at, StringComparison.Ordinal)..at];
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", scroller);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml[at..xaml.IndexOf("/>", at, StringComparison.Ordinal)]);
    }

    [Fact]
    public void DiagramStudioCodeStatus_Wraps()
    {
        var tag = Tag(Read(MermaidXaml), "CodeStatusText");
        Assert.Contains("TextWrapping=\"WrapWholeWords\"", tag);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", tag);
    }
}
