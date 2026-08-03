using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// Unit-level accuracy for the text normalizers: LLM cleanup, bare-math recovery, dialect rewrites,
// diagram-fence sniffing. These run on raw strings so a failure pinpoints the transform itself.
public class NormalizerTests
{
    private static string Normalize(string md)
    {
        var llm = new LlmSourceService();
        return llm.Normalize(md, llm.Classify(md)).Cleaned;
    }

    // ---- bare LaTeX recovery ------------------------------------------------------------------
    [Fact] public void Bare_frac_gets_wrapped() => Assert.Contains(@"$\frac{1}{2}$", Normalize(@"half is\frac{1}{2}of it"));
    [Fact] public void Bare_boxed_gets_wrapped() => Assert.Contains(@"$\boxed{42}$", Normalize(@"answer\boxed{42}done"));
    [Fact] public void Bare_sqrt_gets_wrapped() => Assert.Contains(@"$\sqrt{2}$", Normalize(@"root\sqrt{2}here"));
    [Fact] public void Bare_sqrt_with_degree() => Assert.Contains(@"$\sqrt[3]{8}$", Normalize(@"cube\sqrt[3]{8}here"));
    [Fact] public void Bare_binom_gets_wrapped() => Assert.Contains(@"$\binom{n}{k}$", Normalize(@"choose\binom{n}{k}ways"));
    [Fact] public void Nested_frac_sqrt_wraps_as_one_unit() => Assert.Contains(@"$\frac{\sqrt{x}}{2}$", Normalize(@"v =\frac{\sqrt{x}}{2}end"));
    [Fact] public void Already_delimited_not_double_wrapped() { var n = Normalize(@"already $\frac{a}{b}$ fine"); Assert.DoesNotContain("$$\\frac", n.Replace(" ", "")); Assert.Contains(@"$\frac{a}{b}$", n); }
    [Fact] public void Frac_in_fenced_code_untouched() => Assert.Contains("```\n\\frac{a}{b}\n```", Normalize("```\n\\frac{a}{b}\n```"));
    [Fact] public void Frac_in_inline_code_untouched() => Assert.Contains(@"`\frac{a}{b}`", Normalize(@"use `\frac{a}{b}` syntax"));
    [Fact] public void Frac_without_braces_left_alone() => Assert.Contains(@"the \frac command", Normalize(@"the \frac command needs args"));
    [Fact] public void Unbalanced_braces_left_alone() => Assert.Contains(@"\frac{a}{b", Normalize(@"broken \frac{a}{b oops"));
    [Fact] public void Wrapped_math_has_flanking_spaces() { var n = Normalize(@"x=1\frac{1}{2}y=2"); Assert.Contains(@" $\frac{1}{2}$ ", n); }

    // ---- ChatGPT delimiters -------------------------------------------------------------------
    [Fact] public void Paren_latex_becomes_dollars() => Assert.Contains("$E=mc^2$", Normalize(@"formula \(E=mc^2\) here"));
    [Fact] public void Bracket_latex_becomes_display() => Assert.Contains("$$", Normalize("display \\[x^2\\] here"));
    [Fact] public void Citation_pips_removed() => Assert.DoesNotContain("†", Normalize("claim【12†source】text"));
    [Fact] public void Disclaimer_footer_removed() => Assert.DoesNotContain("can make mistakes", Normalize("body\nChatGPT can make mistakes. Check important info."));

    // ---- admonitions --------------------------------------------------------------------------
    [Fact] public void Admonition_rewrites_to_alert() => Assert.Contains("> [!TIP]", AdmonitionNormalizer.Apply(":::tip\nhi\n:::"));
    [Fact] public void Admonition_missing_closer_still_converts() => Assert.Contains("> [!NOTE]", AdmonitionNormalizer.Apply(":::note\nno closer here"));
    [Fact] public void Python_admonition_single_line_rewrites_to_alert() => Assert.Contains("> [!NOTE]", AdmonitionNormalizer.Apply("!!! note Python Markdown style admonitions work too!"));
    [Fact] public void Python_admonition_multiline_indented_rewrites_to_alert() => Assert.Contains("> [!WARNING]\n> **Watch out**", AdmonitionNormalizer.Apply("!!! warning \"Watch out\"\n    Carefully read this"));
    [Fact] public void Folded_callout_rewrites_to_details() => Assert.Contains("<details", AdmonitionNormalizer.Apply("> [!tip]- T\n> body"));
    [Fact] public void Folded_callout_kind_mapped() => Assert.Contains("md-callout-caution", AdmonitionNormalizer.Apply("> [!danger]- T\n> body"));
    [Fact] public void Plain_alert_untouched_by_fold_rewrite() { var s = AdmonitionNormalizer.Apply("> [!NOTE]\n> plain"); Assert.Contains("> [!NOTE]", s); Assert.DoesNotContain("<details", s); }

