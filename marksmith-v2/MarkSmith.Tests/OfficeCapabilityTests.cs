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
}
