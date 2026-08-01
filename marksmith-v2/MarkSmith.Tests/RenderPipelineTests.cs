using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// End-to-end render behavior through MarkdownHtmlService — deterministic cases that don't depend on
// any installed plugin. Complements HtmlRenderTests with security-integration and robustness cases.
public class RenderPipelineTests
{
    private static string Render(string md, AppSettings? s = null, string theme = "GitHub Light") =>
        new MarkdownHtmlService().Render(md, s ?? new AppSettings(), new ThemeCatalog().GetOrDefault(theme));

    // ---- sanitization integration (pasted active content must not reach the DOM) --------------
    [Fact] public void Script_in_markdown_is_stripped() => Assert.DoesNotContain("alert(1)", Render("text <script>alert(1)</script> more"));
    [Fact] public void Onclick_in_raw_html_is_stripped() => Assert.DoesNotContain("onclick", Render("<div onclick=\"steal()\">x</div>"));
    [Fact] public void Javascript_link_stripped() => Assert.DoesNotContain("javascript:", Render("[click](javascript:alert\\(1\\))"));
    [Fact] public void Iframe_stripped() => Assert.DoesNotContain("<iframe", Render("<iframe src=\"evil\"></iframe>"));
    [Fact] public void Handler_after_gt_attr_stripped_in_pipeline() => Assert.DoesNotContain("onerror", Render("<img alt=\"a>b\" src=x onerror=alert(1)>"));

    // ---- mermaid ------------------------------------------------------------------------------
    [Fact] public void Mermaid_fence_becomes_div() { var h = Render("```mermaid\ngraph TD\nA-->B\n```"); Assert.Contains("class=\"mermaid\"", h); }
    [Fact] public void Mermaid_fence_not_a_code_block() { var h = Render("```mermaid\ngraph TD\nA-->B\n```"); Assert.DoesNotContain("language-mermaid", h); }
    [Fact] public void Mermaid_disabled_omits_script() { var s = new AppSettings { MermaidEnabled = false }; var h = Render("```mermaid\ngraph TD\nA-->B\n```", s); Assert.DoesNotContain("mermaid.initialize", h); }
    [Fact] public void Bare_fence_with_graph_gets_embedded_diagram() { var h = Render("```\ngraph TD\nA-->B\n```"); Assert.Contains("mermaid", h); }

    // ---- theme -------------------------------------------------------------------------------
    [Fact] public void Theme_background_appears_in_output() { var t = new ThemeCatalog().GetOrDefault("GitHub Light"); var h = Render("# Doc"); Assert.Contains(t.Background, h, System.StringComparison.OrdinalIgnoreCase); }
    [Fact] public void Dark_theme_renders_without_throw() => Render("# Doc\n\nBody text.", theme: "Dracula");

    // ---- robustness --------------------------------------------------------------------------
    [Fact] public void Large_document_does_not_throw() => Render(string.Concat(Enumerable.Repeat("# H\n\nParagraph with **bold** and `code`.\n\n", 500)));
    [Fact] public void Unicode_content_preserved() => Assert.Contains("日本語", Render("# 見出し\n\n日本語のテキスト"));
    [Fact] public void Emoji_content_preserved_by_default() => Assert.Contains("🚀", Render("Launch 🚀 now"));
    [Fact] public void NoEmoji_strips_emoji() { var s = new AppSettings { NoEmoji = true }; Assert.DoesNotContain("🚀", Render("Launch 🚀 now", s)); }
    [Fact] public void Deeply_nested_lists_render() => Assert.Contains("<ul", Render("- a\n  - b\n    - c\n      - d"));
    [Fact] public void Html_table_renders() => Assert.Contains("<table", Render("<table><tr><td>x</td></tr></table>"));
    [Fact] public void Math_inline_renders_span() => Assert.Contains("math", Render("value $x^2$ here"));
    [Fact] public void Blockquote_renders() => Assert.Contains("<blockquote", Render("> quoted"));
    [Fact] public void Horizontal_rule_renders() => Assert.Contains("<hr", Render("above\n\n---\n\nbelow"));
    [Fact] public void Code_span_renders() => Assert.Contains("<code", Render("use `x` here"));
    [Fact] public void Nested_bold_italic_render() { var h = Render("***both***"); Assert.Contains("<em", h); Assert.Contains("<strong", h); }
    [Fact] public void Link_renders_with_href() => Assert.Contains("href=\"https://example.com\"", Render("[x](https://example.com)"));
    [Fact] public void Ordered_list_renders() => Assert.Contains("<ol", Render("1. a\n2. b"));
    [Fact] public void Multiple_headings_all_render() { var h = Render("# A\n## B\n### C"); Assert.Contains("<h1", h); Assert.Contains("<h2", h); Assert.Contains("<h3", h); }
    [Fact] public void Autolink_bare_url() => Assert.Contains("<a", Render("<https://example.com>"));
    [Fact] public void Only_whitespace_ok() => Render("   \n\t\n   ");
    [Fact] public void Unterminated_fence_does_not_throw() => Render("```\ncode without close");
    [Fact] public void Adjacent_fences_render() { var h = Render("```\na\n```\n\n```\nb\n```"); Assert.Contains("a", h); Assert.Contains("b", h); }
    [Fact] public void Alert_block_renders() => Assert.Contains("markdown-alert", Render("> [!NOTE]\n> important"));
}
