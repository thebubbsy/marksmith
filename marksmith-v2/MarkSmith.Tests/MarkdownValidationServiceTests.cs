using System.Linq;
using MarkSmith.Core.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class MarkdownValidationServiceTests
{
    private readonly MarkdownValidationService _service = new();

    [Fact]
    public void Validate_Empty_Input_Returns_Empty_Report()
    {
        var report = _service.Validate("");

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.TotalLines);
    }

    [Fact]
    public void Validate_Whitespace_Only_Input_Returns_Empty_Report()
    {
        var report = _service.Validate("   \n  \n");

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Validate_Plain_Prose_Has_No_Issues()
    {
        var report = _service.Validate("# Title\n\nSome ordinary prose with no special syntax.\n");

        Assert.True(report.IsValid);
        Assert.Empty(report.Issues);
        Assert.True(report.TotalLines > 0);
    }

    [Fact]
    public void Validate_Unknown_Container_Type_Warns()
    {
        var report = _service.Validate(":::mystery\ncontent\n:::");

        var issue = Assert.Single(report.Issues);
        Assert.Equal("UNKNOWN_CONTAINER_TYPE", issue.RuleId);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(1, issue.LineNumber);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("Warning")]
    [InlineData("TIP")]
    public void Validate_Known_Container_Type_Does_Not_Warn(string kind)
    {
        var report = _service.Validate($":::{kind}\ncontent\n:::");

        Assert.DoesNotContain(report.Issues, i => i.RuleId == "UNKNOWN_CONTAINER_TYPE");
    }

    [Fact]
    public void Validate_Unclosed_Container_Reports_Error()
    {
        var report = _service.Validate(":::note\nsome content that never closes");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNCLOSED_CONTAINER");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(1, issue.LineNumber);
        Assert.False(report.IsValid);
    }

    [Fact]
    public void Validate_Unmatched_Closer_Reports_Error()
    {
        var report = _service.Validate("prose\n:::");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNMATCHED_CONTAINER_CLOSER");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(2, issue.LineNumber);
    }

    [Fact]
    public void Validate_Unclosed_Code_Fence_Reports_Error()
    {
        var report = _service.Validate("```csharp\nvar x = 1;\n");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNCLOSED_CODE_FENCE");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(1, issue.LineNumber);
    }

    [Fact]
    public void Validate_Container_Like_Syntax_Inside_Code_Fence_Is_Ignored()
    {
        var report = _service.Validate("```\n:::not-a-real-container\n```");

        Assert.Empty(report.Issues);
    }

    [Fact]
    public void Validate_Unpaired_Dollar_Delimiter_Warns()
    {
        var report = _service.Validate("The price is $5 today.");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNPAIRED_DOLLAR_DELIMITER");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Validate_Balanced_Inline_Math_Does_Not_Warn()
    {
        var report = _service.Validate("Euler's identity: $e^{i\\pi} + 1 = 0$ is elegant.");

        Assert.DoesNotContain(report.Issues, i => i.RuleId == "UNPAIRED_DOLLAR_DELIMITER");
        Assert.DoesNotContain(report.Issues, i => i.RuleId == "UNBALANCED_LATEX_BRACES");
    }

    [Fact]
    public void Validate_Unbalanced_Latex_Braces_Warns()
    {
        var report = _service.Validate("Formula: $\\frac{a}{b$ is broken.");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNBALANCED_LATEX_BRACES");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Validate_Unclosed_Display_Math_Reports_Error()
    {
        var report = _service.Validate("$$\nx = y + z\n");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "UNCLOSED_DISPLAY_MATH");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(1, issue.LineNumber);
    }

    [Fact]
    public void Validate_Closed_Display_Math_Does_Not_Warn()
    {
        var report = _service.Validate("$$\nx = y + z\n$$");

        Assert.DoesNotContain(report.Issues, i => i.RuleId == "UNCLOSED_DISPLAY_MATH");
    }

    [Fact]
    public void Validate_Pandoc_Table_Border_Warns()
    {
        var report = _service.Validate("+------+------+\n| a    | b    |");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "PANDOC_BORDER_LEAK");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(1, issue.LineNumber);
    }

    [Fact]
    public void Validate_Table_Column_Mismatch_Warns()
    {
        var report = _service.Validate("| A | B | C |\n|---|---|\n| 1 | 2 | 3 |");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "TABLE_COLUMN_MISMATCH");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(2, issue.LineNumber);
    }

    [Fact]
    public void Validate_Matching_Table_Columns_Does_Not_Warn()
    {
        var report = _service.Validate("| A | B |\n|---|---|\n| 1 | 2 |");

        Assert.DoesNotContain(report.Issues, i => i.RuleId == "TABLE_COLUMN_MISMATCH");
    }

    [Fact]
    public void Validate_Script_Tag_Reports_Error()
    {
        var report = _service.Validate("Some text <script>alert('xss')</script> more text");

        var issue = Assert.Single(report.Issues, i => i.RuleId == "PROHIBITED_SCRIPT_TAG");
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.False(report.IsValid);
    }

    [Fact]
    public void Validate_Counts_Match_Issue_Severities()
    {
        var report = _service.Validate(":::unknownkind\ncontent\n:::\n<script>bad()</script>");

        Assert.Equal(1, report.WarningsCount);
        Assert.Equal(1, report.ErrorsCount);
        Assert.Equal(report.Issues.Count, report.WarningsCount + report.ErrorsCount + report.InfoCount);
    }
}
