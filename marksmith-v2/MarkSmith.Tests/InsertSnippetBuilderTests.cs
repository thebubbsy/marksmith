using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Unit coverage for InsertSnippetBuilder — the Markdown generation behind the Insert-menu
/// modals (ProMode-off path). Pure functions, no UI. The "legacy" assertions pin the builders'
/// defaults to the exact raw placeholders the ProMode-on path inserts.
/// </summary>
public class InsertSnippetBuilderTests
{
    // ---- Table ---------------------------------------------------------------------------------

    [Fact]
    public void Table_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n| Header 1 | Header 2 |\n| --- | --- |\n| Value 1 | Value 2 |\n",
            InsertSnippetBuilder.Table(1, 2, includeHeaderRow: true));
    }

    [Fact]
    public void Table_scales_to_requested_rows_and_columns()
    {
        var md = InsertSnippetBuilder.Table(2, 3, includeHeaderRow: true);

        Assert.Contains("| Header 1 | Header 2 | Header 3 |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| Value 1 | Value 2 | Value 3 |", md);
        Assert.Contains("| Value 4 | Value 5 | Value 6 |", md);
    }

    [Fact]
    public void Table_without_header_still_emits_delimiter_row()
    {
        var md = InsertSnippetBuilder.Table(1, 2, includeHeaderRow: false);

        Assert.DoesNotContain("Header", md);
        Assert.StartsWith("\n| --- | --- |\n", md);
        Assert.Contains("| Value 1 | Value 2 |", md);
    }

    [Fact]
    public void Table_clamps_out_of_range_dimensions()
    {
        var md = InsertSnippetBuilder.Table(0, 0, includeHeaderRow: true);
        Assert.Equal("\n| Header 1 |\n| --- |\n| Value 1 |\n", md);
    }

    // ---- Link / code block ---------------------------------------------------------------------

    [Fact]
    public void Link_builds_markdown_and_falls_back_to_url_placeholder()
    {
        Assert.Equal("[docs](https://x.y)", InsertSnippetBuilder.Link("docs", "https://x.y"));
        Assert.Equal("[docs](url)", InsertSnippetBuilder.Link(" docs ", ""));
        Assert.Equal("[](url)", InsertSnippetBuilder.Link("", ""));
    }

    [Fact]
    public void CodeBlock_emits_fenced_block_with_language()
    {
        Assert.Equal("\n```csharp\nvar x = 1;\n```\n", InsertSnippetBuilder.CodeBlock("csharp", "var x = 1;"));
        Assert.Equal("\n```\n\n```\n", InsertSnippetBuilder.CodeBlock("", ""));
    }

    // ---- Container blocks ----------------------------------------------------------------------

    [Fact]
    public void Chart_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::chart type=\"bar\"\nQ1,10\nQ2,25\nQ3,15\n:::\n",
            InsertSnippetBuilder.Chart("bar", new[] { "Q1,10", "Q2,25", "Q3,15" }));
    }

    [Fact]
    public void Chart_skips_blank_lines_and_honours_type()
    {
        var md = InsertSnippetBuilder.Chart("pie", new[] { " A,1 ", "", "B,2" });
        Assert.Equal("\n:::chart type=\"pie\"\nA,1\nB,2\n:::\n", md);
    }

    [Fact]
    public void Columns_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::columns count=\"2\"\nColumn 1 content\n===\nColumn 2 content\n:::\n",
            InsertSnippetBuilder.Columns(2));
    }

    [Fact]
    public void Columns_separates_each_column_and_clamps_to_4()
    {
        var md = InsertSnippetBuilder.Columns(9);
        Assert.StartsWith("\n:::columns count=\"4\"\n", md);
        Assert.Contains("Column 4 content", md);
        Assert.DoesNotContain("Column 5", md);
        Assert.Equal(3, md.Split("===\n").Length - 1);
    }

    [Fact]
    public void SmartArt_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::smartart type=\"process\"\n- Step 1\n- Step 2\n:::\n",
            InsertSnippetBuilder.SmartArt("process", new[] { "Step 1", "Step 2" }));
    }

    [Fact]
    public void Bulleted_blocks_prefix_steps_without_double_bullets()
    {
        var md = InsertSnippetBuilder.Workflow(new[] { "Step 1", "- Step 2", "  " });
        Assert.Equal("\n:::workflow\n- Step 1\n- Step 2\n:::\n", md);
    }

    [Fact]
    public void Workflow_falls_back_to_single_step_when_input_empty()
    {
        Assert.Equal("\n:::workflow\n- Step 1\n:::\n", InsertSnippetBuilder.Workflow(Array.Empty<string>()));
    }

    [Fact]
    public void Timeline_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::timeline\n- 2020: Started\n- 2023: Progress\n- 2026: Done\n:::\n",
            InsertSnippetBuilder.Timeline(new[] { "2020: Started", "2023: Progress", "2026: Done" }));
    }

    [Fact]
    public void Tabs_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::tabs\n=== Tab 1\nContent 1\n=== Tab 2\nContent 2\n:::\n",
            InsertSnippetBuilder.Tabs(new[] { "Tab 1", "Tab 2" }));
    }

    [Fact]
    public void Tabs_number_content_per_title_and_strip_stray_prefix()
    {
        var md = InsertSnippetBuilder.Tabs(new[] { "Alpha", "=== Beta" });
        Assert.Equal("\n:::tabs\n=== Alpha\nContent 1\n=== Beta\nContent 2\n:::\n", md);
    }

    // ---- Embed / references / datagrid / canvas --------------------------------------------------

    [Fact]
    public void Embed_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::embed provider=\"youtube\" src=\"https://www.youtube.com/watch?v=dQw4w9WgXcQ\"\n:::\n",
            InsertSnippetBuilder.Embed("", ""));
    }

    [Fact]
    public void Embed_honours_provider_and_url()
    {
        Assert.Equal("\n:::embed provider=\"vimeo\" src=\"https://vimeo.com/123\"\n:::\n",
            InsertSnippetBuilder.Embed("vimeo", " https://vimeo.com/123 "));
    }

    [Fact]
    public void References_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::references\n@paper-id\nauthor: Author Name\ntitle: Publication Title\nyear: 2026\n:::\n",
            InsertSnippetBuilder.References("", "", "", ""));
    }

    [Fact]
    public void References_honours_entered_fields()
    {
        Assert.Equal("\n:::references\n@knuth84\nauthor: Donald Knuth\ntitle: Literate Programming\nyear: 1984\n:::\n",
            InsertSnippetBuilder.References("knuth84", "Donald Knuth", "Literate Programming", "1984"));
    }

    [Fact]
    public void Datagrid_defaults_match_legacy_placeholder()
    {
        Assert.Equal("\n:::datagrid\nlabel,value\nQ1,10\nQ2,25\n:::\n",
            InsertSnippetBuilder.Datagrid(Array.Empty<string>()));
    }

    [Fact]
    public void Datagrid_keeps_first_line_as_header()
    {
        Assert.Equal("\n:::datagrid\nname,qty\napples,3\n:::\n",
            InsertSnippetBuilder.Datagrid(new[] { "name,qty", "apples,3" }));
    }

    [Fact]
    public void Canvas_scales_scaffold_to_requested_size()
    {
        var md = InsertSnippetBuilder.Canvas(200, 100);

        Assert.StartsWith("\n:::canvas\n", md);
        Assert.Contains("<svg viewBox=\"0 0 200 100\" width=\"200\" height=\"100\">", md);
        Assert.Contains("cx=\"100\" cy=\"50\" r=\"40\"", md); // centred, r = 40% of the smaller side
        Assert.EndsWith("</svg>\n:::\n", md);
    }
}
