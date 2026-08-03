using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Regression tests for headless/API batch conversion where no live web render host is available
/// (host == null). Before the fix in ExportCoordinator, a DOCX batch over a document containing a
/// ```mermaid block dereferenced the null host and threw a NullReferenceException. The exporter
/// must instead skip web-based mermaid harvesting and still emit a valid DOCX via its parser-based
/// fallbacks.
/// </summary>
public class HeadlessBatchExportTests
{
    private static (string inFolder, string outFolder) MakeTempFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "MarkSmith_headless_" + Guid.NewGuid().ToString("N"));
        var inFolder = Path.Combine(root, "in");
        var outFolder = Path.Combine(root, "out");
        Directory.CreateDirectory(inFolder);
        Directory.CreateDirectory(outFolder);
        return (inFolder, outFolder);
    }

    [Fact]
    public async Task BatchConvert_Docx_WithMermaid_NullHost_DoesNotThrow_AndEmitsDocx()
    {
        var (inFolder, outFolder) = MakeTempFolders();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(inFolder, "doc.md"), """
                # Headless Batch Document

                ```mermaid
                flowchart LR
                  A[Paste a chat] --> B{Marksmith}
                  B --> C[Polished PDF]
                  B --> D[Editable Word]
                ```

                Some trailing prose so the body is non-trivial.
                """);

            var vm = new MainViewModel();

            // host: null simulates an API/headless caller with no WebView2. MermaidDocxMode = 1
            // (ShapeForge) is the branch that previously dereferenced the null host.
            var result = await AppServices.ExportCoordinator.BatchConvertForApiAsync(
                vm,
                inFolder,
                "docx",
                new OutputOverride { OutputFolder = outFolder, MermaidDocxMode = 1 },
                host: null);

            Assert.NotNull(result);

            var produced = Directory.GetFiles(outFolder, "*.docx");
            Assert.Single(produced);
            Assert.True(new FileInfo(produced[0]).Length > 1000, "DOCX should be a non-trivial, valid file.");
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(inFolder)!);
        }
    }

    [Fact]
    public async Task BatchConvert_Docx_NullHost_PlainMarkdown_Succeeds()
    {
        var (inFolder, outFolder) = MakeTempFolders();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(inFolder, "plain.md"),
                "# Plain\n\nNo diagrams here, just **text** and a list:\n\n- one\n- two\n");

            var vm = new MainViewModel();
            var result = await AppServices.ExportCoordinator.BatchConvertForApiAsync(
                vm, inFolder, "docx",
                new OutputOverride { OutputFolder = outFolder }, host: null);

            Assert.NotNull(result);
            Assert.Single(Directory.GetFiles(outFolder, "*.docx"));
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(inFolder)!);
        }
    }

    private static void TryDelete(string dir)
    {
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { /* best effort cleanup */ }
        }
    }
}
