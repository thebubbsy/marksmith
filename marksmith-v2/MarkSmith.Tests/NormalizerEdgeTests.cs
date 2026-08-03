using MarkSmith.Services;
using MarkSmith.Models;
using Xunit;

namespace MarkSmith.Core.Tests;

// Edge cases for the normalizer chain and the LLM cleanup — each targets a behavior a naive
// implementation gets wrong (fence guarding, delimiter conversion, over-eager wrapping).
public class DialectNormalizerEdgeTests
{
    private static string N(string md) => DialectNormalizer.Apply(md);

    [Fact] public void Wikilink_becomes_span() => Assert.Contains("wikilink", N("See [[Some Page]] for more."));
    [Fact] public void Hashtag_becomes_tag() => Assert.Contains("md-tag", N("Topic #planning today."));
    [Fact] public void Hashtag_in_fenced_code_is_untouched()
    {
        var o = N("```\n#define FOO 1\n```");
        Assert.DoesNotContain("md-tag", o);
    }
    [Fact] public void Wikilink_in_fenced_code_untouched()
    {
        var o = N("```\narr[[0]] = 1\n```");
        Assert.DoesNotContain("wikilink", o);
    }
    [Fact] public void Heading_with_hash_not_treated_as_tag() => Assert.DoesNotContain("md-tag", N("# Heading"));
    [Fact] public void Fence_title_becomes_caption() => Assert.Contains("code-title", N("```python title=\"train.py\"\nx=1\n```"));
    [Fact] public void Fence_filetag_becomes_caption() => Assert.Contains("code-title", N("```ts:api.ts\nx\n```"));
    [Fact] public void Clean_fence_language_preserved() { var o = N("```python title=\"a.py\"\nx\n```"); Assert.Contains("```python", o); }
    [Fact] public void Empty_input_ok() => Assert.Equal("", N(""));
    [Fact] public void Plain_prose_unchanged() => Assert.Equal("just some words", N("just some words"));
    [Fact] public void Tilde_fence_also_guarded() { var o = N("~~~\n#tag inside\n~~~"); Assert.DoesNotContain("md-tag", o); }
    [Fact] public void Number_sign_in_url_not_a_tag() => Assert.DoesNotContain("md-tag", N("visit example.com/page#section now"));
}

public class AdmonitionNormalizerEdgeTests
{
    private static string N(string md) => AdmonitionNormalizer.Apply(md);
    [Fact] public void Colon_note_becomes_alert() => Assert.Contains("[!", N(":::note\nbody\n:::"));
    [Fact] public void Empty_ok() => Assert.Equal("", N(""));
    [Fact] public void Plain_text_unchanged() => Assert.Equal("hello world", N("hello world"));
}

public class LlmLatexRecoveryTests
{
    private static readonly LlmSourceService Svc = new();
    private static string Repair(string md) { var (r, _) = Svc.RepairArtifacts(md, new LlmClassification()); return r; }

    [Fact] public void Inline_paren_delims_become_dollar() => Assert.Contains("$x^2$", Repair("Inline \\(x^2\\) here."));
    [Fact] public void Display_bracket_delims_become_double_dollar() { var r = Repair("Display \\[y = mx + b\\]."); Assert.Contains("$$", r); Assert.Contains("y = mx + b", r); }
    [Fact] public void Currency_not_wrapped_as_math() { var r = Repair("It costs $5 and $10 total."); Assert.DoesNotContain("$$", r); }
    [Fact] public void Real_dollar_amounts_preserved() => Assert.Contains("$5", Repair("Price: $5 each."));
    [Fact] public void Thinking_tag_removed() => Assert.DoesNotContain("<thinking>", Repair("<thinking>secret</thinking>answer"));
    [Fact] public void Answer_after_thinking_preserved() => Assert.Contains("answer", Repair("<thinking>secret</thinking>answer"));
    [Fact] public void Fenced_latex_delims_untouched() { var r = Repair("```\n\\(x\\)\n```"); Assert.Contains("\\(x\\)", r); }
    [Fact] public void Empty_ok() => Repair("");
    [Fact] public void Prose_unchanged_when_clean() => Assert.Contains("clean prose", Repair("clean prose with nothing special"));

    // Regression for the sample-doc preview bug: an already-correct $$\begin{bmatrix}…\end{bmatrix}$$
    // block must NOT be "recovered" a second time. Preceding single-$ text (table amounts, inline
    // math) used to mis-pair the DollarMath protection ranges, exposing the block's \begin{bmatrix}
    // body so RecoverMatrixEnvironments re-wrapped it in a SECOND set of $$ — which Markdig parsed as
    // two empty math blocks around a literal "\begin 1 & 2 & 3 \ …" paragraph. Guard: the environment
    // name survives and there is exactly one opening + one closing $$ (never double-wrapped).
    [Fact]
    public void Correct_ddollar_bmatrix_block_is_not_double_wrapped()
    {
        var md = "| A | $4.2M |\n\nReserves $R = x$ follow.\n\n$$\n\\begin{bmatrix}\n1 & 2 & 3 \\\\\n4 & 5 & 6 \\\\\n7 & 8 & 9\n\\end{bmatrix}\n$$";
        var r = Repair(md);
        Assert.Contains("\\begin{bmatrix}", r);
        Assert.Equal(2, CountNonOverlapping(r, "$$"));
    }

    private static int CountNonOverlapping(string s, string sub)
    {
        int count = 0, i = 0;
        while ((i = s.IndexOf(sub, i, System.StringComparison.Ordinal)) >= 0) { count++; i += sub.Length; }
        return count;
    }
}

public class DashAndFormattingTests
{
    [Fact] public void Dash_replacer_leaves_fenced_code() { var o = DashReplacer.Apply("```\na - b\n```", 1, ""); Assert.Contains("a - b", o); }
    [Fact] public void Text_normalizer_converts_bare_cr() { var o = TextNormalizer.Newlines("a\rb\rc"); Assert.DoesNotContain("\r", o); Assert.Contains("a\nb\nc", o); }
    [Fact] public void Text_normalizer_crlf_to_lf() { var o = TextNormalizer.Newlines("a\r\nb"); Assert.Equal("a\nb", o); }
    [Fact] public void Text_normalizer_empty_ok() => Assert.Equal("", TextNormalizer.Newlines(""));
    [Fact] public void Emoji_stripper_removes_emoji() => Assert.DoesNotContain("🚀", EmojiStripper.Strip("launch 🚀 now"));
    [Fact] public void Emoji_stripper_keeps_text() => Assert.Contains("launch", EmojiStripper.Strip("launch 🚀 now"));
}
