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

            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
            var latest = tag.TrimStart('v', 'V');

            if (Compare(latest, CurrentVersion) > 0)
                return new(true, true, tag, url, $"Update available — {tag}. You have {CurrentVersion}.");
            return new(true, false, tag, url, $"You're up to date (v{CurrentVersion}).");
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

    // Numeric dotted-version compare; returns >0 if a is newer than b.
    private static int Compare(string a, string b)
    {
        int[] pa = Parse(a), pb = Parse(b);
        for (var i = 0; i < 3; i++)
        {
            var c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int[] Parse(string v)
    {
        var parts = v.Split('.', '-');
        var r = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
        return r;
    }
}
