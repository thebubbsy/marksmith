using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Core.Office;
using Xunit;

namespace MarkSmith.Tests;

// The marksmith-office seam: must degrade gracefully when the host/Word is absent, and —
// when Word IS installed (this machine) — actually render a docx to an image.
public class OfficeCapabilityTests
{
    [Fact]
    public void MissingHost_IsUnavailable_AndDoesNotThrow()
    {
        var cap = new OfficeCapability(hostPath: @"C:\definitely\missing\marksmith-office-host.exe");
        Assert.False(cap.IsAvailable);

        // graceful degradation on every surface
        Assert.Null(cap.VerifyDocxAsync(@"C:\missing.docx").GetAwaiter().GetResult());
        Assert.Null(cap.RenderDocxToImageAsync(@"C:\missing.docx").GetAwaiter().GetResult());
    }

    [Fact]
    public void MissingDocx_ReturnsNull()
    {
        var cap = new OfficeCapability(hostPath: null); // even with a host present, no file = null
        Assert.Null(cap.RenderDocxToImageAsync(@"C:\missing.docx").GetAwaiter().GetResult());
    }

    [Fact]
    public void LocateHost_FindsShipOrPluginPathsWithoutThrowing()
    {
        // Must not throw regardless of environment; returns null or a path.
        _ = OfficeCapability.LocateHost();
        Assert.True(true);
    }

    [Fact]
    public void WordFidelityPage_BuildsSelfContainedPage()
    {
        var tiles = new[] { new byte[] { 1, 2, 3, 4 }, new byte[] { 5, 6, 7, 8 } };
        string page = WordFidelityPage.Build(tiles, lookingGlassMode: true, stale: false);
        Assert.Contains("src=\"page_1.png\"", page);
        Assert.Contains("src=\"page_2.png\"", page);
        Assert.Contains("fidelity-tile", page);
        Assert.Contains("__portalSetBlur", page);
        Assert.Contains("__portalSetShape", page);
        Assert.Contains("portal-aperture", page);
        Assert.DoesNotContain("out of date", page);

        string stale = WordFidelityPage.Build(tiles, lookingGlassMode: false, stale: true);
        Assert.Contains("Word render updating", stale);
        Assert.DoesNotContain("<div class=\"portal-aperture\"", stale); // overlay markup only in Looking Glass mode
    }

    [Fact]
    public void WordFidelityPage_RefreshingOverlayMarksOnlyDirtyTiles()
    {
        var tiles = new[] { new byte[] { 1 }, new byte[] { 2 }, new byte[] { 3 } };
        var dirty = new HashSet<int> { 2 };
        string page = WordFidelityPage.Build(tiles, lookingGlassMode: false, stale: false, refreshing: dirty);
        Assert.Contains("tile-refreshing\" data-page=\"2\"", page);
        Assert.DoesNotContain("tile-refreshing\" data-page=\"1\"", page);
        Assert.DoesNotContain("tile-refreshing\" data-page=\"3\"", page);
    }

    [Fact]
    public void TileEngine_DirtySpanAndPageMapping()
    {
        // No change -> empty span (first after prefix, last = max(first, ...)).
        var (f0, l0) = WordFidelityTileEngine.ComputeDirtySpan("a\nb\nc", "a\nb\nc");
        Assert.Equal(4, f0); // past the end — no real change
        Assert.Equal(4, l0);

        // Appended line at the end.
        var (f1, l1) = WordFidelityTileEngine.ComputeDirtySpan("a\nb\nc", "a\nb\nc\nd");
        Assert.Equal(4, f1);
        Assert.Equal(4, l1);

        // Edit in the middle.
        var (f2, l2) = WordFidelityTileEngine.ComputeDirtySpan("a\nb\nc\nd\ne", "a\nX\nc\nd\ne");
        Assert.Equal(2, f2);
        Assert.Equal(2, l2);

        // Edit at the very start.
        var (f3, l3) = WordFidelityTileEngine.ComputeDirtySpan("a\nb\nc\nd\ne", "A\nb\nc\nd\ne");
        Assert.Equal(1, f3);
        Assert.Equal(1, l3);

        // Line-span -> page mapping: 10 lines over 5 pages, change line 2 -> pages 1-2 (padded).
        var (p1, p2) = WordFidelityTileEngine.DirtyPages(5, 10, 2, 2);
        Assert.Equal(1, p1);
        Assert.Equal(2, p2);

        // Change in the last line -> last pages only.
        var (q1, q2) = WordFidelityTileEngine.DirtyPages(5, 10, 10, 10);
        Assert.Equal(4, q1);
        Assert.Equal(5, q2);

        // Single-page document is always page 1.
        var (r1, r2) = WordFidelityTileEngine.DirtyPages(1, 10, 5, 5);
        Assert.Equal(1, r1);
        Assert.Equal(1, r2);
    }

    [Fact]
    public void AppSettings_RoundTripsWordFidelity()
    {
        var a = new Models.AppSettings { WordFidelity = true };
        var b = new Models.AppSettings();
        b.UpdateFrom(a);
        Assert.True(b.WordFidelity);
    }

    [Fact]
    public async Task WordInstalled_RendersDocxToImage()
    {
        var cap = new OfficeCapability();
        if (!cap.IsAvailable) return; // skip silently when Word/plugin absent (CI machines)

        // Build a minimal real docx via the export service, then render it through Word.
        string docx = Path.Combine(Path.GetTempPath(), $"office-{Guid.NewGuid():N}.docx");
        string md = ":::shapes\nellipse 1.0 0.5 0.9 0.7 FFD9B3\n:::\n";
        try
        {
            new MarkSmith.Services.DocxExportService()
                .ExportAsync(md, docx, new Models.AppSettings()).GetAwaiter().GetResult();

            var result = await cap.RenderDocxToImageAsync(docx);
            Assert.NotNull(result);
            Assert.NotNull(result!.Value.Bytes);
            Assert.True(result.Value.Bytes!.Length > 1000);
            Assert.StartsWith("image/", result.Value.Mime);
        }
        finally
        {
            if (File.Exists(docx)) File.Delete(docx);
        }
    }

    // Persistent-server mode: Word stays open across commands so page-band re-renders are cheap.
    [Fact]
    public async Task WordInstalled_TileServerOpensAndRendersPageBands()
    {
        var cap = new OfficeCapability();
        if (!cap.IsAvailable) return; // skip silently when Word/plugin absent (CI machines)

        string md = "# Tiled preview\n\n";
        for (int i = 0; i < 40; i++) md += $"## Section {i}\n\nBody text for section {i} to push the document onto a second page.\n\n";
        string docx = Path.Combine(Path.GetTempPath(), $"tileserver-{Guid.NewGuid():N}.docx");
        try
        {
            await new Services.DocxExportService().ExportAsync(md, docx, new Models.AppSettings());

            using var server = MarkSmith.Core.Office.WordTileServer.Start(docx);
            Assert.NotNull(server);
            Assert.True(server.PageCount >= 1);
            var p1 = server.RenderPage(1, 0.5);
            Assert.NotNull(p1);
            Assert.True(p1.Length > 0);
        }
        finally
        {
            try { File.Delete(docx); } catch { }
        }
    }
}