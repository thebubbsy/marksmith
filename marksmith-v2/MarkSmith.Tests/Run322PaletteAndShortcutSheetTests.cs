using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// Run #322:
// - the command palette says "No matches" (and reads out which query) instead of a blank footer;
// - Ctrl+Delete in its search box deletes the next word while there is text after the caret;
// - its three "Paste into search" items paste one line at the caret and give the box its focus back;
// - the search box's help text names every key it takes, and the footer names Shift+F10;
// - the keyboard shortcuts sheet shows its copy results inside the dialog;
// - typing on the recent-files and folder-preset boxes, open or closed, only moves the highlight.
public class Run322PaletteAndShortcutSheetTests
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

    private const string Palette = "MarkSmith.Desktop/Controls/CommandPalette.xaml";
    private const string PaletteCode = "MarkSmith.Desktop/Controls/CommandPalette.xaml.cs";
    private const string Main = "MarkSmith.Desktop/MainWindow.xaml";
    private const string MainCode = "MarkSmith.Desktop/MainWindow.xaml.cs";
    private const string Helper = "MarkSmith.Desktop/Services/ComboArrowOpens.cs";

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

    // ---- Paste into search ----

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   \r\n\t ", "")]
    [InlineData("export pdf", "export pdf")]
    [InlineData("  export   pdf\t now ", "export pdf now")]
    [InlineData("\r\n\r\n  heading two\r\nsecond line\r\n", "heading two")]
    [InlineData("first\nsecond", "first")]
    public void PasteQuery_keeps_the_first_non_blank_line_tidied(string? clipboard, string expected)
    {
        Assert.Equal(expected, CommandSearch.PasteQuery(clipboard));
    }

    [Fact]
    public void PasteQuery_caps_a_long_line()
    {
        var pasted = CommandSearch.PasteQuery(new string('a', 40) + " " + new string('b', 200));
        Assert.True(pasted.Length <= CommandSearch.MaxPastedQuery);
        Assert.StartsWith(new string('a', 40), pasted);
        Assert.False(pasted.EndsWith(' '));
    }

    [Fact]
    public void Every_paste_item_goes_through_the_one_caret_aware_helper()
    {
        var code = Read(PaletteCode);
        Assert.Equal(3, Regex.Matches(code, @"pasteItem\.Click \+= async \(_, _\) => await PasteIntoSearchAsync\(\);").Count);
        // The old bodies replaced the whole query with the raw clipboard.
        Assert.DoesNotContain("SearchBox.Text = text;", code);
        var helper = Body(code, "private async Task PasteIntoSearchAsync()");
        Assert.Contains("CommandSearch.PasteQuery(", helper);
        Assert.Contains(".Remove(start, length).Insert(start, text)", helper);
        Assert.Contains("SearchBox.Focus(FocusState.Programmatic);", helper);
        // An await sits between the click and the edit; don't touch a palette that has closed.
        Assert.Contains("if (_closing) return;", helper);
    }

    // ---- Keys ----

    [Fact]
    public void Ctrl_Delete_removes_a_recent_only_with_nothing_after_the_caret()
    {
        var code = Read(PaletteCode);
        Assert.Contains("case VirtualKey.Delete when ctrl && NothingAfterCaret() && Results.SelectedItem is PaletteCommand recCmd && IsRecent(recCmd.Label):", code);
        var guard = Body(code, "private bool NothingAfterCaret()");
        Assert.Contains("SearchBox.SelectionLength == 0", guard);
        Assert.Contains("SearchBox.SelectionStart >= SearchBox.Text.Length", guard);
    }

    [Fact]
    public void Search_box_help_names_each_key_the_palette_handles()
    {
        var code = Read(PaletteCode);
        var help = Attr(Tag(Read(Palette), "x:Name=\"SearchBox\""), "AutomationProperties.HelpText");
        var keys = new (string Case, string Said)[]
        {
            ("case VirtualKey.Down:", "Down"),
            ("case VirtualKey.PageDown:", "Page Down"),
            ("case VirtualKey.Home when ctrl:", "Ctrl+Home"),
            ("case VirtualKey.End when ctrl:", "Ctrl+End"),
            ("case VirtualKey.Enter:", "Enter"),
            ("case VirtualKey.C when ctrl", "Ctrl+C"),
            ("case VirtualKey.Delete when ctrl", "Ctrl+Delete"),
            ("case VirtualKey.Escape:", "Esc"),
        };
        foreach (var (handled, said) in keys)
        {
            Assert.Contains(handled, code);
            Assert.Contains(said, help);
        }
        Assert.Contains("Shift+F10", help);
    }

    [Fact]
    public void Footer_names_Shift_F10_rather_than_the_Apps_key()
    {
        var xaml = Read(Palette);
        Assert.DoesNotContain("Text=\"Apps\"", xaml);
        Assert.Contains("<TextBlock Text=\"Shift+F10\" Style=\"{StaticResource PaletteKeyCapTextStyle}\" />", xaml);
    }

    // ---- No matches ----

    [Fact]
    public void A_search_with_no_hits_says_so_and_is_read_out()
    {
        var code = Read(PaletteCode);
        Assert.Contains(": count <= 0 ? \"No matches\"", code);
        var refresh = Body(code, "private void Refresh()");
        var empty = refresh[refresh.IndexOf("if (count == 0)", StringComparison.Ordinal)..];
        Assert.Contains("AutomationProperties.SetName(CountText, NoMatchesTitle.Text);", empty);
        // CountText is the tracked live region, so the change of text is what gets announced.
        Assert.Contains("Services.LiveAnnounce.Track(CountText);", code);
    }

    // ---- Keyboard shortcuts sheet ----

    [Fact]
    public void Shortcut_sheet_copies_report_inside_the_dialog()
    {
        var code = Read(MainCode);
        var sheet = Body(code, "private async Task ShowShortcutsCheatsheetAsync()");
        Assert.Contains("Content = new StackPanel { Children = { sheet, copyStatus } },", sheet);
        // Both copy paths (a row's chord, the whole list) use it, and nothing writes the
        // status bar directly any more.
        Assert.Equal(2, Regex.Matches(sheet, @"ShowCopyStatus\(err is null").Count);
        Assert.DoesNotContain("ViewModel.StatusText = err is null", sheet);
        var show = sheet[sheet.IndexOf("void ShowCopyStatus(", StringComparison.Ordinal)..];
        Assert.Contains("copyStatus.Visibility = Visibility.Visible;", show);
        Assert.Contains("AutomationProperties.SetName(copyStatus, message);", show);
        Assert.Contains("Services.LiveAnnounce.Raise(copyStatus);", show);
        // The status bar still has it after the dialog closes.
        Assert.Contains("ViewModel.StatusText = message;", show);
    }

    // ---- Type-ahead on the action boxes ----

    [Theory]
    [InlineData(new[] { "alpha", "beta", "gamma" }, -1, "b", 1)]
    [InlineData(new[] { "alpha", "beta", "gamma" }, -1, "G", 2)]
    [InlineData(new[] { "alpha", "beta", "gamma" }, -1, "z", -1)]
    [InlineData(new[] { "alpha", "beta", "gamma" }, -1, "", -1)]
    // One letter again steps to the next row starting with it, wrapping round.
    [InlineData(new[] { "bar", "alpha", "baz", "beta" }, 0, "b", 2)]
    [InlineData(new[] { "bar", "alpha", "baz", "beta" }, 3, "b", 0)]
    [InlineData(new[] { "bar", "alpha", "baz", "beta" }, 0, "bb", 2)]
    // A longer prefix may stay on the current row.
    [InlineData(new[] { "bar", "alpha", "baz", "beta" }, 2, "ba", 2)]
    [InlineData(new[] { "bar", "alpha", "baz", "beta" }, 0, "be", 3)]
    // The only match wraps back to itself.
    [InlineData(new[] { "alpha", "beta", "gamma" }, 1, "b", 1)]
    public void TypeAheadSearch_finds_the_row_a_prefix_lands_on(string[] names, int current, string prefix, int expected)
    {
        Assert.Equal(expected, TypeAheadSearch.Find(names, current, prefix));
    }

    [Fact]
    public void TypeAheadSearch_skips_blank_names_and_handles_empty_lists()
    {
        Assert.Equal(-1, TypeAheadSearch.Find(Array.Empty<string?>(), -1, "a"));
        Assert.Equal(1, TypeAheadSearch.Find(new string?[] { null, "apple", "" }, -1, "a"));
    }

    [Fact]
    public void TypeAheadSearch_extends_quick_keys_and_restarts_after_a_pause()
    {
        Assert.Equal("be", TypeAheadSearch.Extend("b", 'e', TimeSpan.FromMilliseconds(200)));
        Assert.Equal("e", TypeAheadSearch.Extend("b", 'e', TypeAheadSearch.Pause + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Type_ahead_keys_are_handled_and_only_move_the_highlight()
    {
        var code = Read(Helper);
        Assert.Contains("public static void Attach(ComboBox combo, Func<object, string?>? typeAheadText = null)", code);
        // WinUI's own type-ahead selects (commits) the match, open or closed, so the key is handled.
        var tryTypeAhead = code[code.IndexOf("bool TryTypeAhead(KeyRoutedEventArgs e)", StringComparison.Ordinal)..];
        tryTypeAhead = tryTypeAhead[..tryTypeAhead.IndexOf("return true;", StringComparison.Ordinal)];
        Assert.Contains("ModifierDown()", tryTypeAhead);
        Assert.Contains("e.Handled = true;", tryTypeAhead);
        // Keys in the open list reach its popup, not the combo: the popup's child is hooked once.
        Assert.Contains("combo.DropDownOpened += (_, _) =>", code);
        Assert.Contains("FindPopup(combo)?.Child is UIElement list && Hooked.TryAdd(list, Marker)", code);
        // The highlight is keyboard focus on a row; nothing sets SelectedIndex / SelectedItem.
        var highlight = code[code.IndexOf("private static void HighlightMatch(", StringComparison.Ordinal)..];
        Assert.Contains("TypeAheadSearch.Find(names, current, prefix)", highlight);
        Assert.Contains("row.Focus(FocusState.Keyboard);", highlight);
        Assert.DoesNotContain("SelectedIndex =", code);
        Assert.DoesNotContain("SelectedItem =", code);
        foreach (var range in new[] { "VirtualKey.A and <= VirtualKey.Z", "VirtualKey.Number0 and <= VirtualKey.Number9", "VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9" })
            Assert.Contains(range, code);
        Assert.Contains("IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu)", code);
    }

    [Theory]
    [InlineData("MarkdownFilesCombo", "item => (item as Services.MarkdownFileEntry)?.Name")]
    [InlineData("WatchFolderPresets", "item => (item as Services.AiAgentFolderPresets.FolderPreset)?.Name")]
    public void Step_one_action_boxes_use_type_ahead_and_say_so(string combo, string text)
    {
        Assert.Contains($"Services.ComboArrowOpens.Attach({combo}, {text});", Read(MainCode));
        var help = Attr(Tag(Read(Main), $"x:Name=\"{combo}\""), "AutomationProperties.HelpText");
        Assert.StartsWith("Down arrow opens the list, and so does typing", help);
    }

    [Fact]
    public void Ocr_reclassify_boxes_keep_digits_for_their_own_shortcuts()
    {
        // Keys 1 to 8 reclassify directly there, so those boxes don't take type-ahead.
        var code = Read("MarkSmith.Desktop/Views/Ocr/OcrInspectionStudioWindow.xaml.cs");
        Assert.Contains("ComboArrowOpens.Attach(QuickReclassifyComboBox);", code);
        Assert.DoesNotContain("ComboArrowOpens.Attach(QuickReclassifyComboBox, ", code);
    }
}
