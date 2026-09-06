using System;
using System.Linq;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class MarkdownLintServiceTests
{
    [Fact]
    public void Analyze_Null_Or_Whitespace_Returns_No_Issues()
    {
        Assert.Empty(MarkdownLintService.Analyze(null));
        Assert.Empty(MarkdownLintService.Analyze("   \n  \n"));
    }

    [Fact]
    public void Analyze_Flags_Trailing_Whitespace_On_Content_Line()
    {
        var issues = MarkdownLintService.Analyze("Some text with trailing space   \nMore text.\n");

        var issue = Assert.Single(issues);
        Assert.Equal(1, issue.Line);
        Assert.Equal("Trailing whitespace", issue.Message);
    }

    [Fact]
    public void Analyze_Does_Not_Flag_Trailing_Whitespace_On_Blank_Line()
    {
        var issues = MarkdownLintService.Analyze("Paragraph.\n   \nMore.\n");

        Assert.DoesNotContain(issues, i => i.Message == "Trailing whitespace");
    }

    [Fact]
    public void Analyze_Flags_Hard_Tab()
    {
        var issues = MarkdownLintService.Analyze("line\twith tab\n");

        Assert.Contains(issues, i => i.Message == "Hard tab character" && i.Line == 1);
    }

    [Fact]
    public void Analyze_Flags_Three_Consecutive_Blank_Lines_Once()
    {
        var issues = MarkdownLintService.Analyze("a\n\n\n\nb\n");

        var blankIssues = issues.Where(i => i.Message == "3+ consecutive blank lines").ToList();
        var only = Assert.Single(blankIssues);
        Assert.Equal(4, only.Line); // 1-based line of the 3rd consecutive blank line
    }

    [Fact]
    public void Analyze_Flags_Image_Missing_Alt_Text()
    {
        var issues = MarkdownLintService.Analyze("![](photo.png)\n");

        Assert.Contains(issues, i => i.Message == "Image missing alt text" && i.Line == 1);
    }

    [Fact]
    public void Analyze_Does_Not_Flag_Image_With_Alt_Text()
    {
        var issues = MarkdownLintService.Analyze("![a cat](photo.png)\n");

        Assert.DoesNotContain(issues, i => i.Message == "Image missing alt text");
    }

    [Fact]
    public void Analyze_Flags_Very_Long_Line()
    {
        var issues = MarkdownLintService.Analyze(new string('a', 501) + "\n");

        Assert.Contains(issues, i => i.Message.StartsWith("Very long line", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_Flags_Heading_Missing_Space_After_Hashes()
    {
        var issues = MarkdownLintService.Analyze("#Heading\n");

        Assert.Contains(issues, i => i.Message == "Missing space after '#'" && i.Line == 1);
    }

    [Fact]
    public void Analyze_Does_Not_Flag_Valid_Heading()
    {
        var issues = MarkdownLintService.Analyze("# Heading\n");

        Assert.DoesNotContain(issues, i => i.Message == "Missing space after '#'");
    }

    [Fact]
    public void Analyze_Suspends_Style_Rules_Inside_A_Simple_Fenced_Code_Block()
    {
        var issues = MarkdownLintService.Analyze("```csharp\nvar x = 1;   \n#not a heading\n```\nAfter.\n");

        Assert.Empty(issues);
    }

    [Fact]
    public void Analyze_Flags_Genuinely_Unclosed_Fence()
    {
        var issues = MarkdownLintService.Analyze("```csharp\nvar x = 1;\n");

        var issue = Assert.Single(issues);
        Assert.Equal("Unclosed code fence", issue.Message);
    }

    [Fact]
    public void Analyze_Supports_Tilde_Fences()
    {
        var issues = MarkdownLintService.Analyze("~~~\ncode with trailing space   \n~~~\nAfter.\n");

        Assert.Empty(issues);
    }

    [Fact]
    public void Analyze_A_Shorter_Nested_Fence_Of_The_Same_Character_Does_Not_Close_The_Outer_Block()
    {
        // A longer opening fence (````) may literally contain a shorter fence (```) as an example
        // — per CommonMark, a closing fence must be at least as long as the opener, so the inner
        // ``` here is just code content, not a close. Regression test for a bug where the linter
        // tracked only the fence character, not its length, and closed on any run of >= 3 of it —
        // which both suppressed real issues (a trailing-whitespace line inside the outer block
        // that got exposed as "regular" text) and produced a spurious "Unclosed code fence".
        var issues = MarkdownLintService.Analyze(
            "````markdown\n```\ntrailing whitespace here   \n````\n\nNormal paragraph after.\n");

        Assert.Empty(issues);
    }

    [Fact]
    public void Analyze_A_Closing_Fence_With_An_Info_String_Does_Not_Count_As_Closing()
    {
        // "```notreallyaclose" is not a valid closing fence (CommonMark forbids content after the
        // fence characters other than whitespace), so the block should still be open afterward.
        var issues = MarkdownLintService.Analyze(
            "```\ncode\n```notreallyaclose\nstill code   \n```\nAfter.\n");

        Assert.Empty(issues);
    }

    [Fact]
    public void Analyze_A_Closing_Fence_May_Have_Trailing_Whitespace()
    {
        var issues = MarkdownLintService.Analyze("```\ncode\n```   \nAfter.\n");

        Assert.Empty(issues);
    }
}
