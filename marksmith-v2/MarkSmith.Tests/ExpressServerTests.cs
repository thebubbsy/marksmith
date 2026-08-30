using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using MarkSmith.Express;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Express served a hard-coded list of six theme names — "Modern Clean", "Academic Formal" and so
/// on — that existed nowhere in <see cref="ThemeCatalog"/>. Every theme a caller chose, through the
/// UI or the documented curl command, silently fell back to the default. Its convert endpoint also
/// accepted only markdown/format/theme, so the rest of the export profile could not be reached at
/// all. These tests pin both.
/// </summary>
public sealed class ExpressServerTests : IDisposable
{
    private readonly ExpressServer _server = new();
    private readonly HttpClient _client = new();

    public ExpressServerTests()
    {
        // Port 0 is not offered, so start high and let the server walk to a free port if taken.
        _server.Start(48231, openBrowser: false);
        _client.BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}/");
        _client.Timeout = TimeSpan.FromMinutes(2);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
        => JsonDocument.Parse(await _client.GetStringAsync(path));

    private async Task<byte[]> ConvertAsync(object payload)
    {
        var res = await _client.PostAsJsonAsync("api/convert", payload);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static string DocumentXml(byte[] docx)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(docx));
        using var reader = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Themes_Endpoint_Serves_The_Real_Catalog()
    {
        using var doc = await GetJsonAsync("api/themes");
        var names = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(AppServices.Themes.All.Select(t => t.Name), names);
        Assert.Contains("GitHub Light", names);
        Assert.DoesNotContain("Modern Clean", names);   // the invented name that matched nothing
    }

    [Fact]
    public async Task Options_Endpoint_Serves_Themes_Fonts_And_Formats()
    {
        using var doc = await GetJsonAsync("api/options");
        var root = doc.RootElement;

        Assert.NotEmpty(root.GetProperty("themes").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("formats").EnumerateArray());

        var fonts = root.GetProperty("fonts").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()).ToList();
        Assert.Equal(FontManagerService.Presets.Select(p => p.Id), fonts);
    }

    [Fact]
    public async Task A_Chosen_Theme_Reaches_The_Exported_Document()
    {
        var dracula = DocumentXml(await ConvertAsync(new
        {
            markdown = "# Heading\n\nBody text.\n",
            format = "docx",
            options = new { theme = "Dracula" },
        }));

        // Dracula's page background; proves the name resolved instead of falling back.
        Assert.Contains("282a36", dracula, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, "Heading1")]
    [InlineData(2, "Heading3")]
    public async Task Profile_Fields_Are_Applied_Not_Ignored(int shift, string expectedStyle)
    {
        var xml = DocumentXml(await ConvertAsync(new
        {
            markdown = "# Heading\n\nBody text.\n",
            format = "docx",
            options = new { headingShift = shift },
        }));
        Assert.Contains($"w:val=\"{expectedStyle}\"", xml);
    }

    [Fact]
    public async Task NoEmoji_Strips_Emoji_From_The_Export()
    {
        var md = "# Launch \U0001F680\n\nShipping today.\n";
        Assert.Contains("\U0001F680", DocumentXml(await ConvertAsync(new { markdown = md, format = "docx" })));
        Assert.DoesNotContain("\U0001F680", DocumentXml(await ConvertAsync(new
        {
            markdown = md,
            format = "docx",
            options = new { noEmoji = true },
        })));
    }

    [Fact]
    public async Task NormalizeLlm_Runs_The_Same_Preparation_As_The_Desktop_Path()
    {
        // Express called the exporters directly and skipped classify/repair/normalize entirely, so
        // this promotion never happened and identical input produced different output to the app.
        const string md = "**Overview:**\n\nBody text.\n";

        var on = DocumentXml(await ConvertAsync(new
        {
            markdown = md, format = "docx", options = new { normalizeLlm = true },
        }));
        var off = DocumentXml(await ConvertAsync(new
        {
            markdown = md, format = "docx", options = new { normalizeLlm = false },
        }));

        Assert.Contains("w:val=\"Heading3\"", on);     // bold pseudo-heading promoted
        Assert.DoesNotContain("w:val=\"Heading3\"", off);
    }

    [Fact]
    public async Task Top_Level_Theme_Still_Works_For_The_Documented_One_Liner()
    {
        var xml = DocumentXml(await ConvertAsync(new
        {
            markdown = "# Heading\n", format = "docx", theme = "Dracula",
        }));
        Assert.Contains("282a36", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Download_Is_Named_After_The_Source_File()
    {
        var res = await _client.PostAsJsonAsync("api/convert", new
        {
            markdown = "# Heading\n",
            format = "docx",
            fileName = "Q4 Review.md",
        });
        res.EnsureSuccessStatusCode();

        // Read the raw header: the typed ContentDisposition parser is stricter than the browsers
        // this header is actually for, and an unquoted space trips it.
        var disposition = string.Join("", res.Content.Headers.GetValues("Content-Disposition"));
        Assert.Contains("Q4 Review.docx", disposition);
    }

    [Fact]
    public async Task The_Served_Page_Does_Not_Hard_Code_A_Theme_List()
    {
        var page = await _client.GetStringAsync("/");
        Assert.DoesNotContain("Modern Clean", page);
        Assert.Contains("/api/options", page);   // catalogs are fetched, not baked in
    }
}
