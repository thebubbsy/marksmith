using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class FrontmatterServiceTests
{
    [Fact]
    public void Parse_Extracts_Yaml_Frontmatter()
    {
        var input = "---\ntitle: Sample Doc\nauthor: Antigravity\ndate: 2026-07-27\n---\n\n# Header\n\nContent body.";
        var result = FrontmatterService.Parse(input);

        Assert.Equal("Sample Doc", result.Metadata["title"]);
        Assert.Equal("Antigravity", result.Metadata["author"]);
        Assert.Equal("2026-07-27", result.Metadata["date"]);
        Assert.Equal("\n# Header\n\nContent body.", result.Content);
    }

    [Fact]
    public void Parse_Returns_Original_When_No_Frontmatter()
    {
        var input = "# Header\n\nContent body without frontmatter.";
        var result = FrontmatterService.Parse(input);

        Assert.Empty(result.Metadata);
        Assert.Equal(input, result.Content);
    }
}
