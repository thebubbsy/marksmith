using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// QODER task 6: AI quirk normalization for DeepSeek and Perplexity chat exports —
// <think> reasoning blocks, web-search citation badges ([1], [source]), and raw
// prompt-echo headers — plus regression guards for the pre-existing provider rules.
public class DialectNormalizerTests
{
    private static string N(string md, string? provider) => ProviderDialectNormalizer.Normalize(md, provider);

    // ---- DeepSeek: <think> reasoning blocks ------------------------------------------------------

    [Fact]
    public void DeepSeek_Think_Block_Is_Stripped_Entirely()
    {
        var md = "<think>\nOkay, the user wants a summary.\nLet me check the facts.\n</think>\n\n# Summary\n\nThe answer.";
        var result = N(md, "deepseek");
        Assert.DoesNotContain("think", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user wants", result);
        Assert.Contains("# Summary", result);
        Assert.Contains("The answer.", result);
    }

    [Fact]
    public void DeepSeek_Unclosed_Think_Block_Is_Stripped_To_End_Of_Stream()
    {
        var md = "<think>\nReasoning that never terminates...";
        Assert.Equal("", N(md, "deepseek").Trim());
    }

    [Fact]
    public void Think_Block_Strip_Is_Case_Insensitive()
    {
        var md = "<think>\nstep one\n</think>\nanswer";
        var result = N(md, "deepseek");
        Assert.DoesNotContain("step one", result);
        Assert.Contains("answer", result);
    }

    [Fact]
    public void Think_Block_Is_Stripped_Even_For_Unknown_Provider()
    {
        // Content-detected: the marker is unmistakable, so no source id is required.
        var md = "<think>\nhidden\n</think>\nvisible";
        var result = N(md, null);
        Assert.DoesNotContain("hidden", result);
        Assert.Contains("visible", result);
    }

    [Fact]
    public void Multiple_Think_Blocks_Are_All_Stripped()
    {
        var md = "<think>\na\n</think>\npart one\n<think>\nb\n</think>\npart two";
        var result = N(md, "deepseek");
        Assert.Contains("part one", result);
        Assert.Contains("part two", result);
        Assert.DoesNotContain("think", result, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- DeepSeek: escaped table pipes + prompt echo -------------------------------------------

    [Fact]
    public void DeepSeek_Escaped_Pipes_Are_Unescaped()
    {
        var md = "| a \\| b | c |\n| --- | --- |\n| 1 \\| 2 | 3 |";
        var result = N(md, "deepseek");
        Assert.Contains("| a | b | c |", result);
        Assert.Contains("| 1 | 2 | 3 |", result);
        Assert.DoesNotContain(@"\|", result);
    }

    [Fact]
    public void DeepSeek_Bold_Prompt_Echo_Header_Is_Stripped()
    {
        var md = "**User:** What is the capital of France?\n\nThe capital of France is Paris.";
        var result = N(md, "deepseek");
        Assert.DoesNotContain("User:", result);
        Assert.DoesNotContain("capital of France?", result);
        Assert.Contains("The capital of France is Paris.", result);
    }

    [Fact]
    public void DeepSeek_Blockquoted_Prompt_Echo_Header_Is_Stripped()
    {
        var md = "> User: explain closures\n\nA closure is a function that captures its scope.";
        var result = N(md, "deepseek");
        Assert.DoesNotContain("explain closures", result);
        Assert.Contains("A closure", result);
    }

    [Fact]
    public void Prompt_Echo_Only_Applies_To_First_Line()
    {
        // A "User:" label mid-document is legitimate content (e.g. dialogue) and must survive.
        var md = "# Interview\n\nUser: what do you build?\n\nAssistant: documents.";
        var result = N(md, "deepseek");
        Assert.Contains("User: what do you build?", result);
    }

    [Fact]
    public void Full_DeepSeek_Stream_Is_Cleaned_End_To_End()
    {
        var md = "**User:** compare A and B\n\n<think>\nI should list the differences.\n</think>\n" +
                 "| Feature \\| A \\| B |\n| --- |\n| Speed \\| fast \\| slow |\n\nDone [1].";
        var result = N(md, "deepseek");
        Assert.DoesNotContain("think", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compare A and B", result);
        Assert.DoesNotContain(@"\|", result);
        Assert.Contains("| Feature | A | B |", result);
        Assert.Contains("Done [1].", result); // citation pips are a Perplexity quirk, not DeepSeek's
    }

    // ---- Perplexity: citation badges + prompt echo ----------------------------------------------

    [Fact]
    public void Perplexity_Numbered_Citation_Pips_Are_Stripped()
    {
        var md = "The sky is blue[1] because of scattering[2][3].";
        var result = N(md, "perplexity");
        Assert.Equal("The sky is blue because of scattering.", result);
    }

    [Fact]
    public void Perplexity_Source_Badge_Is_Stripped()
    {
        var md = "Per recent data[source], output grew 12%[Sources].";
        var result = N(md, "perplexity");
        Assert.DoesNotContain("[source]", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Per recent data, output grew 12%.", result);
    }

    [Fact]
    public void Perplexity_Markdown_Link_Text_Is_Preserved()
    {
        // "[1](url)" and "[source](url)" are real links, not citation badges.
        var md = "See [1](https://example.com) and [source](https://example.org).";
        var result = N(md, "perplexity");
        Assert.Contains("[1](https://example.com)", result);
        Assert.Contains("[source](https://example.org)", result);
    }

    [Fact]
    public void Perplexity_Prompt_Echo_Header_Is_Stripped()
    {
        var md = "Prompt: latest GPU benchmarks\n\n# Benchmarks\n\nThe H200 leads[1].";
        var result = N(md, "perplexity");
        Assert.DoesNotContain("latest GPU benchmarks", result);
        Assert.Contains("# Benchmarks", result);
        Assert.Contains("The H200 leads.", result);
    }

    [Fact]
    public void Full_Perplexity_Stream_Is_Cleaned_End_To_End()
    {
        var md = "**User:** is the M4 faster?\n\nYes, roughly 25% faster[1] per early reviews[2].\n\n[source]";
        var result = N(md, "perplexity");
        Assert.DoesNotContain("is the M4 faster?", result);
        Assert.DoesNotContain("[1]", result);
        Assert.DoesNotContain("[2]", result);
        Assert.DoesNotContain("[source]", result, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Yes, roughly 25% faster per early reviews.", result);
    }

    [Fact]
    public void Perplexity_Citations_Survive_In_Non_Perplexity_Providers()
    {
        // [n] pips are only stripped when the source is KNOWN to be Perplexity — a ChatGPT
        // document citing "[1]" keeps them.
        var md = "Reference [1] covers this.";
        Assert.Contains("[1]", N(md, "chatgpt"));
        Assert.Contains("[1]", N(md, null));
    }

    // ---- regression guards: pre-existing provider rules ------------------------------------------

    [Fact]
    public void ChatGPT_Latex_Delimiters_Are_Still_Fixed()
    {
        Assert.Contains("$$x^2$$", N(@"display \[x^2\] here", "chatgpt"));
    }

    [Fact]
    public void Claude_Artifact_Tags_Are_Still_Stripped()
    {
        var result = N("<antArtifact id=\"x\">body</antArtifact>", "claude");
        Assert.DoesNotContain("antArtifact", result);
        Assert.Contains("body", result);
    }

    [Fact]
    public void Quoted_Code_Fence_Is_Still_Unquoted_For_Any_Provider()
    {
        Assert.Contains("```python", N("> ```python\n> print(1)", null));
    }

    [Fact]
    public void Unknown_Provider_Plain_Text_Is_Untouched()
    {
        var md = "# Title\n\nPlain prose with [notes] and a User: label mid-way.";
        Assert.Equal(md, N(md, "some-new-provider"));
    }

    [Fact]
    public void Empty_And_Whitespace_Inputs_Pass_Through()
    {
        Assert.Equal("", N("", "deepseek"));
        Assert.Equal("   ", N("   ", "perplexity"));
    }
}
