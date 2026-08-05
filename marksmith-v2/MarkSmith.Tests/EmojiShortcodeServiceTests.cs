using Xunit;
using MarkSmith.Services;

namespace MarkSmith.Tests;

public class EmojiShortcodeServiceTests
{
    [Fact]
    public void ExpandsKnownShortcodes()
    {
        var input = "This is a ::rocket:: and a ::sparkles::!";
        var expected = "This is a 🚀 and a ✨!";
        var result = EmojiShortcodeService.Expand(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IgnoresUnknownShortcodes()
    {
        var input = "This is an ::unknown:: shortcode.";
        var expected = "This is an ::unknown:: shortcode.";
        var result = EmojiShortcodeService.Expand(input);
        Assert.Equal(expected, result);
    }
}