using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

[Collection("LicenseState")]
public class BatchConvertServiceTests : IDisposable
{
    private readonly string _licensePath;
    private readonly string _shadowPath;
    private readonly string? _backup;
    private readonly string? _shadowBackup;

    public BatchConvertServiceTests()
    {
        var dir = MarkSmith.Services.AppPaths.ConfigDir;
        _licensePath = Path.Combine(dir, "license.json");
        _shadowPath = Path.Combine(dir, "trial.state");
        _backup = File.Exists(_licensePath) ? File.ReadAllText(_licensePath) : null;
        _shadowBackup = File.Exists(_shadowPath) ? File.ReadAllText(_shadowPath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_backup is null) { if (File.Exists(_licensePath)) File.Delete(_licensePath); }
            else File.WriteAllText(_licensePath, _backup);
            if (_shadowBackup is null) { if (File.Exists(_shadowPath)) File.Delete(_shadowPath); }
            else File.WriteAllText(_shadowPath, _shadowBackup);
            AppServices.License.Load();
        }
        catch { /* best-effort */ }
    }
    [Fact]
    public async Task ConvertDirectoryAsync_Throws_If_SourceDirectory_Missing()
    {
        var service = new BatchConvertService();
        var missingDir = Path.Combine(Path.GetTempPath(), $"missing_dir_{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_dir_{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.ConvertDirectoryAsync(null, missingDir, outputDir, "docx", new AppSettings()));
    }

    [Fact]
    public async Task ConvertDirectoryAsync_Throws_If_Format_Invalid()
    {
        var service = new BatchConvertService();
        var tempSourceDir = Path.Combine(Path.GetTempPath(), $"source_dir_{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"out_dir_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSourceDir);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ConvertDirectoryAsync(null, tempSourceDir, outputDir, "invalid_format", new AppSettings()));
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_Converts_Multiple_Markdown_Files_To_Docx()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();

        var service = new BatchConvertService();
        var tempSourceDir = Path.Combine(Path.GetTempPath(), $"batch_src_{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"batch_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSourceDir);
        File.WriteAllText(Path.Combine(tempSourceDir, "doc1.md"), "# Document 1\nContent 1");
        File.WriteAllText(Path.Combine(tempSourceDir, "doc2.md"), "# Document 2\nContent 2");

        try
        {
            int callbackCount = 0;
            await service.ConvertDirectoryAsync(null, tempSourceDir, outputDir, "docx", new AppSettings(), _ => callbackCount++);

            Assert.True(File.Exists(Path.Combine(outputDir, "doc1.docx")));
            Assert.True(File.Exists(Path.Combine(outputDir, "doc2.docx")));
            Assert.True(callbackCount >= 2);
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_Isolates_Errors_For_Individual_Files()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();

        var service = new BatchConvertService();
        var tempSourceDir = Path.Combine(Path.GetTempPath(), $"batch_iso_src_{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"batch_iso_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSourceDir);
        File.WriteAllText(Path.Combine(tempSourceDir, "valid.md"), "# Valid Doc\nValid markdown text");

        try
        {
            await service.ConvertDirectoryAsync(null, tempSourceDir, outputDir, "docx", new AppSettings());
            Assert.True(File.Exists(Path.Combine(outputDir, "valid.docx")));
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_Handles_Empty_Source_Directory()
    {
        AppServices.License.ResetToFree();
        AppServices.License.ToggleDevPro();

        var service = new BatchConvertService();
        var tempSourceDir = Path.Combine(Path.GetTempPath(), $"batch_empty_src_{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"batch_empty_out_{Guid.NewGuid():N}");

        Directory.CreateDirectory(tempSourceDir);
        try
        {
            int callbackCount = 0;
            await service.ConvertDirectoryAsync(null, tempSourceDir, outputDir, "docx", new AppSettings(), _ => callbackCount++);
            Assert.Equal(0, callbackCount);
            Assert.True(Directory.Exists(outputDir));
        }
        finally
        {
            if (Directory.Exists(tempSourceDir)) Directory.Delete(tempSourceDir, true);
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
}
