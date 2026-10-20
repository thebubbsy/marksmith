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

    [Fact]
    public void Run139_TrimSearchHistory_And_FormatSearchHistorySummary()
    {
        var history = new[] { "query1", "query2", "  ", "query1", "query3", null, "query4" };
        var trimmed = TextSearch.TrimSearchHistory(history, maxCapacity: 2);
        Assert.Equal(2, trimmed.Count);
        Assert.Equal("query1", trimmed[0]);
        Assert.Equal("query2", trimmed[1]);

        var summaryEmpty = TextSearch.FormatSearchHistorySummary(null);
        Assert.Equal("Search history: Empty", summaryEmpty);

        var summarySingle = TextSearch.FormatSearchHistorySummary(new[] { "onlyOne" });
        Assert.Equal("Search history (1 query): [\"onlyOne\"]", summarySingle);

        var summaryMultiple = TextSearch.FormatSearchHistorySummary(new[] { "alpha", "beta", "gamma", "delta" });
        Assert.Equal("Search history (4 queries): [\"alpha\", \"beta\", \"gamma\", …]", summaryMultiple);
    }

    [Fact]
    public void Run140_FormatSearchMatchRate_FormatsDensityAndFrequency()
    {
        var zero = TextSearch.FormatSearchMatchRate(0, 1000, 5000);
        Assert.Equal("0 matches across 1,000 words (0.00% density)", zero);

        var single = TextSearch.FormatSearchMatchRate(1, 500, 2500);
        Assert.Equal("1 match across 500 words (0.20% density · 1 every 500 words)", single);

        var multiple = TextSearch.FormatSearchMatchRate(10, 1000, 6000);
        Assert.Equal("10 matches across 1,000 words (1.00% density · 1 every 100 words)", multiple);

        var noWords = TextSearch.FormatSearchMatchRate(3, 0, 0);
        Assert.Equal("3 matches (Word count unavailable)", noWords);
    }

    [Fact]
    public void Run141_ClusterSearchMatches_And_FormatSearchMatchHotspots()
    {
        var emptyClusters = TextSearch.ClusterSearchMatches(null);
        Assert.Empty(emptyClusters);

        var emptySummary = TextSearch.FormatSearchMatchHotspots(null);
        Assert.Equal("0 matches (No clusters)", emptySummary);

        // Dispersed matches
        var dispersedOffsets = new[] { 100, 500, 1000 };
        var dispersedClusters = TextSearch.ClusterSearchMatches(dispersedOffsets, clusterDistance: 200);
        Assert.Equal(3, dispersedClusters.Count);
        Assert.All(dispersedClusters, c => Assert.Equal(1, c.MatchCount));

        var dispersedSummary = TextSearch.FormatSearchMatchHotspots(dispersedOffsets, clusterDistance: 200);
        Assert.Equal("3 matches across 3 isolated locations (Evenly dispersed · 0 hotspots)", dispersedSummary);

        // Clustered matches (hotspot)
        var clusteredOffsets = new[] { 50, 70, 90, 600, 650 };
        var clusters = TextSearch.ClusterSearchMatches(clusteredOffsets, clusterDistance: 100);
        Assert.Equal(2, clusters.Count);
        Assert.Equal(3, clusters[0].MatchCount);
        Assert.Equal(50, clusters[0].StartOffset);
        Assert.Equal(90, clusters[0].EndOffset);
        Assert.Equal(2, clusters[1].MatchCount);
        Assert.Equal(600, clusters[1].StartOffset);
        Assert.Equal(650, clusters[1].EndOffset);

        var clusteredSummary = TextSearch.FormatSearchMatchHotspots(clusteredOffsets, clusterDistance: 100, totalDocLength: 2000);
        Assert.Contains("5 matches in 2 clusters (2 hotspots: 3 matches @ char 50–90, 2 matches @ char 600–650)", clusteredSummary);
        Assert.Contains("(Doc len: 2,000 chars)", clusteredSummary);
    }

    [Fact]
    public void Run142_ExtractRegexCaptureGroups_And_FormatRegexCaptureGroupsSummary()
    {
        // Empty text and query
        Assert.Empty(TextSearch.ExtractRegexCaptureGroups("", @"\w+"));
        Assert.Equal("Regex capture groups: (No query specified)", TextSearch.FormatRegexCaptureGroupsSummary("text", ""));
        Assert.Equal("Regex capture groups for '\\w+': (Document is empty)", TextSearch.FormatRegexCaptureGroupsSummary("", @"\w+"));

        // Non-grouping regex
        var text1 = "Item doc1 and doc2 and doc3";
        var caps1 = TextSearch.ExtractRegexCaptureGroups(text1, @"doc\d+");
        Assert.Equal(3, caps1.Count);
        Assert.Empty(caps1[0].Groups);
        var sum1 = TextSearch.FormatRegexCaptureGroupsSummary(text1, @"doc\d+");
        Assert.Contains("0 capturing groups", sum1);
        Assert.Contains("\"doc1\" @ char 5", sum1);

        // Named and numbered capture groups
        var text2 = "user: 101; admin: 202; guest: 303";
        var pattern2 = @"(?<role>\w+):\s*(?<id>\d+)";
        var caps2 = TextSearch.ExtractRegexCaptureGroups(text2, pattern2, maxMatches: 2);
        Assert.Equal(2, caps2.Count);
        Assert.Equal("user: 101", caps2[0].Value);
        Assert.Equal(2, caps2[0].Groups.Count);
        Assert.Equal("role", caps2[0].Groups[0].GroupName);
        Assert.Equal("user", caps2[0].Groups[0].Value);
        Assert.Equal("id", caps2[0].Groups[1].GroupName);
        Assert.Equal("101", caps2[0].Groups[1].Value);

        var sum2 = TextSearch.FormatRegexCaptureGroupsSummary(text2, pattern2, maxMatches: 2);
        Assert.Contains("2 matches, 4 captured tokens", sum2);
        Assert.Contains("[role]=\"user\"", sum2);
        Assert.Contains("[id]=\"101\"", sum2);
    }

    [Fact]
    public void Run143_ReplacePreservingCase_And_FormatCasePreservingReplaceSummary()
    {
        Assert.Equal("DOCFORGE", TextSearch.ApplyCasePattern("MARKSMITH", "docforge"));
        Assert.Equal("docforge", TextSearch.ApplyCasePattern("marksmith", "DocForge"));
        Assert.Equal("Docforge", TextSearch.ApplyCasePattern("Marksmith", "docforge"));
        Assert.Equal("DocForge", TextSearch.ApplyCasePattern("Marksmith", "docForge"));

        var input = "Welcome to MARKSMITH! Marksmith is fast, and marksmith is native.";
        var (replaced, count, upper, title, lower) = TextSearch.ReplacePreservingCase(input, "marksmith", "docforge");

        Assert.Equal("Welcome to DOCFORGE! Docforge is fast, and docforge is native.", replaced);
        Assert.Equal(3, count);
        Assert.Equal(1, upper);
        Assert.Equal(1, title);
        Assert.Equal(1, lower);

        var summary = TextSearch.FormatCasePreservingReplaceSummary("marksmith", "docforge", count, upper, title, lower);
        Assert.Equal("Case-preserving replace ('marksmith' → 'docforge'): 3 matches replaced (1 UPPER, 1 Title, 1 lower)", summary);

        var zeroSummary = TextSearch.FormatCasePreservingReplaceSummary("missing", "target", 0, 0, 0, 0);
        Assert.Equal("Case-preserving replace ('missing' → 'target'): 0 matches replaced", zeroSummary);
    }

    [Fact]
    public void Run144_FixSafeMarkdownLintIssues_And_FormatSafeLintFixSummary()
    {
        Assert.Equal("Markdown lint auto-fix: 0 safe issues found (Document already clean)",
            TextSearch.FormatSafeLintFixSummary(0, 0, 0, 0));

        var rawMd = "#HeadingOne   \nLine with\thard tab\nIntentional break  \n\n\n\n```csharp\n#NotAHeading\t\n```\n##SubHeading";
        var (fixedMd, trailingFixed, tabsFixed, headingsFixed, blankClustersFixed, totalFixes) =
            TextSearch.FixSafeMarkdownLintIssues(rawMd);

        Assert.Equal(1, trailingFixed);      // "#HeadingOne   " trimmed, while "Intentional break  " preserved
        Assert.Equal(1, tabsFixed);          // "Line with\thard tab" expanded, fenced tab untouched
        Assert.Equal(2, headingsFixed);      // "#HeadingOne" and "##SubHeading" fixed, fenced "#NotAHeading" untouched
        Assert.Equal(1, blankClustersFixed); // 3 blank lines collapsed to 2
        Assert.Equal(5, totalFixes);

        Assert.Contains("# HeadingOne\n", fixedMd);
        Assert.Contains("Line with    hard tab\n", fixedMd);
        Assert.Contains("Intentional break  \n", fixedMd);
        Assert.Contains("#NotAHeading\t\n", fixedMd);
        Assert.EndsWith("## SubHeading", fixedMd);

        // Verify MarkdownLintService finds 0 safe lint issues on the fixed output
        var remainingIssues = MarkdownLintService.Analyze(fixedMd);
        Assert.Empty(remainingIssues);

        var summary = TextSearch.FormatSafeLintFixSummary(trailingFixed, tabsFixed, headingsFixed, blankClustersFixed);
        Assert.Equal("Markdown lint auto-fix: 5 safe issues fixed (1 trailing space, 1 hard tab, 2 '#' heading space, 1 blank-line cluster)", summary);
    }

    [Fact]
    public void Run145_AuditCommonProseCleanupOpportunities_And_FormatProseCleanupAuditSummary()
    {
        Assert.Equal("Prose cleanup audit: 0 issues detected (Clean typography & prose)",
            TextSearch.FormatProseCleanupAuditSummary("Clean prose sentence with standard punctuation."));

        var sample = """
            Here is  a double space and “smart quotes” plus an em—dash…
            It is important to note that the the repeated word is outside fences.
            ```markdown
            Inside  fenced  code “quotes” — delve into the the ignored block.
            ```
            """;

        var (doubleSpaces, smartQuotes, typographyDashes, aiFiller, repeatedWords, total) =
            TextSearch.AuditCommonProseCleanupOpportunities(sample);

        Assert.Equal(1, doubleSpaces);
        Assert.Equal(2, smartQuotes);
        Assert.Equal(2, typographyDashes); // '—' and '…'
        Assert.Equal(1, aiFiller);         // "It is important to note"
        Assert.Equal(1, repeatedWords);    // "the the"
        Assert.Equal(7, total);

        var formatted = TextSearch.FormatProseCleanupAuditSummary(sample);
        Assert.Equal("Prose cleanup audit (7 opportunities): 1 double-space, 2 smart-quote, 2 dash/ellipsis, 1 filler phrase, 1 repeated word", formatted);
    }

    [Fact]
    public void Run146_FormatFocusWindowReadingProfile_And_ResetDials()
    {
        Assert.Equal("Focus reading profile (Scope 45% · Glass: Off · Surround: Off): 0 words in document (Empty document)",
            TextSearch.FormatFocusWindowReadingProfile(""));

        var sample = """
            # Executive Summary
            MarkSmith provides high-fidelity native document compilation with zero external runtime dependencies.
            The Looking Glass focus aperture isolates text for rapid distraction-free reading and density assessment.
            Users can dynamically adjust scope radius, blur profiles, and reading speed metrics in real time.
            """;

        var (totalWords, totalLines, apertureWords, apertureReadSec, totalReadMin, state) =
            TextSearch.AuditFocusWindowReadingProfile(sample, portalRadiusPx: 45, glassBlur: false, surroundBlur: false);

        Assert.True(totalWords >= 35);
        Assert.Equal(4, totalLines);
        Assert.Equal(19, apertureWords); // Math.Round(45 * 0.42) = 19
        Assert.True(apertureReadSec > 5.0);
        Assert.True(totalReadMin > 0.0);
        Assert.Equal("Scope 45% · Clear Preview (No Blur)", state);

        var formatted = TextSearch.FormatFocusWindowReadingProfile(sample, 45, false, false);
        Assert.StartsWith("Focus reading profile (Scope 45% · Clear Preview (No Blur)): ~19 words in aperture", formatted);
        Assert.Contains("words across 4 lines", formatted);

        // Test with blurs enabled
        var blurProfile = TextSearch.FormatFocusWindowReadingProfile(sample, 60, glassBlur: true, surroundBlur: true);
        Assert.Contains("Scope 60% · Glass & Surround Blur Active", blurProfile);
    }

    [Fact]
    public void Run147_ExtractMarkdownSectionBudgets_And_FormatSummary()
    {
        Assert.Equal("Markdown section budgets: 0 sections detected (Empty document)",
            TextSearch.FormatMarkdownSectionBudgetsSummary(""));

        var markdown = """
            Welcome to the documentation suite. This intro sets the foundation.

            # Overview
            Here is the core overview of the architecture and goals.

            ```python
            # This is a comment in python, NOT a markdown heading
            def test():
                pass
            ```

            ## Architecture
            Deep dive into the pipeline stages and data transformations.
            """;

        var budgets = TextSearch.ExtractMarkdownSectionBudgets(markdown);
        Assert.Equal(3, budgets.Count);

        // Section 1: Intro
        Assert.Equal(0, budgets[0].Level);
        Assert.Equal("Intro / Overview", budgets[0].Title);
        Assert.Equal(1, budgets[0].LineNumber);
        Assert.True(budgets[0].WordCount >= 8);

        // Section 2: Overview
        Assert.Equal(1, budgets[1].Level);
        Assert.Equal("Overview", budgets[1].Title);
        Assert.True(budgets[1].WordCount >= 15);

        // Section 3: Architecture
        Assert.Equal(2, budgets[2].Level);
        Assert.Equal("Architecture", budgets[2].Title);
        Assert.True(budgets[2].WordCount >= 8);

        var formatted = TextSearch.FormatMarkdownSectionBudgetsSummary(markdown);
        Assert.Contains("Markdown section budgets (3 sections", formatted);
        Assert.Contains("§1. [Intro] Intro / Overview", formatted);
        Assert.Contains("§2. [H1] Overview", formatted);
        Assert.Contains("§3. [H2] Architecture", formatted);
    }

    [Fact]
    public void Run148_AuditFootnoteAndReferenceIntegrity()
    {
        Assert.Equal("Footnote & reference link integrity: 0 references or definitions detected (Clean document)",
            TextSearch.FormatFootnoteAndReferenceIntegritySummary(""));

        var cleanDoc = """
            # Architecture Review
            This document explains the compiler design[^1] and standard reference links [DocHub][hub].
            Check out [MarkSmith Website][site] for details.

            ```markdown
            [^code_footnote]: Should be ignored inside fences
            [code_link]: https://ignored.com
            ```

            [^1]: First footnote detailing the compiler pipeline.
            [hub]: https://hub.example.com
            [site]: https://marksmith.example.com "Official Site"
            """;

        var cleanAudit = TextSearch.AuditFootnoteAndReferenceIntegrity(cleanDoc);
        Assert.True(cleanAudit.IsClean);
        Assert.Equal(1, cleanAudit.TotalFootnoteRefs);
        Assert.Equal(1, cleanAudit.TotalFootnoteDefs);
        Assert.Equal(2, cleanAudit.TotalLinkRefs);
        Assert.Equal(2, cleanAudit.TotalLinkDefs);

        var cleanSummary = TextSearch.FormatFootnoteAndReferenceIntegritySummary(cleanDoc);
        Assert.Contains("All 1 footnote(s) and 2 reference link(s) are valid and balanced", cleanSummary);

        var brokenDoc = """
            # Faulty Document
            Here is a reference to a missing footnote[^missing_fn].
            Here is a valid footnote[^valid_fn] and a reference link [Feature Link][missing_link].

            [^valid_fn]: Valid definition text.
            [^unused_fn]: This definition is never referenced.
            [^dup_fn]: First definition.
            [^dup_fn]: Second duplicate definition!

            [valid_link]: https://valid.com
            [unused_link]: https://unused.com
            [dup_link]: https://first.com
            [dup_link]: https://second.com

            Reference to [valid_link][valid_link].
            """;

        var brokenAudit = TextSearch.AuditFootnoteAndReferenceIntegrity(brokenDoc);
        Assert.False(brokenAudit.IsClean);

        Assert.Contains("missing_fn", brokenAudit.MissingFootnoteDefs);
        Assert.Contains("unused_fn", brokenAudit.UnusedFootnoteDefs);
        Assert.Contains("dup_fn", brokenAudit.DuplicateFootnoteDefs);

        Assert.Contains("missing_link", brokenAudit.MissingLinkDefs);
        Assert.Contains("unused_link", brokenAudit.UnusedLinkDefs);
        Assert.Contains("dup_link", brokenAudit.DuplicateLinkDefs);

        var brokenSummary = TextSearch.FormatFootnoteAndReferenceIntegritySummary(brokenDoc);
        Assert.Contains("Footnote & reference link integrity", brokenSummary);
        Assert.Contains("Missing footnote definitions: [^missing_fn]", brokenSummary);
        Assert.Contains("Unused footnote definitions:", brokenSummary);
        Assert.Contains("[^unused_fn]", brokenSummary);
        Assert.Contains("Duplicate footnote definitions: [^dup_fn]", brokenSummary);
        Assert.Contains("Missing link definitions: [missing_link]", brokenSummary);
        Assert.Contains("Unused link definitions:", brokenSummary);
        Assert.Contains("[unused_link]", brokenSummary);
        Assert.Contains("Duplicate link definitions: [dup_link]", brokenSummary);
    }

    [Fact]
    public void Run149_CalculateSessionVelocityForecast_ComputesVelocityAndTargetForecast()
    {
        // 1. Initialized session (0 words)
        var init = TextSearch.CalculateSessionVelocityForecast(0, 0, 0);
        Assert.Equal("Session velocity: 0 words · Session initialized", init.Summary);
        Assert.Equal(0, init.CurrentWords);

        // 2. High velocity sprint (500 words in 10 minutes = 50 WPM)
        var sprint = TextSearch.CalculateSessionVelocityForecast(currentWords: 1500, startWords: 1000, elapsedMinutes: 10.0, targetWords: 2000);
        Assert.Equal(500, sprint.NetWordDelta);
        Assert.Equal(50.0, sprint.WordsPerMinute);
        Assert.Equal(3000.0, sprint.WordsPerHour);
        Assert.Equal("High Velocity Sprint", sprint.PaceCategory);
        Assert.Equal(500, sprint.WordsRemaining);
        Assert.Equal(10.0, sprint.EstimatedMinutesRemaining);
        Assert.Contains("High Velocity Sprint · +500 words in 10m", sprint.Summary);
        Assert.Contains("50 WPM (3000 WPH)", sprint.Summary);
        Assert.Contains("500w left · ~10m remaining", sprint.Summary);

        // 3. Target reached
        var reached = TextSearch.CalculateSessionVelocityForecast(currentWords: 2200, startWords: 1000, elapsedMinutes: 20.0, targetWords: 2000);
        Assert.Equal(0, reached.WordsRemaining);
        Assert.Equal(0.0, reached.EstimatedMinutesRemaining);
        Assert.Contains("Target (2,000w) reached! (+200w over)", reached.Summary);

        // 4. Pruning / Condensing (negative delta)
        var pruning = TextSearch.CalculateSessionVelocityForecast(currentWords: 800, startWords: 1000, elapsedMinutes: 10.0, targetWords: 1500);
        Assert.Equal(-200, pruning.NetWordDelta);
        Assert.Equal(-20.0, pruning.WordsPerMinute);
        Assert.Equal("Pruning / Condensing", pruning.PaceCategory);
        Assert.Contains("Pace stalled", pruning.Summary);

        // 5. From markdown text overload
        var doc = "# Title\nThis is a sample markdown document with exactly ten words total.";
        var mdForecast = TextSearch.CalculateSessionVelocityForecast(doc, startWords: 0, elapsedMinutes: 1.0);
        Assert.Equal(13, mdForecast.CurrentWords);
        Assert.Equal(13.0, mdForecast.WordsPerMinute);
    }

    [Fact]
    public void Run150_CalculateReadabilityScorecard_ComputesScoresAndCategories()
    {
        // 1. Empty or whitespace
        var empty = TextSearch.CalculateReadabilityScorecard("");
        Assert.Equal(0, empty.WordCount);
        Assert.Contains("Document is empty", empty.Summary);

        // 2. Simple readable prose
        var simpleText = """
            # Welcome to MarkSmith
            MarkSmith is a fast and simple markdown editor for Windows desktop.
            It helps you write clean notes and export documents quickly.
            You can format tables and draw diagrams with ease.
            """;
        var simple = TextSearch.CalculateReadabilityScorecard(simpleText);
        Assert.True(simple.WordCount > 20);
        Assert.True(simple.SentenceCount >= 3);
        Assert.True(simple.FleschReadingEase >= 60.0); // Should be easily readable
        Assert.True(simple.FleschKincaidGrade < 10.0);
        Assert.Contains("Readability scorecard", simple.Summary);
        Assert.Contains("wps", simple.Summary);
        Assert.Contains("spw", simple.Summary);

        // 3. Complex academic prose with polysyllabic words
        var academicText = """
            # Epistemological Investigation
            The contemporaneous conceptualization of quantum electrodynamics necessitates sophisticated mathematical formulations.
            Interdisciplinary methodologies elucidate fundamental phenomenological characteristics of subatomic particles.
            Consequently, institutional infrastructure requires comprehensive multidimensional organizational governance.
            """;
        var academic = TextSearch.CalculateReadabilityScorecard(academicText);
        Assert.True(academic.PolysyllabicWords >= 6);
        Assert.True(academic.FleschReadingEase < 50.0); // Difficult or Academic
        Assert.True(academic.FleschKincaidGrade >= 12.0);
        Assert.Contains("polysyllabic", academic.Summary);

        // 4. Code fences and HTML stripping: verify code syntax does not pollute prose
        var mixedText = """
            # Implementation
            Here is a simple explanation of our core system.
            ```csharp
            public class InternalSystemBootstrapServiceFactoryConfigurationHandler
            {
                public void Initialize() { }
            }
            ```
            It operates reliably across all devices.
            """;
        var mixed = TextSearch.CalculateReadabilityScorecard(mixedText);
        // Code fence should be stripped, so InternalSystemBootstrap... isn't counted
        Assert.True(mixed.WordCount < 20);
        Assert.Equal(2, mixed.SentenceCount);
    }

    [Fact]
    public void Run151_AuditSentenceFlowCadence_AnalyzesParagraphsAndSentences()
    {
        // 1. Empty or whitespace
        var empty = TextSearch.AuditSentenceFlowCadence("");
        Assert.Equal(0, empty.ParagraphCount);
        Assert.Equal(0, empty.SentenceCount);
        Assert.Contains("Document is empty", empty.Summary);

        // 2. Balanced prose paragraphs
        var doc = """
            # Project Overview

            MarkSmith is a specialized document generation studio for Windows. It provides fast editing and rich export capabilities.

            The application leverages native OpenXML and direct PDF printing. Users can customize themes and shapes effortlessly.
            """;
        var audit = TextSearch.AuditSentenceFlowCadence(doc);
        Assert.Equal(3, audit.ParagraphCount);
        Assert.Equal(5, audit.SentenceCount);
        Assert.True(audit.WordCount > 25);
        Assert.True(audit.AvgWordsPerSentence > 5.0 && audit.AvgWordsPerSentence < 25.0);
        Assert.Equal(0, audit.RunOnCount);
        Assert.Contains("Sentence flow cadence", audit.Summary);
        Assert.Contains("wps", audit.Summary);

        // 3. Run-on sentence detection (>35 words)
        var runOnText = """
            This is an exceptionally long and sprawling sentence that deliberately goes on and on with multiple clauses and detailed descriptions without pausing for any terminal punctuation because it wants to test the sentence cadence analyzer which flags sentences exceeding thirty-five words as run-ons to help authors improve clarity.

            Short note.
            """;
        var runOnAudit = TextSearch.AuditSentenceFlowCadence(runOnText);
        Assert.True(runOnAudit.RunOnCount >= 1);
        Assert.True(runOnAudit.MicroSentenceCount >= 1);
        Assert.True(runOnAudit.MaxSentenceWords > 35);

        // 4. Code block stripping
        var codeDoc = """
            Here is a paragraph before code.

            ```python
            def long_function():
                print("this code should not be treated as a sentence or paragraph in cadence analysis")
            ```

            Here is a paragraph after code.
            """;
        var codeAudit = TextSearch.AuditSentenceFlowCadence(codeDoc);
        Assert.Equal(2, codeAudit.ParagraphCount);
        Assert.Equal(2, codeAudit.SentenceCount);
    }

    [Fact]
    public void Run152_AuditConflictMarkersAndAmbiguities()
    {
        // 1. Empty or whitespace
        var empty = TextSearch.AuditConflictMarkersAndAmbiguities("");
        Assert.True(empty.IsClean);
        Assert.Equal(0, empty.ConflictMarkerCount);
        Assert.Equal(0, empty.AmbiguityCount);
        Assert.Contains("Clean", empty.Summary);

        // 2. Clean document with code block containing conflict-like text
        var cleanDoc = """
            # Architecture Guide
            This is a clean document with valid inline `code` and balanced math $$E = mc^2$$.

            ```bash
            # Conflict markers inside code blocks should be ignored
            <<<<<<< HEAD
            echo "local"
            =======
            echo "remote"
            >>>>>>> branch
            ```

            Final closing paragraph.
            """;
        var cleanAudit = TextSearch.AuditConflictMarkersAndAmbiguities(cleanDoc);
        Assert.True(cleanAudit.IsClean);
        Assert.Equal(0, cleanAudit.ConflictMarkerCount);
        Assert.Equal(0, cleanAudit.AmbiguityCount);
        Assert.Contains("Clean", cleanAudit.Summary);

        // 3. Document with real Git conflict markers
        var conflictDoc = """
            # Introduction
            <<<<<<< HEAD
            Local feature branch changes
            =======
            Remote main branch changes
            >>>>>>> main
            Footer note.
            """;
        var conflictAudit = TextSearch.AuditConflictMarkersAndAmbiguities(conflictDoc);
        Assert.False(conflictAudit.IsClean);
        Assert.Equal(3, conflictAudit.ConflictMarkerCount);
        Assert.Contains("Line 2: <<<<<<< HEAD", conflictAudit.ConflictLines);
        Assert.Contains("Line 4: =======", conflictAudit.ConflictLines);
        Assert.Contains("Line 6: >>>>>>> main", conflictAudit.ConflictLines);
        Assert.Contains("3 conflict markers detected", conflictAudit.Summary);

        // 4. Document with syntax ambiguities (unclosed backtick and unmatched math)
        var ambiguityDoc = """
            # Notes
            Here is an unclosed `backtick that never finishes.
            And here is an unmatched math delimiter: $$ x + y = z
            """;
        var ambAudit = TextSearch.AuditConflictMarkersAndAmbiguities(ambiguityDoc);
        Assert.False(ambAudit.IsClean);
        Assert.Equal(2, ambAudit.AmbiguityCount);
        Assert.Contains("Unclosed inline code backtick", ambAudit.Ambiguities[0]);
        Assert.Contains("unmatched display math delimiter", ambAudit.Ambiguities[1]);
        Assert.Contains("2 syntax ambiguities detected", ambAudit.Summary);
    }

    [Fact]
    public void Run153_AuditReadabilityMetrics()
    {
        // 1. Empty / null document returns graceful defaults
        var emptyAudit = TextSearch.AuditReadabilityMetrics("");
        Assert.Equal(0, emptyAudit.WordCount);
        Assert.Equal(0.0, emptyAudit.FleschReadingEase);
        Assert.Contains("0 words", emptyAudit.Summary);

        // 2. Simple elementary prose
        var simpleText = """
            The cat sat on the mat. The dog ran in the sun. It was a good day.
            """;
        var simpleAudit = TextSearch.AuditReadabilityMetrics(simpleText);
        Assert.True(simpleAudit.WordCount >= 15);
        Assert.True(simpleAudit.FleschReadingEase >= 80.0);
        Assert.True(simpleAudit.ConsensusGrade <= 6.0);
        Assert.Contains("Elementary", simpleAudit.AudienceCategory);

        // 3. Technical markdown with code blocks, tables, and math stripped
        var technicalMarkdown = """
            # Architecture Specification
            ```csharp
            var complexSystem = new MicroserviceCluster(4096);
            ```
            | Port | Protocol | Encryption |
            | --- | --- | --- |
            | 443 | HTTPS | TLS 1.3 |
            $$ \int_{0}^{\infty} e^{-x^2} dx = \frac{\sqrt{\pi}}{2} $$

            The architectural infrastructure facilitates multi-region fault tolerance through asynchronous consensus protocols and distributed ledger synchronization.
            """;
        var techAudit = TextSearch.AuditReadabilityMetrics(technicalMarkdown);
        Assert.True(techAudit.WordCount > 10);
        Assert.True(techAudit.ComplexWordCount >= 3);
        Assert.True(techAudit.GunningFogIndex > 8.0);
        Assert.Contains("Readability audit:", techAudit.Summary);
    }

    [Fact]
    public void Run154_AuditHeadingOutlineBalance()
    {
        // 1. Empty / null document returns graceful defaults
        var emptyAudit = TextSearch.AuditHeadingOutlineBalance("");
        Assert.Equal(0, emptyAudit.TotalHeadings);
        Assert.Equal(0, emptyAudit.SkippedLevelCount);
        Assert.Equal("No Headings (Unstructured)", emptyAudit.BalanceCategory);
        Assert.Contains("0 headings", emptyAudit.Summary);

        // 2. Well-balanced multi-tier hierarchy without skipped levels
        var balancedDoc = """
            # System Architecture
            Overview of the kernel.
            ## Storage Subsystem
            Details about block storage.
            ### Cache Layer
            L1 and L2 cache specs.
            ### WAL Log
            Write-ahead logging protocol.
            ## Compute Pipeline
            Worker scheduler.
            """;
        var balancedAudit = TextSearch.AuditHeadingOutlineBalance(balancedDoc);
        Assert.Equal(5, balancedAudit.TotalHeadings);
        Assert.Equal(1, balancedAudit.MinDepth);
        Assert.Equal(3, balancedAudit.MaxDepth);
        Assert.Equal(0, balancedAudit.SkippedLevelCount);
        Assert.Equal(1, balancedAudit.LevelCounts[1]);
        Assert.Equal(2, balancedAudit.LevelCounts[2]);
        Assert.Equal(2, balancedAudit.LevelCounts[3]);
        Assert.Equal("Well-Balanced Outline", balancedAudit.BalanceCategory);
        Assert.Contains("5 headings across H1–H3", balancedAudit.Summary);
        Assert.Equal(balancedAudit.Summary, TextSearch.FormatHeadingOutlineBalanceSummary(balancedDoc));

        // 3. Document with skipped levels (H1 directly jumping to H3 and H5)
        var fragmentedDoc = """
            # Executive Summary
            ```markdown
            # This is inside a code block and should be ignored
            ```
            ### Deep Implementation Detail
            Skipped H2!
            ##### Ultra Deep Sub-Detail
            Skipped H4!
            """;
        var fragAudit = TextSearch.AuditHeadingOutlineBalance(fragmentedDoc);
        Assert.Equal(3, fragAudit.TotalHeadings);
        Assert.Equal(2, fragAudit.SkippedLevelCount);
        Assert.Equal("Fragmented Outline (Skipped Levels)", fragAudit.BalanceCategory);
        Assert.Contains("Skipped level (H1 -> H3)", fragAudit.SkippedWarnings[0]);
        Assert.Contains("Skipped level (H3 -> H5)", fragAudit.SkippedWarnings[1]);
        Assert.Contains("2 skipped levels warning", fragAudit.Summary);
    }

    [Fact]
    public void Run155_AuditCodeFenceLanguageCoverage()
    {
        // 1. Empty / whitespace document returns graceful defaults
        var emptyAudit = TextSearch.AuditCodeFenceLanguageCoverage("");
        Assert.Equal(0, emptyAudit.TotalFencedBlocks);
        Assert.Equal(0, emptyAudit.TaggedFencedBlocks);
        Assert.Equal(0, emptyAudit.UntaggedFencedBlocks);
        Assert.Equal(0, emptyAudit.InlineCodeSpans);
        Assert.Equal(100.0, emptyAudit.TaggingCoveragePercentage);
        Assert.Equal("No Code Blocks", emptyAudit.CoverageCategory);
        Assert.Contains("0 code blocks", emptyAudit.Summary);

        // 2. Document with tagged fences and inline code
        var mixedDoc = """
            # Architecture Overview
            Use `Install-Package` or `dotnet add` to install dependencies.

            ```csharp
            public class Service { }
            ```

            And for configuration:
            ```json
            { "enabled": true }
            ```

            And untagged snippet:
            ```
            plain raw output
            ```
            """;
        var mixedAudit = TextSearch.AuditCodeFenceLanguageCoverage(mixedDoc);
        Assert.Equal(3, mixedAudit.TotalFencedBlocks);
        Assert.Equal(2, mixedAudit.TaggedFencedBlocks);
        Assert.Equal(1, mixedAudit.UntaggedFencedBlocks);
        Assert.Equal(2, mixedAudit.InlineCodeSpans);
        Assert.Equal(66.7, mixedAudit.TaggingCoveragePercentage);
        Assert.Equal("Mixed Coverage (50–79%)", mixedAudit.CoverageCategory);
        Assert.Equal(1, mixedAudit.LanguageCounts["csharp"]);
        Assert.Equal(1, mixedAudit.LanguageCounts["json"]);
        Assert.Contains("66.7% tagged", mixedAudit.Summary);
        Assert.Contains("Top: csharp (1), json (1)", mixedAudit.Summary);
        Assert.Equal(mixedAudit.Summary, TextSearch.FormatCodeFenceLanguageCoverageSummary(mixedDoc));

        // 3. Document with 100% tagged fences
        var fullyTaggedDoc = """
            ```python
            print("hello")
            ```
            ```python
            print("world")
            ```
            """;
        var fullAudit = TextSearch.AuditCodeFenceLanguageCoverage(fullyTaggedDoc);
        Assert.Equal(2, fullAudit.TotalFencedBlocks);
        Assert.Equal(2, fullAudit.TaggedFencedBlocks);
        Assert.Equal(0, fullAudit.UntaggedFencedBlocks);
        Assert.Equal(100.0, fullAudit.TaggingCoveragePercentage);
        Assert.Equal("Fully Tagged (100%)", fullAudit.CoverageCategory);
    }

    [Fact]
    public void Run156_AuditListIndentationHierarchy()
    {
        // 1. Empty / whitespace document returns graceful defaults
        var emptyAudit = TextSearch.AuditListIndentationHierarchy("");
        Assert.Equal(0, emptyAudit.TotalListItems);
        Assert.Equal(0, emptyAudit.OrderedItemCount);
        Assert.Equal(0, emptyAudit.UnorderedItemCount);
        Assert.Equal(0, emptyAudit.MaxDepth);
        Assert.Equal(0.0, emptyAudit.AverageIndentSpaces);
        Assert.Equal("No Lists", emptyAudit.StructureCategory);
        Assert.Contains("0 list items", emptyAudit.Summary);

        // 2. Multi-tier clean hierarchy with code fence immunity
        var doc = """
            # Project Plan
            - Phase 1: Planning
              - Requirement gathering
              - Architecture blueprint
            - Phase 2: Implementation
              1. Backend API
              2. Frontend UI
            ```markdown
            - This is inside a code fence and must be ignored
              - Also ignored
            ```
            > - Callout list item
            """;
        var audit = TextSearch.AuditListIndentationHierarchy(doc);
        Assert.Equal(7, audit.TotalListItems);
        Assert.Equal(5, audit.UnorderedItemCount);
        Assert.Equal(2, audit.OrderedItemCount);
        Assert.Equal(1, audit.MaxDepth);
        Assert.Equal(0, audit.InconsistentIndentCount);
        Assert.Contains("avg indent", audit.Summary);
        Assert.Equal(audit.Summary, TextSearch.FormatListIndentationSummary(doc));

        // 3. Inconsistent indentation jumps (>4 spaces in single forward step)
        var inconsistentDoc = """
            - Root item
                    - Deeply jumped child (8 spaces)
            """;
        var incAudit = TextSearch.AuditListIndentationHierarchy(inconsistentDoc);
        Assert.Equal(2, incAudit.TotalListItems);
        Assert.Equal(1, incAudit.InconsistentIndentCount);
        Assert.Equal("Inconsistent Indentation", incAudit.StructureCategory);
        Assert.Contains("1 indent jumps", incAudit.Summary);
    }

    [Fact]
    public void Run157_AuditMarkdownTableColumnAlignment()
    {
        // 1. Empty / degenerate document
        var emptyAudit = TextSearch.AuditMarkdownTableColumnAlignment("");
        Assert.Equal(0, emptyAudit.TotalTables);
        Assert.Equal(0, emptyAudit.TotalTableRows);
        Assert.Equal(0, emptyAudit.TotalRaggedRows);
        Assert.Equal("No Tables", emptyAudit.AlignmentCategory);
        Assert.Contains("0 tables", emptyAudit.Summary);

        // 2. Perfectly aligned table with alignments
        var doc = """
            # Executive Summary
            | Metric | Status | SLA |
            | :--- | :---: | ---: |
            | Availability | 99.99% | Meets SLA |
            | Latency | 24ms | Exceeds SLA |
            | Errors | 0.01% | Meets SLA |

            ```markdown
            | Code | Fenced | Pipe |
            | Inside | Code | Fence |
            ```
            """;
        var audit = TextSearch.AuditMarkdownTableColumnAlignment(doc);
        Assert.Equal(1, audit.TotalTables);
        Assert.Equal(4, audit.TotalTableRows);
        Assert.Equal(0, audit.TotalRaggedRows);
        Assert.Equal(3, audit.MaxColumnsObserved);
        Assert.Equal("Perfect Grid Alignment", audit.AlignmentCategory);
        Assert.Contains("1 table [4 rows, max 3 cols]", audit.Summary);
        Assert.Equal("left", audit.Tables[0].Alignments[0]);
        Assert.Equal("center", audit.Tables[0].Alignments[1]);
        Assert.Equal("right", audit.Tables[0].Alignments[2]);
        Assert.Equal(audit.Summary, TextSearch.FormatTableAlignmentSummary(doc));

        // 3. Ragged row detection (mismatched columns)
        var raggedDoc = """
            | Name | Role | Department |
            | --- | --- | --- |
            | Alice | Engineering | Systems |
            | Bob | Product |
            | Charlie | Design | UX | Extra |
            """;
        var ragAudit = TextSearch.AuditMarkdownTableColumnAlignment(raggedDoc);
        Assert.Equal(1, ragAudit.TotalTables);
        Assert.Equal(4, ragAudit.TotalTableRows);
        Assert.Equal(2, ragAudit.TotalRaggedRows);
        Assert.Equal("Ragged Rows Detected", ragAudit.AlignmentCategory);
        Assert.Contains("2 ragged rows", ragAudit.Summary);
    }

    [Fact]
    public void AuditTaskListProgress_AccuratelyEvaluatesProgress_FenceImmunityAndSections()
    {
        // 1. Empty or whitespace
        var emptyAudit = TextSearch.AuditTaskListProgress("");
        Assert.Equal(0, emptyAudit.TotalTasks);
        Assert.Equal("No Tasks", emptyAudit.VelocityCategory);
        Assert.Contains("0 items", emptyAudit.Summary);

        // 2. Comprehensive task document with code fences, HTML comments, and multiple sections
        var doc = """
            # Project Roadmap

            Introductory text.

            ## Milestone 1: Core Engine
            - [x] Initial kernel architecture
            - [x] Unit test setup
            - [ ] Performance profiling

            ```markdown
            Code fence containing fake tasks:
            - [ ] Fake task in code block
            - [x] Another fake task
            ```

            <!--
            HTML comment:
            - [ ] Ignored task
            -->

            ## Milestone 2: Desktop UI
            * [X] WinUI 3 Window shell
            + [ ] Status bar integration
            - [ ] MenuFlyout polish
            """;

        var audit = TextSearch.AuditTaskListProgress(doc);
        Assert.Equal(6, audit.TotalTasks);
        Assert.Equal(3, audit.CompletedTasks);
        Assert.Equal(3, audit.PendingTasks);
        Assert.Equal(50.0, audit.CompletionPercentage);
        Assert.Equal("Active In-Progress", audit.VelocityCategory);
        Assert.Equal(2, audit.SectionProgress.Count);

        var m1 = audit.SectionProgress.First(s => s.Section == "Milestone 1: Core Engine");
        Assert.Equal(3, m1.TotalTasks);
        Assert.Equal(2, m1.CompletedTasks);
        Assert.Equal(1, m1.PendingTasks);

        var m2 = audit.SectionProgress.First(s => s.Section == "Milestone 2: Desktop UI");
        Assert.Equal(3, m2.TotalTasks);
        Assert.Equal(1, m2.CompletedTasks);
        Assert.Equal(2, m2.PendingTasks);

        Assert.NotEmpty(audit.PendingTasksSample);
        Assert.Equal("Performance profiling", audit.PendingTasksSample[0].TaskText);
        Assert.Contains("6 tasks · 3 completed (50%), 3 pending · Active In-Progress", audit.Summary);
        Assert.Equal(audit.Summary, TextSearch.FormatTaskListProgressSummary(doc));

        // 3. All completed
        var completeDoc = """
            - [x] Task 1
            - [X] Task 2
            """;
        var compAudit = TextSearch.AuditTaskListProgress(completeDoc);
        Assert.Equal(2, compAudit.TotalTasks);
        Assert.Equal(2, compAudit.CompletedTasks);
        Assert.Equal(0, compAudit.PendingTasks);
        Assert.Equal(100.0, compAudit.CompletionPercentage);
        Assert.Equal("All Tasks Completed (100%)", compAudit.VelocityCategory);
    }

    [Fact]
    public void AuditFootnoteLinkage_AccuratelyEvaluatesReferencesAndDefinitions()
    {
        // 1. Empty or whitespace
        var emptyAudit = TextSearch.AuditFootnoteLinkage("");
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal("No Footnotes", emptyAudit.LinkageCategory);
        Assert.Contains("0 items", emptyAudit.Summary);

        // 2. 100% Bidirectional Linkage with code fence immunity
        var doc = """
            # Document with Footnotes

            Here is a reference [^first] and another [^second].

            ```markdown
            Code fence with ignored footnote:
            [^ignored]: definition inside code fence
            [^fake-ref]
            ```

            <!--
            HTML comment:
            [^comment-ref]
            -->

            [^first]: This is the first footnote definition.
            [^second]: This is the second footnote definition.
            """;

        var audit = TextSearch.AuditFootnoteLinkage(doc);
        Assert.Equal(2, audit.TotalReferences);
        Assert.Equal(2, audit.TotalDefinitions);
        Assert.Empty(audit.UnresolvedReferenceIds);
        Assert.Empty(audit.OrphanDefinitionIds);
        Assert.Empty(audit.DuplicateDefinitionIds);
        Assert.Equal("100% Bidirectional Linkage", audit.LinkageCategory);
        Assert.Contains("100% Bidirectional Linkage", audit.Summary);
        Assert.Equal(audit.Summary, TextSearch.FormatFootnoteLinkageSummary(doc));

        // 3. Unresolved and orphan footnotes
        var brokenDoc = """
            Here is a reference [^missing-target] and [^valid].

            [^valid]: Valid definition.
            [^orphan-target]: Unused definition.
            """;
        var brokenAudit = TextSearch.AuditFootnoteLinkage(brokenDoc);
        Assert.Equal(2, brokenAudit.TotalReferences);
        Assert.Equal(2, brokenAudit.TotalDefinitions);
        Assert.Contains("missing-target", brokenAudit.UnresolvedReferenceIds);
        Assert.Contains("orphan-target", brokenAudit.OrphanDefinitionIds);
        Assert.Equal("Broken Footnote Linkage", brokenAudit.LinkageCategory);
    }

    [Fact]
    public void AuditFrontmatterYamlSchema_AccuratelyEvaluatesSchemaAndCompliance()
    {
        // 1. Empty or whitespace
        var emptyAudit = TextSearch.AuditFrontmatterYamlSchema("");
        Assert.False(emptyAudit.HasFrontmatter);
        Assert.False(emptyAudit.IsClosed);
        Assert.Equal(0, emptyAudit.KeyCount);
        Assert.Equal("Empty Document", emptyAudit.SchemaHealthCategory);
        Assert.Contains("0 properties", emptyAudit.Summary);

        // 2. Valid complete schema
        var doc = string.Join("\n",
            "---",
            "title: Engineering Architecture Guide",
            "author: MarkSmith Core Team",
            "date: 2026-10-19",
            "tags: [dotnet, winui3, architecture]",
            "draft: false",
            "---",
            "",
            "# Document Content",
            "Here is the body.");

        var audit = TextSearch.AuditFrontmatterYamlSchema(doc);
        Assert.True(audit.HasFrontmatter, audit.Summary);
        Assert.True(audit.IsClosed, audit.Summary);
        Assert.Equal(5, audit.KeyCount);
        Assert.Contains("title", audit.PresentKeys);
        Assert.Contains("author", audit.PresentKeys);
        Assert.Contains("date", audit.PresentKeys);
        Assert.Empty(audit.MissingRequiredKeys);
        Assert.Empty(audit.DuplicateKeys);
        Assert.Equal(100.0, audit.CompletenessPercentage);
        Assert.Equal("Valid Complete Schema", audit.SchemaHealthCategory);
        Assert.Equal(audit.Summary, TextSearch.FormatFrontmatterSchemaSummary(doc));

        // 3. Missing required keys
        var partialDoc = string.Join("\n",
            "---",
            "title: Only Title",
            "category: Documentation",
            "---",
            "",
            "# Partial Document");
        var partialAudit = TextSearch.AuditFrontmatterYamlSchema(partialDoc);
        Assert.True(partialAudit.HasFrontmatter);
        Assert.True(partialAudit.IsClosed);
        Assert.Contains("author", partialAudit.MissingRequiredKeys);
        Assert.Contains("date", partialAudit.MissingRequiredKeys);
        Assert.Equal("Missing Required Keys", partialAudit.SchemaHealthCategory);
        Assert.True(partialAudit.CompletenessPercentage < 100.0);

        // 4. Unclosed frontmatter
        var unclosedDoc = string.Join("\n",
            "---",
            "title: Unclosed",
            "author: Tester",
            "# No closing marker");
        var unclosedAudit = TextSearch.AuditFrontmatterYamlSchema(unclosedDoc);
        Assert.True(unclosedAudit.HasFrontmatter);
        Assert.False(unclosedAudit.IsClosed);
        Assert.Equal("Unclosed Frontmatter", unclosedAudit.SchemaHealthCategory);
    }

    [Fact]
    public void Run161_AuditTocDepthAndAnchorCollisions_ValidatesHeadingsSlugsAndInternalLinks()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditTocDepthAndAnchorCollisions("");
        Assert.Equal(0, emptyAudit.TotalHeadings);
        Assert.Equal("Empty Document", emptyAudit.HealthCategory);
        Assert.Equal(100.0, emptyAudit.IntegrityPercentage);

        // 2. Valid hierarchy with anchors
        var validDoc = string.Join("\n",
            "# Executive Summary",
            "",
            "See [Architecture](#system-architecture) and [Overview](#overview).",
            "",
            "## System Architecture",
            "Details here.",
            "",
            "### Overview",
            "Core overview notes.");

        var validAudit = TextSearch.AuditTocDepthAndAnchorCollisions(validDoc);
        Assert.Equal(3, validAudit.TotalHeadings);
        Assert.Equal(1, validAudit.MinHeadingLevel);
        Assert.Equal(3, validAudit.MaxHeadingLevel);
        Assert.Equal(0, validAudit.HierarchyJumpCount);
        Assert.Equal(2, validAudit.TotalAnchorLinks);
        Assert.Equal(2, validAudit.ValidAnchorLinks);
        Assert.Equal(0, validAudit.BrokenAnchorLinks);
        Assert.Equal("Consistent Hierarchy (100% Valid)", validAudit.HealthCategory);
        Assert.Equal(100.0, validAudit.IntegrityPercentage);
        Assert.Equal(validAudit.Summary, TextSearch.FormatTocAnchorSummary(validDoc));

        // 3. Hierarchy jump (H1 -> H3)
        var jumpDoc = string.Join("\n",
            "# Root Heading",
            "### Skipped Deep Subsection");
        var jumpAudit = TextSearch.AuditTocDepthAndAnchorCollisions(jumpDoc);
        Assert.Equal(2, jumpAudit.TotalHeadings);
        Assert.Equal(1, jumpAudit.HierarchyJumpCount);
        Assert.Equal("Skipped Depth Levels Detected", jumpAudit.HealthCategory);
        Assert.True(jumpAudit.Headings[1].HasHierarchyJump);

        // 4. Broken anchor link target
        var brokenDoc = string.Join("\n",
            "# Heading One",
            "Link to [Ghost Target](#non-existent-section).");
        var brokenAudit = TextSearch.AuditTocDepthAndAnchorCollisions(brokenDoc);
        Assert.Equal(1, brokenAudit.TotalHeadings);
        Assert.Equal(1, brokenAudit.TotalAnchorLinks);
        Assert.Equal(1, brokenAudit.BrokenAnchorLinks);
        Assert.Contains("non-existent-section", brokenAudit.BrokenTargetSlugs);
        Assert.Equal("Broken Anchor References Detected", brokenAudit.HealthCategory);

        // 5. Duplicate heading slugs
        var dupDoc = string.Join("\n",
            "# Section",
            "## Section",
            "### Section");
        var dupAudit = TextSearch.AuditTocDepthAndAnchorCollisions(dupDoc);
        Assert.Equal(3, dupAudit.TotalHeadings);
        Assert.Equal("section", dupAudit.Headings[0].Slug);
        Assert.Equal("section-1", dupAudit.Headings[1].Slug);
        Assert.Equal("section-2", dupAudit.Headings[2].Slug);
        Assert.Contains("section", dupAudit.DuplicateSlugs);
    }

    [Fact]
    public void Run162_AuditReferenceLinkIntegrity_ValidatesDefinitionsAndReferences()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditReferenceLinkIntegrity("");
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal("Empty Document", emptyAudit.Category);
        Assert.Equal(100.0, emptyAudit.IntegrityScore);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatReferenceLinkIntegritySummary(""));

        // 2. Optimal linkage (Full, Collapsed, Shortcut)
        var validDoc = string.Join("\n",
            "See [Documentation][docs] and [API Guide][] for details.",
            "Also refer to [Architecture] overview.",
            "",
            "[docs]: https://example.com/docs \"Docs\"",
            "[api guide]: https://example.com/api",
            "[architecture]: https://example.com/arch");

        var validAudit = TextSearch.AuditReferenceLinkIntegrity(validDoc);
        Assert.Equal(3, validAudit.TotalDefinitions);
        Assert.Equal(3, validAudit.UniqueDefinitions);
        Assert.Equal(3, validAudit.TotalReferences);
        Assert.Equal(3, validAudit.ResolvedReferences);
        Assert.Equal(0, validAudit.BrokenReferences);
        Assert.Equal(0, validAudit.OrphanDefinitions);
        Assert.Equal(0, validAudit.DuplicateDefinitions);
        Assert.Equal("Optimal Reference Linkage (100%)", validAudit.Category);
        Assert.Equal(100.0, validAudit.IntegrityScore);
        Assert.Equal(validAudit.Summary, TextSearch.FormatReferenceLinkIntegritySummary(validDoc));

        // 3. Broken reference link (unresolved target)
        var brokenDoc = string.Join("\n",
            "Link to [Ghost Target][non-existent-ref].",
            "",
            "[existing]: https://example.com/existing");

        var brokenAudit = TextSearch.AuditReferenceLinkIntegrity(brokenDoc);
        Assert.Equal(1, brokenAudit.TotalDefinitions);
        Assert.Equal(1, brokenAudit.TotalReferences);
        Assert.Equal(0, brokenAudit.ResolvedReferences);
        Assert.Equal(1, brokenAudit.BrokenReferences);
        Assert.Contains("non-existent-ref", brokenAudit.BrokenLabels);
        Assert.Equal("Broken Links & Orphan Definitions", brokenAudit.Category);

        // 4. Orphan reference definition
        var orphanDoc = string.Join("\n",
            "Plain text without any links.",
            "",
            "[unused-ref]: https://example.com/unused");

        var orphanAudit = TextSearch.AuditReferenceLinkIntegrity(orphanDoc);
        Assert.Equal(1, orphanAudit.TotalDefinitions);
        Assert.Equal(0, orphanAudit.TotalReferences);
        Assert.Equal(1, orphanAudit.OrphanDefinitions);
        Assert.Contains("unused-ref", orphanAudit.OrphanLabels);
        Assert.Equal("Orphan Reference Definitions Present", orphanAudit.Category);

        // 5. Duplicate definition labels
        var dupDoc = string.Join("\n",
            "See [docs][dup].",
            "",
            "[dup]: https://example.com/first",
            "[dup]: https://example.com/second");

        var dupAudit = TextSearch.AuditReferenceLinkIntegrity(dupDoc);
        Assert.Equal(2, dupAudit.TotalDefinitions);
        Assert.Equal(1, dupAudit.UniqueDefinitions);
        Assert.Equal(1, dupAudit.DuplicateDefinitions);
        Assert.Contains("dup", dupAudit.DuplicateLabels);
        Assert.Equal("Duplicate Reference Definitions", dupAudit.Category);
    }

    [Fact]
    public void Run163_AuditSyntaxAmbiguityAndDelimiterBalance_ValidatesDelimitersAndFences()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditSyntaxAmbiguityAndDelimiterBalance("");
        Assert.Equal(0, emptyAudit.TotalAmbiguities);
        Assert.Equal(0, emptyAudit.UnclosedCodeSpans);
        Assert.Equal(0, emptyAudit.UnclosedEmphasisPairs);
        Assert.Equal(0, emptyAudit.UnclosedMathDelimiters);
        Assert.Equal(0, emptyAudit.UnmatchedBrackets);
        Assert.Equal("Empty Document", emptyAudit.Category);
        Assert.Equal(100.0, emptyAudit.SyntaxIntegrityScore);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatSyntaxAmbiguitySummary(""));

        // 2. Perfectly balanced markdown document
        var balancedDoc = string.Join("\n",
            "# Heading",
            "This is **bold** text and `inline code` and $E=mc^2$.",
            "A link [OpenAI](https://openai.com) with (parentheses) and {braces}.",
            "```csharp",
            "Console.WriteLine(\"Hello\");",
            "```",
            ":::smartart",
            "- Process 1",
            ":::");
        var balancedAudit = TextSearch.AuditSyntaxAmbiguityAndDelimiterBalance(balancedDoc);
        Assert.Equal(0, balancedAudit.TotalAmbiguities);
        Assert.Equal(100.0, balancedAudit.SyntaxIntegrityScore);
        Assert.Equal("Optimal Markdown Syntax (100% Balanced)", balancedAudit.Category);
        Assert.Contains("0 issues", balancedAudit.Summary);

        // 3. Unbalanced markdown (unclosed inline code and unclosed bracket)
        var unbalancedDoc = string.Join("\n",
            "# Unfinished Section",
            "Here is an unclosed `code span without closing backtick.",
            "And an unclosed [bracket link without closing square.");
        var unbalAudit = TextSearch.AuditSyntaxAmbiguityAndDelimiterBalance(unbalancedDoc);
        Assert.True(unbalAudit.TotalAmbiguities >= 2);
        Assert.True(unbalAudit.SyntaxIntegrityScore < 100.0);
        Assert.NotEmpty(unbalAudit.Ambiguities);
        Assert.Contains(unbalAudit.Ambiguities, d => d.Delimiter == "`");
        Assert.Contains(unbalAudit.Ambiguities, d => d.Delimiter == "[");
    }

    [Fact]
    public void Run164_AuditMathFormulaComplexityAndDelimiters_ValidatesKaTeXDelimitersAndBraces()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditMathFormulaComplexityAndDelimiters("");
        Assert.Equal(0, emptyAudit.TotalFormulas);
        Assert.Equal(0, emptyAudit.ProblematicFormulas);
        Assert.Equal(100.0, emptyAudit.SyntaxIntegrityScore);
        Assert.Equal("No Formulas (Clean)", emptyAudit.ComplexityCategory);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatMathFormulaSummary(""));

        // 2. Valid inline and display math formulas
        var validDoc = string.Join("\n",
            "# Physics Notes",
            "Einstein's energy relation is $E = mc^2$ and momentum is $p = \\hbar k$.",
            "Display equation:",
            "$$",
            "\\int_{0}^{\\infty} e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}",
            "$$",
            "Currency should be ignored: $50 and $100.",
            "Inline code with dollar should be ignored: `$foo$`.");

        var validAudit = TextSearch.AuditMathFormulaComplexityAndDelimiters(validDoc);
        Assert.Equal(3, validAudit.TotalFormulas);
        Assert.Equal(2, validAudit.InlineFormulas);
        Assert.Equal(1, validAudit.DisplayFormulas);
        Assert.Equal(3, validAudit.ValidFormulas);
        Assert.Equal(0, validAudit.ProblematicFormulas);
        Assert.True(validAudit.MeanComplexityScore > 0.0);
        Assert.Equal(100.0, validAudit.SyntaxIntegrityScore);
        Assert.Contains("100% valid", validAudit.Summary);

        // 3. Formula with syntax error (unbalanced curly brace and mismatched environment)
        var invalidDoc = string.Join("\n",
            "Broken inline math: $\\frac{a}{b$",
            "Broken environment:",
            "$$",
            "\\begin{matrix}",
            "1 & 2 \\\\",
            "3 & 4",
            "\\end{pmatrix}",
            "$$");

        var invalidAudit = TextSearch.AuditMathFormulaComplexityAndDelimiters(invalidDoc);
        Assert.Equal(2, invalidAudit.TotalFormulas);
        Assert.True(invalidAudit.ProblematicFormulas >= 2);
        Assert.True(invalidAudit.SyntaxIntegrityScore < 100.0);
        Assert.Equal("LaTeX Syntax Errors Detected", invalidAudit.ComplexityCategory);
        Assert.Contains("issues", invalidAudit.Summary);
    }

    [Fact]
    public void Run165_AuditTableAlignmentAndColumnSpacing_EvaluatesTableFormatAndAlignment()
    {
        // 1. Empty / no tables
        var emptyAudit = TextSearch.AuditTableAlignmentAndColumnSpacing("");
        Assert.Equal(0, emptyAudit.TotalTables);
        Assert.Equal(100.0, emptyAudit.MeanAlignmentScore);
        Assert.Equal("No Tables", emptyAudit.Category);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatTableAlignmentAndColumnSpacingSummary(""));

        // 2. Well-formatted table with alignments and paddings
        var validDoc = string.Join("\n",
            "# Service Endpoints",
            "| Endpoint | Method | Latency | Status |",
            "| :--- | :---: | ---: | :--- |",
            "| /api/v1/auth | POST | 12ms | Active |",
            "| /api/v1/user | GET | 5ms | Active |");

        var validAudit = TextSearch.AuditTableAlignmentAndColumnSpacing(validDoc);
        Assert.Equal(1, validAudit.TotalTables);
        Assert.Equal(4, validAudit.TotalColumns);
        Assert.Equal(3, validAudit.TotalRows);
        Assert.Equal(12, validAudit.TotalCells);
        Assert.Equal(100.0, validAudit.MeanAlignmentScore);
        Assert.Equal(0, validAudit.RaggedRows);
        Assert.Equal(validAudit.Summary, TextSearch.FormatTableAlignmentAndColumnSpacingSummary(validDoc));

        // 3. Ragged table (inconsistent column counts)
        var raggedDoc = string.Join("\n",
            "| Header A | Header B |",
            "| --- | --- |",
            "| Cell 1 | Cell 2 | Cell 3 Extra |",
            "| Single |");

        var raggedAudit = TextSearch.AuditTableAlignmentAndColumnSpacing(raggedDoc);
        Assert.Equal(1, raggedAudit.TotalTables);
        Assert.True(raggedAudit.RaggedRows >= 1);
        Assert.True(raggedAudit.MeanAlignmentScore < 100.0);
        Assert.Equal("Minor Ragged Rows Detected", raggedAudit.Category);
    }

    [Fact]
    public void Run166_AuditDefinitionListConsistency_DetectsIndentsAndUniformity()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditDefinitionListConsistency("");
        Assert.Equal(0, emptyAudit.TotalLists);
        Assert.Equal(100.0, emptyAudit.UniformityScore);
        Assert.Equal("No Definition Lists", emptyAudit.Category);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatDefinitionListSummary(""));

        // 2. Uniform definition list (loose format)
        var uniformDoc = string.Join("\n",
            "Apple",
            ":   A sweet red fruit with seeds inside",
            "",
            "Banana",
            ":   A long yellow fruit rich in potassium");

        var uniformAudit = TextSearch.AuditDefinitionListConsistency(uniformDoc);
        Assert.True(uniformAudit.TotalLists >= 1);
        Assert.Equal(2, uniformAudit.TotalDefinitions);
        Assert.Equal(0, uniformAudit.IrregularIndentCount);
        Assert.Equal(100.0, uniformAudit.UniformityScore);
        Assert.Equal("Strict Uniform Indent (100%)", uniformAudit.Category);
        Assert.Equal(uniformAudit.Summary, TextSearch.FormatDefinitionListSummary(uniformDoc));

        // 3. Fenced block :::deflist
        var fencedDoc = string.Join("\n",
            ":::deflist",
            "Term A",
            ":   Standard definition text",
            "Term B",
            ":   Another standard definition",
            ":::");

        var fencedAudit = TextSearch.AuditDefinitionListConsistency(fencedDoc);
        Assert.Equal(1, fencedAudit.TotalLists);
        Assert.Equal(1, fencedAudit.FencedDefLists);
        Assert.Equal(2, fencedAudit.TotalDefinitions);
        Assert.Equal(100.0, fencedAudit.UniformityScore);

        // 4. Irregular indent (1 space or 6 spaces)
        var irregularDoc = string.Join("\n",
            "Term C",
            ": OneSpace",
            "Term D",
            ":      SixSpaces");

        var irregularAudit = TextSearch.AuditDefinitionListConsistency(irregularDoc);
        Assert.Equal(2, irregularAudit.IrregularIndentCount);
        Assert.True(irregularAudit.UniformityScore < 100.0);
    }

    [Fact]
    public void Run167_AuditFootnoteAndReferenceConsistency_DetectsLinkageAndMonotonicity()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditFootnoteAndReferenceConsistency("");
        Assert.Equal(0, emptyAudit.TotalAnchors);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.FootnoteConsistencyScore);
        Assert.Equal("Clean Baseline (Zero Footnotes)", emptyAudit.Category);
        Assert.Equal(emptyAudit.Summary, TextSearch.FormatFootnoteReferenceSummary(""));

        // 2. Clean monotonic bidirectional footnotes
        var cleanDoc = string.Join("\n",
            "This is the first claim[^1] and this is the second claim[^2].",
            "",
            "[^1]: Details for the first claim.",
            "[^2]: Details for the second claim.");

        var cleanAudit = TextSearch.AuditFootnoteAndReferenceConsistency(cleanDoc);
        Assert.Equal(2, cleanAudit.TotalAnchors);
        Assert.Equal(2, cleanAudit.TotalDefinitions);
        Assert.Empty(cleanAudit.MissingDefinitionIds);
        Assert.Empty(cleanAudit.OrphanDefinitionIds);
        Assert.Empty(cleanAudit.DuplicateDefinitionIds);
        Assert.Equal(0, cleanAudit.NonMonotonicAnchorCount);
        Assert.Equal(100.0, cleanAudit.FootnoteConsistencyScore);
        Assert.Equal("Perfect Bi-Directional Consistency", cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, TextSearch.FormatFootnoteReferenceSummary(cleanDoc));

        // 3. Document with missing target, orphan definition, duplicate definition, and non-monotonic sequence
        var mixedDoc = string.Join("\n",
            "Non-monotonic anchor sequence: second first[^2] then earlier[^1] and missing[^missing].",
            "",
            "[^1]: First definition.",
            "[^1]: Duplicate first definition.",
            "[^2]: Second definition.",
            "[^orphan]: This footnote is never referenced in body text.");

        var mixedAudit = TextSearch.AuditFootnoteAndReferenceConsistency(mixedDoc);
        Assert.Equal(3, mixedAudit.TotalAnchors);
        Assert.Equal(4, mixedAudit.TotalDefinitions);
        Assert.Contains("missing", mixedAudit.MissingDefinitionIds);
        Assert.Contains("orphan", mixedAudit.OrphanDefinitionIds);
        Assert.Contains("1", mixedAudit.DuplicateDefinitionIds);
        Assert.True(mixedAudit.NonMonotonicAnchorCount >= 1);
        Assert.True(mixedAudit.FootnoteConsistencyScore < 100.0);
        Assert.Equal("Missing Footnote Targets", mixedAudit.Category);
    }

    [Fact]
    public void AdmonitionCalloutConsistency_Audits_Properly()
    {
        // 1. Empty / whitespace doc
        var emptyAudit = TextSearch.AuditAdmonitionCalloutConsistency("");
        Assert.Equal(0, emptyAudit.TotalCallouts);
        Assert.Equal(100.0, emptyAudit.ParityScore);
        Assert.Equal("No Admonitions", emptyAudit.Category);

        // 2. Clean GFM callouts
        var cleanDoc = string.Join("\n",
            "> [!NOTE]",
            "> Useful background context for the section.",
            "",
            "> [!TIP] Pro Tip",
            "> Press Ctrl+S frequently to save work.",
            "",
            "> [!WARNING]+ Collapsible Warning",
            "> High voltage wiring inside.");

        var cleanAudit = TextSearch.AuditAdmonitionCalloutConsistency(cleanDoc);
        Assert.Equal(3, cleanAudit.TotalCallouts);
        Assert.Equal(3, cleanAudit.StandardCallouts);
        Assert.Equal(0, cleanAudit.CustomCallouts);
        Assert.Equal(0, cleanAudit.ImproperSpacingCount);
        Assert.Equal(0, cleanAudit.EmptyBodyCount);
        Assert.Equal(100.0, cleanAudit.ParityScore);
        Assert.Equal("Pristine GFM Admonition Syntax", cleanAudit.Category);
        Assert.True(cleanAudit.Callouts[2].IsFoldable);
        Assert.True(cleanAudit.Callouts[2].FoldableExpanded);

        // 3. Document with improper spacing and nested depth
        var mixedDoc = string.Join("\n",
            ">[!NOTE]", // missing space
            "> No space after chevron.",
            "",
            ">> [!CAUTION]", // nested
            ">> Deeply nested alert.");

        var mixedAudit = TextSearch.AuditAdmonitionCalloutConsistency(mixedDoc);
        Assert.Equal(2, mixedAudit.TotalCallouts);
        Assert.Equal(1, mixedAudit.ImproperSpacingCount);
        Assert.Equal(1, mixedAudit.NestedCallouts);
        Assert.Equal(2, mixedAudit.MaxNestingDepth);
        Assert.True(mixedAudit.ParityScore < 100.0);
        Assert.Equal(mixedAudit.Summary, TextSearch.FormatAdmonitionCalloutSummary(mixedDoc));
    }

    [Fact]
    public void Run169_AuditImageLinkAndAltTextAccessibility_ValidatesWcagAltTextAndHttp()
    {
        // 1. Empty / null document
        var emptyAudit = TextSearch.AuditImageLinkAndAltTextAccessibility(null);
        Assert.Equal(0, emptyAudit.TotalImages);
        Assert.Equal(100.0, emptyAudit.AccessibilityScore);
        Assert.Equal("No Images Detected", emptyAudit.ComplianceCategory);

        // 2. Clean accessible document with markdown and HTML images
        var cleanDoc = string.Join("\n",
            "# Accessibility Showcase",
            "![Diagram showing system flow architecture](https://example.com/system-flow.svg)",
            "<img src=\"https://example.com/photo-nature.jpg\" alt=\"Mountain range at sunset\" />",
            "![Embedded SVG icon](data:image/svg+xml;utf8,<svg></svg>)"
        );
        var cleanAudit = TextSearch.AuditImageLinkAndAltTextAccessibility(cleanDoc);
        Assert.Equal(3, cleanAudit.TotalImages);
        Assert.Equal(3, cleanAudit.ValidAccessibleImages);
        Assert.Equal(0, cleanAudit.MissingAltCount);
        Assert.Equal(0, cleanAudit.GenericAltCount);
        Assert.Equal(0, cleanAudit.InsecureHttpCount);
        Assert.Equal(1, cleanAudit.DataUriCount);
        Assert.Equal(100.0, cleanAudit.AccessibilityScore);
        Assert.Equal("WCAG AAA Compliant Image Content", cleanAudit.ComplianceCategory);
        Assert.True(cleanAudit.Images[1].IsHtmlTag);
        Assert.True(cleanAudit.Images[2].IsDataUri);

        // 3. Document with missing alt, generic alt, insecure HTTP, and code fences (which must be ignored)
        var mixedDoc = string.Join("\n",
            "```markdown",
            "![ignored in fence](http://insecure.com/fake.png)",
            "```",
            "Here is an inline code snippet `![ignored in span](http://insecure.com/fake2.png)`.",
            "![](https://example.com/missing-alt.png)",
            "![picture](https://example.com/generic-alt.jpg)",
            "![Valid description](http://insecure.example.com/banner.png)"
        );
        var mixedAudit = TextSearch.AuditImageLinkAndAltTextAccessibility(mixedDoc);
        Assert.Equal(3, mixedAudit.TotalImages);
        Assert.Equal(1, mixedAudit.MissingAltCount);
        Assert.Equal(1, mixedAudit.GenericAltCount);
        Assert.Equal(1, mixedAudit.InsecureHttpCount);
        Assert.True(mixedAudit.AccessibilityScore < 100.0);
        Assert.Equal(mixedAudit.Summary, TextSearch.FormatImageAccessibilitySummary(mixedDoc));
    }

    [Fact]
    public void AuditMathDelimiterAndMacroIntegrity_ValidMath_Returns100Score()
    {
        // Null or empty
        var emptyAudit = TextSearch.AuditMathDelimiterAndMacroIntegrity("");
        Assert.Equal(0, emptyAudit.TotalMathSpans);
        Assert.Equal(100.0, emptyAudit.KaTeXInteropScore);
        Assert.Contains("0 formulas", emptyAudit.Summary);

        // Clean document with inline and display math
        var cleanDoc = string.Join("\n",
            "# Math Formulas",
            "Euler's identity is $e^{i\\pi} + 1 = 0$ in inline form.",
            "Display equations:",
            "$$",
            "\\sum_{k=1}^n k = \\frac{n(n+1)}{2}",
            "$$",
            "Alternative brackets: \\[E = mc^2\\] and \\(\\alpha + \\beta = \\gamma\\)."
        );

        var audit = TextSearch.AuditMathDelimiterAndMacroIntegrity(cleanDoc);
        Assert.Equal(4, audit.TotalMathSpans);
        Assert.Equal(2, audit.InlineMathCount);
        Assert.Equal(2, audit.DisplayMathCount);
        Assert.Equal(4, audit.ValidMathCount);
        Assert.Equal(0, audit.SyntaxIssueCount);
        Assert.Equal(0, audit.UnbalancedDelimiters);
        Assert.Equal(0, audit.UnbalancedBraces);
        Assert.Equal(100.0, audit.KaTeXInteropScore);
        Assert.Equal("Full KaTeX & MathML Render Fidelity", audit.CompatibilityCategory);
        Assert.Equal(audit.Summary, TextSearch.FormatMathDelimiterSummary(cleanDoc));
    }

    [Fact]
    public void AuditMathDelimiterAndMacroIntegrity_SyntaxHazards_DetectsIssues()
    {
        var invalidDoc = string.Join("\n",
            "Invalid spacing: $ x = 5 $ has spaces around delimiters.",
            "Unbalanced braces: $\\frac{a}{b$",
            "Unknown macro: $\\bogusmacro{123}$",
            "Unclosed display math:",
            "$$",
            "\\int_0^1 x dx"
        );

        var audit = TextSearch.AuditMathDelimiterAndMacroIntegrity(invalidDoc);
        Assert.True(audit.SyntaxIssueCount > 0);
        Assert.True(audit.InvalidSpacingCount > 0);
        Assert.True(audit.UnbalancedBraces > 0);
        Assert.True(audit.UnknownMacroCount > 0);
        Assert.True(audit.UnbalancedDelimiters > 0);
        Assert.True(audit.KaTeXInteropScore < 80.0);
    }

    [Fact]
    public void AuditMathDelimiterAndMacroIntegrity_ExcludesCodeFencesAndCurrency()
    {
        var doc = string.Join("\n",
            "```markdown",
            "$not_math_in_code_fence$",
            "$$",
            "\\sum in code block",
            "$$",
            "```",
            "Inline code: `$also_ignored$`",
            "Cost is $50 and $100 today."
        );

        var audit = TextSearch.AuditMathDelimiterAndMacroIntegrity(doc);
        Assert.Equal(0, audit.TotalMathSpans);
        Assert.Equal(0, audit.SyntaxIssueCount);
        Assert.Equal(100.0, audit.KaTeXInteropScore);
    }

    [Fact]
    public void Run171_AuditDocumentReadingEaseAndGradeLevel_ValidatesSyllablesAndScores()
    {
        // 1. Empty document
        var empty = TextSearch.AuditDocumentReadingEaseAndGradeLevel("");
        Assert.Equal(0, empty.TotalWords);
        Assert.Equal(0, empty.TotalSentences);
        Assert.Equal(100.0, empty.FleschReadingEase);
        Assert.Equal(0.0, empty.FleschKincaidGradeLevel);
        Assert.Equal(empty.Summary, TextSearch.FormatReadingEaseSummary(""));

        // 2. Simple plain English document
        string simpleDoc = string.Join("\n",
            "# Welcome to MarkSmith",
            "The quick dog ran to the park. The cat sat on the soft rug.",
            "We can write clean text each day. It is easy and fun to read."
        );
        var simpleAudit = TextSearch.AuditDocumentReadingEaseAndGradeLevel(simpleDoc);
        Assert.True(simpleAudit.TotalWords >= 20);
        Assert.True(simpleAudit.TotalSentences >= 4);
        Assert.True(simpleAudit.MonosyllableWords > simpleAudit.PolysyllableWords);
        Assert.True(simpleAudit.FleschReadingEase >= 75.0);
        Assert.True(simpleAudit.FleschKincaidGradeLevel <= 6.0);
        Assert.True(simpleAudit.SilentReadingTimeMinutes > 0.0);
        Assert.NotEmpty(simpleAudit.Metrics);

        // 3. Technical academic text with polysyllabic complexity
        string complexDoc = string.Join("\n",
            "Philosophical investigations regarding multidimensional epistemological architectures " +
            "demonstrate comprehensive incomprehensibility within counterproductive computational infrastructures."
        );
        var complexAudit = TextSearch.AuditDocumentReadingEaseAndGradeLevel(complexDoc);
        Assert.True(complexAudit.PolysyllableWords >= 5);
        Assert.True(complexAudit.AverageSyllablesPerWord > 2.0);
        Assert.True(complexAudit.FleschReadingEase < 50.0);
        Assert.True(complexAudit.FleschKincaidGradeLevel >= 12.0);

        // 4. Code fences and HTML immunity
        string mixedDoc = string.Join("\n",
            "```csharp",
            "public static void LongMethodWithComplexNames(int paramA, string paramB) { }",
            "```",
            "$$",
            "\\int_{0}^{\\infty} e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}",
            "$$",
            "<div><span><img src='test.png'/></span></div>",
            "This is simple clean text for our readers."
        );
        var mixedAudit = TextSearch.AuditDocumentReadingEaseAndGradeLevel(mixedDoc);
        Assert.True(mixedAudit.TotalWords <= 10);
        Assert.True(mixedAudit.FleschReadingEase >= 60.0);
    }

    [Fact]
    public void Run172_AuditFootnoteReferenceIntegrity_ValidatesAnchorsAndMonotonicity()
    {
        var empty = TextSearch.AuditFootnoteReferenceIntegrity("");
        Assert.Equal(0, empty.TotalAnchors);
        Assert.Equal(0, empty.TotalDefinitions);
        Assert.Equal(100.0, empty.FootnoteConsistencyScore);
        Assert.Equal(empty.Summary, TextSearch.FormatFootnoteReferenceSummary(""));

        string validDoc = string.Join("\n",
            "This is an introductory concept[^1].",
            "Here is additional clarification[^2] with further details[^3].",
            "",
            "[^1]: First footnote definition.",
            "[^2]: Second footnote definition.",
            "[^3]: Third footnote definition."
        );
        var validAudit = TextSearch.AuditFootnoteReferenceIntegrity(validDoc);
        Assert.Equal(3, validAudit.TotalAnchors);
        Assert.Equal(3, validAudit.TotalDefinitions);
        Assert.Equal(0, validAudit.MissingDefinitions);
        Assert.Equal(0, validAudit.UnreferencedDefinitions);
        Assert.Equal(0, validAudit.NonMonotonicAnchorCount);
        Assert.Equal(100.0, validAudit.FootnoteConsistencyScore);

        string brokenDoc = string.Join("\n",
            "First concept[^3].",
            "Second concept[^1].",
            "Missing concept[^99].",
            "",
            "[^1]: Definition one.",
            "[^3]: Definition three.",
            "[^orphan]: Forgotten definition."
        );
        var brokenAudit = TextSearch.AuditFootnoteReferenceIntegrity(brokenDoc);
        Assert.Equal(3, brokenAudit.TotalAnchors);
        Assert.Equal(3, brokenAudit.TotalDefinitions);
        Assert.Equal(1, brokenAudit.MissingDefinitions);
        Assert.Equal(1, brokenAudit.UnreferencedDefinitions);
        Assert.True(brokenAudit.NonMonotonicAnchorCount >= 1);
        Assert.True(brokenAudit.FootnoteConsistencyScore < 100.0);
    }

    [Fact]
    public void Run173_AuditBlockquoteCalloutsAndDepth_ValidatesGfmAlertsAndNesting()
    {
        var empty = TextSearch.AuditBlockquoteCalloutsAndDepth("");
        Assert.Equal(0, empty.TotalCallouts);
        Assert.Equal(100.0, empty.ParityScore);
        Assert.Equal("No Admonitions", empty.Category);
        Assert.Equal(empty.Summary, TextSearch.FormatBlockquoteCalloutSummary(""));

        string validDoc = string.Join("\n",
            "> [!NOTE]",
            "> Essential context for document reader.",
            "",
            "> [!TIP]",
            "> Recommended shortcut for productivity.",
            "",
            "> [!WARNING]",
            "> Cautionary warning regarding security."
        );
        var validAudit = TextSearch.AuditBlockquoteCalloutsAndDepth(validDoc);
        Assert.Equal(3, validAudit.TotalCallouts);
        Assert.Equal(3, validAudit.StandardCallouts);
        Assert.Equal(0, validAudit.CustomCallouts);
        Assert.Equal(100.0, validAudit.ParityScore);
        Assert.Equal("Pristine GFM Admonition Syntax", validAudit.Category);

        string mixedDoc = string.Join("\n",
            ">[!NOTE]", // missing spacing after chevron
            "> First alert with syntax defect.",
            "",
            ">>> [!DANGER]", // deep nesting (depth 3) + non-standard tag
            ">>> Nested severe alert."
        );
        var mixedAudit = TextSearch.AuditBlockquoteCalloutsAndDepth(mixedDoc);
        Assert.Equal(2, mixedAudit.TotalCallouts);
        Assert.Equal(1, mixedAudit.ImproperSpacingCount);
        Assert.Equal(1, mixedAudit.CustomCallouts);
        Assert.True(mixedAudit.MaxNestingDepth >= 3);
        Assert.True(mixedAudit.ParityScore < 100.0);
    }

    [Fact]
    public void Run174_AuditDefinitionListsAndGlossaryTerms_ValidatesColonSyntaxAndGlossary()
    {
        var empty = TextSearch.AuditDefinitionListsAndGlossaryTerms("");
        Assert.Equal(0, empty.TotalTerms);
        Assert.Equal(0, empty.TotalDefinitions);
        Assert.Equal(100.0, empty.GlossaryConsistencyScore);
        Assert.Equal("No Definition Lists", empty.Category);
        Assert.Equal(empty.Summary, TextSearch.FormatDefinitionListsAndGlossarySummary(""));

        string validDoc = string.Join("\n",
            "Markdown",
            ": A lightweight markup language with plain text formatting syntax.",
            "",
            "KaTeX",
            ": Fast math typesetting library for the web.",
            ": Built by Khan Academy."
        );
        var validAudit = TextSearch.AuditDefinitionListsAndGlossaryTerms(validDoc);
        Assert.Equal(2, validAudit.TotalTerms);
        Assert.Equal(3, validAudit.TotalDefinitions);
        Assert.Equal(1, validAudit.MultiDefinitionTerms);
        Assert.Equal(0, validAudit.IrregularIndentationCount);
        Assert.Equal(100.0, validAudit.GlossaryConsistencyScore);
        Assert.Equal("Pristine Definition List Syntax", validAudit.Category);

        string irregularDoc = string.Join("\n",
            "TermOne",
            ":NoSpaceAfterColon",
            "",
            "<dl>",
            "  <dt>HTML Term</dt>",
            "  <dd>Definition inside HTML tag.</dd>",
            "</dl>"
        );
        var irregularAudit = TextSearch.AuditDefinitionListsAndGlossaryTerms(irregularDoc);
        Assert.Equal(2, irregularAudit.TotalTerms);
        Assert.Equal(2, irregularAudit.TotalDefinitions);
        Assert.Equal(1, irregularAudit.IrregularIndentationCount);
        Assert.True(irregularAudit.GlossaryConsistencyScore < 100.0);
    }

    [Fact]
    public void Run175_AuditTaskListHierarchyAndProgress_ValidatesParentChildStateAndIndentation()
    {
        var empty = TextSearch.AuditTaskListHierarchyAndProgress("");
        Assert.Equal(0, empty.TotalTasks);
        Assert.Equal(0, empty.CompletedTasks);
        Assert.Equal(100.0, empty.HierarchyConsistencyScore);
        Assert.Equal("No Task Lists", empty.Category);
        Assert.Equal(empty.Summary, TextSearch.FormatTaskListHierarchySummary(""));

        string validDoc = string.Join("\n",
            "- [x] Feature Architecture Specification",
            "  - [x] Define Domain Models",
            "  - [x] Wire ViewModels",
            "- [ ] Quality Assurance & Production Release",
            "  - [ ] Execute Unit Test Suite",
            "  - [ ] Perform Desktop Smoke Verification"
        );
        var validAudit = TextSearch.AuditTaskListHierarchyAndProgress(validDoc);
        Assert.Equal(6, validAudit.TotalTasks);
        Assert.Equal(3, validAudit.CompletedTasks);
        Assert.Equal(2, validAudit.RootTasks);
        Assert.Equal(4, validAudit.NestedSubtasks);
        Assert.Equal(0, validAudit.InconsistentParentCount);
        Assert.Equal(50.0, validAudit.CompletionPercentage);
        Assert.Equal(100.0, validAudit.HierarchyConsistencyScore);
        Assert.Equal("Coherent Task Hierarchy (In-Progress)", validAudit.Category);

        string inconsistentDoc = string.Join("\n",
            "- [x] Inconsistent Parent Claimed Done",
            "  - [ ] Child task is still incomplete"
        );
        var incAudit = TextSearch.AuditTaskListHierarchyAndProgress(inconsistentDoc);
        Assert.Equal(2, incAudit.TotalTasks);
        Assert.Equal(1, incAudit.CompletedTasks);
        Assert.Equal(1, incAudit.InconsistentParentCount);
        Assert.True(incAudit.HierarchyConsistencyScore < 100.0);
        Assert.Contains("Discrepancy", incAudit.Category);

        string formatted = TextSearch.FormatTaskListHierarchySummary(
            validAudit.TotalTasks, validAudit.CompletedTasks, validAudit.RootTasks,
            validAudit.NestedSubtasks, validAudit.InconsistentParentCount,
            validAudit.CompletionPercentage, validAudit.HierarchyConsistencyScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);
    }

    [Fact]
    public void Run176_AuditTableColumnAlignmentSyntax_Comprehensive()
    {
        var empty = TextSearch.AuditTableColumnAlignmentSyntax("");
        Assert.Equal(0, empty.TotalTables);
        Assert.Equal(0, empty.TotalColumns);
        Assert.Equal(100.0, empty.AlignmentConsistencyScore);
        Assert.Equal("No Tables", empty.Category);
        Assert.Equal(empty.Summary, TextSearch.FormatTableColumnAlignmentSummary(""));

        string validDoc = string.Join("\n",
            "# Document with Table",
            "",
            "| Component | Alignment | Status |",
            "|:---|:---:|---:|",
            "| Engine | Center | 100% |",
            "| Pipeline | Balanced | Complete |",
            "",
            "```markdown",
            "| Fenced | Ignored |",
            "|---|---|",
            "```"
        );

        var validAudit = TextSearch.AuditTableColumnAlignmentSyntax(validDoc);
        Assert.Equal(1, validAudit.TotalTables);
        Assert.Equal(3, validAudit.TotalColumns);
        Assert.Equal(1, validAudit.LeftAlignedCount);
        Assert.Equal(1, validAudit.CenterAlignedCount);
        Assert.Equal(1, validAudit.RightAlignedCount);
        Assert.Equal(0, validAudit.DefaultAlignedCount);
        Assert.Equal(0, validAudit.RaggedRowCount);
        Assert.Equal(0, validAudit.MissingOuterPipeCount);
        Assert.Equal(100.0, validAudit.AlignmentConsistencyScore);
        Assert.Equal("Strict Standard GFM Delimiter Alignment", validAudit.Category);

        string raggedDoc = string.Join("\n",
            "| Name | Age | City |",
            "|---|---|---|",
            "| Alice | 30 |", // Ragged row (2 cells instead of 3)
            "| Bob | 25 | London |"
        );

        var raggedAudit = TextSearch.AuditTableColumnAlignmentSyntax(raggedDoc);
        Assert.Equal(1, raggedAudit.TotalTables);
        Assert.Equal(1, raggedAudit.RaggedRowCount);
        Assert.True(raggedAudit.AlignmentConsistencyScore < 100.0);
        Assert.Contains("Ragged", raggedAudit.Category);

        string formatted = TextSearch.FormatTableColumnAlignmentSummary(
            validAudit.TotalTables, validAudit.TotalColumns, validAudit.LeftAlignedCount,
            validAudit.CenterAlignedCount, validAudit.RightAlignedCount, validAudit.DefaultAlignedCount,
            validAudit.RaggedRowCount, validAudit.MissingOuterPipeCount, validAudit.AlignmentConsistencyScore,
            validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);
    }

    [Fact]
    public void Run177_AuditFootnoteReferencePairing_And_FormatFootnoteReferencePairingSummary()
    {
        var emptyAudit = TextSearch.AuditFootnoteReferencePairing("");
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.PairingIntegrityScore);
        Assert.Contains("0 footnotes", emptyAudit.Summary);

        string validDoc = string.Join("\n",
            "# Footnote Document",
            "This document references the first note[^1] and a named note[^alpha].",
            "```markdown",
            "Code fence demonstrating [^fenced-fake] which should be ignored.",
            "```",
            "[^1]: This is the first definition.",
            "[^alpha]: This is the named alpha definition."
        );

        var validAudit = TextSearch.AuditFootnoteReferencePairing(validDoc);
        Assert.Equal(2, validAudit.TotalReferences);
        Assert.Equal(2, validAudit.UniqueReferences);
        Assert.Equal(2, validAudit.TotalDefinitions);
        Assert.Equal(2, validAudit.UniqueDefinitions);
        Assert.Equal(0, validAudit.DanglingReferences);
        Assert.Equal(0, validAudit.OrphanDefinitions);
        Assert.Equal(0, validAudit.DuplicateDefinitions);
        Assert.Equal(2, validAudit.MatchedPairs);
        Assert.Equal(100.0, validAudit.PairingIntegrityScore);
        Assert.Equal("Complete Bi-Directional Footnote Pairing", validAudit.Category);

        string inconsistentDoc = string.Join("\n",
            "Here is an unresolved reference[^dangling].",
            "Here is a reference with duplicate definitions[^multi].",
            "[^multi]: Definition 1.",
            "[^multi]: Definition 2.",
            "[^orphan]: Orphaned definition with no references."
        );

        var inconsistentAudit = TextSearch.AuditFootnoteReferencePairing(inconsistentDoc);
        Assert.Equal(2, inconsistentAudit.TotalReferences);
        Assert.Equal(3, inconsistentAudit.TotalDefinitions);
        Assert.Equal(1, inconsistentAudit.DanglingReferences);
        Assert.Equal(1, inconsistentAudit.OrphanDefinitions);
        Assert.Equal(1, inconsistentAudit.DuplicateDefinitions);
        Assert.Equal(1, inconsistentAudit.MatchedPairs);
        Assert.True(inconsistentAudit.PairingIntegrityScore < 100.0);
        Assert.Contains("Mixed", inconsistentAudit.Category);

        string formatted = TextSearch.FormatFootnoteReferencePairingSummary(
            validAudit.TotalReferences, validAudit.UniqueReferences, validAudit.TotalDefinitions, validAudit.UniqueDefinitions,
            validAudit.DanglingReferences, validAudit.OrphanDefinitions, validAudit.DuplicateDefinitions, validAudit.MatchedPairs,
            validAudit.PairingIntegrityScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);
    }

    [Fact]
    public void Run178_AuditDocumentImageAltTextAndAccessibility()
    {
        var emptyAudit = TextSearch.AuditDocumentImageAltTextAndAccessibility("");
        Assert.Equal(0, emptyAudit.TotalImages);
        Assert.Equal(100.0, emptyAudit.AccessibilityComplianceScore);
        Assert.Contains("0 images", emptyAudit.Summary);

        string validDoc = string.Join("\n",
            "# Accessibility Guide",
            "![System architecture diagram illustrating API endpoints](diagram.png)",
            "```markdown",
            "![code fence demo image](code.png)",
            "```",
            "<img src=\"chart.svg\" alt=\"Quarterly financial revenue bar chart showing 15 percent growth\" />"
        );

        var validAudit = TextSearch.AuditDocumentImageAltTextAndAccessibility(validDoc);
        Assert.Equal(2, validAudit.TotalImages);
        Assert.Equal(2, validAudit.DescriptiveCount);
        Assert.Equal(0, validAudit.EmptyCount);
        Assert.Equal(0, validAudit.GenericPlaceholderCount);
        Assert.Equal(100.0, validAudit.AccessibilityComplianceScore);
        Assert.Equal("Full WCAG 2.1 Accessibility Compliance", validAudit.Category);

        string flawedDoc = string.Join("\n",
            "![](missing.png)",
            "![screenshot](screenshot.png)",
            "![image of user profile avatar badge](user.jpg)"
        );

        var flawedAudit = TextSearch.AuditDocumentImageAltTextAndAccessibility(flawedDoc);
        Assert.Equal(3, flawedAudit.TotalImages);
        Assert.Equal(1, flawedAudit.EmptyCount);
        Assert.Equal(1, flawedAudit.GenericPlaceholderCount);
        Assert.Equal(1, flawedAudit.RedundantPrefixCount);
        Assert.True(flawedAudit.AccessibilityComplianceScore < 60.0);

        string formatted = TextSearch.FormatImageAltTextAccessibilitySummary(
            validAudit.TotalImages, validAudit.DescriptiveCount, validAudit.EmptyCount,
            validAudit.GenericPlaceholderCount, validAudit.RedundantPrefixCount,
            validAudit.OverlyLongCount, validAudit.DataUriCount,
            validAudit.AccessibilityComplianceScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);
    }

    [Fact]
    public void Run179_AuditStrikethroughAndHighlightFormatting()
    {
        var emptyAudit = TextSearch.AuditStrikethroughAndHighlightFormatting("");
        Assert.Equal(0, emptyAudit.TotalSpans);
        Assert.Equal(100.0, emptyAudit.FormattingBalanceScore);
        Assert.Contains("0 styled spans", emptyAudit.Summary);

        string validDoc = string.Join("\n",
            "# Styling Guide",
            "This text has ~~deleted content~~ and ==highlighted notes==.",
            "```markdown",
            "~~inside code fence is ignored~~",
            "```",
            "Inline code `~~code~~` is also ignored.",
            "Nested styles: ~~==both strikethrough and highlight==~~."
        );

        var validAudit = TextSearch.AuditStrikethroughAndHighlightFormatting(validDoc);
        Assert.Equal(4, validAudit.TotalSpans);
        Assert.Equal(2, validAudit.StrikethroughCount);
        Assert.Equal(2, validAudit.HighlightCount);
        Assert.Equal(4, validAudit.BalancedSpanCount);
        Assert.Equal(0, validAudit.UnclosedSpanCount);
        Assert.Equal(0, validAudit.WhitespaceErrorCount);
        Assert.Equal(100.0, validAudit.FormattingBalanceScore);
        Assert.Equal("Flawless Inline Styling Balance", validAudit.Category);

        string flawedDoc = string.Join("\n",
            "This has ~~unclosed strikethrough",
            "This has ~~ leading whitespace~~ error",
            "This has == trailing whitespace ==",
            "Empty strikethrough ~~~~ here"
        );

        var flawedAudit = TextSearch.AuditStrikethroughAndHighlightFormatting(flawedDoc);
        Assert.Equal(4, flawedAudit.TotalSpans);
        Assert.Equal(1, flawedAudit.UnclosedSpanCount);
        Assert.Equal(2, flawedAudit.WhitespaceErrorCount);
        Assert.Equal(1, flawedAudit.EmptySpanCount);
        Assert.Equal(0, flawedAudit.BalancedSpanCount);
        Assert.Equal(0.0, flawedAudit.FormattingBalanceScore);

        string formatted = TextSearch.FormatStrikethroughHighlightSummary(
            validAudit.TotalSpans, validAudit.StrikethroughCount, validAudit.HighlightCount,
            validAudit.BalancedSpanCount, validAudit.UnclosedSpanCount,
            validAudit.WhitespaceErrorCount, validAudit.EmptySpanCount,
            validAudit.FormattingBalanceScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);
    }

    [Fact]
    public void AuditBlockquoteNestingAndCallouts_CalculatesDepthAlertsAndErgonomicsCorrectly()
    {
        var emptyAudit = TextSearch.AuditBlockquoteNestingAndCallouts(null);
        Assert.Equal(0, emptyAudit.TotalQuoteLines);
        Assert.Equal(100.0, emptyAudit.ErgonomicsScore);
        Assert.Equal("Optimal", emptyAudit.Category);

        string doc = string.Join("\n",
            "# Document Title",
            "> [!NOTE]",
            "> This is a note alert.",
            ">",
            "> [!TIP]",
            "> Here is an actionable tip.",
            "> -- Famous Author",
            "",
            "Paragraph in between.",
            "",
            ">> Nested quote level 2",
            ">>> Nested quote level 3",
            ">>>> Nested quote level 4",
            "",
            "```markdown",
            "> [!WARNING] inside fence is ignored",
            "```"
        );

        var audit = TextSearch.AuditBlockquoteNestingAndCallouts(doc);
        Assert.True(audit.TotalQuoteLines >= 9);
        Assert.Equal(2, audit.TotalQuoteBlocks);
        Assert.Equal(4, audit.MaxNestingDepth);
        Assert.Equal(1, audit.NoteAlertCount);
        Assert.Equal(1, audit.TipAlertCount);
        Assert.Equal(1, audit.CitationCount);
        Assert.True(audit.NestedDeepCount >= 2);
        Assert.True(audit.ErgonomicsScore < 100.0);

        string summary = TextSearch.FormatBlockquoteNestingSummary(
            audit.TotalQuoteLines, audit.TotalQuoteBlocks, audit.MaxNestingDepth,
            audit.NoteAlertCount, audit.TipAlertCount, audit.ImportantAlertCount,
            audit.WarningAlertCount, audit.CautionAlertCount, audit.CitationCount,
            audit.NestedDeepCount, audit.ErgonomicsScore, audit.Category);
        Assert.Equal(audit.Summary, summary);
    }

    [Fact]
    public void Run181_AuditDefinitionListContinuityAndSpacing_EvaluatesSpacingAndIntegrity()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditDefinitionListContinuityAndSpacing(null);
        Assert.Equal(0, emptyAudit.TotalTerms);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.ContinuityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 terms", emptyAudit.Summary);

        // 2. Structured definition lists with terms and definitions
        var doc = string.Join("\n",
            "# Glossary",
            "",
            "Apple",
            ": A sweet pomaceous fruit.",
            ": Also a technology company.",
            "",
            "Banana",
            ": A long curved fruit with yellow skin.",
            "  Rich in potassium and vitamins.",
            "",
            "```markdown",
            "Ignored",
            ": Not a def list inside fence",
            "```"
        );

        var audit = TextSearch.AuditDefinitionListContinuityAndSpacing(doc);
        Assert.Equal(2, audit.TotalTerms);
        Assert.Equal(3, audit.TotalDefinitions);
        Assert.Equal(1, audit.MultilineDefinitions);
        Assert.True(audit.ContinuityScore >= 90.0);
        Assert.Equal("Optimal", audit.Category);

        string summary = TextSearch.FormatDefinitionListContinuitySummary(
            audit.TotalTerms, audit.TotalDefinitions, audit.OrphanTerms,
            audit.MultilineDefinitions, audit.SpacingIssues, audit.ContinuityScore, audit.Category);
        Assert.Equal(audit.Summary, summary);
    }

    [Fact]
    public void Run182_AuditFootnoteOrderAndOrphans_EvaluatesOrderingAndOrphans()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditFootnoteOrderAndOrphans(null);
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.IntegrityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 references", emptyAudit.Summary);

        // 2. Structured valid markdown with ordered numeric footnotes & multi-paragraph continuation
        var doc = string.Join("\n",
            "# Footnote Document",
            "",
            "This is the first statement[^1].",
            "This is the second statement[^2].",
            "",
            "[^1]: This is the first footnote definition.",
            "[^2]: This is the second footnote definition.",
            "",
            "    This is a second paragraph for footnote 2.",
            "",
            "```markdown",
            "Ignored code block with [^3] reference and [^3]: def inside fence",
            "```"
        );

        var audit = TextSearch.AuditFootnoteOrderAndOrphans(doc);
        Assert.Equal(2, audit.TotalReferences);
        Assert.Equal(2, audit.TotalDefinitions);
        Assert.Equal(0, audit.OrphanReferences);
        Assert.Equal(0, audit.UnusedDefinitions);
        Assert.Equal(0, audit.OutOfOrderReferences);
        Assert.Equal(1, audit.MultiParagraphDefinitions);
        Assert.True(audit.IntegrityScore >= 95.0);
        Assert.Equal("Optimal", audit.Category);

        string formatted = TextSearch.FormatFootnoteOrderSummary(
            audit.TotalReferences, audit.TotalDefinitions, audit.OrphanReferences,
            audit.UnusedDefinitions, audit.OutOfOrderReferences, audit.MultiParagraphDefinitions,
            audit.IntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Out-of-order references and orphans
        var disorderedDoc = string.Join("\n",
            "Second ref first[^2] and first ref second[^1]. Orphan ref[^9].",
            "",
            "[^1]: Definition 1",
            "[^2]: Definition 2",
            "[^unused]: Unused definition"
        );

        var disorderedAudit = TextSearch.AuditFootnoteOrderAndOrphans(disorderedDoc);
        Assert.Equal(3, disorderedAudit.TotalReferences);
        Assert.Equal(3, disorderedAudit.TotalDefinitions);
        Assert.Equal(1, disorderedAudit.OrphanReferences);
        Assert.Equal(1, disorderedAudit.UnusedDefinitions);
        Assert.Equal(1, disorderedAudit.OutOfOrderReferences);
        Assert.True(disorderedAudit.IntegrityScore < 100.0);
    }

    [Fact]
    public void Run183_AuditInlineMathAndTexSyntax_EvaluatesDelimitersAndSyntax()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditInlineMathAndTexSyntax(null);
        Assert.Equal(0, emptyAudit.TotalMathBlocks);
        Assert.Equal(100.0, emptyAudit.SyntaxIntegrityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 math blocks", emptyAudit.Summary);

        // 2. Structured valid markdown with inline and display math
        var doc = string.Join("\n",
            "# Physics Notes",
            "",
            "Einstein showed that $E = mc^2$ holds universally.",
            "",
            "$$\\frac{-b \\pm \\sqrt{b^2 - 4ac}}{2a}$$",
            "",
            "Here is bracket math \\[ \\int_0^\\infty e^{-x} dx = 1 \\] and inline paren \\( \\alpha + \\beta \\).",
            "",
            "```latex",
            "$Ignored inside code fence$ with unclosed { brace",
            "```"
        );

        var audit = TextSearch.AuditInlineMathAndTexSyntax(doc);
        Assert.True(audit.TotalMathBlocks >= 4);
        Assert.True(audit.InlineMathCount >= 2);
        Assert.True(audit.DisplayMathCount >= 2);
        Assert.Equal(0, audit.MalformedBlockCount);
        Assert.Equal(0, audit.UnclosedBraceCount);
        Assert.Equal(100.0, audit.SyntaxIntegrityScore);
        Assert.Equal("Optimal", audit.Category);

        string formatted = TextSearch.FormatInlineMathSummary(
            audit.TotalMathBlocks, audit.InlineMathCount, audit.DisplayMathCount,
            audit.MalformedBlockCount, audit.UnclosedBraceCount, audit.CurrencyCollisionCount,
            audit.SyntaxIntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Document with unclosed braces and stray delimiters
        var malformedDoc = string.Join("\n",
            "Malformed formula $\\frac{a}{b$ with missing brace.",
            "Stray dollar without math $ and price is $50 item."
        );

        var malformedAudit = TextSearch.AuditInlineMathAndTexSyntax(malformedDoc);
        Assert.True(malformedAudit.TotalMathBlocks >= 1);
        Assert.True(malformedAudit.UnclosedBraceCount >= 1 || malformedAudit.MalformedBlockCount >= 1);
        Assert.True(malformedAudit.SyntaxIntegrityScore < 100.0);
    }

    [Fact]
    public void Run184_AuditHeaderAnchorsAndTocSync_EvaluatesSlugsAndCollisions()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditHeaderAnchorsAndTocSync(null);
        Assert.Equal(0, emptyAudit.TotalHeaders);
        Assert.Equal(100.0, emptyAudit.AnchorSyncScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 headings", emptyAudit.Summary);

        // 2. Structured markdown document with headings and TOC links
        var doc = string.Join("\n",
            "# Getting Started",
            "",
            "## Table of Contents",
            "- [Getting Started](#getting-started)",
            "- [Installation](#installation)",
            "- [Configuration](#configuration)",
            "- [Advanced Usage](#advanced-usage)",
            "",
            "## Installation",
            "Run setup commands here.",
            "",
            "## Configuration",
            "Configure settings here.",
            "",
            "## Advanced Usage",
            "Deep dive into advanced topics.",
            "",
            "```markdown",
            "# Ignored Heading inside Code Fence",
            "```"
        );

        var audit = TextSearch.AuditHeaderAnchorsAndTocSync(doc);
        Assert.Equal(5, audit.TotalHeaders);
        Assert.Equal(5, audit.UniqueSlugs);
        Assert.Equal(0, audit.CollisionCount);
        Assert.Equal(4, audit.TocInternalLinksCount);
        Assert.Equal(0, audit.BrokenTocLinksCount);
        Assert.Equal(100.0, audit.AnchorSyncScore);
        Assert.Equal("Optimal Anchor & TOC Sync (≥90%)", audit.Category);

        string formatted = TextSearch.FormatHeaderAnchorSummary(
            audit.TotalHeaders, audit.UniqueSlugs, audit.CollisionCount,
            audit.TocInternalLinksCount, audit.BrokenTocLinksCount,
            audit.UnreferencedHeadersCount, audit.AnchorSyncScore,
            audit.Category, audit.BrokenTocSlugs);
        Assert.Equal(audit.Summary, formatted);

        // 3. Document with duplicate headings (collision) and broken TOC link
        var collisionDoc = string.Join("\n",
            "# Overview",
            "- [Overview](#overview)",
            "- [Broken Section](#non-existent-section)",
            "",
            "## Setup",
            "Initial setup.",
            "",
            "## Setup",
            "Duplicate setup section.",
            "",
            "## Setup {#custom-setup-id}",
            "Explicit custom ID setup."
        );

        var collisionAudit = TextSearch.AuditHeaderAnchorsAndTocSync(collisionDoc);
        Assert.True(collisionAudit.TotalHeaders >= 4);
        Assert.True(collisionAudit.CollisionCount >= 1);
        Assert.Equal(1, collisionAudit.BrokenTocLinksCount);
        Assert.True(collisionAudit.AnchorSyncScore < 100.0);
    }

    [Fact]
    public void Run185_AuditCalloutDepthAndSemantics_EvaluatesAlertsAndNesting()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditCalloutDepthAndSemantics(null);
        Assert.Equal(0, emptyAudit.TotalCallouts);
        Assert.Equal(100.0, emptyAudit.SemanticIntegrityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 alerts", emptyAudit.Summary);

        // 2. Structured document with valid, well-formed GitHub-style alerts
        var doc = string.Join("\n",
            "# Document Title",
            "",
            "> [!NOTE]",
            "> This is a helpful note with valid description content.",
            "",
            "Some intermediate paragraph text.",
            "",
            "> [!WARNING]",
            "> Critical warning alert with detailed instructions.",
            "",
            "```markdown",
            "> [!NOTE]",
            "> Ignored callout inside code block",
            "```"
        );

        var audit = TextSearch.AuditCalloutDepthAndSemantics(doc);
        Assert.Equal(2, audit.TotalCallouts);
        Assert.Equal(2, audit.ValidCallouts);
        Assert.Equal(0, audit.NestedCallouts);
        Assert.Equal(0, audit.ConsecutiveCallouts);
        Assert.Equal(0, audit.EmptyBodyCallouts);
        Assert.Equal(0, audit.UnknownTypeCallouts);
        Assert.Equal(1, audit.MaxQuoteDepth);
        Assert.Equal(100.0, audit.SemanticIntegrityScore);
        Assert.Equal("Optimal Callout Semantics (≥90%)", audit.Category);

        string formatted = TextSearch.FormatCalloutDepthSummary(
            audit.TotalCallouts, audit.ValidCallouts, audit.NestedCallouts,
            audit.ConsecutiveCallouts, audit.EmptyBodyCallouts, audit.UnknownTypeCallouts,
            audit.MaxQuoteDepth, audit.SemanticIntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Document with consecutive alerts, nested alerts, empty body, and unknown type
        var invalidDoc = string.Join("\n",
            ">> [!NOTE]",
            ">> Nested alert inside blockquote depth 2",
            "",
            "> [!UNKNOWNALERT]",
            "> Invalid alert type",
            "> [!TIP]",
            "",
            "> [!CAUTION]"
        );

        var invalidAudit = TextSearch.AuditCalloutDepthAndSemantics(invalidDoc);
        Assert.True(invalidAudit.TotalCallouts >= 3);
        Assert.True(invalidAudit.NestedCallouts >= 1);
        Assert.True(invalidAudit.ConsecutiveCallouts >= 1 || invalidAudit.EmptyBodyCallouts >= 1);
        Assert.True(invalidAudit.UnknownTypeCallouts >= 1);
        Assert.True(invalidAudit.SemanticIntegrityScore < 100.0);
    }

    [Fact]
    public void Run186_AuditImageAccessibilityAndCaptions_EvaluatesAltTextAndCaptions()
    {
        // 1. Empty / null document
        var emptyAudit = TextSearch.AuditImageAccessibilityAndCaptions("");
        Assert.Equal(0, emptyAudit.TotalImages);
        Assert.Equal(100.0, emptyAudit.AccessibilityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 images", emptyAudit.Summary);

        // 2. Clean accessible images
        var cleanDoc = string.Join("\n",
            "# Document Title",
            "![System Architecture Diagram](images/arch.png \"High-level system topology\")",
            "Some descriptive text.",
            "![User Authentication Flow](diagrams/auth.svg)",
            "```markdown",
            "![Ignored Image In Code Block](ignored.png)",
            "```"
        );

        var cleanAudit = TextSearch.AuditImageAccessibilityAndCaptions(cleanDoc);
        Assert.Equal(2, cleanAudit.TotalImages);
        Assert.Equal(2, cleanAudit.DescriptiveAltCount);
        Assert.Equal(0, cleanAudit.EmptyAltCount);
        Assert.Equal(0, cleanAudit.GenericFilenameAltCount);
        Assert.Equal(0, cleanAudit.OverlyVerboseAltCount);
        Assert.Equal(1, cleanAudit.CaptionedCount);
        Assert.Equal(100.0, cleanAudit.AccessibilityScore);
        Assert.Equal("Optimal Image Accessibility (≥90%)", cleanAudit.Category);

        string formatted = TextSearch.FormatImageCaptionAccessibilitySummary(
            cleanAudit.TotalImages, cleanAudit.DescriptiveAltCount, cleanAudit.EmptyAltCount,
            cleanAudit.GenericFilenameAltCount, cleanAudit.OverlyVerboseAltCount,
            cleanAudit.CaptionedCount, cleanAudit.AccessibilityScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Document with missing alt, generic placeholder, and overly verbose alt
        var verboseAlt = new string('x', 160);
        var problematicDoc = string.Join("\n",
            "![](/images/unlabelled.jpg)",
            "![screenshot.png](/images/screenshot.png)",
            $"![{verboseAlt}](/images/verbose.png)"
        );

        var probAudit = TextSearch.AuditImageAccessibilityAndCaptions(problematicDoc);
        Assert.Equal(3, probAudit.TotalImages);
        Assert.Equal(1, probAudit.EmptyAltCount);
        Assert.Equal(1, probAudit.GenericFilenameAltCount);
        Assert.Equal(1, probAudit.OverlyVerboseAltCount);
        Assert.True(probAudit.AccessibilityScore < 100.0);
    }

    [Fact]
    public void Run187_AuditFootnoteAndReferenceDefinitions_DetectsOrphansAndCycles()
    {
        // 1. Empty / null document
        var emptyAudit = TextSearch.AuditFootnoteAndReferenceDefinitions("");
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.IntegrityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 references", emptyAudit.Summary);

        // 2. Clean valid footnotes and reference links
        var cleanDoc = string.Join("\n",
            "# Document Title",
            "Here is a footnote reference[^1] and a reference link [Documentation][doc_ref].",
            "Also a shortcut reference [guide].",
            "",
            "[^1]: This is the verified footnote body.",
            "[doc_ref]: https://marksmith.dev/docs \"Documentation\"",
            "[guide]: https://marksmith.dev/guide",
            "```markdown",
            "Ignored [^99] in code block",
            "```"
        );

        var cleanAudit = TextSearch.AuditFootnoteAndReferenceDefinitions(cleanDoc);
        Assert.True(cleanAudit.TotalReferences >= 2);
        Assert.Equal(3, cleanAudit.TotalDefinitions);
        Assert.Equal(0, cleanAudit.OrphanReferencesCount);
        Assert.Equal(0, cleanAudit.DanglingDefinitionsCount);
        Assert.Equal(0, cleanAudit.DuplicateDefinitionsCount);
        Assert.Equal(0, cleanAudit.CyclicReferencesCount);
        Assert.Equal(100.0, cleanAudit.IntegrityScore);
        Assert.Equal("Optimal Footnote & Reference Governance (≥90%)", cleanAudit.Category);

        string formatted = TextSearch.FormatFootnoteAndReferenceDefinitionsSummary(
            cleanAudit.TotalReferences, cleanAudit.TotalDefinitions, cleanAudit.ValidPairsCount,
            cleanAudit.OrphanReferencesCount, cleanAudit.DanglingDefinitionsCount,
            cleanAudit.DuplicateDefinitionsCount, cleanAudit.CyclicReferencesCount,
            cleanAudit.IntegrityScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic document with orphaned reference, dangling definition, and cyclic loop
        var probDoc = string.Join("\n",
            "Orphan footnote reference [^missing_fn].",
            "Orphan link [unlinked_text][missing_link].",
            "",
            "[^dangling_fn]: Defined but never cited in body text.",
            "[loop_a]: [loop_b]",
            "[loop_b]: [loop_a]",
            "[dup_key]: https://alpha.com",
            "[dup_key]: https://beta.com"
        );

        var probAudit = TextSearch.AuditFootnoteAndReferenceDefinitions(probDoc);
        Assert.True(probAudit.OrphanReferencesCount >= 2);
        Assert.True(probAudit.DanglingDefinitionsCount >= 1);
        Assert.True(probAudit.DuplicateDefinitionsCount >= 1);
        Assert.True(probAudit.CyclicReferencesCount >= 1);
        Assert.True(probAudit.IntegrityScore < 80.0);
    }

    [Fact]
    public void Run188_AuditTableAlignmentAndDelimiters_ValidatesTableStructureAndParity()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditTableAlignmentAndDelimiters(null);
        Assert.Equal(0, emptyAudit.TotalTables);
        Assert.Equal(100.0, emptyAudit.StructureComplianceScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 tables", emptyAudit.Summary);

        // 2. Clean table with valid delimiter and outer pipes
        var cleanDoc = string.Join("\n",
            "# Document",
            "",
            "| Feature | Status | Alignment |",
            "| :--- | :---: | ---: |",
            "| Markdown | Complete | Optimal |",
            "| WinUI 3 | Verified | Polished |",
            "",
            "```markdown",
            "| Ignored | In Code |",
            "| --- | --- |",
            "```"
        );

        var cleanAudit = TextSearch.AuditTableAlignmentAndDelimiters(cleanDoc);
        Assert.Equal(1, cleanAudit.TotalTables);
        Assert.Equal(4, cleanAudit.TotalRows);
        Assert.Equal(3, cleanAudit.TotalColumns);
        Assert.Equal(0, cleanAudit.RaggedRowsCount);
        Assert.Equal(0, cleanAudit.DelimiterIssuesCount);
        Assert.Equal(0, cleanAudit.UnescapedPipesCount);
        Assert.Equal(0, cleanAudit.OuterPipesMissingCount);
        Assert.Equal(100.0, cleanAudit.StructureComplianceScore);
        Assert.Equal("Optimal Table Structure & Alignment (≥90%)", cleanAudit.Category);

        string formatted = TextSearch.FormatTableAlignmentSummary(
            cleanAudit.TotalTables, cleanAudit.TotalRows, cleanAudit.TotalColumns,
            cleanAudit.RaggedRowsCount, cleanAudit.DelimiterIssuesCount,
            cleanAudit.UnescapedPipesCount, cleanAudit.OuterPipesMissingCount,
            cleanAudit.StructureComplianceScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic table with ragged rows and invalid delimiter
        var probDoc = string.Join("\n",
            "| Header 1 | Header 2 | Header 3 |",
            "| --- | invalid_delim | --- |",
            "| Only One Cell |",
            "| Cell 1 | Cell 2 | Cell 3 | Cell 4 Extra |"
        );

        var probAudit = TextSearch.AuditTableAlignmentAndDelimiters(probDoc);
        Assert.Equal(1, probAudit.TotalTables);
        Assert.True(probAudit.RaggedRowsCount >= 2);
        Assert.True(probAudit.DelimiterIssuesCount >= 1);
        Assert.True(probAudit.StructureComplianceScore < 75.0);
    }

    [Fact]
    public void Run189_AuditTaskListPriorityAndDueDates_ValidatesPrioritiesAndDeadlines()
    {
        var refDate = new DateTime(2026, 10, 20);

        // 1. Empty document
        var emptyAudit = TextSearch.AuditTaskListPriorityAndDueDates(null, refDate);
        Assert.Equal(0, emptyAudit.TotalTasks);
        Assert.Equal(100.0, emptyAudit.TrackingRigorScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 tasks", emptyAudit.Summary);

        // 2. Clean document with priorities, future deadlines, and completed items
        var cleanDoc = string.Join("\n",
            "# Sprint Tasks",
            "- [x] Fix WinUI 3 layout styling [p0] @due(2026-10-19)",
            "- [ ] Implement matrix symmetry auditor [p1] @due(2026-10-25)",
            "- [ ] Update documentation [p2] due:2026-10-30",
            "- [x] Add unit test suite 📅 2026-10-18",
            "",
            "```markdown",
            "- [ ] Ignored task in code block",
            "```"
        );

        var cleanAudit = TextSearch.AuditTaskListPriorityAndDueDates(cleanDoc, refDate);
        Assert.Equal(4, cleanAudit.TotalTasks);
        Assert.Equal(2, cleanAudit.CompletedTasks);
        Assert.Equal(2, cleanAudit.PendingTasks);
        Assert.Equal(1, cleanAudit.HighPriorityCount);
        Assert.Equal(1, cleanAudit.MediumPriorityCount);
        Assert.Equal(1, cleanAudit.LowPriorityCount);
        Assert.Equal(0, cleanAudit.OverdueCount); // completed past task is not overdue
        Assert.Equal(4, cleanAudit.TaggedTasksCount);
        Assert.Equal(50.0, cleanAudit.CompletionPercentage);
        Assert.Equal(100.0, cleanAudit.TrackingRigorScore);
        Assert.Equal("Optimal Task Tracking & Deadline Rigor (≥90%)", cleanAudit.Category);

        string formatted = TextSearch.FormatTaskListPrioritySummary(
            cleanAudit.TotalTasks, cleanAudit.CompletedTasks, cleanAudit.PendingTasks,
            cleanAudit.HighPriorityCount, cleanAudit.OverdueCount, cleanAudit.CompletionPercentage,
            cleanAudit.TrackingRigorScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic document with overdue tasks
        var probDoc = string.Join("\n",
            "# Overdue Backlog",
            "- [ ] Urgent server patch [high] @due(2026-10-10)", // overdue
            "- [ ] Critical database migration #urgent @due(2026-10-15)", // overdue
            "- [ ] Untracked loose task",
            "- [ ] Another untracked task"
        );

        var probAudit = TextSearch.AuditTaskListPriorityAndDueDates(probDoc, refDate);
        Assert.Equal(4, probAudit.TotalTasks);
        Assert.Equal(0, probAudit.CompletedTasks);
        Assert.Equal(2, probAudit.OverdueCount);
        Assert.True(probAudit.TrackingRigorScore < 75.0);
    }

    [Fact]
    public void Run190_AuditBlockquoteCalloutsAndAttribution_ValidatesNestingAndAlerts()
    {
        // 1. Empty document
        var emptyAudit = TextSearch.AuditBlockquoteCalloutsAndAttribution(null);
        Assert.Equal(0, emptyAudit.TotalQuoteLines);
        Assert.Equal(100.0, emptyAudit.StructuralRigorScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 quote lines", emptyAudit.Summary);

        // 2. Clean document with standard alerts, nested quote, and attribution footer
        var cleanDoc = string.Join("\n",
            "# Document with Callouts",
            "> [!NOTE]",
            "> This is a verified informational note for the user.",
            "",
            "> [!TIP]",
            "> Use keyboard accelerators for faster editing.",
            "",
            ">> Second level quote providing further context.",
            "> -- Donald Knuth",
            "",
            "```markdown",
            "> Ignored blockquote inside code fence",
            "```"
        );

        var cleanAudit = TextSearch.AuditBlockquoteCalloutsAndAttribution(cleanDoc);
        Assert.Equal(6, cleanAudit.TotalQuoteLines);
        Assert.Equal(3, cleanAudit.TotalQuoteBlocks);
        Assert.Equal(2, cleanAudit.MaxNestingDepth);
        Assert.Equal(2, cleanAudit.StandardAlertCount);
        Assert.Equal(0, cleanAudit.CustomAlertCount);
        Assert.Equal(0, cleanAudit.ExcessiveNestingCount);
        Assert.Equal(0, cleanAudit.EmptyQuoteCount);
        Assert.Equal(1, cleanAudit.AttributionCount);
        Assert.Equal(100.0, cleanAudit.StructuralRigorScore);
        Assert.Equal("Optimal Blockquote & Callout Governance (≥90%)", cleanAudit.Category);

        string formatted = TextSearch.FormatBlockquoteCalloutSummary(
            cleanAudit.TotalQuoteLines, cleanAudit.TotalQuoteBlocks, cleanAudit.MaxNestingDepth,
            cleanAudit.StandardAlertCount, cleanAudit.CustomAlertCount, cleanAudit.ExcessiveNestingCount,
            cleanAudit.EmptyQuoteCount, cleanAudit.AttributionCount, cleanAudit.StructuralRigorScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Problematic document with excessive nesting (>3) and empty quotes
        var probDoc = string.Join("\n",
            ">>>> [!CUSTOM_ALERT]",
            ">>>> Level 4 excessive nesting depth.",
            ">",
            ">"
        );

        var probAudit = TextSearch.AuditBlockquoteCalloutsAndAttribution(probDoc);
        Assert.Equal(4, probAudit.TotalQuoteLines);
        Assert.Equal(4, probAudit.MaxNestingDepth);
        Assert.Equal(1, probAudit.CustomAlertCount);
        Assert.True(probAudit.ExcessiveNestingCount >= 1);
        Assert.True(probAudit.EmptyQuoteCount >= 2);
        Assert.True(probAudit.StructuralRigorScore < 75.0);
    }

    [Fact]
    public void Run191_AuditHtmlEntitiesAndEscapedCharacters_ValidatesEntitiesAndAmbiguities()
    {
        // 1. Empty / null input
        var emptyAudit = TextSearch.AuditHtmlEntitiesAndEscapedCharacters(null);
        Assert.Equal(0, emptyAudit.TotalEntities);
        Assert.Equal(100.0, emptyAudit.HygieneScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);
        Assert.Contains("0 entities", emptyAudit.Summary);

        // 2. Document with valid named/numeric entities and code blocks
        var validDoc = string.Join("\n",
            "# Entities and Unicode",
            "This document has &nbsp; spaces, &amp; ampersands, and &copy; copyright notices.",
            "Numeric entity: &#123; and hex &#x7B; bracket symbols.",
            "",
            "Inline code with generics should be ignored: `List<string>` and `Dictionary<int, string>`.",
            "",
            "```csharp",
            "var brokenInFence = &amp without semicolon is ignored;",
            "int a = x < y ? 1 : 2;",
            "```"
        );

        var validAudit = TextSearch.AuditHtmlEntitiesAndEscapedCharacters(validDoc);
        Assert.Equal(5, validAudit.TotalEntities);
        Assert.Equal(3, validAudit.NamedEntityCount);
        Assert.Equal(2, validAudit.NumericEntityCount);
        Assert.Equal(0, validAudit.BrokenEntityCount);
        Assert.Equal(0, validAudit.AmbiguousEscapeCount);
        Assert.Equal(0, validAudit.UnescapedBracketCount);
        Assert.Equal(100.0, validAudit.HygieneScore);
        Assert.Equal("Optimal Entity & Character Encoding Governance (≥90%)", validAudit.Category);

        string formatted = TextSearch.FormatHtmlEntitiesSummary(
            validAudit.TotalEntities, validAudit.NamedEntityCount, validAudit.NumericEntityCount,
            validAudit.BrokenEntityCount, validAudit.AmbiguousEscapeCount, validAudit.UnescapedBracketCount,
            validAudit.HygieneScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);

        // 3. Document with broken entities missing semicolon, ambiguous escapes, and raw unescaped angle brackets
        var problemDoc = string.Join("\n",
            "Here is broken entity &amp without semicolon.",
            "Another broken entity &copy in text.",
            "Ambiguous regex escape \\w and \\d in markdown.",
            "Unescaped bracket: if x < y then y > x."
        );

        var problemAudit = TextSearch.AuditHtmlEntitiesAndEscapedCharacters(problemDoc);
        Assert.True(problemAudit.BrokenEntityCount >= 2);
        Assert.True(problemAudit.AmbiguousEscapeCount >= 2);
        Assert.True(problemAudit.UnescapedBracketCount >= 2);
        Assert.True(problemAudit.HygieneScore < 75.0);
    }

    [Fact]
    public void Run192_AuditFootnoteReferencesAndDefinitions_ValidatesPairingAndNumberingSequence()
    {
        // 1. Empty / null document
        var emptyAudit = TextSearch.AuditFootnoteReferencesAndDefinitions(null);
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(100.0, emptyAudit.StructuralIntegrityScore);
        Assert.Equal("Empty Document", emptyAudit.Category);
        Assert.Contains("0 footnote references", emptyAudit.Summary);

        // 2. Document with valid sequential footnotes and definitions
        var validDoc = string.Join("\n",
            "# Footnote Analysis",
            "Here is the first reference[^1] in the introduction.",
            "Here is the second reference[^2] and named footnote[^note].",
            "",
            "Code fence with bracket should be ignored:",
            "```markdown",
            "Inside code fence [^99] ignored",
            "```",
            "",
            "[^1]: Definition of footnote one.",
            "[^2]: Definition of footnote two.",
            "[^note]: Named note definition."
        );

        var validAudit = TextSearch.AuditFootnoteReferencesAndDefinitions(validDoc);
        Assert.Equal(3, validAudit.TotalReferences);
        Assert.Equal(3, validAudit.TotalDefinitions);
        Assert.Equal(3, validAudit.MatchedPairs);
        Assert.Equal(0, validAudit.OrphanedReferences);
        Assert.Equal(0, validAudit.OrphanedDefinitions);
        Assert.Equal(0, validAudit.SequentialMismatches);
        Assert.Equal(100.0, validAudit.StructuralIntegrityScore);
        Assert.Equal("Optimal Footnote Sequence & Definition Governance (≥90%)", validAudit.Category);

        string formatted = TextSearch.FormatFootnoteReferencesSummary(
            validAudit.TotalReferences, validAudit.TotalDefinitions, validAudit.MatchedPairs,
            validAudit.OrphanedReferences, validAudit.OrphanedDefinitions, validAudit.SequentialMismatches,
            validAudit.StructuralIntegrityScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);

        // 3. Problematic document: orphaned reference, orphaned definition, out-of-order numeric sequence
        var probDoc = string.Join("\n",
            "First ref is out of order [^3].",
            "Second ref is missing definition [^orphan].",
            "",
            "[^3]: Definition for 3.",
            "[^unreferenced]: Definition with no anchor."
        );

        var probAudit = TextSearch.AuditFootnoteReferencesAndDefinitions(probDoc);
        Assert.Equal(2, probAudit.TotalReferences);
        Assert.Equal(2, probAudit.TotalDefinitions);
        Assert.True(probAudit.OrphanedReferences >= 1);
        Assert.True(probAudit.OrphanedDefinitions >= 1);
        Assert.True(probAudit.SequentialMismatches >= 1);
        Assert.True(probAudit.StructuralIntegrityScore < 75.0);
    }

    [Fact]
    public void Run193_AuditListNestingAndMarkerSequence_ValidatesIndentationAndSequence()
    {
        // 1. Empty / null document
        var emptyAudit = TextSearch.AuditListNestingAndMarkerSequence(null);
        Assert.Equal(0, emptyAudit.TotalListItems);
        Assert.Equal(100.0, emptyAudit.ConsistencyScore);
        Assert.Equal("Empty Document", emptyAudit.Category);
        Assert.Contains("0 list items", emptyAudit.Summary);

        // 2. Document with valid consistent lists
        var validDoc = string.Join("\n",
            "# Document with Lists",
            "",
            "Unordered list:",
            "- Item 1",
            "- Item 2",
            "  - Sub item 2.1",
            "  - Sub item 2.2",
            "",
            "Ordered list:",
            "1. First step",
            "2. Second step",
            "3. Third step"
        );

        var validAudit = TextSearch.AuditListNestingAndMarkerSequence(validDoc);
        Assert.Equal(7, validAudit.TotalListItems);
        Assert.Equal(4, validAudit.UnorderedCount);
        Assert.Equal(3, validAudit.OrderedCount);
        Assert.Equal(0, validAudit.IndentationAnomalyCount);
        Assert.Equal(0, validAudit.MarkerInconsistencyCount);
        Assert.Equal(0, validAudit.SequenceMismatchCount);
        Assert.Equal(100.0, validAudit.ConsistencyScore);
        Assert.Equal("Optimal List Structure & Marker Hierarchy (≥90%)", validAudit.Category);

        string formatted = TextSearch.FormatListNestingSummary(
            validAudit.TotalListItems, validAudit.UnorderedCount, validAudit.OrderedCount,
            validAudit.MaxNestingDepth, validAudit.IndentationAnomalyCount,
            validAudit.MarkerInconsistencyCount, validAudit.SequenceMismatchCount,
            validAudit.ConsistencyScore, validAudit.Category);
        Assert.Equal(validAudit.Summary, formatted);

        // 3. Problematic document: odd indent, mixed markers, sequence jump
        var probDoc = string.Join("\n",
            "- Item 1",
            "* Item 2 mixed marker",
            "   - Odd indent 3 spaces",
            "",
            "1. Step 1",
            "3. Step 3 (jump from 1)"
        );

        var probAudit = TextSearch.AuditListNestingAndMarkerSequence(probDoc);
        Assert.Equal(5, probAudit.TotalListItems);
        Assert.True(probAudit.MarkerInconsistencyCount >= 1);
        Assert.True(probAudit.IndentationAnomalyCount >= 1);
        Assert.True(probAudit.SequenceMismatchCount >= 1);
        Assert.True(probAudit.ConsistencyScore < 80.0);
    }

    [Fact]
    public void AuditMarkdownInlineHtmlSecurity_DetectsDangerousTagsAndAttributes_ComputesHygieneScore()
    {
        // 1. Empty/null document
        var emptyAudit = TextSearch.AuditMarkdownInlineHtmlSecurity(null);
        Assert.Equal(0, emptyAudit.TotalHtmlTags);
        Assert.Equal(100.0, emptyAudit.SecurityScore);
        Assert.Equal("Clean Document", emptyAudit.Category);

        // 2. Safe document with code blocks and safe tags
        var safeDoc = string.Join("\n",
            "# Safe Document",
            "",
            "Here is a paragraph with <span class=\"highlight\">safe HTML</span> and a break <br/>.",
            "",
            "```html",
            "<script>alert('xss in code block');</script>",
            "```"
        );

        var safeAudit = TextSearch.AuditMarkdownInlineHtmlSecurity(safeDoc);
        Assert.True(safeAudit.TotalHtmlTags >= 2);
        Assert.Equal(0, safeAudit.DangerousTagCount);
        Assert.Equal(0, safeAudit.DangerousAttributeCount);
        Assert.True(safeAudit.SecurityScore >= 90.0);

        string formatted = TextSearch.FormatInlineHtmlSecuritySummary(
            safeAudit.TotalHtmlTags, safeAudit.DangerousTagCount, safeAudit.DangerousAttributeCount,
            safeAudit.SafeTagCount, safeAudit.UnclosedTagCount, safeAudit.SecurityScore, safeAudit.Category);
        Assert.Equal(safeAudit.Summary, formatted);

        // 3. Dangerous document with <script>, <iframe>, and onload=
        var dangerousDoc = string.Join("\n",
            "# Dangerous Document",
            "<script>evil();</script>",
            "<iframe src=\"javascript:alert(1)\"></iframe>",
            "<img src=\"x.png\" onload=\"steal()\" />"
        );

        var dangerAudit = TextSearch.AuditMarkdownInlineHtmlSecurity(dangerousDoc);
        Assert.True(dangerAudit.DangerousTagCount >= 2);
        Assert.True(dangerAudit.DangerousAttributeCount >= 2);
        Assert.True(dangerAudit.SecurityScore < 40.0);
        Assert.Contains(dangerAudit.Items, i => i.TagName == "script" && i.IsDangerousTag);
        Assert.Contains(dangerAudit.Items, i => i.TagName == "iframe" && i.IsDangerousTag);
    }
    [Fact]
    public void AuditMarkdownTaskListProgression_CalculatesMetricsAndNesting_ComputesHygieneScore()
    {
        // 1. Empty text
        var emptyAudit = TextSearch.AuditMarkdownTaskListProgression("");
        Assert.Equal(0, emptyAudit.TotalTasks);
        Assert.Equal(100.0, emptyAudit.HygieneScore);
        Assert.Equal("No Task Lists", emptyAudit.Category);

        // 2. Clean task list with parent and sub-tasks
        var doc = string.Join("\n",
            "# Project Tasks",
            "- [x] Design WinUI 3 desktop architecture",
            "  - [x] Wire MenuFlyoutItem",
            "  - [ ] Implement ViewModel audit logic",
            "- [ ] Run empirical test suite verification",
            "```markdown",
            "- [ ] Code block checkbox should be ignored",
            "```"
        );

        var audit = TextSearch.AuditMarkdownTaskListProgression(doc);
        Assert.Equal(4, audit.TotalTasks);
        Assert.Equal(2, audit.CompletedTasks);
        Assert.Equal(2, audit.PendingTasks);
        Assert.Equal(0, audit.EmptyTaskCount);
        Assert.Equal(1, audit.MaxNestingDepth);
        Assert.Equal(0.5, audit.CompletionRatio);
        Assert.Equal(100.0, audit.HygieneScore);

        string formatted = TextSearch.FormatTaskListProgressionSummary(
            audit.TotalTasks, audit.CompletedTasks, audit.PendingTasks,
            audit.EmptyTaskCount, audit.MaxNestingDepth, audit.HygieneScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Unlabeled task and indentation jump
        var badDoc = string.Join("\n",
            "- [ ]",
            "        - [x] Indented too far"
        );
        var badAudit = TextSearch.AuditMarkdownTaskListProgression(badDoc);
        Assert.Equal(2, badAudit.TotalTasks);
        Assert.Equal(1, badAudit.EmptyTaskCount);
        Assert.True(badAudit.HygieneScore < 80.0);
    }

    [Fact]
    public void Run196_AuditMarkdownLinkReferenceDefinitions_ResolvesFullCollapsedShortcutAndFlagsOrphans()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownLinkReferenceDefinitions("");
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(0, emptyAudit.TotalUsages);
        Assert.Equal(100.0, emptyAudit.ResolutionFidelityScore);
        Assert.Equal("No Reference Links", emptyAudit.Category);

        // 2. Clean doc with full, collapsed, and shortcut reference links
        var doc = string.Join("\n",
            "# Reference Links Overview",
            "See the [documentation manual][docs] and [API Reference][] for details.",
            "For guidelines, inspect [rules].",
            "",
            "[docs]: https://example.com/docs \"Documentation Manual\"",
            "[api reference]: https://example.com/api",
            "[rules]: https://example.com/rules 'Engineering Rules'"
        );

        var audit = TextSearch.AuditMarkdownLinkReferenceDefinitions(doc);
        Assert.Equal(3, audit.TotalDefinitions);
        Assert.Equal(3, audit.TotalUsages);
        Assert.Equal(3, audit.ResolvedCount);
        Assert.Equal(0, audit.UnresolvedUsageCount);
        Assert.Equal(0, audit.UnreferencedDefinitionCount);
        Assert.Equal(0, audit.DuplicateDefinitionCount);
        Assert.Equal(100.0, audit.ResolutionFidelityScore);

        string formatted = TextSearch.FormatLinkReferenceSummary(
            audit.TotalDefinitions, audit.TotalUsages, audit.ResolvedCount,
            audit.UnresolvedUsageCount, audit.UnreferencedDefinitionCount,
            audit.DuplicateDefinitionCount, audit.ResolutionFidelityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Broken reference and unused definition
        var brokenDoc = string.Join("\n",
            "[broken link][missing_target]",
            "[unused_ref]: https://unused.org"
        );

        var brokenAudit = TextSearch.AuditMarkdownLinkReferenceDefinitions(brokenDoc);
        Assert.Equal(1, brokenAudit.TotalDefinitions);
        Assert.Equal(1, brokenAudit.TotalUsages);
        Assert.Equal(0, brokenAudit.ResolvedCount);
        Assert.Equal(1, brokenAudit.UnresolvedUsageCount);
        Assert.Equal(1, brokenAudit.UnreferencedDefinitionCount);
        Assert.True(brokenAudit.ResolutionFidelityScore < 80.0);
    }

    [Fact]
    public void Run197_AuditMarkdownFootnoteDefinitions_ValidatesPairingAndFlagsOrphans()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownFootnoteDefinitions("");
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(0, emptyAudit.TotalCallouts);
        Assert.Equal(100.0, emptyAudit.StructuralIntegrityScore);
        Assert.Equal("No Footnotes", emptyAudit.Category);

        // 2. Clean doc with paired footnotes
        var doc = string.Join("\n",
            "# Document With Footnotes",
            "Here is an initial point[^1] and a secondary note[^2].",
            "Also a named reference[^note-alpha].",
            "",
            "[^1]: First detailed reference content.",
            "[^2]: Second detailed reference content.",
            "[^note-alpha]: Named footnote explanation."
        );

        var audit = TextSearch.AuditMarkdownFootnoteDefinitions(doc);
        Assert.Equal(3, audit.TotalDefinitions);
        Assert.Equal(3, audit.TotalCallouts);
        Assert.Equal(3, audit.ResolvedCount);
        Assert.Equal(0, audit.UnresolvedCalloutCount);
        Assert.Equal(0, audit.OrphanDefinitionCount);
        Assert.Equal(0, audit.DuplicateDefinitionCount);
        Assert.True(audit.IsMonotonicNumeric);
        Assert.Equal(100.0, audit.StructuralIntegrityScore);

        string formatted = TextSearch.FormatFootnoteDefinitionSummary(
            audit.TotalDefinitions, audit.TotalCallouts, audit.ResolvedCount,
            audit.UnresolvedCalloutCount, audit.OrphanDefinitionCount,
            audit.DuplicateDefinitionCount, audit.StructuralIntegrityScore, audit.Category);
        Assert.Equal(audit.Summary, formatted);

        // 3. Broken callout and orphan definition
        var brokenDoc = string.Join("\n",
            "An unlinked callout[^unresolved_marker].",
            "[^unused_footnote]: Standalone unreferenced footnote text."
        );

        var brokenAudit = TextSearch.AuditMarkdownFootnoteDefinitions(brokenDoc);
        Assert.Equal(1, brokenAudit.TotalDefinitions);
        Assert.Equal(1, brokenAudit.TotalCallouts);
        Assert.Equal(0, brokenAudit.ResolvedCount);
        Assert.Equal(1, brokenAudit.UnresolvedCalloutCount);
        Assert.Equal(1, brokenAudit.OrphanDefinitionCount);
        Assert.True(brokenAudit.StructuralIntegrityScore < 80.0);
    }

    [Fact]
    public void Run198_AuditMarkdownMathDelimiters_ValidatesBalancingAndEscapedDollars()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownMathDelimiters("");
        Assert.Equal(0, emptyAudit.TotalMathExpressions);
        Assert.Equal(100.0, emptyAudit.MathRenderingFidelityScore);
        Assert.Equal("No Math Detected", emptyAudit.Category);

        // 2. Clean markdown with inline math, display math, and escaped currency
        var cleanDoc = string.Join("\n",
            "# Physics Notes",
            "Energy equation: $E = mc^2$ in relativity.",
            @"Display formula: $$\int_0^\infty e^{-x} dx = 1$$",
            "The cost of the book is \\$50 or \\$75."
        );

        var cleanAudit = TextSearch.AuditMarkdownMathDelimiters(cleanDoc);
        Assert.True(cleanAudit.TotalMathExpressions >= 2);
        Assert.Equal(0, cleanAudit.UnclosedDelimiterCount);
        Assert.Equal(0, cleanAudit.UnbalancedBraceCount);
        Assert.Equal(0, cleanAudit.UnescapedCurrencyCount);
        Assert.Equal(100.0, cleanAudit.MathRenderingFidelityScore);

        string formatted = TextSearch.FormatMathDelimiterFidelitySummary(
            cleanAudit.TotalMathExpressions, cleanAudit.InlineMathCount, cleanAudit.DisplayMathCount,
            cleanAudit.ValidMathCount, cleanAudit.UnclosedDelimiterCount,
            cleanAudit.UnescapedCurrencyCount, cleanAudit.UnbalancedBraceCount,
            cleanAudit.MathRenderingFidelityScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Broken doc with unclosed delimiter, unescaped currency and unbalanced braces
        var brokenDoc = string.Join("\n",
            "Broken formula: $x + y with no closing marker",
            "Price is $99.99 right here.",
            "Unbalanced braces in KaTeX: $\\frac{a}{b$"
        );

        var brokenAudit = TextSearch.AuditMarkdownMathDelimiters(brokenDoc);
        Assert.True(brokenAudit.UnclosedDelimiterCount > 0 || brokenAudit.UnescapedCurrencyCount > 0 || brokenAudit.UnbalancedBraceCount > 0);
        Assert.True(brokenAudit.MathRenderingFidelityScore < 90.0);
    }

    [Fact]
    public void Run199_AuditMarkdownHtmlEntities_ValidatesEntitiesAndBrokenEscapes()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownHtmlEntities("");
        Assert.Equal(0, emptyAudit.TotalEntities);
        Assert.Equal(100.0, emptyAudit.HygieneScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);

        // 2. Clean doc with valid entities
        var cleanDoc = string.Join("\n",
            "# Guide &amp; Overview",
            "This document costs &pound;20 &copy; 2026.",
            "Water is H&#x2082;O."
        );
        var cleanAudit = TextSearch.AuditMarkdownHtmlEntities(cleanDoc);
        Assert.True(cleanAudit.TotalEntities >= 2);
        Assert.Equal(0, cleanAudit.BrokenEntityCount);
        Assert.True(cleanAudit.HygieneScore >= 95.0);

        string formatted = TextSearch.FormatHtmlEntitiesSummary(
            cleanAudit.TotalEntities, cleanAudit.NamedEntityCount, cleanAudit.NumericEntityCount,
            cleanAudit.BrokenEntityCount, cleanAudit.AmbiguousEscapeCount,
            cleanAudit.UnescapedBracketCount, cleanAudit.HygieneScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Broken doc with broken entity missing semicolon and unescaped angle brackets
        var brokenDoc = string.Join("\n",
            "Invalid entity &amp without semicolon here.",
            "This <tag> is unescaped outside code fence."
        );
        var brokenAudit = TextSearch.AuditMarkdownHtmlEntities(brokenDoc);
        Assert.True(brokenAudit.BrokenEntityCount > 0 || brokenAudit.UnescapedBracketCount > 0);
        Assert.True(brokenAudit.HygieneScore < 95.0);
    }
[Fact]
    public void Run200_AuditMarkdownStrikethroughAndUnderline_ValidatesGfmAndPseudoHtmlSpans()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownStrikethroughAndUnderline("");
        Assert.Equal(0, emptyAudit.TotalSpans);
        Assert.Equal(100.0, emptyAudit.ConsistencyScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);

        // 2. Clean doc with valid GFM strikethrough and pseudo-HTML underline
        var cleanDoc = string.Join("\n",
            "# Heading",
            "This is ~~deleted text~~ and <u>underlined text</u>.",
            "Another <del>removed item</del> and <ins>inserted item</ins>."
        );
        var cleanAudit = TextSearch.AuditMarkdownStrikethroughAndUnderline(cleanDoc);
        Assert.True(cleanAudit.TotalSpans >= 2);
        Assert.Equal(0, cleanAudit.UnclosedCount);
        Assert.Equal(0, cleanAudit.InvalidWhitespaceCount);
        Assert.True(cleanAudit.ConsistencyScore >= 95.0);

        string formatted = TextSearch.FormatStrikethroughSummary(
            cleanAudit.TotalSpans, cleanAudit.GfmStrikethroughCount, cleanAudit.PseudoHtmlCount,
            cleanAudit.ValidSpansCount, cleanAudit.UnclosedCount, cleanAudit.InvalidWhitespaceCount,
            cleanAudit.ConsistencyScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Broken doc with unclosed tildes and whitespace violation
        var brokenDoc = string.Join("\n",
            "Invalid ~~ space padded ~~ strikethrough.",
            "Dangling ~~unclosed delimiter here."
        );
        var brokenAudit = TextSearch.AuditMarkdownStrikethroughAndUnderline(brokenDoc);
        Assert.True(brokenAudit.UnclosedCount > 0 || brokenAudit.InvalidWhitespaceCount > 0);
        Assert.True(brokenAudit.ConsistencyScore < 95.0);
    }

[Fact]
    public void Run201_AuditMarkdownTaskLists_ValidatesGfmCheckboxesAndCompletionMetrics()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownTaskLists("");
        Assert.Equal(0, emptyAudit.TotalTasks);
        Assert.Equal(100.0, emptyAudit.ComplianceScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);

        // 2. Clean doc with completed and pending tasks
        var cleanDoc = string.Join("\n",
            "# Sprint Tasks",
            "- [x] Setup repository",
            "- [x] Write baseline tests",
            "- [ ] Implement feature A",
            "- [ ] Implement feature B",
            "* [x] Verify CI pipeline"
        );
        var cleanAudit = TextSearch.AuditMarkdownTaskLists(cleanDoc);
        Assert.Equal(5, cleanAudit.TotalTasks);
        Assert.Equal(3, cleanAudit.CompletedCount);
        Assert.Equal(2, cleanAudit.PendingCount);
        Assert.Equal(0, cleanAudit.MalformedCount);
        Assert.Equal(0, cleanAudit.OrphanedCount);
        Assert.Equal(60.0, cleanAudit.CompletionPercentage);
        Assert.True(cleanAudit.ComplianceScore >= 95.0);

        string formatted = TextSearch.FormatTaskListsSummary(
            cleanAudit.TotalTasks, cleanAudit.CompletedCount, cleanAudit.PendingCount,
            cleanAudit.MalformedCount, cleanAudit.OrphanedCount,
            cleanAudit.CompletionPercentage, cleanAudit.ComplianceScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Broken doc with malformed and orphaned checkboxes
        var brokenDoc = string.Join("\n",
            "- [] Malformed missing space",
            "- [  ] Redundant space",
            "[ ] Orphaned checkbox without list bullet"
        );
        var brokenAudit = TextSearch.AuditMarkdownTaskLists(brokenDoc);
        Assert.True(brokenAudit.MalformedCount >= 2);
        Assert.True(brokenAudit.OrphanedCount >= 1);
        Assert.True(brokenAudit.ComplianceScore < 75.0);
    }


    [Fact]
    public void Run202_AuditMarkdownFootnotes_ValidatesReferencesDefinitionsAndOrphans()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownFootnotes("");
        Assert.Equal(0, emptyAudit.TotalDefinitions);
        Assert.Equal(0, emptyAudit.TotalReferences);
        Assert.Equal(100.0, emptyAudit.StructuralIntegrityScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);

        // 2. Clean doc with resolved footnotes
        var cleanDoc = string.Join("\n",
            "# Footnote Document",
            "Here is the first claim[^1] and a second claim[^2].",
            "",
            "[^1]: First citation reference.",
            "[^2]: Second citation reference."
        );
        var cleanAudit = TextSearch.AuditMarkdownFootnotes(cleanDoc);
        Assert.Equal(2, cleanAudit.TotalDefinitions);
        Assert.Equal(2, cleanAudit.TotalReferences);
        Assert.Equal(2, cleanAudit.ResolvedCount);
        Assert.Equal(0, cleanAudit.MissingDefinitionCount);
        Assert.Equal(0, cleanAudit.UnreferencedDefinitionCount);
        Assert.True(cleanAudit.StructuralIntegrityScore >= 95.0);

        string formatted = TextSearch.FormatFootnotesSummary(
            cleanAudit.TotalDefinitions, cleanAudit.TotalReferences, cleanAudit.ResolvedCount,
            cleanAudit.MissingDefinitionCount, cleanAudit.UnreferencedDefinitionCount,
            cleanAudit.DuplicateDefinitionCount, cleanAudit.IsSequentialNumeric,
            cleanAudit.StructuralIntegrityScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Broken doc with missing def, orphan def, duplicate def
        var brokenDoc = string.Join("\n",
            "Reference with missing definition[^missing].",
            "Another citation[^dup].",
            "",
            "[^orphan]: Defined but never cited.",
            "[^dup]: First definition.",
            "[^dup]: Duplicate definition."
        );
        var brokenAudit = TextSearch.AuditMarkdownFootnotes(brokenDoc);
        Assert.True(brokenAudit.MissingDefinitionCount >= 1);
        Assert.True(brokenAudit.UnreferencedDefinitionCount >= 1);
        Assert.True(brokenAudit.DuplicateDefinitionCount >= 1);
        Assert.True(brokenAudit.StructuralIntegrityScore < 70.0);
    }

    [Fact]
    public void Run203_AuditMarkdownBlockquoteNesting_ValidatesHierarchyAndAlertCallouts()
    {
        // 1. Empty markdown
        var emptyAudit = TextSearch.AuditMarkdownBlockquoteNesting("");
        Assert.Equal(0, emptyAudit.TotalBlockquoteLines);
        Assert.Equal(100.0, emptyAudit.StructuralHierarchyScore);
        Assert.Equal("Clean Baseline", emptyAudit.Category);

        // 2. Clean blockquotes with alert callout
        var cleanDoc = string.Join("\n",
            "# Document with Quotes",
            "> [!NOTE]",
            "> This is a helpful callout note.",
            "",
            "> Standard single level blockquote text."
        );
        var cleanAudit = TextSearch.AuditMarkdownBlockquoteNesting(cleanDoc);
        Assert.Equal(3, cleanAudit.TotalBlockquoteLines);
        Assert.Equal(1, cleanAudit.AlertCalloutCount);
        Assert.Equal(2, cleanAudit.StandardQuoteCount);
        Assert.Equal(0, cleanAudit.RaggedPrefixCount);
        Assert.True(cleanAudit.StructuralHierarchyScore >= 95.0);

        string formatted = TextSearch.FormatBlockquoteNestingHierarchySummary(
            cleanAudit.TotalBlockquoteLines, cleanAudit.MaxNestingDepth,
            cleanAudit.StandardQuoteCount, cleanAudit.AlertCalloutCount,
            cleanAudit.NestedCalloutCount, cleanAudit.RaggedPrefixCount,
            cleanAudit.StructuralHierarchyScore, cleanAudit.Category);
        Assert.Equal(cleanAudit.Summary, formatted);

        // 3. Ragged blockquote prefix jumping from depth 1 to depth 3
        var raggedDoc = string.Join("\n",
            "> First line level 1",
            ">>> Jumped directly to level 3 without level 2"
        );
        var raggedAudit = TextSearch.AuditMarkdownBlockquoteNesting(raggedDoc);
        Assert.True(raggedAudit.RaggedPrefixCount >= 1);
        Assert.True(raggedAudit.StructuralHierarchyScore < 95.0);
    }

}

