using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DiagramFenceSnifferTests
{
    [Fact]
    public void Apply_Sniffs_Bare_Dot_Fence()
    {
        var input = "```\ndigraph G { A -> B }\n```".Replace("\r", "");
        var result = DiagramFenceSniffer.Apply(input);
        Assert.StartsWith("```dot", result);
    }

    [Fact]
    public void Apply_Matches_Opening_Fence_Length()
    {
        var input = "````\n```\ndigraph G { A -> B }\n```\n````".Replace("\r", "");
        var result = DiagramFenceSniffer.Apply(input);
        Assert.StartsWith("````dot", result);
        Assert.EndsWith("````", result.Trim());
    }

    [Fact]
    public void Apply_Ignores_Labeled_Code_Blocks()
    {
        var input = "```python\ndef foo(): return 'digraph G { A -> B }'\n```".Replace("\r", "");
        var result = DiagramFenceSniffer.Apply(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void Apply_Sniffs_PlantUML_Fence()
    {
        var input = "```\n@startuml\nAlice -> Bob\n@enduml\n```".Replace("\r", "");
        var result = DiagramFenceSniffer.Apply(input);
        Assert.StartsWith("```plantuml", result);
    }

    [Fact]
    public void Apply_Preserves_Multiple_Fences_In_Sequence()
    {
        var input = "```python\nx = 1\n```\n\n```\ndigraph G { A -> B }\n```".Replace("\r", "");
        var result = DiagramFenceSniffer.Apply(input);
        Assert.Contains("```python", result);
        Assert.Contains("```dot", result);
    }
}
