using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class EmojiReplacerTests
{
    [Theory]
    [InlineData("rocket", "🚀")]
    [InlineData("smile", "😄")]
    [InlineData("heart", "❤️")]
    [InlineData("fire", "🔥")]
    [InlineData("party", "🎉")]
    [InlineData("bulb", "💡")]
    [InlineData("zap", "⚡")]
    [InlineData("globe", "🌐")]
    public void ReplaceShortcode_ResolvesEveryKnownName(string key, string emoji)
    {
        Assert.Equal(emoji, EmojiReplacer.ReplaceShortcode(key, "::" + key + "::"));
    }

    [Fact]
    public void ReplaceShortcode_IsCaseInsensitive()
    {
        Assert.Equal("🚀", EmojiReplacer.ReplaceShortcode("ROCKET", "::ROCKET::"));
        Assert.Equal("🚀", EmojiReplacer.ReplaceShortcode("RoCkEt", "::RoCkEt::"));
    }

    [Fact]
    public void ReplaceShortcode_ReturnsFallbackForUnknownName()
    {
        Assert.Equal("::notanemoji::", EmojiReplacer.ReplaceShortcode("notanemoji", "::notanemoji::"));
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_ReplacesKnownShortcodeInProse()
    {
        var result = EmojiReplacer.ReplaceDoubleColonShortcodes("Launch ::rocket:: now");
        Assert.Equal("Launch 🚀 now", result);
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_LeavesUnknownShortcodeUntouched()
    {
        var input = "This is ::notanemoji:: text";
        Assert.Equal(input, EmojiReplacer.ReplaceDoubleColonShortcodes(input));
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_LeavesCppScopeResolutionUntouched()
    {
        // std::vector::foo shouldn't be mistaken for a shortcode: "vector" isn't a known emoji name.
        var input = "std::vector::foo";
        Assert.Equal(input, EmojiReplacer.ReplaceDoubleColonShortcodes(input));
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_ReplacesMultipleSeparateShortcodes()
    {
        var result = EmojiReplacer.ReplaceDoubleColonShortcodes("::fire:: and ::zap:: and ::bulb::");
        Assert.Equal("🔥 and ⚡ and 💡", result);
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_IsCaseInsensitive()
    {
        var result = EmojiReplacer.ReplaceDoubleColonShortcodes("::ROCKET::");
        Assert.Equal("🚀", result);
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_HandlesEmptyAndPlainText()
    {
        Assert.Equal("", EmojiReplacer.ReplaceDoubleColonShortcodes(""));
        Assert.Equal("no shortcodes here", EmojiReplacer.ReplaceDoubleColonShortcodes("no shortcodes here"));
    }

    [Fact]
    public void ReplaceDoubleColonShortcodes_SupportsUnderscoreAndHyphenAndPlusNames()
    {
        // The shortcode pattern allows letters, digits, '_', '+' and '-'; unknown names of that
        // shape should still pass through untouched rather than throwing or being mangled.
        var input = "::thumbs_up-2+:: stays literal";
        Assert.Equal(input, EmojiReplacer.ReplaceDoubleColonShortcodes(input));
    }
}
