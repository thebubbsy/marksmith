using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MarkSmith.Tests;

// Run #321:
// - combo boxes that act on selection (Step 1's recent files, the AI-agent folder presets, the OCR
//   studio's quick reclassify and per-card reclassify boxes) no longer act once per arrow key on
//   the closed box: Up/Down/Home/End/PageUp/PageDown open the list, Enter picks;
// - those boxes, and the main window's Markdown issues, outline and recent exports lists, say
//   which keys work.
public class Run321ComboArrowKeysTests
{
    private static string RepoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MarkSmith.Desktop", "MarkSmith.Desktop.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoDir(), relative)).Replace("\r\n", "\n");

    private const string Main = "MarkSmith.Desktop/MainWindow.xaml";
    private const string MainCode = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string Ocr = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml";
    private const string OcrCode = "MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs";
    private const string Helper = "MarkSmith.Desktop/Services/ComboArrowOpens.cs";

    private static string Tag(string xaml, string marker)
    {
        var at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, marker);
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Help(string tag)
    {
        var m = Regex.Match(tag, "AutomationProperties.HelpText=\"([^\"]*)\"");
        Assert.True(m.Success, tag);
        return m.Groups[1].Value;
    }

    private static string Method(string code, string name)
    {
        var at = code.IndexOf($"void {name}(", StringComparison.Ordinal);
        Assert.True(at >= 0, name);
        var next = Regex.Match(code[(at + 10)..], @"\n\s+(private|public|internal|protected) ");
        return next.Success ? code.Substring(at, next.Index + 10) : code[at..];
    }

    [Fact]
    public void HelperOpensTheClosedListOnNavigationKeysOnly()
    {
        var code = Read(Helper);
        Assert.Contains("PreviewKeyDown", code);
        // Leaves an open list alone: there arrows only move the highlight.
        Assert.Contains("if (combo.IsDropDownOpen) return;", code);
        Assert.Contains("e.Handled = true;", code);
        Assert.Contains("combo.IsDropDownOpen = true;", code);
        foreach (var key in new[] { "Up", "Down", "Home", "End", "PageUp", "PageDown" })
            Assert.Contains($"VirtualKey.{key}", code);
        // Not Enter, Space, F4 or Esc: those keep the stock behaviour (letters: see Run322).
        foreach (var key in new[] { "Enter", "Space", "F4", "Escape" })
            Assert.DoesNotContain($"VirtualKey.{key}", code);
    }

    [Theory]
    [InlineData("MarkdownFilesCombo", "OnMarkdownFileSelected", "OpenOrImportDocumentAsync")]
    [InlineData("WatchFolderPresets", "OnWatchFolderPresetSelected", "ViewModel.WatchFolder =")]
    public void MainWindowActionCombosOpenOnArrows(string combo, string handler, string action)
    {
        var code = Read(MainCode);
        Assert.Matches($@"Services\.ComboArrowOpens\.Attach\({combo}(, [^;]+)?\);", code);
        // The reason: the selection handler acts straight away.
        Assert.Contains(action, Method(code, handler));
        Assert.StartsWith("Down arrow opens the list", Help(Tag(Read(Main), $"x:Name=\"{combo}\"")));
    }

    [Fact]
    public void OcrReclassifyCombosOpenOnArrows()
    {
        var code = Read(OcrCode);
        Assert.Contains("ComboArrowOpens.Attach(QuickReclassifyComboBox);", code);
        Assert.Contains("ReclassifyElement(_selectedElement, newKind);", Method(code, "OnQuickReclassifySelectionChanged"));
        // The per-card boxes, made in code.
        var card = code[code.IndexOf("$\"Reclassify element {elem.Id}\"", StringComparison.Ordinal)..];
        card = card[..card.IndexOf("cardStack.Children.Add(combo);", StringComparison.Ordinal)];
        Assert.Contains("ComboArrowOpens.Attach(combo);", card);
        Assert.Contains("AutomationProperties.SetHelpText(combo, ReclassifyComboHelp);", card);
        Assert.Contains("ReclassifyElement(elem, newKind);", card);
        // The XAML box and the cards say the same thing.
        var help = Help(Tag(Read(Ocr), "x:Name=\"QuickReclassifyComboBox\""));
        Assert.Contains($"\"{help}\"", code);
        Assert.Contains("Keys 1 to 8 reclassify directly, R resets", help);
    }

    [Fact]
    public void LintListNamesItsKeys()
    {
        var help = Help(Tag(Read(Main), "x:Name=\"LintList\""));
        var keys = Method(Read(MainCode), "OnLintListKeyDown");
        Assert.Contains("Enter or Space jumps to the line", help);
        Assert.Contains("JumpToLintIssue", keys);
        Assert.Contains("Ctrl+Shift+C copies all", help);
        Assert.Contains("OnCopyAllLintIssuesClick", keys);
        Assert.Contains("Shift+F10", help);
        Assert.Contains("VirtualKey.F10", keys);
    }

    [Fact]
    public void OutlineListNamesItsKeys()
    {
        var help = Help(Tag(Read(Main), "x:Name=\"TocList\""));
        var keys = Method(Read(MainCode), "OnTocListKeyDown");
        Assert.Contains("Enter or Space jumps to the heading", help);
        Assert.Contains("NavigateToTocEntry", keys);
        Assert.Contains("Ctrl+Shift+C copies a Markdown link", help);
        Assert.Contains("Copied Markdown link", keys);
        Assert.Contains("Ctrl+F filters", help);
        Assert.Contains("TocFilterBox.Focus", keys);
    }

    [Fact]
    public void RecentExportsListNamesItsKeys()
    {
        var help = Help(Tag(Read(Main), "x:Name=\"HistoryList\""));
        var keys = Method(Read(MainCode), "OnHistoryListKeyDown");
        Assert.Contains("Enter or Space opens the file", help);
        Assert.Contains("OpenExportedFile", keys);
        Assert.Contains("Ctrl+O shows it in its folder", help);
        Assert.Contains("ShowExportedFileInFolder", keys);
        Assert.Contains("Delete removes it from the list", help);
        Assert.Contains("RemoveHistoryEntryFromFlyout", keys);
    }

    // Guard, as Run320's for the studios: a main-window list with its own keys says which keys work.
    [Fact]
    public void EveryMainWindowListWithItsOwnKeysHasHelpText()
    {
        var matches = Regex.Matches(Read(Main), "<(ListView|GridView)\\s[^>]*KeyDown=\"[^\"]+\"[^>]*>");
        Assert.True(matches.Count >= 3);
        foreach (Match m in matches)
            Assert.True(m.Value.Contains("AutomationProperties.HelpText=", StringComparison.Ordinal), m.Value);
    }
}
