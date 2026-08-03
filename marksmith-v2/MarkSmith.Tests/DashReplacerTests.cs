using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class DashReplacerTests
{
    [Fact]
    public void NormalizeDoubleHyphens_Replaces_Double_Hyphen_In_Prose()
    {
        var input = "hello -- world";
        var result = DashReplacer.NormalizeDoubleHyphens(input);
        Assert.Equal("hello — world", result);
    }

    [Fact]
    public void NormalizeDoubleHyphens_Skips_Fenced_Code_Blocks()
    {
        var input = "prose -- start\n```csharp\nvar x = a -- b;\n```\nprose -- end";
        var result = DashReplacer.NormalizeDoubleHyphens(input);
        Assert.Contains("prose — start", result);
        Assert.Contains("var x = a -- b;", result);
        Assert.Contains("prose — end", result);
    }

    [Fact]
    public void NormalizeDoubleHyphens_Skips_Indented_Code_Blocks()
    {
        var input = "    var code = 1 -- 2;\nprose -- text";
        var result = DashReplacer.NormalizeDoubleHyphens(input);
        Assert.Contains("    var code = 1 -- 2;", result);
        Assert.Contains("prose — text", result);
    }

    [Fact]
    public void Apply_Respects_DashMode_Keep()
    {
        var input = "keep — this — intact";
        var result = DashReplacer.Apply(input, DashReplacer.Keep, null);
        Assert.Equal("keep — this — intact", result);
    }

    [Fact]
    public void Apply_Respects_DashMode_Hyphen()
    {
        var input = "word — word";
        var result = DashReplacer.Apply(input, DashReplacer.Hyphen, null);
        Assert.Equal("word-word", result);
    }
}
