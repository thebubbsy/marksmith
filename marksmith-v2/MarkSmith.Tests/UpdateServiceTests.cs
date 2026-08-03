using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

// Task 13 — Auto-Updater. The network call itself (CheckAsync) is thin; the decision logic that
// matters is the dotted-version comparison and the GitHub "releases/latest" JSON evaluation, both
// exercised here directly (internal helpers are visible to this assembly via InternalsVisibleTo).
public sealed class UpdateServiceTests
{
    // ---- Compare: ordering ----

    [Theory]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("1.0.1", "1.0.0")]
    public void Compare_reports_newer_version_as_greater(string newer, string older)
    {
        Assert.True(UpdateService.Compare(newer, older) > 0);
        Assert.True(UpdateService.Compare(older, newer) < 0);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("2.1.0", "2.1.0")]
    [InlineData("0.0.1", "0.0.1")]
    public void Compare_reports_equal_versions_as_zero(string a, string b)
    {
        Assert.Equal(0, UpdateService.Compare(a, b));
    }

    [Fact]
    public void Compare_handles_four_component_build_bumps()
    {
        // A build-number-only bump (1.0.0.9 vs 1.0.0.5) must still register as newer.
        Assert.True(UpdateService.Compare("1.0.0.9", "1.0.0.5") > 0);
        Assert.True(UpdateService.Compare("1.0.0", "1.0.0.5") < 0); // missing component reads as 0
    }

    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("V2.1.0", "v2.1.0")]
    [InlineData(" v1.2.3 ", "1.2.3")]
    public void Compare_ignores_leading_v_prefix_and_whitespace(string a, string b)
    {
        Assert.Equal(0, UpdateService.Compare(a, b));
    }

    // ---- Compare: SemVer prerelease ranking ----

    [Fact]
    public void Compare_ranks_prerelease_below_same_core_stable()
    {
        Assert.True(UpdateService.Compare("1.2.0-beta", "1.2.0") < 0);
        Assert.True(UpdateService.Compare("1.2.0", "1.2.0-rc.1") > 0);
    }

    [Fact]
    public void Compare_treats_two_prereleases_of_same_core_as_equal_core()
    {
        // Both carry a suffix on the same numeric core: neither is the stable release, so they rank
        // equal for the purposes of "is there a newer stable to offer".
        Assert.Equal(0, UpdateService.Compare("1.2.0-alpha", "1.2.0-beta"));
    }

    [Fact]
    public void Compare_tolerates_malformed_or_empty_input()
    {
        // Garbage parses to 0.0.0.0 rather than throwing — the updater must never crash on a bad tag.
        Assert.Equal(0, UpdateService.Compare("", ""));
        Assert.True(UpdateService.Compare("1.0.0", "not-a-version") > 0);
        Assert.True(UpdateService.Compare("garbage", "1.0.0") < 0);
    }

    // ---- EvaluateReleaseJson: the GitHub payload contract ----

    [Fact]
    public void EvaluateReleaseJson_flags_update_when_tag_is_newer()
    {
        const string json = """{"tag_name":"v2.1.0","html_url":"https://github.com/thebubbsy/marksmith/releases/tag/v2.1.0"}""";
        var r = UpdateService.EvaluateReleaseJson(json, "2.0.0");

        Assert.True(r.Ok);
        Assert.True(r.UpdateAvailable);
        Assert.Equal("v2.1.0", r.LatestTag);
        Assert.Contains("v2.1.0", r.ReleaseUrl);
    }

    [Fact]
    public void EvaluateReleaseJson_reports_up_to_date_when_tag_matches_or_is_older()
    {
        var same = UpdateService.EvaluateReleaseJson("""{"tag_name":"v2.0.0"}""", "2.0.0");
        Assert.True(same.Ok);
        Assert.False(same.UpdateAvailable);

        var older = UpdateService.EvaluateReleaseJson("""{"tag_name":"v1.9.0"}""", "2.0.0");
        Assert.True(older.Ok);
        Assert.False(older.UpdateAvailable);
    }

    [Fact]
    public void EvaluateReleaseJson_falls_back_to_releases_url_when_html_url_missing()
    {
        var r = UpdateService.EvaluateReleaseJson("""{"tag_name":"v3.0.0"}""", "2.0.0");
        Assert.Equal(UpdateService.ReleasesUrl, r.ReleaseUrl);
    }

    [Fact]
    public void EvaluateReleaseJson_returns_not_ok_when_tag_missing()
    {
        // A private repo answers the "latest" endpoint with an error body that has no tag_name.
        var r = UpdateService.EvaluateReleaseJson("""{"message":"Not Found"}""", "2.0.0");
        Assert.False(r.Ok);
        Assert.False(r.UpdateAvailable);
        Assert.Equal("", r.LatestTag);
    }
}
