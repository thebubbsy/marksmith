using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class LineFormattingTests
{
    private static (string Text, int Start, int Length) Run(string text, int start, int length, Func<string, int, int, LineEdit> op)
    {
        var e = op(text, start, length);
        var result = text[..e.Start] + e.Replacement + text[(e.Start + e.Length)..];
        return (result, e.SelectionStart, e.SelectionLength);
    }

    [Fact]
    public void Heading_With_The_Caret_At_The_End_Of_A_Line_Marks_The_Line_Not_The_Caret()
    {
        // The old toolbar inserted "# " at the caret: "Hello# ".
        var (text, start, length) = Run("Hello", 5, 0, (t, s, l) => LineFormatting.Heading(t, s, l, 1));
        Assert.Equal("# Hello", text);
        Assert.Equal(7, start);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Heading_Of_The_Same_Level_Toggles_Off_And_Another_Level_Replaces()
    {
        Assert.Equal("Hello", Run("# Hello", 3, 0, (t, s, l) => LineFormatting.Heading(t, s, l, 1)).Text);
        var (text, start, _) = Run("## Hello", 5, 0, (t, s, l) => LineFormatting.Heading(t, s, l, 3));
        Assert.Equal("### Hello", text);
        Assert.Equal(6, start); // still between "He" and "llo"
    }

    [Fact]
    public void Heading_Only_Touches_The_Caret_Line_With_Bare_CR_Breaks()
    {
        // A WinUI TextBox stores line breaks as a bare '\r'.
        var (text, _, _) = Run("one\rtwo\rthree", 5, 0, (t, s, l) => LineFormatting.Heading(t, s, l, 2));
        Assert.Equal("one\r## two\rthree", text);
    }

    [Fact]
    public void Heading_On_An_Empty_Line_Leaves_The_Caret_Ready_To_Type()
    {
        var (text, start, _) = Run("a\n\nb", 2, 0, (t, s, l) => LineFormatting.Heading(t, s, l, 2));
        Assert.Equal("a\n## \nb", text);
        Assert.Equal(5, start);
    }

    [Fact]
    public void Bullets_Apply_To_Every_Selected_Line_And_Skip_Blank_Ones()
    {
        var src = "one\r\rtwo\rthree";
        var (text, start, length) = Run(src, 0, src.Length, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Bullet));
        Assert.Equal("- one\r\r- two\r- three", text);
        Assert.Equal(0, start);
        Assert.Equal(text.Length, length);
    }

    [Fact]
    public void Pressing_Bullets_Again_Removes_Them()
    {
        var src = "- one\n- two";
        Assert.Equal("one\ntwo", Run(src, 0, src.Length, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Bullet)).Text);
    }

    [Fact]
    public void Numbered_Replaces_Bullets_And_Counts_Up()
    {
        var src = "- one\n- two\n  - nested";
        Assert.Equal("1. one\n2. two\n  3. nested",
            Run(src, 0, src.Length, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Numbered)).Text);
    }

    [Fact]
    public void Task_Replaces_A_Bullet_And_A_Bullet_Is_Not_Mistaken_For_A_Task()
    {
        Assert.Equal("- [ ] milk", Run("- milk", 6, 0, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Task)).Text);
        // Pressing Task on a task line removes it, like pressing Bullet on a bullet.
        Assert.Equal("milk", Run("- [ ] milk", 3, 0, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Task)).Text);
        Assert.Equal("- milk", Run("- [x] milk", 3, 0, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Bullet)).Text);
    }

    [Fact]
    public void Quote_Toggles_And_Keeps_The_Caret_On_Its_Character()
    {
        var (text, start, _) = Run("wise words", 4, 0, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Quote));
        Assert.Equal("> wise words", text);
        Assert.Equal(6, start);
        Assert.Equal("wise words", Run(text, start, 0, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Quote)).Text);
    }

    [Fact]
    public void A_Selection_Ending_At_The_Start_Of_A_Line_Does_Not_Pull_That_Line_In()
    {
        var src = "one\ntwo\nthree";
        // "one\n" selected by dragging down to the start of "two".
        Assert.Equal("> one\ntwo\nthree", Run(src, 0, 4, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Quote)).Text);
        Assert.Equal("> one\r\ntwo", Run("one\r\ntwo", 0, 5, (t, s, l) => LineFormatting.Toggle(t, s, l, LineMarker.Quote)).Text);
    }

    [Fact]
    public void Edits_Replace_Only_The_Touched_Lines()
    {
        var e = LineFormatting.Heading("intro\ntitle\noutro", 8, 0, 1);
        Assert.Equal(6, e.Start);
        Assert.Equal(5, e.Length);
        Assert.Equal("# title", e.Replacement);
    }
}
