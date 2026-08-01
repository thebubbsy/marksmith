using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class TaskListProgressServiceTests
{
    [Fact]
    public void Calculate_CountsCompletedAndTotal()
    {
        var md = """
            - [x] Done one
            - [ ] Open one
            - [X] Done two
            - [ ] Open two
            """;
        var p = TaskListProgressService.Calculate(md);
        Assert.Equal(2, p.Completed);
        Assert.Equal(4, p.Total);
        Assert.Equal(50.0, p.Percentage);
        Assert.True(p.HasTasks);
    }

    [Fact]
    public void Calculate_SupportsOrderedAndNestedItems()
    {
        var md = """
            1. [x] First
            2. [ ] Second
               - [x] Nested done
            """;
        var p = TaskListProgressService.Calculate(md);
        Assert.Equal(2, p.Completed);
        Assert.Equal(3, p.Total);
    }

    [Fact]
    public void Calculate_IgnoresCheckboxesInsideCodeFences()
    {
        var md = """
            - [x] Real task

            ```md
            - [ ] Not a task
            - [x] Also not a task
            ```
            """;
        var p = TaskListProgressService.Calculate(md);
        Assert.Equal(1, p.Completed);
        Assert.Equal(1, p.Total);
    }

    [Fact]
    public void Calculate_ComputesPercentageMetric()
    {
        var md = "- [x] a\n- [ ] b\n- [ ] c\n";
        var p = TaskListProgressService.Calculate(md);
        Assert.Equal(33.3, p.Percentage);
        Assert.Equal("33.3%", p.PercentageText);
    }

    [Fact]
    public void Calculate_EmptyDocumentHasNoTasks()
    {
        var p = TaskListProgressService.Calculate("Just prose, no tasks.");
        Assert.Equal(0, p.Total);
        Assert.False(p.HasTasks);
        Assert.Equal(0.0, p.Percentage);
        Assert.Equal("No tasks", p.SummaryText);
    }
}
