using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MarkSmith.Tests;

// Run #256, Suite Hub's recent documents menu (the ⋯ button, the count pill and the strip's right-click):
// - about 48 rows at the top level (reports, advisors and file-rewriting actions mixed together), so it
//   ran off the screen; 43 items across it and its Diagnostics submenu shared one picture glyph (E8B9);
// - two identical "Copy workspace file size distribution & storage bloat advisor" entries ran the same report;
// - "Apply … normalization", "Apply … table … fixes" and "Restore … backups" rewrote every recent document
//   on one click, and "Clear recent documents history" erased the list, all without asking;
// - seven Diagnostics entries were in Title Case and two MAX_PATH audits read the same.
// Version History: with no document, the file name button copied "No document selected" as a path and,
// as the first tab stop, opened the window with a focus ring on it.
public class Run256RecentDocsMenuAndHistoryTests
{
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

    private static string Read(string relative) => File.ReadAllText(Path.Combine(DesktopDir(), relative)).Replace("\r\n", "\n");

    private static XElement RecentDocsFlyout()
    {
        var doc = XDocument.Load(Path.Combine(DesktopDir(), "Views", "SuiteHubView.xaml"));
        return doc.Descendants(Ui + "MenuFlyout").Single(m => (string?)m.Attribute(XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name") == "RecentDocsFlyout");
    }

    private static XElement Sub(XElement menu, string text) =>
        menu.Elements(Ui + "MenuFlyoutSubItem").Single(s => (string?)s.Attribute("Text") == text);

    private static string Glyph(XElement item) =>
        (string)item.Descendants(Ui + "FontIcon").First().Attribute("Glyph")!;

    private static string Click(XElement item) => (string)item.Attribute("Click")!;

    // Method body from its signature to the next member at class indent.
    private static string Method(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        var end = code.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? code[start..] : code[start..end];
    }

    [Fact]
    public void TopLevel_IsShort_AndGroupsReportsAdvisorsAndFixesIntoSubmenus()
    {
        var menu = RecentDocsFlyout();
        var top = menu.Elements().Where(e => e.Name.LocalName is "MenuFlyoutItem" or "MenuFlyoutSubItem").ToList();
        Assert.InRange(top.Count, 10, 16);

        var subs = menu.Elements(Ui + "MenuFlyoutSubItem").Select(s => (string)s.Attribute("Text")!).ToList();
        Assert.Equal(new[] { "Library reports", "Workspace advisors", "Normalize & fix files", "Diagnostics" }, subs);

        // Everyday actions stay one click away.
        var topClicks = menu.Elements(Ui + "MenuFlyoutItem").Select(Click).ToList();
        foreach (var c in new[] { "OnBrowseFileToOpenClick", "OnOpenDocumentsFolderClick", "OnFilterMarkdownRecentClick",
                                  "OnClearRecentFilterClick", "OnCopyAllRecentPathsClick", "OnCopyRecentAsMarkdownListClick",
                                  "OnRefreshRecentDocsClick", "OnClearRecentDocsClick" })
            Assert.Contains(c, topClicks);

        Assert.All(Sub(menu, "Workspace advisors").Elements(Ui + "MenuFlyoutItem"), i => Assert.Contains("Advisor", Click(i)));
        Assert.Equal(new[] { "OnGenerateWorkspaceDialectConfigFileClick", "OnSimulateWorkspaceDialectNormalizationClick",
                             "OnApplyWorkspaceDialectNormalizationClick", "OnApplyWorkspaceMarkdownTableAutoAlignClick",
                             "OnRestoreWorkspaceTableBackupsClick" },
                     Sub(menu, "Normalize & fix files").Elements(Ui + "MenuFlyoutItem").Select(Click));
        Assert.True(Sub(menu, "Library reports").Elements(Ui + "MenuFlyoutItem").Count() >= 10);
        foreach (var s in menu.Elements(Ui + "MenuFlyoutSubItem"))
            Assert.False(string.IsNullOrWhiteSpace(s.Attributes().FirstOrDefault(a => a.Name.LocalName == "ToolTipService.ToolTip")?.Value));
    }

    [Fact]
    public void EveryItem_HasAnIcon_AndNoneUsesThePictureGlyph()
    {
        var items = RecentDocsFlyout().Descendants().Where(e => e.Name.LocalName is "MenuFlyoutItem" or "MenuFlyoutSubItem").ToList();
        Assert.True(items.Count > 80);
        foreach (var item in items)
            Assert.NotEqual("", Glyph(item));

        var all = RecentDocsFlyout().Descendants(Ui + "MenuFlyoutItem").ToDictionary(Click);
        Assert.Equal("", Glyph(all["OnApplyWorkspaceMarkdownTableAutoAlignClick"]));
        Assert.Equal("", Glyph(all["OnRestoreWorkspaceTableBackupsClick"]));
        Assert.Equal("", Glyph(all["OnSimulateWorkspaceDialectNormalizationClick"]));
        Assert.Equal("", Glyph(all["OnCopyWorkspaceCodeFenceAdvisorClick"]));
        Assert.Equal("", Glyph(all["OnCopyWorkspaceHeadingAdvisorClick"]));
        Assert.Equal("", Glyph(all["OnAuditWorkspaceBacklinksClick"]));
        Assert.Equal("", Glyph(all["OnCopyRecentAsMarkdownListClick"]));
        Assert.Equal("", Glyph(all["OnCopyWorkspaceTableFormattingAdvisorClick"]));
        Assert.Equal("", Glyph(all["OnCopyWorkspaceAssetDeduplicationAdvisorClick"]));
    }

    [Fact]
    public void NoTwoItems_ShareALabelOrAHandler()
    {
        var items = RecentDocsFlyout().Descendants(Ui + "MenuFlyoutItem").ToList();
        var texts = items.Select(i => (string)i.Attribute("Text")!).ToList();
        Assert.Equal(texts.Count, texts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(items.Count, items.Select(Click).Distinct().Count());

        var code = Read("Views/SuiteHubView.xaml.cs");
        Assert.DoesNotContain("OnCopyWorkspaceFileSizeAdvisorClick", code);
        Assert.Contains("Copy path length, nesting depth & MAX_PATH (260 char) audit", texts);
        Assert.Contains("Copy path length distribution & MAX_PATH audit", texts);
    }

    [Fact]
    public void Diagnostics_AreSentenceCase()
    {
        var texts = Sub(RecentDocsFlyout(), "Diagnostics").Elements(Ui + "MenuFlyoutItem").Select(i => (string)i.Attribute("Text")!);
        foreach (var t in texts)
        {
            // After the first word, a capital only for acronyms (TOC, YAML, KaTeX…) or names (MAX_PATH).
            foreach (var word in t.Split(' ').Skip(1))
                Assert.False(Regex.IsMatch(word, "^[A-Z][a-z]+$") && word is not "KaTeX" and not "LaTeX", $"'{word}' in '{t}'");
        }
    }

    [Theory]
    [InlineData("OnApplyWorkspaceDialectNormalizationClick", "ApplyWorkspaceDialectNormalization(files)")]
    [InlineData("OnApplyWorkspaceMarkdownTableAutoAlignClick", "ApplyWorkspaceTableAutoAlignmentFixes(explicitFiles: files)")]
    [InlineData("OnRestoreWorkspaceTableBackupsClick", "RestoreWorkspaceTableAutoAlignmentBackups(explicitFiles: files)")]
    public void FileRewritingActions_AskFirst_ThenWorkOffTheUiThread(string handler, string call)
    {
        var body = Method(Read("Views/SuiteHubView.xaml.cs"), $"void {handler}(");
        var ask = body.IndexOf("if (!await ConfirmRecentDocsActionAsync(", StringComparison.Ordinal);
        var run = body.IndexOf($"await Task.Run(() => RecentFilesService.{call})", StringComparison.Ordinal);
        Assert.True(ask >= 0, $"{handler} doesn't ask");
        Assert.True(run > ask, $"{handler} rewrites files before asking, or on the UI thread");
        Assert.Contains("DocumentCount(files.Count)", body);
    }

    [Fact]
    public void ClearingHistory_AsksFirst_WhenThereIsAnythingToClear()
    {
        var body = Method(Read("Views/SuiteHubView.xaml.cs"), "void OnClearRecentDocsClick(");
        Assert.Contains("private async void OnClearRecentDocsClick", Read("Views/SuiteHubView.xaml.cs"));
        var ask = body.IndexOf("count > 0 && !await ConfirmRecentDocsActionAsync(\"Clear recent documents?\"", StringComparison.Ordinal);
        Assert.True(ask >= 0);
        Assert.True(body.IndexOf("App.RecentFiles?.Clear();", StringComparison.Ordinal) > ask);
        Assert.Contains("The files themselves are not touched.", body);
    }

    [Fact]
    public void ConfirmFlyout_DefaultsToCancel_AndIsPolished()
    {
        var code = Read("Services/ConfirmFlyout.cs");
        Assert.Contains("cancel.Focus(FocusState.Programmatic);", code);
        Assert.Contains("HoverPolish.Apply(content);", code);
        Assert.Contains("\"AccentButtonStyle\"", code);
        Assert.Contains("flyout.Closed += (_, _) => answer.TrySetResult(chosen);", code);
        Assert.Contains("ThemeBrush.Set(body, TextBlock.ForegroundProperty, \"TextFillColorSecondaryBrush\");", code);
        // Shown after the menu that asked has closed, and never throws if the anchor has gone.
        Assert.Contains("DispatcherQueuePriority.Low", code);
        Assert.Contains("catch (System.ArgumentException)", code);

        // Anchored to the menu's button when it's on screen, else the hub itself.
        var hub = Read("Views/SuiteHubView.xaml.cs");
        Assert.Contains("RecentDocsActionBtn is { XamlRoot: not null, ActualWidth: > 0 } ? RecentDocsActionBtn : this", hub);
    }

    [Fact]
    public void History_FileNameButton_HasNothingToCopyWithoutADocument()
    {
        var code = Read("Views/History/HistoryWindow.xaml.cs");
        var click = Method(code, "void OnFileNameButtonClick(");
        Assert.Contains("var filePath = _vm.SelectedFile?.Summary.FilePath;", click);
        Assert.DoesNotContain("_vm.FileName", click);

        var menu = Method(code, "void OnFileNameRightTapped(");
        Assert.Contains("if (_vm.SelectedFile is not { } file) return;", menu);
        Assert.DoesNotContain("?? _vm.FileName", menu);

        var update = Method(code, "void UpdateFileNameButton(");
        Assert.Contains("FileNameButton.IsTabStop = hasFile;", update);
        // Focus leaves the button before it leaves the tab order.
        Assert.True(update.IndexOf("SearchBox.Focus(FocusState.Programmatic)", StringComparison.Ordinal)
                    < update.IndexOf("FileNameButton.IsTabStop = hasFile;", StringComparison.Ordinal));
        Assert.Contains("e.PropertyName == nameof(HistoryWindowViewModel.SelectedFile)", code);
        Assert.Contains("UpdateFileNameButton();\n    }", code);

        // The XAML keeps the with-a-document text; the code swaps it while there is none.
        Assert.Contains("Versions appear here once a document is saved", update);
    }
}
