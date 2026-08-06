using MarkSmith.Services;
using Xunit;
using MarkSmith.Models;
using Xunit;

namespace MarkSmith.Tests;

public class CustomNormalizationRuleTests
{
    private static (string Cleaned, List<string> Fixes) Run(string md, params TextCleanupRule[] rules)
    {
        var svc = new LlmSourceService();
        var classification = svc.Classify(md);
        return svc.NormalizeStyle(md, classification, rules);
    }

    [Fact]
    public void PlainRule_StripsBuzzwordCaseInsensitively()
    {
        var (cleaned, fixes) = Run("In conclusion, this is great.\n\nDelve into the details.\n\nIN CONCLUSION, again.",
            new TextCleanupRule { Find = "In conclusion, ", Replace = "" },
            new TextCleanupRule { Find = "delve" });
        Assert.DoesNotContain("In conclusion", cleaned);
        Assert.DoesNotContain("Delve", cleaned);
        Assert.DoesNotContain("IN CONCLUSION", cleaned);
        Assert.Contains("this is great", cleaned);
        Assert.Equal(2, fixes.Count(r => r.StartsWith("Custom:")));
    }

    [Fact]
    public void PlainRule_ReplacesPhrase()
    {
        var (cleaned, _) = Run("As an AI, I think X. As an AI, I think Y.",
            new TextCleanupRule { Find = "As an AI, I think ", Replace = "" });
        Assert.Equal("X. Y.", cleaned);
    }

    [Fact]
    public void RegexRule_UsesCaptures()
    {
        var (cleaned, _) = Run("Note: this is point one. Note: this is point two.",
            new TextCleanupRule { Find = @"Note: (this is point [a-z]+)", Replace = "$1", IsRegex = true });
        Assert.Equal("this is point one. this is point two.", cleaned);
    }

    [Fact]
    public void BlankFind_IsIgnored()
    {
        var (cleaned, fixes) = Run("Hello world", new TextCleanupRule { Find = "   " });
        Assert.Equal("Hello world", cleaned);
        Assert.DoesNotContain(fixes, f => f.StartsWith("Custom:"));
    }

    [Fact]
    public void BadRegex_IsSkippedNotThrown()
    {
        var (cleaned, fixes) = Run("hello", new TextCleanupRule { Find = "([unclosed", IsRegex = true });
        Assert.Equal("hello", cleaned);
        Assert.Contains(fixes, f => f.Contains("skipped"));
    }

    [Fact]
    public void Defaults_AreOneRegexTwoPlain_AndWireInHarmlessly()
    {
        var defaults = AppSettings.DefaultCustomNormalizationRules();
        Assert.Equal(3, defaults.Count);
        Assert.Equal(1, defaults.Count(r => r.IsRegex));
        Assert.Equal(2, defaults.Count(r => !r.IsRegex));

        // The examples mirror built-in fixes, so applying them to already-clean text is a no-op...
        var (cleaned, _) = Run("# Title\n\nplain text");
        Assert.Equal("# Title\n\nplain text", cleaned);

        // ...and each example actually does its advertised job on its target text.
        var (d, _) = Run("Some body.\n\nChatGPT can make mistakes. Check important info.",
            defaults[0]);
        Assert.DoesNotContain("ChatGPT can make mistakes", d);

        // A 2-character bold ("**xy**") is below the built-in's 3-char minimum, so this proves the
        // REGEX example itself fired (not the built-in pseudo-heading pass) and promoted it.
        var (h, _) = Run("**xy**", defaults[1]);
        Assert.Contains("### xy", h);

        var (b, _) = Run("a\n\n\n\nb", defaults[2]);
        Assert.Contains("a\n\nb", b); // built-in collapsed first; the plain example no-ops harmlessly
    }

    [Fact]
    public void NoRules_IsNoOp()
    {
        var (cleaned, fixes) = Run("plain text");
        Assert.Equal("plain text", cleaned);
        Assert.DoesNotContain(fixes, f => f.StartsWith("Custom:"));
    }
}
