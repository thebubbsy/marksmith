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
    public void NoRules_IsNoOp()
    {
        var (cleaned, fixes) = Run("plain text");
        Assert.Equal("plain text", cleaned);
        Assert.DoesNotContain(fixes, f => f.StartsWith("Custom:"));
    }
}
