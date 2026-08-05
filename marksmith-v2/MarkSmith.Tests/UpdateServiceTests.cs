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
    public void EvaluateReleaseJson_extracts_matching_setup_asset_url()
    {
        const string json = """
        {
          "tag_name": "v2.13.0",
          "html_url": "https://github.com/thebubbsy/marksmith/releases/tag/v2.13.0",
          "assets": [
            {
              "name": "Marksmith-Setup-x64.exe",
              "browser_download_url": "https://github.com/thebubbsy/marksmith/releases/download/v2.13.0/Marksmith-Setup-x64.exe"
            },
            {
              "name": "Marksmith-Setup-arm64.exe",
              "browser_download_url": "https://github.com/thebubbsy/marksmith/releases/download/v2.13.0/Marksmith-Setup-arm64.exe"
            }
          ]
        }
        """;

        var r = UpdateService.EvaluateReleaseJson(json, "2.0.0");
        Assert.True(r.Ok);
        Assert.True(r.UpdateAvailable);
        Assert.NotEmpty(r.DownloadUrl);
        Assert.EndsWith(".exe", r.DownloadUrl);
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

    // Regression: the download loop must run off the UI thread (ConfigureAwait(false) + an
    // asynchronous FileStream) so a slow update download can never freeze the app, and the
    // progress callback must reach 100% when the payload lands intact. Served from a local
    // HttpListener so no network is involved; the "install" step is never reached because the
    // payload is not an executable, so the method returns false after a complete download.
    [Fact]
    public async Task DownloadAndInstallAsync_StreamsToDiskAndReportsProgress()
    {
        var payload = new byte[512 * 1024];
        new Random(42).NextBytes(payload);

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://localhost:51997/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.ContentLength64 = payload.Length; // HttpListener would otherwise chunk (no Content-Length -> no progress)
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.OutputStream.Close();
        });

        try
        {
            var reports = new List<double>();
            var progress = new Progress<double>(p => { lock (reports) reports.Add(p); });
            var result = await new UpdateService().DownloadAndInstallAsync("http://localhost:51997/update.exe", progress);
            Assert.False(result); // download succeeded; 'install' of a non-exe must fail cleanly

            var spool = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe");
            Assert.Equal(payload.Length, new System.IO.FileInfo(spool).Length);

            // Progress<T> posts callbacks asynchronously (no SynchronizationContext in the test
            // host), so wait for the final 100% report instead of asserting immediately.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            double last = -1;
            while (DateTime.UtcNow < deadline)
            {
                lock (reports) { if (reports.Count > 0) last = reports[^1]; }
                if (last >= 99.5) break;
                await Task.Delay(25);
            }
            Assert.True(last >= 99.5, $"final progress was {last:F1}% (reports={reports.Count})");
        }
        finally
        {
            listener.Stop();
            try { System.IO.File.Delete(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MarksmithUpdates", "Marksmith-Setup-Latest.exe")); } catch { }
        }
    }

    // ---- Auto-incrementing build version (revision = UTC timestamp) ----

    [Fact]
    public void Compare_autostamped_build_against_same_release_line_is_not_newer()
    {
        // A dev build carries a 2.14.0.<utc-timestamp> revision; the released tag is plain 2.14.0.
        // The timestamp revision must NOT make the tag look newer (no false 'update available'),
        // and it must survive the long parse (12-digit revision overflows int).
        Assert.True(UpdateService.Compare("2.14.0", "2.14.0.202608051200") < 0); // tag < dev build
        Assert.True(UpdateService.Compare("2.14.0.202608051200", "2.14.0") > 0); // symmetric
        Assert.Equal(0, UpdateService.Compare("2.14.0", "2.14.0.0"));
    }

    [Fact]
    public void Compare_next_minor_release_still_wins_over_autostamped_build()
    {
        Assert.True(UpdateService.Compare("2.15.0", "2.14.0.202608051200") > 0);
        Assert.True(UpdateService.Compare("2.14.1", "2.14.0.202608051200") > 0);
    }

    [Fact]
    public void CurrentVersion_includes_all_four_parts()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", new UpdateService().CurrentVersion);
    }
}
