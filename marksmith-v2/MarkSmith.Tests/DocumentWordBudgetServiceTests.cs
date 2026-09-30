using System;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DocumentWordBudgetServiceTests
{
    [Fact]
    public void Analyze_EmptyMarkdown_ReturnsEmptyReport()
    {
        var result = DocumentWordBudgetService.Analyze("");
        Assert.False(result.IsOverallOverBudget);
        Assert.Equal(0, result.TotalActualWords);
        Assert.Null(result.OverallBudgetWords);
    }

    [Fact]
    public void Analyze_WithDocumentBudget_ComputesOverallProgress()
    {
        var markdown = @"
<!-- doc-budget: 100 words -->

This is a short test document. It contains ten words exactly.";

        var result = DocumentWordBudgetService.Analyze(markdown);
        Assert.Equal(100, result.OverallBudgetWords);
        Assert.Equal(11, result.TotalActualWords);
        Assert.Equal(11, result.OverallProgressPercentage);
        Assert.Equal(89, result.OverallRemainingWords);
        Assert.False(result.IsOverallOverBudget);
    }
}
