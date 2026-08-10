using System;
using System.IO;
using System.Threading.Tasks;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Core.Tests;

[Collection("LicenseState")]
public class MainViewModelIntegrationTests
{
    private class DummyWebRenderHost : IWebRenderHost
    {
        public Task<bool> EnsureReadyAsync() => Task.FromResult(true);
        public Task NavigateToStringAsync(string html) => Task.CompletedTask;
        public Task<string?> ExecuteScriptAsync(string javaScript) => Task.FromResult<string?>(null);
        public Task<bool> PrintToPdfAsync(string outputPath, PdfPageSetup setup)
        {
            // A real minimal PDF — PdfSourceStore (metadata + embedded source) runs on every export
            // now, so the fixture must produce a file the post-processor can actually open.
            using var doc = new PdfSharp.Pdf.PdfDocument();
            doc.AddPage();
            doc.Save(outputPath);
            return Task.FromResult(true);
        }
        public Task BeginHarvestAsync() => Task.CompletedTask;
        public Task EndHarvestAsync() => Task.CompletedTask;
    }

    // DOCX export is gated on the license (Free users can't export DOCX). These integration tests
    // start a one-export trial (which grants it) and restore the real license file afterwards so
    // the suite never clobbers a genuine activation.
    private static string? _licenseBackup;
    private static string LicensePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith", "license.json");

    private static void AllowDocxExport()
    {
        _licenseBackup = File.Exists(LicensePath) ? File.ReadAllText(LicensePath) : null;
        AppServices.License.Load();
        if (!AppServices.License.CanExportDocx)
        {
            // Self-healing: a leftover SPENT trial (TrialUsed=true) from an earlier test run would
            // make StartTrial refuse and the docx gate fire. Reset to Free first, then re-trial.
            if (!AppServices.License.StartTrial().ok) AppServices.License.ResetToFree();
            if (!AppServices.License.CanExportDocx) AppServices.License.StartTrial();
        }
    }

    private static void RestoreLicense()
    {
        try
        {
            if (_licenseBackup is null) { if (File.Exists(LicensePath)) File.Delete(LicensePath); }
            else File.WriteAllText(LicensePath, _licenseBackup);
            AppServices.License.Load();
        }
        catch { /* best-effort */ }
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
        AllowDocxExport();
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

            Assert.True(string.IsNullOrEmpty(vm.StatusText) || !vm.StatusText.StartsWith("Error"),
                "export status: " + vm.StatusText);
            Assert.NotNull(vm.LastOutputPath);
            Assert.True(File.Exists(vm.LastOutputPath));
            Assert.True(vm.HasOutput);
            Assert.EndsWith(".docx", vm.LastOutputPath, StringComparison.OrdinalIgnoreCase);

            // The trial cap is enforced inside DocxExportService (the one chokepoint for every
            // DOCX path): a successful export must have consumed one of the 3 trial exports.
            Assert.Equal(2, AppServices.License.State.TrialExportsRemaining);
        }
        finally
        {
            RestoreLicense();

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
            RestoreLicense();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExportDocxWithMermaidDiagram_GeneratesValidDocxWithShapes()
    {
        AllowDocxExport();
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
            RestoreLicense();

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


// A free user must not be able to switch automation on — the toggle reverts and the pro-gate
// event fires (the old bug: watchers started and only the export step complained).
[Collection("LicenseState")]
public class FreeTierToggleGateTests : IDisposable
{
    private readonly string _licensePath;
    private readonly string? _backup;

    public FreeTierToggleGateTests()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkSmith");
        _licensePath = Path.Combine(dir, "license.json");
        _backup = File.Exists(_licensePath) ? File.ReadAllText(_licensePath) : null;
    }

    public void Dispose()
    {
        try
        {
            if (_backup is null) { if (File.Exists(_licensePath)) File.Delete(_licensePath); }
            else File.WriteAllText(_licensePath, _backup);
            AppServices.License.Load();
        }
        catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("AutoClipboardIngest")]
    [InlineData("WatchFolderEnabled")]
    [InlineData("AutoConvertIngests")]
    public void FreeUser_CannotSwitchAutomationOn(string prop)
    {
        AppServices.License.ResetToFree(); // guarantee Free
        var vm = new MainViewModel();
        MarkSmith.Models.FeatureId? raised = null;
        vm.ProFeatureAttempted += id => raised = id;

        // diagnostics
        Assert.False(AppServices.License.CanAutomate, "precondition: automation must be locked");
        var property = typeof(MainViewModel).GetProperty(prop)!;
        property.SetValue(vm, false); // ensure the pre-state is OFF (a real user's settings may have it on)
        property.SetValue(vm, true);  // the attempt to switch automation ON must be refused

        var after = (bool)property.GetValue(vm)!;
        Assert.False(after, $"{prop} must stay OFF for a free user (after={after}, raised={(raised?.ToString() ?? "null")})");
        Assert.NotNull(raised); // the standardized pro-gate fired
    }
}
