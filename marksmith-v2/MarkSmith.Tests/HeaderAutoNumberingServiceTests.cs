using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

public class HeaderAutoNumberingServiceTests
{
    [Fact]
    public void ComputeHeadingNumbers_IgnoresHashCommentsInsideFencedCodeBlocks()
    {
        // A '#'-prefixed line inside a fenced code block (e.g. a shell/Python comment)
        // must not be treated as a Markdown heading.
        string md = """
            # Introduction

            ```bash
            # This is a shell comment, not a heading
            echo "hello"
            ```

            ## Usage
            """;

        var headings = HeaderAutoNumberingService.ComputeHeadingNumbers(md);

        Assert.Equal(2, headings.Count);
        Assert.Equal("Introduction", headings[0].CleanText);
        Assert.Equal("1.", headings[0].NumberPrefix);
        Assert.Equal("Usage", headings[1].CleanText);
        Assert.Equal("1.1.", headings[1].NumberPrefix);
    }

    [Fact]
    public void ApplyNumberingToMarkdown_LeavesFencedCodeBlockContentUntouched()
    {
        string md = """
            # Introduction

            ```bash
            # This is a shell comment, not a heading
            echo "hello"
            ```

            ## Usage
            """;

        string result = HeaderAutoNumberingService.ApplyNumberingToMarkdown(md);

        Assert.Contains("# 1. Introduction", result);
        Assert.Contains("## 1.1. Usage", result);
        // The comment line inside the fence must survive unnumbered and unmodified.
        Assert.Contains("# This is a shell comment, not a heading", result);
        Assert.DoesNotContain("# 2. This is a shell comment", result);
    }

    [Fact]
    public void ComputeHeadingNumbers_HandlesTildeFencesAndUnclosedFence()
    {
        // Tilde fences behave the same as backtick fences, and an unterminated fence
        // should simply swallow the rest of the document rather than throwing.
        string md = """
            # Title

            ~~~
            # not a heading
            """;

        var headings = HeaderAutoNumberingService.ComputeHeadingNumbers(md);

        Assert.Single(headings);
        Assert.Equal("Title", headings[0].CleanText);
    }
}
