using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using Xunit;

namespace MarkSmith.Core.Tests;

public class SmartArtLayoutSuggesterTests
{
    private static string? Suggest(string markdown)
        => SmartArtLayoutSuggester.Suggest(MarkdownAstParser.Parse(markdown));

    [Fact]
    public void Flat_bullet_list_is_a_list_not_a_hierarchy()
    {
        var md = "Skills:\n- Python\n- Go\n- TypeScript\n- Rust\n";
        Assert.Equal("list", Suggest(md));
    }

    [Fact]
    public void Nested_list_is_a_hierarchy()
    {
        var md = "- Engineering\n  - Backend\n  - Frontend\n- Design\n  - UX\n";
        Assert.Equal("hierarchy", Suggest(md));
    }

    [Fact]
    public void Numbered_steps_are_a_process()
    {
        var md = "- 1. Plan\n- 2. Build\n- 3. Test\n- 4. Ship\n";
        Assert.Equal("process", Suggest(md));
    }

    [Fact]
    public void Step_worded_items_are_a_process()
    {
        var md = "- First, gather requirements\n- Next, design the API\n- Then, implement\n- Finally, ship\n";
        Assert.Equal("process", Suggest(md));
    }

    [Fact]
    public void Plain_prose_has_no_smartart_shape()
    {
        var md = "Just a paragraph of prose with no list structure at all in it.\n";
        Assert.Null(Suggest(md));
    }

    [Fact]
    public void Cycle_pattern_is_a_cycle()
    {
        var md = "- Plan the sprint\n- Do the work\n- Check the results\n- Act and Iterate\n";
        Assert.Equal("cycle", Suggest(md));
    }

    [Fact]
    public void Continuous_loop_is_a_cycle()
    {
        var md = "- Identify problem\n- Design solution\n- Deploy changes\n- Feedback loop and Repeat\n";
        Assert.Equal("cycle", Suggest(md));
    }

    [Fact]
    public void Pyramid_tiers_are_a_pyramid()
    {
        var md = "- Tier 1: Core Foundation\n- Tier 2: Application Logic\n- Tier 3: Presentation Apex\n";
        Assert.Equal("pyramid", Suggest(md));
    }

    [Fact]
    public void Maslow_levels_are_a_pyramid()
    {
        var md = "- Physiological needs\n- Safety needs\n- Belonging needs\n- Esteem needs\n- Self-Actualization\n";
        Assert.Equal("pyramid", Suggest(md));
    }

    [Fact]
    public void SWOT_matrix_is_a_matrix()
    {
        var md = "- Strengths in technology\n- Weaknesses in market reach\n- Opportunities in enterprise\n- Threats from competitors\n";
        Assert.Equal("matrix", Suggest(md));
    }

    [Fact]
    public void Timeline_dates_are_a_process()
    {
        var md = "- 2024: Architecture design\n- 2025: Global rollout\n- 2026: Optimization\n";
        Assert.Equal("process", Suggest(md));
    }

    [Fact]
    public void Venn_pattern_is_a_venn()
    {
        var md = "Design Thinking:\n- Desirable to users\n- Feasible technologically\n- Viable economically\n";
        Assert.Equal("venn", Suggest(md));
    }

    [Fact]
    public void Picture_list_is_a_picturelist()
    {
        var md = "- ![Architecture diagram](images/arch.png)\n- ![Database schema](images/db.png)\n";
        Assert.Equal("picturelist", Suggest(md));
    }
}
