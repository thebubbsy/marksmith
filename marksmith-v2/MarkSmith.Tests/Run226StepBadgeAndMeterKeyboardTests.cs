using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #226: the pipeline step badges, the outline's heading count, the recent-exports count and
// History's line / word delta meters are buttons the keyboard reaches (Enter runs the click,
// Shift+F10 opens the menu), and none of them copies twice on a double-click any more.
public class Run226StepBadgeAndMeterKeyboardTests
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

    private static XDocument Xaml(string relative) => XDocument.Load(Path.Combine(DesktopDir(), relative));

    private static XElement Named(XDocument doc, string name) =>
        doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

    private static string HistoryXaml => Path.Combine("Views", "History", "HistoryWindow.xaml");
    private static string HistoryCode => Path.Combine("Views", "History", "HistoryWindow.xaml.cs");

    [Theory]
    [InlineData("Step1Badge", "OnStep1BadgeClick")]
    [InlineData("Step2Badge", "OnStep2BadgeClick")]
    [InlineData("Step3Badge", "OnStep3BadgeClick")]
    public void StepBadges_AreButtons_BridgedForShiftF10(string name, string click)
    {
        var el = Named(Xaml("MainWindow.xaml"), name);
        Assert.Equal("Button", el.Name.LocalName);
        Assert.Equal("{StaticResource StepBadgeButtonStyle}", (string?)el.Attribute("Style"));
        Assert.Equal(click, (string?)el.Attribute("Click"));
        Assert.Equal("OnPipelineStepBadgeRightTapped", (string?)el.Attribute("RightTapped"));
        Assert.Null(el.Attribute("Tapped"));
        Assert.StartsWith("Step ", (string?)el.Attribute("AutomationProperties.Name"));
        Assert.Contains("Shift+F10", (string?)el.Attribute("ToolTipService.ToolTip"));

        var code = Read("MainWindow.xaml.cs");
        Assert.Contains($"({name}, OnPipelineStepBadgeRightTapped),", code);
        Assert.Contains($"private void {click}(object sender, RoutedEventArgs e)", code);
    }

    [Fact]
    public void StepBadges_OldBordersAndTappedHandlers_AreGone()
    {
        var code = Read("MainWindow.xaml.cs");
        var xaml = Read("MainWindow.xaml");
        foreach (var gone in new[] { "Step1BadgeBorder", "Step2BadgeBorder", "Step3BadgeBorder", "OnStep1BadgeTapped", "OnStep2BadgeTapped", "OnStep3BadgeTapped" })
        {
            Assert.DoesNotContain(gone, code);
            Assert.DoesNotContain(gone, xaml);
        }
        // The pipeline menu reuses the same step actions as the badges.
        Assert.Contains("step1.Click += (_, _) => GoToSourceStep();", code);
        Assert.Contains("step3.Click += (_, _) => GoToExportStep();", code);
    }

    [Fact]
    public void StepBadgeButtonStyle_KeepsTheAccentDisc()
    {
        var app = Xaml("App.xaml");
        var style = app.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "StepBadgeButtonStyle");
        Assert.Equal("Button", (string?)style.Attribute("TargetType"));
        Assert.Equal("{StaticResource AccentButtonStyle}", (string?)style.Attribute("BasedOn"));
        string Setter(string prop) => (string)style.Elements().Single(s => (string?)s.Attribute("Property") == prop).Attribute("Value")!;
        Assert.Equal("24", Setter("Width"));
        Assert.Equal("24", Setter("Height"));
        Assert.Equal("12", Setter("CornerRadius"));
        Assert.Equal("0", Setter("MinWidth"));
        Assert.Equal("0", Setter("MinHeight"));
    }

    [Fact]
    public void FocusMode_FromStepBadgeMenu_HandsFocusToTheEditor()
    {
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf("private void OnPipelineStepBadgeRightTapped", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, code.IndexOf("// ---- Editor<->preview sync-scroll helpers", start, StringComparison.Ordinal) - start);
        Assert.Contains("enteredFocusMode = FocusModeToggle?.IsChecked == true;", body);
        Assert.Contains("menu.Closed +=", body);
        Assert.Contains("ReferenceEquals(sender, Step1Badge) || ReferenceEquals(sender, Step3Badge)", body);
        Assert.Contains("PasteTextBox?.Focus(FocusState.Programmatic)", body);
    }

    [Theory]
    [InlineData("TocCountButton", "TocCountText", "OnTocCountButtonClick", "OnTocCountTextRightTapped")]
    [InlineData("HistoryCountButton", "HistoryCountText", "OnHistoryCountButtonClick", "OnHistoryCountTextRightTapped")]
    public void FlyoutCounts_AreButtons_WrappingTheirText(string name, string text, string click, string rightTapped)
    {
        var el = Named(Xaml("MainWindow.xaml"), name);
        Assert.Equal("HyperlinkButton", el.Name.LocalName);
        Assert.Equal("False", (string?)el.Attribute("IsTabStop"));
        Assert.Equal(click, (string?)el.Attribute("Click"));
        Assert.Equal(rightTapped, (string?)el.Attribute("RightTapped"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.NotNull(el.Attribute("AutomationProperties.HelpText"));
        Assert.Contains("Shift+F10", (string?)el.Attribute("ToolTipService.ToolTip"));
        Assert.Contains(el.Descendants(), d => (string?)d.Attribute(X + "Name") == text);

        var code = Read("MainWindow.xaml.cs");
        Assert.Contains($"({name}, {rightTapped}),", code);
        Assert.Contains($"menu.ShowAt({name}, KeyboardContextMenu.PointIn(e, {name}));", code);
        Assert.DoesNotContain(text.Replace("Text", "TextDoubleTapped"), code);
    }

    [Fact]
    public void HeadingCount_IsATabStopOnlyWithACount_AndMovesFocusWhenEmptied()
    {
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf("private void UpdateTocCountButton()", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, 700);
        var guard = body.IndexOf("OutlineButton?.Focus(FocusState.Programmatic)", StringComparison.Ordinal);
        var tabStop = body.IndexOf("TocCountButton.IsTabStop = hasCount;", StringComparison.Ordinal);
        Assert.True(guard > 0 && tabStop > guard, "focus must move before the count stops being a tab stop");
        // Every path through the outline's state refresh updates it.
        var outline = code.Substring(code.IndexOf("private void UpdateOutlineEmptyState()", StringComparison.Ordinal), 2500);
        Assert.True(outline.Split("UpdateTocCountButton();").Length - 1 >= 3);
    }

    [Fact]
    public void ExportCount_FocusGuard_CoversTheCountButton()
    {
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf("private void UpdateExportHistoryFlyoutState()", StringComparison.Ordinal);
        var body = code.Substring(start, 1800);
        Assert.Contains("HistoryCountButton.FocusState != FocusState.Unfocused", body);
        Assert.Contains("HistoryCountButton.IsTabStop = !empty;", body);
        Assert.Contains("\"1 recent export\"", body);
    }

    [Theory]
    [InlineData("private void OnOutlineFlyoutOpened", "Services.HoverPolish.Apply(TocCountButton)")]
    [InlineData("private void OnExportHistoryFlyoutOpened", "Services.HoverPolish.Apply(HistoryCountButton)")]
    public void FlyoutCounts_GetHoverPolishWhenTheirFlyoutOpens(string handler, string call)
    {
        // Flyout content sits in the popup root, outside the window-wide walk.
        var code = Read("MainWindow.xaml.cs");
        var start = code.IndexOf(handler, StringComparison.Ordinal);
        Assert.True(start > 0);
        Assert.Contains(call, code.Substring(start, 400));
    }

    [Theory]
    [InlineData("DeltaMeterButton", "OnDeltaMeterClick", "OnDeltaMeterRightTapped")]
    [InlineData("DiffWordDeltaPill", "OnDiffWordDeltaPillClick", "OnDiffWordDeltaPillRightTapped")]
    public void HistoryMeters_AreButtons_BridgedForShiftF10(string name, string click, string rightTapped)
    {
        var el = Named(Xaml(HistoryXaml), name);
        Assert.Equal("Button", el.Name.LocalName);
        Assert.Equal(click, (string?)el.Attribute("Click"));
        Assert.Equal(rightTapped, (string?)el.Attribute("RightTapped"));
        Assert.Null(el.Attribute("Tapped"));
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.NotNull(el.Attribute("AutomationProperties.HelpText"));

        var code = Read(HistoryCode);
        Assert.Contains($"({name}, {rightTapped})", code);
        Assert.Contains($"private void {click}(object sender, RoutedEventArgs e)", code);
    }

    [Fact]
    public void HistoryMeters_DoubleClickCopiesOnce_DetailedCopyInMenu()
    {
        var code = Read(HistoryCode);
        var xaml = Read(HistoryXaml);
        Assert.DoesNotContain("OnDiffWordDeltaPillTapped", code);
        Assert.DoesNotContain("OnDiffWordDeltaPillDoubleTapped", code);
        Assert.DoesNotContain("OnDeltaMeterDoubleTapped", code);
        Assert.DoesNotContain("DeltaMeterBorder", code + xaml);
        Assert.Contains("Text = \"Copy detailed word delta\"", code);
        Assert.Contains("copyDetailed.Click += (_, _) => CopyDetailedWordDelta();", code);
        // The meter's own action is in the menu it opens.
        Assert.Contains("copyStats.Click += (_, _) => CopyDiffStats();", code);
    }

    [Fact]
    public void HistoryCopyButton_NoLongerCopiesThreeTimesOnDoubleClick()
    {
        var el = Named(Xaml(HistoryXaml), "CopyToolbarButton");
        Assert.Null(el.Attribute("DoubleTapped"));
        Assert.DoesNotContain("Double-click", (string?)el.Attribute("ToolTipService.ToolTip"));
        Assert.DoesNotContain("OnCopyToolbarButtonDoubleTapped", Read(HistoryCode));
    }

    [Fact]
    public void WordDeltaPill_MovesFocusBeforeCollapsing()
    {
        var code = Read(HistoryCode);
        Assert.DoesNotContain("DiffWordDeltaPill.Visibility = Visibility.Collapsed; }", code);
        var start = code.IndexOf("private void CollapseWordDeltaPill()", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = code.Substring(start, 700);
        var focus = body.IndexOf("CopyToolbarButton.Focus(FocusState.Programmatic)", StringComparison.Ordinal);
        var collapse = body.IndexOf("DiffWordDeltaPill.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);
        Assert.True(focus > 0 && collapse > focus);
        Assert.Contains("SearchBox?.Focus(FocusState.Programmatic)", body);
        // Both collapse paths in the stats bar go through the guard.
        Assert.Equal(2, code.Split("CollapseWordDeltaPill();").Length - 1);
    }
}
