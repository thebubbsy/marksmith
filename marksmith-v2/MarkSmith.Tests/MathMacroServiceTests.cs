using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class MathMacroServiceTests
{
    [Fact]
    public void Apply_ReturnsUnchanged_WhenNoMacrosPresent()
    {
        var input = "# Title\n\nInline $E = mc^2$ and block $$\n\\sum x\n$$";
        var output = MathMacroService.Apply(input);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Apply_Expands_Simple_NewCommand_Macros()
    {
        var input = "\\newcommand{\\R}{\\mathbb{R}}\n\nLet $x \\in \\R$ be real.";
        var output = MathMacroService.Apply(input);
        Assert.Equal("\n\nLet $x \\in \\mathbb{R}$ be real.", output);
    }

    [Fact]
    public void Apply_Expands_Def_Macros()
    {
        var input = "\\def\\N{\\mathbb{N}}\n\nFor $n \\in \\N$.";
        var output = MathMacroService.Apply(input);
        Assert.Equal("\n\nFor $n \\in \\mathbb{N}$.", output);
    }

    [Fact]
    public void Apply_Expands_Parametrized_Macros()
    {
        var input = "\\newcommand{\\vector}[1]{\\mathbf{#1}}\n\nFormula: $\\vector{v} = \\vector{u} + \\vector{a}t$.";
        var output = MathMacroService.Apply(input);
        Assert.Equal("\n\nFormula: $\\mathbf{v} = \\mathbf{u} + \\mathbf{a}t$.", output);
    }
}