    // ---- dialect ------------------------------------------------------------------------------
    [Fact] public void Wikilink_rewritten() => Assert.Contains("class=\"wikilink\"", DialectNormalizer.Apply("see [[Page Name]]"));
    [Fact] public void Wikilink_multiple_on_line() { var s = DialectNormalizer.Apply("[[A]] and [[B]]"); Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(s, "wikilink").Count); }
    [Fact] public void Tag_rewritten() => Assert.Contains("class=\"md-tag\"", DialectNormalizer.Apply("plan #roadmap now"));
    [Fact] public void Tag_requires_word_boundary() => Assert.DoesNotContain("md-tag", DialectNormalizer.Apply("c#roadmap is glued"));
    [Fact] public void Fence_title_extracted() { var s = DialectNormalizer.Apply("```python title=\"a.py\"\nx\n```"); Assert.Contains("code-title", s); Assert.Contains("```python\n", s); }
    [Fact] public void Fence_body_untouched_by_dialect() => Assert.Contains("[[literal]]", DialectNormalizer.Apply("```\n[[literal]] #notag\n```"));
    [Fact] public void Tilde_fence_respected() => Assert.Contains("[[lit]]", DialectNormalizer.Apply("~~~\n[[lit]]\n~~~"));
    [Fact] public void Table_glue_fix_inserts_blank() { var s = DialectNormalizer.Apply("| a |\n|---|\n| 1 |\ntext after"); Assert.Contains("| 1 |\n\ntext after", s); }
    [Fact] public void Pagebreak_variants_normalized()
    {
        Assert.Contains("page-break", DialectNormalizer.Apply("<!-- pagebreak -->"));
        Assert.Contains("page-break", DialectNormalizer.Apply("\\pagebreak"));
        Assert.Contains("page-break", DialectNormalizer.Apply("<div style=\"page-break-after:always\"></div>"));
    }
    [Fact] public void Double_hyphen_converted_to_em_dash() => Assert.Equal("hello — world", DialectNormalizer.Apply("hello -- world"));
    [Fact] public void Double_hyphen_in_code_block_untouched() => Assert.Contains("a -- b", DialectNormalizer.Apply("```\na -- b\n```"));
    [Fact] public void Double_hyphen_in_inline_code_untouched() => Assert.Contains("`a -- b`", DialectNormalizer.Apply("use `a -- b` syntax"));
    [Fact] public void Double_hyphen_in_html_comment_untouched() => Assert.Contains("<!-- comment -- test -->", DialectNormalizer.Apply("<!-- comment -- test -->"));
    [Fact] public void Triple_hyphens_untouched() => Assert.Contains("---\ntitle: test\n---", DialectNormalizer.Apply("---\ntitle: test\n---"));


    // ---- sniffer ------------------------------------------------------------------------------
    [Fact] public void Bare_digraph_sniffed_as_dot() => Assert.Contains("```dot", DiagramFenceSniffer.Apply("```\ndigraph G { A -> B }\n```"));
    [Fact] public void Strict_digraph_sniffed() => Assert.Contains("```dot", DiagramFenceSniffer.Apply("```\nstrict digraph G { A -> B }\n```"));
    [Fact] public void Undirected_graph_with_brace_sniffed() => Assert.Contains("```dot", DiagramFenceSniffer.Apply("```\ngraph G { A -- B }\n```"));
    [Fact] public void Mermaid_style_graph_td_not_sniffed() => Assert.DoesNotContain("```dot", DiagramFenceSniffer.Apply("```\ngraph TD\nA-->B\n```"));
    [Fact] public void Startuml_sniffed_as_plantuml() => Assert.Contains("```plantuml", DiagramFenceSniffer.Apply("```\n@startuml\nA -> B\n@enduml\n```"));
    [Fact] public void Vega_schema_sniffed() => Assert.Contains("```vega-lite", DiagramFenceSniffer.Apply("```\n{\"$schema\": \"https://vega.github.io/schema/vega-lite/v5.json\"}\n```"));
    [Fact] public void Labeled_fence_never_resniffed() => Assert.DoesNotContain("```dot", DiagramFenceSniffer.Apply("```c\ndigraph G { }\n```"));
    [Fact] public void Prose_digraph_untouched() { var md = "the word digraph { appears } in prose"; Assert.Equal(md, DiagramFenceSniffer.Apply(md)); }
    [Fact] public void Unterminated_fence_left_alone() { var md = "```\ndigraph G {"; Assert.Equal(md, DiagramFenceSniffer.Apply(md)); }
    [Fact] public void Tilde_fence_sniffed() => Assert.Contains("~~~dot", DiagramFenceSniffer.Apply("~~~\ndigraph G { A -> B }\n~~~"));
}
