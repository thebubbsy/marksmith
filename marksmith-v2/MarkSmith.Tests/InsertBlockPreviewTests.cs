using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Every Insert-menu block must render in the HTML preview (and so in PDF/HTML export), not only
/// in DOCX. Tabs, data grid, references, embed, AI context, workflow and timeline all used to
/// preview as raw text — or, for :::embed, as nothing at all.
/// </summary>
public class InsertBlockPreviewTests
{
    private static string Render(string md, string theme = "GitHub Light") =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault(theme));

    private static string Body(string html)
    {
        var start = html.IndexOf("<body", StringComparison.Ordinal);
        return start < 0 ? html : html[start..];
    }

    [Fact]
    public void Every_insert_dialog_default_renders_without_leaking_its_source()
    {
        var snippets = new Dictionary<string, string>
        {
            ["tabs"] = InsertSnippetBuilder.Tabs(new[] { "Tab 1", "Tab 2" }),
            ["datagrid"] = InsertSnippetBuilder.Datagrid(new[] { "label,value", "Q1,10", "Q2,25" }),
            ["references"] = InsertSnippetBuilder.References("", "", "", ""),
            ["embed"] = InsertSnippetBuilder.Embed("youtube", "https://www.youtube.com/watch?v=dQw4w9WgXcQ"),
            ["workflow"] = InsertSnippetBuilder.Workflow(new[] { "Step 1", "Step 2", "Step 3" }),
            ["timeline"] = InsertSnippetBuilder.Timeline(new[] { "2020: Started", "2023: Progress" }),
            ["ai-context"] = "\n:::ai-context\npromptHash: abc123\nmodel: Gemini Pro\n:::\n",
        };
        foreach (var (name, md) in snippets)
        {
            var body = Body(Render("Before.\n" + md + "\nAfter."));
            Assert.DoesNotContain(":::", body);
            Assert.DoesNotContain("<!--MSBLOCK", body);
            Assert.Contains("Before.", body);
            Assert.Contains("After.", body);
        }
    }

    [Fact]
    public void Blocks_typed_in_the_editor_render_with_its_bare_carriage_return_line_breaks()
    {
        // The desktop editor is a WinUI TextBox, which stores every line break as a lone '\r';
        // that is what reaches the preview when a block is typed or inserted there.
        var md = ("Before.\n" + InsertSnippetBuilder.Workflow(new[] { "Plan", "Build", "Ship" })
                 + "\n" + InsertSnippetBuilder.Tabs(new[] { "One", "Two" })
                 + "\n:::datagrid\nlabel,value\nQ1,10\n:::\n\nAfter.").Replace('\n', '\r');
        var body = Body(Render(md));
        Assert.DoesNotContain(":::", body);
        Assert.Contains(">Ship<", body);
        Assert.Contains(">Two</label>", body);
        Assert.Contains("class=\"ms-datagrid\"", body);
        Assert.Contains("After.", body);
    }

    [Fact]
    public void Tabs_render_as_switchable_radio_group_with_every_panel()
    {
        var body = Body(Render(InsertSnippetBuilder.Tabs(new[] { "Alpha", "Beta" })));
        Assert.Contains("class=\"ms-tabs\"", body);
        Assert.Contains("<label for=\"ms-tabs-0-0\">Alpha</label>", body);
        Assert.Contains("<label for=\"ms-tabs-0-1\">Beta</label>", body);
        Assert.Contains("id=\"ms-tabs-0-0\" checked", body);
        Assert.Contains("Content 2", body);
        Assert.DoesNotContain("=== Alpha", body);
        Assert.Contains("@media print", body);   // print/PDF shows every panel
    }

    [Fact]
    public void Tabs_accept_nested_tab_containers_and_ignore_headers_inside_code()
    {
        var md = ":::tabs\n:::tab title=\"Windows\"\nRun it.\n```\n=== not a tab\n```\n:::\n:::tab title=\"macOS\"\nDrag it.\n:::\n:::\n\nTail paragraph.";
        var tabs = ContainerBlockParsers.ParseTabs(md.Split('\n', 2)[1]);
        Assert.Equal(new[] { "Windows", "macOS" }, tabs.Select(t => t.Title));
        Assert.Contains("=== not a tab", tabs[0].Content);

        var body = Body(Render(md));
        Assert.Contains(">macOS</label>", body);
        Assert.Contains("Tail paragraph.", body);
        Assert.DoesNotContain(":::", body);
    }

    [Fact]
    public void Two_tab_groups_get_distinct_radio_names_and_one_stylesheet()
    {
        var md = InsertSnippetBuilder.Tabs(new[] { "A", "B" }) + "\nMiddle\n" + InsertSnippetBuilder.Tabs(new[] { "C", "D" });
        var body = Body(Render(md));
        Assert.Contains("name=\"ms-tabs-0\"", body);
        Assert.Contains("name=\"ms-tabs-1\"", body);
        Assert.Equal(1, CountOf(body, ".ms-tabs{margin"));
    }

    [Fact]
    public void Datagrid_renders_a_table_with_header_and_right_aligned_numbers()
    {
        var body = Body(Render(":::datagrid\nRegion,Sales\nNorth,1200\nSouth,980\n:::\n"));
        Assert.Contains("class=\"ms-datagrid\"", body);
        Assert.Contains(">Region</th>", body);
        Assert.Contains("text-align:right\">Sales</th>", body);
        Assert.Contains(">1200</td>", body);
    }

    [Fact]
    public void Datagrid_reads_pipe_rows_and_skips_the_separator()
    {
        var rows = ContainerBlockParsers.ParseDatagrid("| A | B |\n|---|---|\n| 1 | 2 |\n| 3 |");
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows[0]);
        Assert.Equal(new[] { "3", "" }, rows[2]);   // short row padded
    }

    [Theory]
    [InlineData("GitHub Light", "#ffffff")]   // black primary -> white header text
    [InlineData("GitHub Dark", "#111111")]    // light-grey primary -> dark header text
    public void Datagrid_header_text_contrasts_with_the_theme_primary(string theme, string expectedFg)
    {
        var body = Body(Render(":::datagrid\nA,B\n1,2\n:::\n", theme));
        Assert.Contains($"color:{expectedFg};", body);
    }

    [Fact]
    public void References_render_as_a_bibliography_list()
    {
        var md = ":::references\n@smith2024\nauthor: Smith, J.\ntitle: Polished Software\nyear: 2024\nurl: https://example.com/p\n\n@doe\nauthor: Doe, A.\ntitle: Draft\n:::\n";
        var body = Body(Render(md));
        Assert.Contains("<h2>Bibliography</h2>", body);
        Assert.Contains("id=\"ref-smith2024\"", body);
        Assert.Contains("Smith, J. (2024). <em>Polished Software</em>.", body);
        Assert.Contains("Doe, A. (n.d.). <em>Draft</em>.", body);
        Assert.Contains("href=\"https://example.com/p\"", body);
    }

    [Fact]
    public void Embed_renders_a_card_that_links_to_the_video()
    {
        var body = Body(Render(":::embed provider=\"vimeo\" src=\"https://vimeo.com/76979871\"\nLaunch trailer\n:::\n"));
        Assert.Contains("class=\"ms-embed\" href=\"https://vimeo.com/76979871\"", body);
        Assert.Contains("Launch trailer", body);
        Assert.Contains("Vimeo · vimeo.com", body);
    }

    [Fact]
    public void Embed_never_links_a_non_web_url()
    {
        var body = Body(Render(":::embed src=\"javascript:alert(1)\"\n:::\n"));
        Assert.DoesNotContain("href=\"javascript", body);
        Assert.Contains("isn't a web address", body);
    }

    [Theory]
    [InlineData("https://youtu.be/abc", "youtube")]
    [InlineData("https://www.youtube.com/watch?v=x", "youtube")]
    [InlineData("https://player.vimeo.com/video/1", "vimeo")]
    [InlineData("https://www.loom.com/share/x", "loom")]
    [InlineData("https://codepen.io/a/pen/b", "codepen")]
    [InlineData("https://example.com", null)]
    [InlineData("not a url", null)]
    public void Embed_provider_is_detected_from_the_url(string url, string? expected) =>
        Assert.Equal(expected, ContainerBlockParsers.DetectProvider(url));

    [Fact]
    public void Ai_context_renders_as_a_metadata_panel_and_escapes_values()
    {
        var body = Body(Render(":::ai-context\nmodel: <b>X</b>\ntimestamp: 2026-10-07\n:::\n"));
        Assert.Contains("class=\"ms-ai-context\"", body);
        Assert.Contains("&lt;b&gt;X&lt;/b&gt;", body);
        Assert.Contains(">timestamp</dt>", body);
    }

    [Fact]
    public void Workflow_and_timeline_draw_their_own_layouts_not_the_guessers()
    {
        // Four plain steps used to be guessed as a 2x2 block list.
        var wf = Body(Render(":::workflow\n- Draft\n- Review\n- Approve\n- Publish\n:::\n"));
        Assert.Contains("data-family=\"Process\"", wf);
        var tl = Body(Render(":::timeline\n- 2020: Founded\n- 2024: Launch\n:::\n"));
        Assert.Contains("data-family=\"Timeline\"", tl);
        Assert.DoesNotContain("Layout:  (", tl);
    }

    [Fact]
    public void Containers_inside_code_fences_stay_as_code()
    {
        var body = Body(Render("```markdown\n:::datagrid\nA,B\n:::\n```\n"));
        Assert.DoesNotContain("ms-datagrid", body);
        Assert.Contains(":::datagrid", body);
    }

    [Fact]
    public void Unclosed_container_is_left_alone()
    {
        var body = Body(Render(":::datagrid\nA,B\n1,2\n"));
        Assert.DoesNotContain("ms-datagrid", body);
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
