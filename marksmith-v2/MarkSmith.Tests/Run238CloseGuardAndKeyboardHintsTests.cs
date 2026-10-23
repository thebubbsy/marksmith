using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MarkSmith.Ocr;
using Xunit;

namespace MarkSmith.Tests;

// Run #238: the OCR studio asks before Esc / Discard / X throws away reclassifications, its
// Shift+F10 opens the page menu when nothing is selected, every "Right-click for …" hint on a
// keyboard-bridged control names Shift+F10, History's Documents header has a focusable refresh
// button, and the extension address chips, Suite Hub's recent documents and the Code / Split /
// Preview bar open their own menus from the keyboard.
public class Run238CloseGuardAndKeyboardHintsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    // Same exclusions as Run223: those studios are being reworked in another uncommitted edit.
    private static readonly string[] Excluded = { "ShapeStudio", "SmartArtStudio", "MindMap", "SplashWindow" };

    private static readonly HashSet<string> Focusable = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "HyperlinkButton", "SplitButton", "DropDownButton", "SelectorBar",
    };

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

    public static IEnumerable<object[]> Surfaces()
    {
        var root = DesktopDir();
        foreach (var xaml in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .Where(p => !Excluded.Any(p.Contains))
                     .Where(p => File.ReadAllText(p).Contains("RightTapped=\""))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetRelativePath(root, xaml).Replace('\\', '/') };
        }
    }

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void BridgedControls_RightClickHints_AlsoNameShiftF10(string relativeXaml)
    {
        // Run223 only caught the exact words "Right-click for options"; "Right-click for page
        // navigation", "right-click for sizes" and friends were left saying mouse only.
        var code = Read(relativeXaml + ".cs");
        var doc = XDocument.Load(Path.Combine(DesktopDir(), relativeXaml));
        var stale = new List<string>();
        foreach (var e in doc.Descendants())
        {
            if (!Focusable.Contains(e.Name.LocalName)) continue;
            var name = (string?)e.Attribute(X + "Name");
            var handler = (string?)e.Attribute("RightTapped");
            var tip = (string?)e.Attribute("ToolTipService.ToolTip");
            if (name is null || handler is null || tip is null) continue;
            if (!Regex.IsMatch(code, $@"\(\s*{name}\s*,\s*{handler}\s*\)")) continue;
            if (Regex.IsMatch(tip, @"[Rr]ight-click (for|to) ") && !tip.Contains("Shift+F10")) stale.Add(name);
        }
        Assert.True(stale.Count == 0, $"{relativeXaml}: {string.Join(", ", stale)}");
    }

    [Fact]
    public void CloseGuard_ReturnsACloseRequest_ThatAsksOnlyWithUnkeptWork()
    {
        var code = Read("Services/CloseGuard.cs");
        Assert.Contains("public static Action Attach(", code);
        // The request closes straight away when nothing would be lost, and otherwise asks.
        Assert.Matches(@"if \(allowClose \|\| !hasUnkeptWork\(\)\)\s*\{\s*allowClose = true;\s*window\.Close\(\);", code);
        Assert.Contains("else Ask();", code);
    }

    [Fact]
    public void OcrStudio_DiscardEscAndX_GoThroughTheGuard_AfterReclassifying()
    {
        var code = Read("Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.Contains("_requestClose = CloseGuard.Attach(this,", code);
        Assert.Contains("_session.TotalReclassifications > 0 && !_decisionTcs.Task.IsCompleted", code);
        Assert.Contains("private void OnDiscardClick(object sender, RoutedEventArgs e) => _requestClose();", code);
        // Esc still routes to the same discard path.
        Assert.Matches(@"VirtualKey\.Escape && !inputFocused\)\s*\{\s*e\.Handled = true;\s*OnDiscardClick\(", code);
        // "Insert and close" completes the import without a second Close().
        var insert = code.IndexOf("() => { CompleteImport(); return true; }", StringComparison.Ordinal);
        Assert.True(insert > 0);
    }

    [Fact]
    public void OcrStudio_ShiftF10_WithNothingSelected_OpensThePageMenu()
    {
        var code = Read("Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.Contains("if (KeyboardContextMenu.IsContextMenuKey(e.Key))", code);
        Assert.Contains("KeyboardContextMenu.Invoke(PageSheetBorder, OnPageSheetRightTapped);", code);
        Assert.DoesNotContain("(e.Key == Windows.System.VirtualKey.F10 && shift)", code);
        Assert.Contains("Right-click or Shift+F10 for actions", code);
    }

    [Fact]
    public void OcrSession_ReclassificationCount_DropsToZeroAfterReset()
    {
        // The guard's predicate: once every change is reset there is nothing to ask about.
        var session = new OcrInspectionSession();
        var page = new OcrInspectionPage { PageNumber = 1, PageIndex = 0, PageCount = 1 };
        page.Elements.Add(new OcrInspectionElement
        {
            Id = 1, PageNumber = 1, OriginalKind = OcrBlockKind.Paragraph, Kind = OcrBlockKind.Paragraph,
            OriginalMarkdown = "Quarterly results", CurrentMarkdown = "Quarterly results",
        });
        session.Pages.Add(page);
        Assert.Equal(0, session.TotalReclassifications);

        page.Elements[0].Reclassify(OcrBlockKind.Heading);
        Assert.Equal(1, session.TotalReclassifications);

        session.ResetCurrentPage();
        Assert.Equal(0, session.TotalReclassifications);
    }

    [Fact]
    public void History_DocumentsHeader_HasAFocusableRefreshButton_WithItsMenu()
    {
        var xaml = Read("Views/History/HistoryWindow.xaml");
        var code = Read("Views/History/HistoryWindow.xaml.cs");
        Assert.Contains("x:Name=\"DocumentsRefreshButton\"", xaml);
        Assert.Contains("Click=\"OnDocumentsRefreshButtonClick\" RightTapped=\"OnDocumentsHeaderRightTapped\"", xaml);
        Assert.Contains("(DocumentsRefreshButton, OnDocumentsHeaderRightTapped)", code);
        // A double-click on the button must not refresh a third time through the header.
        Assert.Contains("IsWithin(e.OriginalSource as DependencyObject, DocumentsRefreshButton)", code);
    }

    [Fact]
    public void ExtensionSetup_AddressChips_OpenTheirMenuFromTheKeyboard()
    {
        var code = Read("Views/ExtensionSetupView.xaml.cs");
        Assert.Contains("KeyboardContextMenu.Bridge(button, showMenu);", code);
        Assert.Contains("ShowChipContextMenu(button, icon, browser, address, KeyboardContextMenu.PointIn(e, button));", code);
        Assert.DoesNotContain("e.GetPosition(button)", code);
        Assert.Contains("(Right-click or Shift+F10 for options)", code);
    }

    [Fact]
    public void SuiteHub_RecentDocuments_AreBridged()
    {
        var code = Read("Views/SuiteHubView.xaml.cs");
        Assert.Contains("KeyboardContextMenu.Bridge(btn, OnRecentFileItemRightTapped);", code);
    }

    [Fact]
    public void MainWindow_ViewSelector_AndSectionTooltip_AreKeyboardHonest()
    {
        var code = Read("MainWindow.xaml.cs");
        Assert.Contains("(CenterViewSelector, OnCenterViewSelectorRightTapped)", code);
        Assert.Contains("right-click or Shift+F10 for options\");", code);
        Assert.DoesNotContain("{activeSection.LineNumber}, right-click for options", code);
    }
}
