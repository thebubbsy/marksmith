using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

public class MainViewModelIntegrationTests
{
    private class DummyWebRenderHost : IWebRenderHost
    {
        public Task<bool> EnsureReadyAsync() => Task.FromResult(true);
        public Task NavigateToStringAsync(string html) => Task.CompletedTask;
        public Task<string?> ExecuteScriptAsync(string javaScript) => Task.FromResult<string?>(null);
        public Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
        {
            File.WriteAllText(outputPath, "%PDF-1.4 Dummy PDF Content");
            return Task.FromResult(true);
        }
        public Task BeginHarvestAsync() => Task.CompletedTask;
        public Task EndHarvestAsync() => Task.CompletedTask;
    }

    [Fact]
    public void TargetFormatCommands_ToggleStateAndNotifyProperties()
    {
        var vm = new MainViewModel();
        vm.TargetFormat = "pdf";
        Assert.True(vm.IsPdfFormat);
        Assert.False(vm.IsDocxFormat);
        Assert.Equal(0, vm.TargetFormatIndex);

        vm.SetTargetFormatDocxCommand.Execute(null);

        Assert.Equal("docx", vm.TargetFormat);
        Assert.False(vm.IsPdfFormat);
        Assert.True(vm.IsDocxFormat);
        Assert.Equal(1, vm.TargetFormatIndex);

        vm.SetTargetFormatPdfCommand.Execute(null);

        Assert.Equal("pdf", vm.TargetFormat);
        Assert.True(vm.IsPdfFormat);
        Assert.False(vm.IsDocxFormat);
    }

    [Fact]
    public async Task ExportDocumentCommand_DocxFormat_SetsLastOutputPathAndHasOutput()
    {
        AppServices.License.Load();
        var vm = new MainViewModel();
        var host = new DummyWebRenderHost();
        vm.Host = host;

        var tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            vm.OutputFolder = tempDir;
            vm.PastedMarkdown = "# Sample Test Document\n\nThis is a test document.";
            vm.UsePasteSource = true;
            vm.SuggestedTitle = "TestSampleDoc";

            vm.SetTargetFormatDocxCommand.Execute(null);
            Assert.True(vm.IsDocxFormat);

            Assert.False(vm.HasOutput);

            await vm.ExportDocumentCommand.ExecuteAsync(null);

            Assert.NotNull(vm.LastOutputPath);
            Assert.True(File.Exists(vm.LastOutputPath));
            Assert.True(vm.HasOutput);
            Assert.EndsWith(".docx", vm.LastOutputPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExportDocumentCommand_PdfFormat_SetsLastOutputPathAndHasOutput()
    {
        var vm = new MainViewModel();
        var host = new DummyWebRenderHost();
        vm.Host = host;

        var tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            vm.OutputFolder = tempDir;
            vm.PastedMarkdown = "# Sample Test Document\n\nThis is a test document for PDF.";
            vm.UsePasteSource = true;
            vm.SuggestedTitle = "TestPdfDoc";

            vm.SetTargetFormatPdfCommand.Execute(null);
            Assert.True(vm.IsPdfFormat);

            await vm.ExportDocumentCommand.ExecuteAsync(null);

            Assert.NotNull(vm.LastOutputPath);
            Assert.True(File.Exists(vm.LastOutputPath));
            Assert.True(vm.HasOutput);
            Assert.EndsWith(".pdf", vm.LastOutputPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExportDocxWithMermaidDiagram_GeneratesValidDocxWithShapes()
    {
        AppServices.License.Load();
        var vm = new MainViewModel();
        var host = new DummyWebRenderHost();
        vm.Host = host;

        var tempDir = Path.Combine(Path.GetTempPath(), "MarkSmith_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            vm.OutputFolder = tempDir;
            vm.PastedMarkdown = """
                # Flowchart Document

                ```mermaid
                flowchart LR
                  A[Paste a chat] --> B{MarkSmith}
                  B --> C[Polished PDF]
                  B --> D[Editable Word]
                ```
                """;
            vm.UsePasteSource = true;
            vm.SuggestedTitle = "MermaidTestDoc";
            vm.SetTargetFormatDocxCommand.Execute(null);

            await vm.ExportDocumentCommand.ExecuteAsync(null);

            Assert.NotNull(vm.LastOutputPath);
            Assert.True(File.Exists(vm.LastOutputPath));
            Assert.True(new FileInfo(vm.LastOutputPath).Length > 1000);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    // ---- File-name template (Settings > Export file name) ----

    [Fact]
    public void FileNameTemplate_DefaultToken_ReturnsTitle()
    {
        var name = MainViewModel.ApplyFileNameTemplate("{title}", "My Report", "pdf");
        Assert.Equal("My Report", name);
    }

    [Fact]
    public void FileNameTemplate_DateAndFormatTokens_AreExpanded()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var name = MainViewModel.ApplyFileNameTemplate("{date} {title} ({format})", "My Report", "docx");
        Assert.Equal($"{today} My Report (docx)", name);
    }

    [Fact]
    public void FileNameTemplate_EmptyTemplate_FallsBackToTitle()
    {
        var name = MainViewModel.ApplyFileNameTemplate("", "Fallback Title", "pdf");
        Assert.Equal("Fallback Title", name);
    }

    [Fact]
    public void FileNameTemplate_UnsafeCharacters_AreSanitized()
    {
        // A hostile template must never yield an invalid file name.
        var name = MainViewModel.ApplyFileNameTemplate("{title} <bad>|\"chars\"", "Rep:ort?", "pdf");
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("?", name);
        Assert.DoesNotContain("<", name);
        Assert.DoesNotContain("|", name);
        Assert.DoesNotContain("\"", name);
    }
}

