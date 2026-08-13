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
}
