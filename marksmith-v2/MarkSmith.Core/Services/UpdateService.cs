using System.Net.Http;
using System.Text.Json;

namespace MdToPdf.Services;

// Checks GitHub Releases for a newer version. Works against the public releases API; while the repo
// is private the API returns 404, which surfaces as a friendly "couldn't check" rather than an error.
public sealed class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/thebubbsy/marksmith/releases/latest";
    public const string RepoUrl = "https://github.com/thebubbsy/marksmith";
    public const string ReleasesUrl = RepoUrl + "/releases";

    public string CurrentVersion
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public sealed record Result(bool Ok, bool UpdateAvailable, string LatestTag, string ReleaseUrl, string Message);

    public async Task<Result> CheckAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Marksmith-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var json = await http.GetStringAsync(LatestReleaseApi);
            return EvaluateReleaseJson(json, CurrentVersion);
        }
        catch (HttpRequestException)
        {
            return new(false, false, "", ReleasesUrl,
                "Couldn't reach the releases feed — the repository may be private, or you're offline.");
        }
        catch (Exception ex)
        {
            return new(false, false, "", ReleasesUrl, $"Update check failed: {ex.Message}");
        }
    }

    // Parses a GitHub "releases/latest" JSON payload and decides whether an update is available.
    // Extracted from CheckAsync so the parse + version-decision contract is unit-testable without a
    // network call. Returns Ok=false with a friendly message when the payload carries no tag (e.g.
    // the repo is private and the API answered with an error body instead of a release).
    internal static Result EvaluateReleaseJson(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;

        if (string.IsNullOrWhiteSpace(tag))
            return new(false, false, "", ReleasesUrl, "The releases feed returned no tag information.");

        var latest = tag.TrimStart('v', 'V');
        if (Compare(latest, currentVersion) > 0)
            return new(true, true, tag, url, $"Update available — {tag}. You have {currentVersion}.");
        return new(true, false, tag, url, $"You're up to date (v{currentVersion}).");
    }

    // Numeric dotted-version compare; returns >0 if a is newer than b.
    // Compares two versions. Handles up to four numeric components (a build-number-only bump like
    // 1.0.0.9 vs 1.0.0.5 is now detected) and treats a prerelease as LOWER than the same x.y.z
    // stable (1.2.0-beta < 1.2.0), per SemVer — so a stable release is offered over a prerelease
    // of the same core version, and the two aren't reported "equal".
    internal static int Compare(string a, string b)
    {
        var (na, pra) = Parse(a);
        var (nb, prb) = Parse(b);
        for (var i = 0; i < 4; i++)
        {
            var c = na[i].CompareTo(nb[i]);
            if (c != 0) return c;
        }
        // Equal numeric core: a prerelease (has a suffix) ranks below a stable (no suffix).
        if (pra == prb) return 0;
        if (pra && !prb) return -1;
        if (!pra && prb) return 1;
        return 0;
    }

    private static (int[] Numbers, bool IsPrerelease) Parse(string v)
    {
        v = v.Trim().TrimStart('v', 'V').Trim();
        var dash = v.IndexOf('-');
        var isPre = dash >= 0;
        var core = isPre ? v[..dash] : v;
        var parts = core.Split('.');
        var r = new int[4];
        for (var i = 0; i < 4 && i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
        return (r, isPre);
    }
}
