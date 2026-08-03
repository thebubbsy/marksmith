using System.IO;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class RelativeLinkResolverServiceTests
{
    // An absolute, platform-neutral root; Expect() mirrors the service's normalization so assertions
    // hold regardless of drive letter or directory separator.
    private static readonly string Root = Path.GetFullPath(Path.Combine("repo", "docs"));

    private static string Expect(string relative) =>
        Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)))
            .Replace('\\', '/');

    [Fact]
    public void Resolve_CombinesRelativePathsAgainstRoot()
    {
        Assert.Equal(Expect("./guide.md"), RelativeLinkResolverService.Resolve("./guide.md", Root));
        Assert.Equal(Expect("sub/page.md"), RelativeLinkResolverService.Resolve("sub/page.md", Root));
    }

    [Fact]
    public void Resolve_NormalizesParentSegments()
    {
        // ../img.png from repo/docs lands in repo/img.png — the ".." is collapsed by GetFullPath.
        Assert.Equal(Expect("../img.png"), RelativeLinkResolverService.Resolve("../img.png", Root));
        Assert.DoesNotContain("/docs/", RelativeLinkResolverService.Resolve("../img.png", Root));
    }

    [Fact]
    public void Resolve_PreservesTrailingFragment()
    {
        Assert.Equal(Expect("doc.md") + "#heading", RelativeLinkResolverService.Resolve("doc.md#heading", Root));
    }

    [Fact]
    public void Resolve_LeavesExternalUrlsAndAnchorsUntouched()
    {
        Assert.Equal("https://example.com/a.md", RelativeLinkResolverService.Resolve("https://example.com/a.md", Root));
        Assert.Equal("mailto:a@b.com", RelativeLinkResolverService.Resolve("mailto:a@b.com", Root));
        Assert.Equal("#section", RelativeLinkResolverService.Resolve("#section", Root));
        Assert.Equal("", RelativeLinkResolverService.Resolve("", Root));
    }

    [Fact]
    public void IsRelativeFileLink_ClassifiesCorrectly()
    {
        Assert.True(RelativeLinkResolverService.IsRelativeFileLink("./a.md"));
        Assert.True(RelativeLinkResolverService.IsRelativeFileLink("../a.md"));
        Assert.True(RelativeLinkResolverService.IsRelativeFileLink("img.png"));
        Assert.False(RelativeLinkResolverService.IsRelativeFileLink("https://x.com"));
        Assert.False(RelativeLinkResolverService.IsRelativeFileLink("#anchor"));
        Assert.False(RelativeLinkResolverService.IsRelativeFileLink("mailto:x@y.com"));
        Assert.False(RelativeLinkResolverService.IsRelativeFileLink(""));
    }

    [Fact]
    public void ResolveMarkdown_RewritesRelativeTargetsOnly()
    {
        var md = "See [guide](./guide.md) and ![pic](../img.png) plus [web](https://x.com).";
        var result = RelativeLinkResolverService.ResolveMarkdown(md, Root);

        Assert.Contains("](" + Expect("./guide.md") + ")", result);
        Assert.Contains("](" + Expect("../img.png") + ")", result);
        Assert.Contains("(https://x.com)", result); // external untouched
    }
}
