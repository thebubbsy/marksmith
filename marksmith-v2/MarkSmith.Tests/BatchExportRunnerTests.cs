using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

public class BatchExportRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public BatchExportRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mk-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task RunAsync_ReturnsError_WhenDirectoryDoesNotExist()
    {
        var runner = new BatchExportRunner();
        var result = await runner.RunAsync(new BatchExportOptions { InputDirectory = Path.Combine(_tempDir, "nonexistent") });

        Assert.Equal(0, result.TotalExported);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task RunAsync_Exports_MultipleFiles_To_Html_And_Docx()
    {
        var doc1 = Path.Combine(_tempDir, "doc1.md");
        var doc2 = Path.Combine(_tempDir, "doc2.md");
        File.WriteAllText(doc1, "# Document 1\n\nContent 1");
        File.WriteAllText(doc2, "# Document 2\n\nContent 2");

        var outDir = Path.Combine(_tempDir, "output");
        var runner = new BatchExportRunner();
        var result = await runner.RunAsync(new BatchExportOptions
        {
            InputDirectory = _tempDir,
            OutputDirectory = outDir,
            Formats = new[] { "html", "docx" }
        });

        Assert.Equal(2, result.TotalFilesFound);
        Assert.Equal(4, result.TotalExported); // 2 files x 2 formats
        Assert.Equal(0, result.TotalFailed);
        Assert.True(File.Exists(Path.Combine(outDir, "doc1.html")));
        Assert.True(File.Exists(Path.Combine(outDir, "doc1.docx")));
        Assert.True(File.Exists(Path.Combine(outDir, "doc2.html")));
        Assert.True(File.Exists(Path.Combine(outDir, "doc2.docx")));
    }
}
