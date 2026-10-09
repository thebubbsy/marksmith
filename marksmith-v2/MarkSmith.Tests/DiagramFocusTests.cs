using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Tests;

// The live preview turns a document that is "just a diagram" into a pan/zoom diagram viewer.
// Anything more than a title and a line of intro must render as the document it is.
public class DiagramFocusTests
{
    private const string Diagram = "```mermaid\nflowchart LR\n  A-->B\n```\n";

    [Fact]
    public void ATitleAndADiagram_IsFocused()
    {
        var (focused, title, subtitle) = MarkdownHtmlService.AnalyzeDiagramFocus("# Flow\n\nHow it works.\n\n" + Diagram);
        Assert.True(focused);
        Assert.Equal("Flow", title);
        Assert.Equal("How it works.", subtitle);
    }

    [Fact]
    public void AReportWithSeveralSections_IsNotFocused()
    {
        var md = "# Quarterly report\n\n## Summary\n\nRevenue grew.\n\n## Details\n\n" + Diagram + "\n## Next steps\n\nSee the plan.\n";
        Assert.False(MarkdownHtmlService.AnalyzeDiagramFocus(md).Focused);
    }

    [Fact]
    public void EditorLineBreaks_AreReadTheSameWay()
    {
        var md = ("# Flow\n\n" + Diagram).Replace('\n', '\r');
        Assert.True(MarkdownHtmlService.AnalyzeDiagramFocus(md).Focused);
    }
}
