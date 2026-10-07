using System.Collections.Generic;
using System.Linq;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

/// <summary>
/// Run #21: the custom cleanup-rule editor. Rules holding a line break looked blank (or were cut off
/// and then saved that way), a bad regex failed silently, and nothing said whether a rule matched.
/// </summary>
public class CleanupRuleEditorTests
{
    [Theory]
    [InlineData("\n\n\n", @"\n\n\n")]
    [InlineData("a\r\nb", @"a\nb")]
    [InlineData("col\tcol", @"col\tcol")]
    [InlineData(@"C:\new", @"C:\\new")]
    [InlineData("In conclusion, ", "In conclusion, ")]
    public void Plain_text_shows_breaks_as_escapes_and_round_trips(string stored, string shown)
    {
        Assert.Equal(shown, CleanupRuleEngine.ToDisplay(stored));
        Assert.Equal(stored.Replace("\r\n", "\n"), CleanupRuleEngine.FromDisplay(shown));
    }

    [Fact]
    public void A_regex_pattern_keeps_its_backslashes_and_only_escapes_real_breaks()
    {
        Assert.Equal(@"^\*\*([^*\n]+)", CleanupRuleEngine.ToDisplay("^\\*\\*([^*\r\n]+)", regexPattern: true));
        Assert.Equal(@"\d+\\n", CleanupRuleEngine.FromDisplay(@"\d+\\n", regexPattern: true));
    }

    [Fact]
    public void The_seeded_regex_example_is_single_line()
    {
        var regex = AppSettings.DefaultCustomNormalizationRules().Single(r => r.IsRegex);
        Assert.DoesNotContain('\n', regex.Find);
        Assert.DoesNotContain('\r', regex.Find);
    }

    // The engine skipped any Find that was all whitespace, so the "\n\n\n" example never ran.
    [Fact]
    public void A_line_break_rule_runs_but_spaces_alone_stay_ignored()
    {
        var (text, outcome) = CleanupRuleEngine.Apply("a\n\n\nb", new TextCleanupRule { Find = "\n\n\n", Replace = "\n\n" });
        Assert.Equal("a\n\nb", text);
        Assert.Equal(1, outcome.Matches);

        var (same, skipped) = CleanupRuleEngine.Apply("a b", new TextCleanupRule { Find = "  " });
        Assert.Equal("a b", same);
        Assert.Equal(0, skipped.Matches);
    }

    [Fact]
    public void Plain_rules_count_every_match_any_case()
    {
        var (text, outcome) = CleanupRuleEngine.Apply("Delve, delve, DELVE.", new TextCleanupRule { Find = "delve", Replace = "dig" });
        Assert.Equal("dig, dig, dig.", text);
        Assert.Equal(3, outcome.Matches);
    }

    [Fact]
    public void A_bad_pattern_explains_itself_in_words()
    {
        var error = CleanupRuleEngine.Validate(new TextCleanupRule { Find = "(unclosed", IsRegex = true });
        Assert.NotNull(error);
        Assert.StartsWith("Not a valid pattern:", error);
        Assert.Contains("at character", error);
        Assert.DoesNotContain("Invalid pattern '", error);

        Assert.Null(CleanupRuleEngine.Validate(new TextCleanupRule { Find = "(unclosed" }));            // plain text is fine
        Assert.Null(CleanupRuleEngine.Validate(new TextCleanupRule { Find = @"Note: (\w+)", IsRegex = true }));
    }

    [Fact]
    public void A_runaway_pattern_times_out_instead_of_freezing_the_preview()
    {
        var input = new string('a', 40) + "!";
        var (text, outcome) = CleanupRuleEngine.Apply(input, new TextCleanupRule { Find = "^(a+)+$", IsRegex = true });
        Assert.Equal(input, text);
        Assert.NotNull(outcome.Error);
        Assert.Contains("too long", outcome.Error);
    }

