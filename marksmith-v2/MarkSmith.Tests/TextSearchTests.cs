using System;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class TextSearchTests
{
    private const StringComparison Any = StringComparison.OrdinalIgnoreCase;

    [Fact]
    public void Finds_every_match_without_overlaps()
        => Assert.Equal(new[] { 0, 2 }, TextSearch.FindAll("aaaa", "aa", Any));

    [Fact]
    public void Match_case_is_respected()
    {
        Assert.Equal(new[] { 0, 8 }, TextSearch.FindAll("The cat the", "the", Any));
        Assert.Equal(new[] { 8 }, TextSearch.FindAll("The cat the", "the", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_query_or_text_finds_nothing()
    {
        Assert.Empty(TextSearch.FindAll("abc", "", Any));
        Assert.Empty(TextSearch.FindAll("", "a", Any));
    }

    [Fact]
    public void Next_without_a_current_match_starts_at_the_caret()
    {
        var m = new[] { 2, 10, 20 };
        Assert.Equal((1, false), TextSearch.Next(m, -1, 5));
        Assert.Equal((0, true), TextSearch.Next(m, -1, 25));
    }

    [Fact]
    public void Next_and_previous_report_wrapping()
    {
        var m = new[] { 2, 10, 20 };
        Assert.Equal((2, false), TextSearch.Next(m, 1, 0));
        Assert.Equal((0, true), TextSearch.Next(m, 2, 0));
        Assert.Equal((0, false), TextSearch.Previous(m, 1, 0));
        Assert.Equal((2, true), TextSearch.Previous(m, 0, 0));
        Assert.Equal((0, false), TextSearch.Previous(m, -1, 5));
        Assert.Equal((2, true), TextSearch.Previous(m, -1, 1));
    }

    [Fact]
    public void A_single_match_never_announces_a_wrap()
    {
        Assert.Equal((0, false), TextSearch.Next(new[] { 4 }, 0, 0));
        Assert.Equal((0, false), TextSearch.Previous(new[] { 4 }, 0, 0));
    }

    [Fact]
    public void Replace_all_counts_and_rewrites()
    {
        var (text, count, _) = TextSearch.ReplaceAll("The cat and the hat", "the", "a", Any, 0);
        Assert.Equal("a cat and a hat", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Caret_after_matches_shifts_by_the_growth()
    {
        // caret before "hat" (offset 16); two 3-char matches become 5-char "those" -> +4
        var (text, _, caret) = TextSearch.ReplaceAll("The cat and the hat", "the", "those", Any, 16);
        Assert.Equal("hat", text.Substring(caret, 3));
    }

    [Fact]
    public void Caret_before_every_match_stays_put()
    {
        var (_, _, caret) = TextSearch.ReplaceAll("ab the", "the", "x", Any, 1);
        Assert.Equal(1, caret);
    }

    [Fact]
    public void Caret_inside_a_match_lands_at_its_replacement()
    {
        var (text, _, caret) = TextSearch.ReplaceAll("one TWO three", "two", "2", Any, 5);
        Assert.Equal("one 2 three", text);
        Assert.Equal(4, caret);
    }

    [Fact]
    public void Caret_at_the_end_stays_at_the_end()
    {
        var (text, _, caret) = TextSearch.ReplaceAll("a b a", "a", "xyz", Any, 5);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void No_match_leaves_text_and_caret_alone()
    {
        var (text, count, caret) = TextSearch.ReplaceAll("hello", "z", "y", Any, 3);
        Assert.Equal(("hello", 0, 3), (text, count, caret));
    }

    [Fact]
    public void Replacing_with_text_that_contains_the_query_does_not_loop()
    {
        var (text, count, _) = TextSearch.ReplaceAll("a a", "a", "aa", Any, 0);
        Assert.Equal("aa aa", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Empty_match_list_returns_minus_one_without_wrap()
    {
        var empty = Array.Empty<int>();
        Assert.Equal((-1, false), TextSearch.Next(empty, -1, 0));
        Assert.Equal((-1, false), TextSearch.Previous(empty, -1, 0));
    }

    [Fact]
    public void Replace_all_with_empty_replacement_deletes_matches_and_shifts_caret()
    {
        var (text, count, caret) = TextSearch.ReplaceAll("foo bar foo baz", "foo ", "", Any, 12);
        Assert.Equal("bar baz", text);
        Assert.Equal(2, count);
        Assert.Equal(4, caret);
    }

    [Fact]
    public void Replace_all_clamps_out_of_range_caret_and_ignores_empty_query()
    {
        Assert.Equal(("hello", 0, 0), TextSearch.ReplaceAll("hello", "", "x", Any, -5));
        Assert.Equal(("hello", 0, 5), TextSearch.ReplaceAll("hello", "", "x", Any, 99));
    }

    [Fact]
    public void Next_after_single_replace_in_overlapping_run_resolves_valid_match_index()
    {
        // Replacing the first "aa" in "aaaa" with "a" leaves "aaa" with caret at 1;
        // FindAll("aaa", "aa") is [0], and Next([0], -1, 1) must wrap to index 0 rather than -1.
        var matches = TextSearch.FindAll("aaa", "aa", Any);
        Assert.Equal(new[] { 0 }, matches);
        Assert.Equal((0, true), TextSearch.Next(matches, -1, 1));
    }

    [Fact]
    public void InputFilePath_with_invalid_path_characters_or_quotes_does_not_throw()
    {
        var vm = new MarkSmith.ViewModels.MainViewModel();
        var ex = Record.Exception(() =>
        {
            vm.InputFilePath = "\"C:\\nonexistent\\quoted.md\"";
            vm.InputFilePath = "C:\\bad:path|<>\"*?.md";
        });
        Assert.Null(ex);
        Assert.False(vm.HasInputFile);
        Assert.False(vm.IsCurrentFilePinned);
    }

    [Fact]
    public void ReplaceAll_undo_and_redo_round_trips_text_and_clamped_caret()
    {
        var vm = new MarkSmith.ViewModels.MainViewModel();
        vm.CurrentMarkdown = "alpha one alpha two";
        vm.EditorCaret = 6;
        vm.BreakUndoBurst();

        var (newText, count, caret) = TextSearch.ReplaceAll(vm.CurrentMarkdown, "alpha", "BETA", Any, vm.EditorCaret);
        Assert.Equal(2, count);
        var clampedCaret = Math.Clamp(caret, 0, newText.Length);

        vm.BreakUndoBurst();
        vm.EditorCaret = clampedCaret;
        vm.CurrentMarkdown = newText;
        vm.BreakUndoBurst();

        var undoSnap = vm.UndoStep();
        Assert.NotNull(undoSnap);
        Assert.Equal("alpha one alpha two", undoSnap!.Text);
        vm.EditorCaret = undoSnap.Caret;
        vm.CurrentMarkdown = undoSnap.Text;

        var redoSnap = vm.RedoStep();
        Assert.NotNull(redoSnap);
        Assert.Equal("BETA one BETA two", redoSnap!.Text);
        Assert.Equal(clampedCaret, redoSnap.Caret);
    }

    [Fact]
    public void FindAll_with_matchWholeWord_matches_only_complete_tokens()
    {
        var text = "The cat in the catch concatenation cat.";
        var normal = TextSearch.FindAll(text, "cat", Any, matchWholeWord: false);
        Assert.Equal(new[] { 4, 15, 24, 35 }, normal);

        var whole = TextSearch.FindAll(text, "cat", Any, matchWholeWord: true);
        Assert.Equal(new[] { 4, 35 }, whole);
    }

    [Fact]
    public void ReplaceAll_with_matchWholeWord_replaces_only_whole_words()
    {
        var text = "The cat in the catch concatenation cat.";
        var (result, count, _) = TextSearch.ReplaceAll(text, "cat", "dog", Any, 0, matchWholeWord: true);
        Assert.Equal("The dog in the catch concatenation dog.", result);
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("hello world", 0, 5, true)]
    [InlineData("hello world", 6, 5, true)]
    [InlineData("hello world", 1, 3, false)]
    [InlineData("foo_bar", 0, 3, false)] // underscore is treated as word character
    [InlineData("foo-bar", 0, 3, true)]  // hyphen is a delimiter
    public void IsWholeWordBoundary_identifies_delimiters_correctly(string text, int index, int length, bool expected)
    {
        Assert.Equal(expected, TextSearch.IsWholeWordBoundary(text, index, length));
    }

    [Fact]
    public void TryValidateRegex_IdentifiesValidAndInvalidPatterns()
    {
        Assert.True(TextSearch.TryValidateRegex(@"\b\w+\d+", out var err1));
        Assert.Null(err1);

        Assert.False(TextSearch.TryValidateRegex(@"[unclosed_class", out var err2));
        Assert.NotNull(err2);

        Assert.True(TextSearch.TryValidateRegex("", out var err3));
        Assert.Null(err3);
    }

    [Fact]
    public void FindMatches_WithRegex_FindsTokensWithAccurateLengths()
    {
        var text = "Items: doc1, doc23, doc456, doc.";
        var matches = TextSearch.FindMatches(text, @"doc\d+", Any, useRegex: true);
        Assert.Equal(3, matches.Count);
        Assert.Equal(new TextSearch.TextMatch(7, 4), matches[0]);   // "doc1"
        Assert.Equal(new TextSearch.TextMatch(13, 5), matches[1]);  // "doc23"
        Assert.Equal(new TextSearch.TextMatch(20, 6), matches[2]);  // "doc456"
    }

    [Fact]
    public void ReplaceAll_WithRegex_PerformsSubstitutions()
    {
        var text = "user_123 and user_456";
        var (result, count, _) = TextSearch.ReplaceAll(text, @"user_\d+", "ID", Any, 0, useRegex: true);
        Assert.Equal("ID and ID", result);
        Assert.Equal(2, count);
    }

    [Fact]
    public void AddToSearchHistory_maintains_mru_order_and_bounds_capacity()
    {
        var history = new List<string> { "second", "third" };
        var updated = TextSearch.AddToSearchHistory(history, "first", maxCapacity: 3);
        Assert.Equal(new[] { "first", "second", "third" }, updated);

        // Deduplication moves existing item to the front without duplicates
        var deduped = TextSearch.AddToSearchHistory(updated, "second", maxCapacity: 3);
        Assert.Equal(new[] { "second", "first", "third" }, deduped);

        // Exceeding capacity drops oldest item
        var capped = TextSearch.AddToSearchHistory(deduped, "fourth", maxCapacity: 3);
        Assert.Equal(new[] { "fourth", "second", "first" }, capped);

        // Empty query keeps history unchanged
        var empty = TextSearch.AddToSearchHistory(capped, "", maxCapacity: 3);
        Assert.Equal(capped, empty);
    }

    [Theory]
    [InlineData(0, 0, false, "No results")]
    [InlineData(1, 0, false, "No results")]
    [InlineData(0, 5, false, "5 matches")]
    [InlineData(0, 1, false, "1 match")]
    [InlineData(1, 1, false, "1 of 1")]
    [InlineData(3, 10, false, "3 of 10")]
    [InlineData(1, 5, true, "1 of 5 (wrapped)")]
    [InlineData(5, 5, true, "5 of 5 (wrapped)")]
    public void FormatFindMatchStatus_FormatsAccurateStatus(int current, int total, bool wrapped, string expected)
    {
        Assert.Equal(expected, TextSearch.FormatFindMatchStatus(current, total, wrapped));
    }

    [Theory]
    [InlineData(-5, 10, 0)]
    [InlineData(0, 10, 0)]
    [InlineData(4, 10, 4)]
    [InlineData(9, 10, 9)]
    [InlineData(15, 10, 9)]
    [InlineData(2, 0, -1)]
    [InlineData(0, -1, -1)]
    public void Run134_ClampMatchIndex_ClampsBoundsAccurately(int target, int total, int expected)
    {
        Assert.Equal(expected, TextSearch.ClampMatchIndex(target, total));
    }

    [Fact]
    public void Run134_JumpTo_ReturnsCorrectIndicesForTargets()
    {
        var (firstIdx, firstValid) = TextSearch.JumpTo(10, TextSearch.MatchJumpTarget.First);
        Assert.True(firstValid);
        Assert.Equal(0, firstIdx);

        var (lastIdx, lastValid) = TextSearch.JumpTo(10, TextSearch.MatchJumpTarget.Last);
        Assert.True(lastValid);
        Assert.Equal(9, lastIdx);

        var (emptyIdx, emptyValid) = TextSearch.JumpTo(0, TextSearch.MatchJumpTarget.First);
        Assert.False(emptyValid);
        Assert.Equal(-1, emptyIdx);
    }

    [Fact]
    public void Run135_GetMatchLocation_ReturnsCorrectLineColumnLength()
    {
        var text = "Line 1\nLine 2 is longer\nLine 3 target match\nLine 4";
        // Target "target" starts at offset 24, length 6
        var targetOffset = text.IndexOf("target", StringComparison.Ordinal);
        var (line, col, len) = TextSearch.GetMatchLocation(text, targetOffset, 6);
        Assert.Equal(3, line);
        Assert.Equal(8, col); // "Line 3 " is 7 chars, so 1-based col is 8
        Assert.Equal(6, len);
    }

    [Fact]
    public void Run135_FormatMatchInspectorSummary_FormatsCorrectly()
    {
        var text = "First line\nSecond line hello world\nThird line";
        var helloOffset = text.IndexOf("hello", StringComparison.Ordinal);
        var summary = TextSearch.FormatMatchInspectorSummary(text, helloOffset, 5, 2, 7);
        Assert.Equal("Match 2 of 7 · Line 2, Col 13 (Len: 5)", summary);

        var emptySummary = TextSearch.FormatMatchInspectorSummary("", -1, 0, 0, 0);
        Assert.Equal("No match selected", emptySummary);
    }

    [Fact]
    public void Run136_FormatMatchContextSnippet_ExtractsCleanSnippet()
    {
        var text = "The quick brown fox jumps over the lazy dog in the meadow.";
        var jumpsOffset = text.IndexOf("jumps", StringComparison.Ordinal);
        var snippet = TextSearch.FormatMatchContextSnippet(text, jumpsOffset, 5, 10);
        Assert.Contains("[jumps]", snippet);
        Assert.StartsWith("...", snippet);
        Assert.EndsWith("...", snippet);

        // Near beginning of text (no leading ellipsis)
        var theOffset = 0;
        var startSnippet = TextSearch.FormatMatchContextSnippet(text, theOffset, 3, 10);
        Assert.StartsWith("[The]", startSnippet);
        Assert.False(startSnippet.StartsWith("..."));

        // Invalid / empty
        var emptySnippet = TextSearch.FormatMatchContextSnippet("", -1, 0, 10);
        Assert.Equal("No match context available", emptySnippet);

        // Multi-line replacement
        var multiLine = "Line one\r\nLine two [TARGET]\r\nLine three";
        var targetOffset = multiLine.IndexOf("[TARGET]", StringComparison.Ordinal);
        var multiSnippet = TextSearch.FormatMatchContextSnippet(multiLine, targetOffset, 8, 12);
        Assert.DoesNotContain("\r", multiSnippet);
        Assert.DoesNotContain("\n", multiSnippet);
        Assert.Contains("[[TARGET]]", multiSnippet);
    }

    [Fact]
    public void Run137_UnwrapMatchText_NormalizesWhitespaceAndNewlines()
    {
        Assert.Equal("", TextSearch.UnwrapMatchText(""));
        Assert.Equal("", TextSearch.UnwrapMatchText("   \r\n\t  "));
        Assert.Equal("hello world", TextSearch.UnwrapMatchText("hello   world"));
        Assert.Equal("first line second line", TextSearch.UnwrapMatchText("first line\r\n   second line"));
        Assert.Equal("a b c", TextSearch.UnwrapMatchText("\t a \n b \r\n c \t"));
    }

    [Fact]
    public void Run137_FormatMatchLineContext_FormatsLineAndColumnContext()
    {
        var doc = "First header\nvar x = CalculateValue();\nThird line";
        var calcOffset = doc.IndexOf("CalculateValue", StringComparison.Ordinal);
        var ctx = TextSearch.FormatMatchLineContext(doc, calcOffset, "CalculateValue".Length);

        Assert.Equal("L2 (Col 9): var x = CalculateValue();", ctx);

        // Edge case: invalid bounds
        Assert.Equal("No line context available", TextSearch.FormatMatchLineContext("", -1, 0));
        Assert.Equal("No line context available", TextSearch.FormatMatchLineContext(doc, 9999, 5));
    }

    [Fact]
    public void Run138_FormatSearchQueryDiagnostics_FormatsOptionsAndValidation()
    {
        var diag1 = TextSearch.FormatSearchQueryDiagnostics("markdown", 5, isCaseSensitive: true, isWholeWord: true, isRegex: false);
        Assert.Equal("Query: \"markdown\" · 5 matches · Options: [Match Case, Whole Word] · Mode: Plain Text", diag1);

        var diag2 = TextSearch.FormatSearchQueryDiagnostics(@"\d{3}", 1, isCaseSensitive: false, isWholeWord: false, isRegex: true);
        Assert.Equal("Query: \"\\d{3}\" · 1 match · Options: [Regex] · Mode: Regular Expression (Valid)", diag2);

        var diag3 = TextSearch.FormatSearchQueryDiagnostics(@"(unclosed", 0, isCaseSensitive: false, isWholeWord: false, isRegex: true);
        Assert.Contains("Mode: Regular Expression (Invalid:", diag3);

        var emptyDiag = TextSearch.FormatSearchQueryDiagnostics("", 0, false, false, false);
        Assert.Equal("Empty query (0 matches)", emptyDiag);
    }
}
