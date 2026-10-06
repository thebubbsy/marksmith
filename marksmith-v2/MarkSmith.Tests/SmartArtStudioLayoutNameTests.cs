using MarkSmith.ViewModels.SmartArtStudio;
using Xunit;

namespace MarkSmith.Core.Tests;

public class SmartArtStudioLayoutNameTests
{
    [Theory]
    [InlineData("AlternatingCircleProcess", "Alternating Circle Process")]
    [InlineData("arrow1", "Arrow 1")]
    [InlineData("architecture", "Architecture")]
    [InlineData("SWOTMatrix", "SWOT Matrix")]
    [InlineData("hub_and-spoke", "Hub and spoke")]
    public void DisplayName_Humanizes_Untitled_Layouts(string alias, string expected)
    {
        var item = new StudioLayoutItem { Name = alias, Alias = alias };
        Assert.Equal(expected, item.DisplayName);
    }

    [Fact]
    public void DisplayName_Keeps_A_Real_Title()
    {
        var item = new StudioLayoutItem { Name = "Basic Block List", Alias = "default" };
        Assert.Equal("Basic Block List", item.DisplayName);
    }
}
