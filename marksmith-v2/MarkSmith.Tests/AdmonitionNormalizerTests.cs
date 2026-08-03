using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class AdmonitionNormalizerTests
{
    [Fact]
    public void Apply_Transforms_Tip_Admonition_To_GitHub_Alert()
    {
        var input = ":::tip\nRemember to save.\n:::";
        var result = AdmonitionNormalizer.Apply(input);
        Assert.Contains("> [!TIP]", result);
        Assert.Contains("> Remember to save.", result);
    }

    [Fact]
    public void Apply_Transforms_Warning_With_Title()
    {
        var input = ":::warning[Security Notice]\nDo not share keys.\n:::";
        var result = AdmonitionNormalizer.Apply(input);
        Assert.Contains("> [!WARNING]", result);
        Assert.Contains("> **Security Notice**", result);
        Assert.Contains("> Do not share keys.", result);
    }

    [Fact]
    public void Apply_Ignores_Colons_Inside_Code_Fences()
    {
        var input = ":::note Code Snippet\n```\n:::\n```\nEnd of note.\n:::";
        var result = AdmonitionNormalizer.Apply(input);
        Assert.Contains("> [!NOTE]", result);
        Assert.Contains("> **Code Snippet**", result);
        Assert.Contains("> :::", result);
        Assert.Contains("> End of note.", result);
    }

    [Fact]
    public void Apply_Handles_Multiple_Admonitions_In_Series()
    {
        var input = ":::note First\nContent 1\n:::\n\n:::warning Second\nContent 2\n:::";
        var result = AdmonitionNormalizer.Apply(input);
        Assert.Contains("> [!NOTE]", result);
        Assert.Contains("Content 1", result);
        Assert.Contains("> [!WARNING]", result);
        Assert.Contains("Content 2", result);
    }

    [Fact]
    public void Apply_Preserves_Plain_Prose_Without_Admonitions()
    {
        var input = "# Title\nPlain paragraph without callout blocks.";
        var result = AdmonitionNormalizer.Apply(input);
        Assert.Equal(input, result);
    }
}
