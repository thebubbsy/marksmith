using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #323, the find and replace bar:
// - Ctrl+Alt+Enter replaces every match from either box (it opens the replace row first if hidden);
// - a regex error's reason is in the count's accessible name, not only its tooltip;
// - both boxes' help text names every key they take, and the find tooltip mentions Up/Down history;
// - the option toggles and Replace buttons expose their keys as AcceleratorKey, not in their names;
// - the empty count says "No matches", like the status line and the command palette;
// - the shortcuts sheet's one-row copy writes "Ctrl+E or Ctrl+Shift+P", as Copy all does.
public class Run323FindBarKeysTests
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

    private static string Tag(string xaml, string marker)
    {
        var at = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, marker);
        var start = xaml.LastIndexOf('<', at);
        var end = xaml.IndexOf('>', at);
        return xaml[start..(end + 1)];
    }

    private static string Attr(string tag, string name)
    {
        var m = Regex.Match(tag, Regex.Escape(name) + "=\"([^\"]*)\"");
        Assert.True(m.Success, name);
        return m.Groups[1].Value;
    }

    private static string Body(string code, string signature)
    {
        var at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        var next = Regex.Match(code[(at + signature.Length)..], @"\n    (private|public|internal|protected) ");
        return next.Success ? code.Substring(at, next.Index + signature.Length) : code[at..];
    }


    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    public void EmptyFindCount_SaysNoMatches(int current, int total) =>
        Assert.Equal("No matches", TextSearch.FormatFindMatchStatus(current, total));

    [Theory]
    [InlineData("x:Name=\"FindTextBox\"", "Ctrl+Alt+Enter: replace all")]
    [InlineData("x:Name=\"FindTextBox\"", "Up and Down: earlier searches")]
    [InlineData("x:Name=\"FindTextBox\"", "Alt+Home or Alt+End")]
    [InlineData("x:Name=\"FindTextBox\"", "Esc: close")]
    [InlineData("x:Name=\"ReplaceTextBox\"", "Ctrl+Alt+Enter: replace all")]
    [InlineData("x:Name=\"ReplaceTextBox\"", "Shift+Tab: back to Find")]
    public void FindBarBoxes_NameTheirKeys(string marker, string expected) =>
        Assert.Contains(expected, Attr(Tag(Read(Main), marker), "AutomationProperties.HelpText"));

    [Fact]
    public void FindBox_TooltipMentionsHistory() =>
        Assert.Contains("Up and Down recall earlier searches", Attr(Tag(Read(Main), "x:Name=\"FindTextBox\""), "ToolTipService.ToolTip"));

    [Fact]
    public void ReplaceAllButton_TooltipNamesItsKey() =>
        Assert.Contains("Ctrl+Alt+Enter", Attr(Tag(Read(Main), "x:Name=\"ReplaceAllButton\""), "ToolTipService.ToolTip"));

    [Theory]
    [InlineData("MatchCaseCheck", "Match case", "Alt+C")]
    [InlineData("MatchWholeWordCheck", "Match whole word", "Alt+W")]
    [InlineData("UseRegexCheck", "Use regular expression", "Alt+R")]
    [InlineData("ReplaceOneButton", "Replace match", "Enter")]
    [InlineData("ReplaceAllButton", "Replace all matches", "Ctrl+Alt+Enter")]
    public void FindBarButtons_KeepKeysOutOfTheirNames(string name, string accessibleName, string key)
    {
        var tag = Tag(Read(Main), $"x:Name=\"{name}\"");
        Assert.Equal(accessibleName, Attr(tag, "AutomationProperties.Name"));
        Assert.Equal(key, Attr(tag, "AutomationProperties.AcceleratorKey"));
    }

    [Theory]
    [InlineData("private void OnFindTextBoxKeyDown(")]
    [InlineData("private void OnReplaceTextBoxKeyDown(")]
    public void BothBoxes_TakeCtrlAltEnterBeforePlainEnter(string signature)
    {
        var body = Body(Read(MainCode), signature);
        var chord = body.IndexOf("VirtualKey.Enter && CtrlDown() && AltDown()", StringComparison.Ordinal);
        var plain = body.IndexOf("else if (e.Key == Windows.System.VirtualKey.Enter)", StringComparison.Ordinal);
        Assert.True(chord >= 0 && plain > chord, signature);
        Assert.Contains("ReplaceAllFromKeyboard();", body);
    }

    [Fact]
    public void ReplaceAllFromKeyboard_OpensAHiddenRowAndRefusesABadPattern()
    {
        var body = Body(Read(MainCode), "private void ReplaceAllFromKeyboard()");
        var hidden = body.IndexOf("ReplaceRow.Visibility != Visibility.Visible", StringComparison.Ordinal);
        var regex = body.IndexOf("_regexErrorMessage", StringComparison.Ordinal);
        var run = body.IndexOf("OnReplaceAllClick(", StringComparison.Ordinal);
        Assert.True(hidden >= 0 && regex > hidden && run > regex);
        Assert.Contains("ReplaceExpandToggle.IsChecked = true;", body);
        Assert.Contains("ReplaceTextBox.Focus(FocusState.Keyboard);", body);
    }

    [Fact]
    public void RegexError_ReasonIsInTheAccessibleName()
    {
        var body = Body(Read(MainCode), "private void UpdateFindCount()");
        Assert.Contains("$\"Regex error: {_regexErrorMessage}\"", body);
        Assert.Contains("SetName(FindCountText, countName)", body);
        Assert.Contains("SetName(FindCountButton, hasCount ? countName", body);
    }

    [Fact]
    public void ReplaceAllStatus_UsesCountText() =>
        Assert.Contains("Replaced {CountText.Of(count, \"occurrence\")}", Body(Read(MainCode), "private void OnReplaceAllClick("));

    [Fact]
    public void ShortcutSheetRowCopy_JoinsChordsWithOr()
    {
        var code = Read(MainCode);
        Assert.Contains("string.Join(\" or \", shortcut.Chords.Select(c => c.Display))", code);
        Assert.DoesNotContain("string.Join(\", \", shortcut.Chords.Select(c => c.Display))", code);
        Assert.DoesNotContain("Copied shortcut chord", code);
    }

    [Fact]
    public void ShortcutSheetCount_CountsActionsNotChordLines()
    {
        // "Copied 30 shortcuts" counts rows (actions); multi-chord rows show more key lines. That is
        // what SheetText writes too: one bullet per action.
        var rows = KeyboardShortcuts.Sheet().Sum(s => s.Rows.Count);
        var bullets = KeyboardShortcuts.SheetText().Split((char)10).Count(l => l.StartsWith("- ", StringComparison.Ordinal));
        Assert.Equal(rows, bullets);
    }
}