    [Fact]
    public void Typing_escapes_in_the_box_writes_the_real_characters()
    {
        var saves = 0;
        var item = new TextCleanupRuleItem(() => saves++);
        item.FindText = @"\n\n\n";
        item.ReplaceText = @"\n\n";
        Assert.Equal("\n\n\n", item.Find);
        Assert.Equal("\n\n", item.Replace);
        Assert.Equal(@"\n\n\n", item.FindText);   // what was typed stays as typed
        Assert.True(saves >= 2);

        // A half-typed escape isn't rewritten under the caret.
        item.FindText = "end\\";
        Assert.Equal("end\\", item.FindText);
    }

    [Fact]
    public void A_stored_rule_with_a_line_break_loads_visibly()
    {
        var item = new TextCleanupRuleItem(() => { }, "\n\n\n", "\n\n");
        Assert.Equal(@"\n\n\n", item.FindText);
        Assert.Equal(@"\n\n", item.ReplaceText);
    }

    [Fact]
    public void Switching_to_regex_keeps_the_box_text_and_flags_a_bad_pattern()
    {
        var item = new TextCleanupRuleItem(() => { }) { FindText = "(draft" };
        Assert.False(item.HasPatternError);

        item.IsRegex = true;
        Assert.Equal("(draft", item.FindText);
        Assert.True(item.HasPatternError);
        Assert.False(item.HasStatus);

        item.FindText = @"\(draft\)";
        Assert.False(item.HasPatternError);
        Assert.Equal(@"\(draft\)", item.Find);
    }

    [Fact]
    public void Match_counts_read_as_sentences()
    {
        var item = new TextCleanupRuleItem(() => { }, "delve");
        item.ShowOutcome(new CleanupRuleOutcome(0, null));
        Assert.Equal("No matches in this document", item.Status);
        item.ShowOutcome(new CleanupRuleOutcome(1, null));
        Assert.Equal("1 match in this document", item.Status);
        item.ShowOutcome(new CleanupRuleOutcome(4, null));
        Assert.Equal("4 matches in this document", item.Status);
        Assert.True(item.HasStatus);

        item.ShowOutcome(null);
        Assert.False(item.HasStatus);

        var blank = new TextCleanupRuleItem(() => { });
        blank.ShowOutcome(new CleanupRuleOutcome(0, null));
        Assert.False(blank.HasStatus);   // nothing to report for an empty row
    }

    [Fact]
    public void The_preview_pass_reports_matches_per_rule_and_pauses_with_the_toggle()
    {
        var settings = AppServices.Settings.Current;
        var savedRules = settings.CustomNormalizationRules;
        var savedNormalize = settings.NormalizeLlm;
        try
        {
            settings.CustomNormalizationRules = new List<TextCleanupRule>
            {
                new() { Find = "delve", Replace = "dig" },
                new() { Find = "nowhere-to-be-found" },
                new() { Find = "(bad", IsRegex = true },
            };
            var vm = new MainViewModel { NormalizeLlm = true };
            Assert.Equal(3, vm.NormalizationRules.Count);
            Assert.False(vm.NormalizationRulesPaused);

            vm.PrepareMarkdown("# Notes\n\nWe delve and delve again.", forPreview: true);
            Assert.Equal("2 matches in this document", vm.NormalizationRules[0].Status);
            Assert.Equal("No matches in this document", vm.NormalizationRules[1].Status);
            Assert.True(vm.NormalizationRules[2].HasPatternError);

            // Exports and other non-preview passes leave the rows alone.
            vm.PrepareMarkdown("delve delve delve");
            Assert.Equal("2 matches in this document", vm.NormalizationRules[0].Status);

            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.NormalizeLlm = false;
            Assert.Contains(nameof(MainViewModel.NormalizationRulesPaused), raised);
            Assert.True(vm.NormalizationRulesPaused);

            vm.PrepareMarkdown("delve", forPreview: true);
            Assert.False(vm.NormalizationRules[0].HasStatus);   // no counts while the rules don't run
        }
        finally
        {
            settings.CustomNormalizationRules = savedRules;
            settings.NormalizeLlm = savedNormalize;
        }
    }
}
