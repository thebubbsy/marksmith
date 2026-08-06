using System;
using System.IO;
using MarkSmith.ViewModels;
using Xunit;

namespace MarkSmith.Tests;

// The house-style flow must not depend on the browser extension: the user can copy the AI prompt,
// run it in any web chat, and paste the JSON reply back into the app (ApplyHouseStyleThemeJson) —
// the same parse/save/select path the extension result takes.
public class HouseStyleJsonFallbackTests
{
    private const string ValidJson = """
        {"name":"Corp Brand","background":"#FFFFFF","text":"#1A1A1A","heading":"#003366",
         "code":"#F5F5F5","border":"#DDDDDD","primary":"#0066CC","secondary":"#004488","line":"#E0E0E0"}
        """;

    [Fact]
    public void ApplyHouseStyleThemeJson_applies_valid_json_without_extension()
    {
        var vm = new MainViewModel();
        bool ok = vm.ApplyHouseStyleThemeJson(ValidJson);

        Assert.True(ok);
        Assert.Equal("Corp Brand", vm.SelectedThemeName);
        Assert.Contains("created and selected", vm.HouseStyleStatus);
        Assert.Equal("", vm.HouseStyleJsonResult); // box cleared so a stale paste can't re-apply
    }

    [Fact]
    public void ApplyHouseStyleThemeJson_accepts_fenced_json()
    {
        // Users copy the AI reply verbatim — often wrapped in ```json fences or with prose.
        var vm = new MainViewModel();
        string fenced = "Here is the theme:\n```json\n" + ValidJson + "\n```";
        Assert.True(vm.ApplyHouseStyleThemeJson(fenced));
        Assert.Equal("Corp Brand", vm.SelectedThemeName);
    }

    [Fact]
    public void ApplyHouseStyleThemeJson_rejects_invalid_json()
    {
        var vm = new MainViewModel();
        vm.HouseStyleJsonResult = "this is not json"; // the user pasted it into the box
        Assert.False(vm.ApplyHouseStyleThemeJson(vm.HouseStyleJsonResult));
        Assert.Contains("wasn't valid theme JSON", vm.HouseStyleStatus);
        Assert.Equal("this is not json", vm.HouseStyleJsonResult); // keep the paste so the user can fix it
    }

    [Fact]
    public void BeginHouseStyleImport_works_without_extension_and_surfaces_manual_path()
    {
        // A minimal template: no theme/styles parts — ParseDotx falls back to defaults.
        string dotx = Path.Combine(Path.GetTempPath(), $"house-manual-{Guid.NewGuid():N}.dotx");
        try
        {
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(
                       dotx, DocumentFormat.OpenXml.WordprocessingDocumentType.Template))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                    new DocumentFormat.OpenXml.Wordprocessing.Body(
                        new DocumentFormat.OpenXml.Wordprocessing.SectionProperties(
                            new DocumentFormat.OpenXml.Wordprocessing.PageSize { Width = 11906, Height = 16838 })));
                doc.Save();
            }

            var vm = new MainViewModel();
            var jobId = vm.BeginHouseStyleImport(dotx);

            // The prompt is always produced (manual fallback) and the command is enqueued on the
            // reverse channel (harmless when the extension is away).
            Assert.False(string.IsNullOrWhiteSpace(jobId));
            Assert.False(string.IsNullOrWhiteSpace(vm.HouseStylePrompt));
            Assert.Equal(dotx, vm.BrandTemplatePath);
        }
        finally { if (File.Exists(dotx)) File.Delete(dotx); }
    }
}
