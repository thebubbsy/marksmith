using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class ExportCoordinatorTests
{
    private readonly ExportCoordinator _coordinator = new();

    [Fact]
    public void ParseFormats_Handles_Various_Inputs()
    {
        Assert.Equal(new[] { "pdf" }, ExportCoordinator.ParseFormats(null));
        Assert.Equal(new[] { "pdf" }, ExportCoordinator.ParseFormats(""));
        Assert.Equal(new[] { "pdf", "docx" }, ExportCoordinator.ParseFormats("both"));
        Assert.Equal(new[] { "pdf", "docx", "pptx", "epub" }, ExportCoordinator.ParseFormats("pdf,docx,pptx,epub"));
        Assert.Equal(new[] { "docx" }, ExportCoordinator.ParseFormats("docx,invalid"));
    }

    [Fact]
    public async Task ExportToDocxAsync_Generates_Valid_Docx_File()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}.docx");
        try
        {
            var settings = new AppSettings();
            await _coordinator.ExportToDocxAsync("# Heading\n\nSome body text", tempFile, settings);

            Assert.True(File.Exists(tempFile));
            var bytes = await File.ReadAllBytesAsync(tempFile);
            Assert.NotEmpty(bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportToPptxAsync_Generates_Valid_Pptx_File()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}.pptx");
        try
        {
            var settings = new AppSettings();
            await _coordinator.ExportToPptxAsync("# Slide Title\n- Bullet point 1\n- Bullet point 2", tempFile, settings);

            Assert.True(File.Exists(tempFile));
            var bytes = await File.ReadAllBytesAsync(tempFile);
            Assert.NotEmpty(bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ExportToEpubAsync_Generates_Valid_Epub_File()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"export_test_{Guid.NewGuid():N}.epub");
        try
        {
            var settings = new AppSettings();
            await _coordinator.ExportToEpubAsync("# Book Title\n\nChapter 1 text.", tempFile, settings);

            Assert.True(File.Exists(tempFile));
            var bytes = await File.ReadAllBytesAsync(tempFile);
            Assert.NotEmpty(bytes);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ExportToHtml_Returns_Rendered_Html()
    {
        var settings = new AppSettings();
        var html = _coordinator.ExportToHtml("# Hello World\n\nThis is **bold**.", settings);

        Assert.NotNull(html);
        Assert.Contains("Hello World", html);
        Assert.Contains("bold", html);
    }
}
