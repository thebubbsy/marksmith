using System.Threading;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ExportDiagnosticsServiceTests
{
    [Fact]
    public void StartSession_Tracks_Steps_And_Duration()
    {
        using var session = ExportDiagnosticsService.StartSession("pdf");
        Thread.Sleep(10);
        session.Step("ParseMarkdown");
        Thread.Sleep(10);
        session.Step("RenderLayout");

        var steps = session.GetSteps();
        Assert.Equal(2, steps.Count);
        Assert.Equal("ParseMarkdown", steps[0].StepName);
        Assert.Equal("RenderLayout", steps[1].StepName);
        Assert.True(session.TotalDurationMs >= 15);
    }
}
