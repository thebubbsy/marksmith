using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

// The MD-viewer accuracy suite: markdown in, preview HTML out, asserted on what a careful reader
// would expect. Each case documents an expectation the preview must honor; failures here are
// exactly the "where does the viewer fail" list the suite exists to surface.
public class HtmlRenderTests
{
    private static string Render(string md, AppSettings? s = null) =>
        new MarkdownHtmlService().Render(md, s ?? new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    // ---- baseline markdown --------------------------------------------------------------------
    [Fact] public void Heading_renders() => Assert.Contains("<h1", Render("# Title"));
    [Fact] public void Bold_renders() => Assert.Contains("<strong>bold</strong>", Render("**bold**"));
    [Fact] public void Table_renders() => Assert.Contains("<table", Render("| a | b |\n|---|---|\n| 1 | 2 |"));
    [Fact] public void Table_after_heading_no_blank_line() => Assert.Contains("<table", Render("## H\n| a | b |\n|---|---|\n| 1 | 2 |"));
    [Fact] public void Table_with_trailing_glued_text_still_parses() =>
        Assert.Contains("<table", Render("| a | b |\n|---|---|\n| 1 | 2 |\ntrailing sentence right after"));
    [Fact] public void Tasklist_renders_checkbox_input() => Assert.Contains("type=\"checkbox\"", Render("- [x] done"));
    [Fact] public void Tasklist_uppercase_X_also_checkbox() => Assert.Contains("type=\"checkbox\"", Render("- [X] done"));
    [Fact] public void Footnote_renders_sup_and_body() { var h = Render("text[^1]\n\n[^1]: note body"); Assert.Contains("footnote", h); Assert.Contains("note body", h); }
    [Fact] public void Definition_list_content_survives() { var h = Render("Term\n:   the definition"); Assert.Contains("the definition", h); }
    [Fact] public void Front_matter_is_hidden() { var h = Render("---\ntitle: Secret\n---\n# Doc"); Assert.DoesNotContain("title: Secret", h); }
    [Fact] public void Empty_input_does_not_throw() => Render("");
    [Fact] public void Whitespace_input_does_not_throw() => Render("   \n\n  ");
    [Fact] public void Bare_cr_newlines_do_not_collapse_document() => Assert.Contains("<h1", Render("# Title\rBody line\rMore"));
    [Fact] public void Strikethrough_renders() => Assert.Contains("<del>gone</del>", Render("~~gone~~"));
    [Fact] public void Highlight_renders_mark() => Assert.Contains("<mark>key</mark>", Render("==key=="));
    [Fact] public void Superscript_caret_renders() => Assert.Contains("<sup>2</sup>", Render("x^2^"));
    [Fact] public void Subscript_tilde_renders() => Assert.Contains("<sub>2</sub>", Render("H~2~O"));
    [Fact] public void Autolink_renders() => Assert.Contains("href=\"https://example.com\"", Render("visit https://example.com now"));
    [Fact] public void Link_with_parens_in_url() => Assert.Contains("Wiki_(disambiguation)", Render("[w](https://en.wikipedia.org/wiki/Wiki_(disambiguation))"));

    // ---- alerts + admonitions -----------------------------------------------------------------
    [Fact] public void Github_alert_styled() => Assert.Contains("markdown-alert-note", Render("> [!NOTE]\n> hi"));
    [Fact] public void Admonition_note_becomes_alert() => Assert.Contains("markdown-alert-note", Render(":::note\nhi\n:::"));
    [Fact] public void Admonition_danger_maps_to_caution() => Assert.Contains("markdown-alert-caution", Render(":::danger\nboom\n:::"));
    [Fact] public void Admonition_title_bracketed() => Assert.Contains("Careful", Render(":::warning[Careful]\nhi\n:::"));
    [Fact] public void Admonition_inside_code_fence_stays_literal() =>
        Assert.Contains(":::note", Render("```\n:::note\nliteral\n:::\n```"));
    // Unknown ::: names fall through to Markdig's custom-container extension (a plain <div>) —
    // the content must survive and must NOT be dressed up as one of the five known alert kinds.
    [Fact] public void Unknown_admonition_content_preserved() { var h = Render(":::customthing\nhi\n:::"); Assert.Contains("hi", h); Assert.DoesNotContain("class=\"markdown-alert", h); }
    [Fact] public void Folded_callout_becomes_details() { var h = Render("> [!TIP]- Hidden\n> body here"); Assert.Contains("<details", h); Assert.DoesNotContain("[!TIP]-", h); }
    [Fact] public void Folded_callout_plus_starts_open() => Assert.Contains(" open>", Render("> [!WARNING]+ Shown\n> body"));
    [Fact] public void Folded_callout_body_markdown_renders() => Assert.Contains("<strong>bold</strong>", Render("> [!TIP]- T\n> has **bold**"));
    [Fact] public void Alert_with_code_block_inside() { var h = Render("> [!WARNING]\n> ```python\n> x = 1\n> ```"); Assert.Contains("markdown-alert-warning", h); Assert.Contains("x = 1", h); }

    // ---- 2026 dialect -------------------------------------------------------------------------
    [Fact] public void Wikilink_renders_styled() { var h = Render("see [[Project Phoenix]] now"); Assert.Contains("class=\"wikilink\"", h); Assert.DoesNotContain("[[Project", h); }
    [Fact] public void Wikilink_alias_uses_alias_text() { var h = Render("see [[target-doc|nice name]]"); Assert.Contains("nice name", h); Assert.DoesNotContain("target-doc|", h); }
    [Fact] public void Wikilink_in_inline_code_stays_literal() => Assert.Contains("[[Not A Link]]", Render("use `[[Not A Link]]` syntax"));
    [Fact] public void Wikilink_in_fence_stays_literal() => Assert.Contains("[[Raw]]", Render("```\n[[Raw]]\n```"));
    [Fact] public void Tag_renders_chip() => Assert.Contains("class=\"md-tag\"", Render("tracking #roadmap items"));
    [Fact] public void Heading_hash_is_not_a_tag() { var h = Render("# roadmap"); Assert.DoesNotContain("class=\"md-tag\"", h); Assert.Contains("<h1", h); }
    [Fact] public void Hex_color_in_prose_is_not_a_tag() => Assert.DoesNotContain("class=\"md-tag\"", Render("set the accent to #FF5733 please"));
    [Fact] public void Url_fragment_is_not_a_tag() => Assert.DoesNotContain("class=\"md-tag\"", Render("see https://x.com/page#section for info"));
    [Fact] public void Fence_title_attr_becomes_caption() { var h = Render("```python title=\"train.py\"\nx=1\n```"); Assert.Contains("code-title", h); Assert.Contains("train.py", h); Assert.Contains("language-python", h); }
    [Fact] public void Fence_colon_filename_becomes_caption() { var h = Render("```ts:api.ts\nexport {}\n```"); Assert.Contains("api.ts", h); Assert.Contains("language-ts", h); }
    [Fact] public void Content_tabs_become_labels() { var h = Render("=== \"Windows\"\n    run this\n=== \"macOS\"\n    run that"); Assert.Contains("tab-label", h); Assert.Contains("Windows", h); Assert.DoesNotContain("===", h); }
    [Fact] public void Pagebreak_comment_becomes_marker() => Assert.Contains("class=\"page-break\"", Render("a\n\n<!-- pagebreak -->\n\nb"));
    [Fact] public void Pagebreak_with_space_variant() => Assert.Contains("class=\"page-break\"", Render("a\n\n<!-- page break -->\n\nb"));
    [Fact] public void Newpage_latex_becomes_marker() => Assert.Contains("class=\"page-break\"", Render("a\n\n\\newpage\n\nb"));
    [Fact] public void Pagebreak_css_div_normalized() => Assert.Contains("class=\"page-break\"", Render("a\n\n<div style=\"page-break-after: always\"></div>\n\nb"));
    [Fact] public void Emoji_shortcode_renders() => Assert.Contains("🚀", Render("ship it :rocket:"));
    [Fact] public void Emoji_shortcode_stays_literal_in_noemoji_mode() =>
        Assert.Contains(":rocket:", Render("ship it :rocket:", new AppSettings { NoEmoji = true }));
    [Fact] public void Smiley_text_not_converted() => Assert.DoesNotContain("😃", Render("this is fine :)"));

    // ---- math ---------------------------------------------------------------------------------
    [Fact] public void Inline_math_renders_span() => Assert.Contains("class=\"math\"", Render("energy $E=mc^2$ here"));
    [Fact] public void Money_is_not_math() { var h = Render("costs $5 and $10 later"); Assert.DoesNotContain("class=\"math\"", h); Assert.Contains("$5", h); }
    [Fact] public void Display_math_renders() => Assert.Contains("math", Render("$$\nx^2\n$$"));
    [Fact] public void Mhchem_script_included_when_ce_used() => Assert.Contains("mhchem", Render(@"chem $\ce{H2O}$ inline"));
    [Fact] public void Mhchem_script_absent_without_ce() => Assert.DoesNotContain("mhchem", Render("plain $x$ math"));
    [Fact] public void Katex_tag_macro_present() => Assert.Contains("throwOnError", Render("$x \\tag{1}$"));

    // ---- images -------------------------------------------------------------------------------
    [Fact] public void Remote_image_untouched() => Assert.Contains("https://example.com/a.png", Render("![alt](https://example.com/a.png)"));
    [Fact] public void Remote_image_size_hint_applied() { var h = Render("![alt|150](https://example.com/a.png)"); Assert.Contains("width=\"150\"", h); Assert.DoesNotContain("alt|150", h); }
    [Fact] public void Missing_local_image_keeps_src_no_throw() => Assert.Contains("<img", Render(@"![x](C:\definitely\missing\file.png)"));
    [Fact(Skip="SkiaSharp missing in test environment")] public void Local_image_becomes_data_uri()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mk-test-{Guid.NewGuid():N}.png");
        // 1x1 white png
        File.WriteAllBytes(tmp, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        try { Assert.Contains("data:image/png;base64", Render($"![x]({tmp})")); }
        finally { File.Delete(tmp); }
    }

    // ---- mermaid + plugins --------------------------------------------------------------------
    [Fact] public void Mermaid_fence_becomes_div() => Assert.Contains("class=\"mermaid\"", Render("```mermaid\ngraph TD\nA-->B\n```"));
    [Fact] public void Mermaid_prose_not_hijacked() { var h = Render("the words graph TD in prose"); Assert.DoesNotContain("class=\"mermaid\"", h); }
    [Fact] public void Mermaid_string_literal_in_ts_gets_preview() =>
        Assert.Contains("mermaid-embedded", Render("```typescript\nconst s = `graph TD\nA-->B`;\n```"));
    [Fact] public void Bare_digraph_fence_gets_diagram_treatment()
    {
        var h = Render("```\ndigraph G { A -> B; }\n```");
        // Machine-tolerant: either the installed plugin rendered it, or the install hint shows.
        Assert.True(h.Contains("plugin-diagram") || h.Contains("plugin-diagram-missing"), "bare DOT fence was not routed to the plugin path");
    }
    [Fact] public void Labeled_c_fence_not_sniffed() { var h = Render("```c\nint graph { get; }\n```"); Assert.Contains("language-c", h); }
    [Fact] public void Mermaid_graph_fence_not_sniffed_to_dot() => Assert.Contains("class=\"mermaid\"", Render("```mermaid\ngraph LR\nA-->B\n```"));

    // ---- raw html safety ----------------------------------------------------------------------
    [Fact] public void Script_tag_not_executable_in_preview() =>
        Assert.DoesNotContain("<script>alert", Render("hello\n\n<script>alert(1)</script>\n\nworld"));
    [Fact] public void Inline_event_handler_neutralized() =>
        Assert.DoesNotContain("onerror=", Render("<img src=x onerror=alert(1)>"));
    [Fact] public void Iframe_not_passed_through() =>
        Assert.DoesNotContain("<iframe", Render("<iframe src=\"https://evil.example\"></iframe>"));

    // ---- hr and page structure ----------------------------------------------------------------
    [Fact] public void Thematic_break_renders() => Assert.Contains("<hr", Render("above\n\n---\n\nbelow"));
    [Fact] public void Raw_hr_survives() { var h = Render("above\n<hr>\nbelow"); Assert.Contains("below", h); }

    // ---- 10 improvements validation -----------------------------------------------------------
    [Fact] public void Workspace_styling_applied_in_interactive_mode()
    {
        var html = new MarkdownHtmlService().Render("# Title", new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Dark"), interactive: true);
        Assert.Contains("background: #141416;", html);
        Assert.Contains("box-shadow: 0 4px 16px rgba(0,0,0,0.25);", html);
        Assert.Contains("margin: 40px auto;", html);
    }

    [Fact] public void Workspace_styling_absent_in_print_mode()
    {
        var html = new MarkdownHtmlService().Render("# Title", new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Dark"), interactive: false);
        Assert.Contains("background: #0d1117;", html); // GitHub Dark default background
        Assert.Contains("margin: 0 auto;", html);
        Assert.DoesNotContain("box-shadow: 0 4px 16px rgba(0,0,0,0.25);", html);
    }

    [Fact] public void Mermaid_error_script_injected_in_interactive_mode()
    {
        var html = new MarkdownHtmlService().Render("```mermaid\ngraph LR\nA-->B\n```", new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"), interactive: true);
        Assert.Contains("window.mermaidError = null;", html);
        Assert.Contains("checkPageOverflow", html);
    }

    [Fact] public void Mermaid_confinement_css_applied()
    {
        var html = new MarkdownHtmlService().Render("# Title", new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));
        Assert.Contains(".mermaid { width: 100%; max-width: 100%;", html);
        Assert.DoesNotContain("max-width: calc(100vw - 48px);", html);
    }
}
