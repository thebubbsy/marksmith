using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MdToPdf.Models;
using MdToPdf.Services;
using MdToPdf.ViewModels;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MdToPdf.Core.Tests;

/// <summary>
/// End-to-end ViewModel-level coverage of the house-style .dotx theme flow:
/// import a template -> prompt enqueued on the reverse command channel -> extension posts
/// the web AI's reply back -> a custom theme is created and selected. The extension side is
/// simulated via ApiServer's internal DrainCommands/PostResult seams (no HTTP, no LLM).
/// </summary>
public class HouseStyleThemeFlowTests
{
    /// <summary>Creates a minimal (styles-less) .dotx — enough for ParseDotx to fall back to defaults.</summary>
    private static void CreateMinimalDotx(string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("House style")))));
        main.Document.Save();
    }

    [Fact]
    public void ImportDotx_enqueues_prompt_and_posted_result_creates_and_selects_theme()
    {
        var vm = new MainViewModel();
        var dotx = Path.Combine(Path.GetTempPath(), $"mk-housestyle-{Guid.NewGuid():N}.dotx");
        // "Test Theme <32-hex>" names are recognised as test artifacts by CustomThemeStore.
        var themeName = "Test Theme " + Guid.NewGuid().ToString("N");
        try
        {
            CreateMinimalDotx(dotx);

            // 1. Import: parse the template, build the prompt, enqueue it for the extension.
            var jobId = vm.BeginHouseStyleImport(dotx);
            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.Equal(dotx, vm.BrandTemplatePath);
            Assert.True(vm.HasHouseStylePrompt);
            Assert.Contains("JSON", vm.HouseStylePrompt);
            Assert.True(vm.HasHouseStyleStatus);

            // 2. Simulate the extension polling GET /api/commands: it receives the theme-prompt job.
            var jobs = ApiServer.DrainCommands();
            var job = Assert.Single(jobs, j => j.Id == jobId);
            Assert.Equal("theme-prompt", job.Type);
            Assert.Equal(vm.HouseStylePrompt, job.Prompt);

            // 3. No reply posted yet — polling must find nothing.
            Assert.False(vm.PollThemeJobResult());

            // 4. Simulate the extension posting the web AI's reply (POST /api/commands/result).
            var reply = $$"""
                {"name":"{{themeName}}","background":"#FFFFFF","text":"#111111","heading":"#1F4E79","code":"#F2F2F2","border":"#D0D0D0","primary":"#2E75B6","secondary":"#ED7D31","line":"#E5E5E5"}
                """;
            ApiServer.PostResult(new ApiServer.CommandResult(jobId, reply));

            // 5. The heartbeat poll consumes the result: theme saved and selected.
            Assert.True(vm.PollThemeJobResult());
            Assert.Equal(themeName, vm.SelectedThemeName);
            Assert.Contains(themeName, vm.ThemeNames);
            Assert.Contains(CustomThemeStore.All, t => t.Name == themeName);
            Assert.Contains(themeName, vm.HouseStyleStatus);
            Assert.Equal(StatusSeverity.Success, vm.StatusSeverity);

            // 6. Results are one-shot: a second poll finds nothing new.
            Assert.False(vm.PollThemeJobResult());
        }
        finally
        {
            CustomThemeStore.Remove(themeName);
            try { File.Delete(dotx); } catch { }
        }
    }

    [Fact]
    public void ImportDotx_invalid_ai_reply_surfaces_error_and_keeps_previous_theme()
    {
        var vm = new MainViewModel();
        var dotx = Path.Combine(Path.GetTempPath(), $"mk-housestyle-{Guid.NewGuid():N}.dotx");
        var previousTheme = vm.SelectedThemeName;
        try
        {
            CreateMinimalDotx(dotx);
            var jobId = vm.BeginHouseStyleImport(dotx);
            ApiServer.DrainCommands(); // extension picks the job up…

            // …but the web AI replied with prose, not theme JSON.
            ApiServer.PostResult(new ApiServer.CommandResult(jobId, "Sorry, I can't help with that."));

            Assert.True(vm.PollThemeJobResult()); // the result was consumed…
            Assert.Equal(previousTheme, vm.SelectedThemeName); // …but no theme was created or selected.
            Assert.Contains("valid theme JSON", vm.HouseStyleStatus);
            Assert.Equal(StatusSeverity.Error, vm.StatusSeverity);
        }
        finally { try { File.Delete(dotx); } catch { } }
    }
}
