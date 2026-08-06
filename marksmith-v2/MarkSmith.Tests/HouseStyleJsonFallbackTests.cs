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

    // ---- the JSON is the COMPLETE house-style spec: it can carry page geometry too ----

    [Fact]
    public void ParseAiResponse_carries_page_geometry_back_with_the_palette()
    {
        const string json = """
            {"name":"Corp A4","background":"#FFFFFF","text":"#1A1A1A","heading":"#003366",
             "primary":"#0066CC","secondary":"#004488","line":"#E0E0E0",
             "pageWidth":11906,"pageHeight":16838,"orientation":"portrait",
             "marginTop":1800,"marginRight":1080,"marginBottom":1800,"marginLeft":1440,
             "headerDistance":700,"footerDistance":720,"columns":2,"columnSpace":360}
            """;

        var theme = MarkSmith.Services.TemplateThemeService.ParseAiResponse(json);

        Assert.NotNull(theme);
        Assert.NotNull(theme!.Layout);
        Assert.Equal(11906u, theme.Layout!.PageWidthTwips);
        Assert.Equal(16838u, theme.Layout.PageHeightTwips);
        Assert.Equal("portrait", theme.Layout.Orientation);
        Assert.Equal(1800, theme.Layout.MarginTop);
        Assert.Equal(1440, theme.Layout.MarginLeft);
        Assert.Equal(700, theme.Layout.HeaderDistance);
        Assert.Equal(720, theme.Layout.FooterDistance);
        Assert.Equal(2, theme.Layout.ColumnCount);
        Assert.Equal(360, theme.Layout.ColumnSpace);
    }

    [Fact]
    public void ParseAiResponse_without_geometry_leaves_layout_null()
    {
        var theme = MarkSmith.Services.TemplateThemeService.ParseAiResponse(ValidJson);
        Assert.NotNull(theme);
        Assert.Null(theme!.Layout);
    }

    [Fact]
    public void HouseLayout_Merge_keeps_template_header_footer_and_applies_json_overrides()
    {
        var local = new Models.HouseLayout
        {
            PageWidthTwips = 12240, PageHeightTwips = 15840,
            MarginTop = 1440, HeaderXml = "<w:hdr>logo</w:hdr>", FooterXml = "<w:ftr>page</w:ftr>",
        };
        var ai = new Models.HouseLayout { PageWidthTwips = 11906, PageHeightTwips = 16838, ColumnCount = 2 };

        var merged = Models.HouseLayout.Merge(local, ai);

        Assert.NotNull(merged);
        Assert.Equal(11906u, merged!.PageWidthTwips);      // AI wins for geometry
        Assert.Equal(1440, merged.MarginTop);              // local fills the gaps
        Assert.Equal("<w:hdr>logo</w:hdr>", merged.HeaderXml);  // header/footer stay from template
        Assert.Equal("<w:ftr>page</w:ftr>", merged.FooterXml);
        Assert.Equal(2, merged.ColumnCount);
    }

    [Fact]
    public void BuildPrompt_hands_the_layout_facts_to_the_ai()
    {
        var layout = new Models.HouseLayout
        {
            PageWidthTwips = 11906, PageHeightTwips = 16838,
            MarginTop = 1800, MarginRight = 1080, MarginBottom = 1800, MarginLeft = 1440,
            ColumnCount = 2, ColumnSpace = 360,
            HeaderXml = "<w:hdr/>", FooterXml = "<w:ftr/>",
        };
        var prompt = MarkSmith.Services.TemplateThemeService.BuildPrompt(
            new MarkSmith.Services.TemplateThemeService.TemplateStyleSummary("Calibri", "Calibri", null, null, null, null, null, null),
            layout);

        Assert.Contains("Page Size: 11906 x 16838 twips", prompt);
        Assert.Contains("Margins (twips): top=1800", prompt);
        Assert.Contains("Columns: 2 (space 360)", prompt);
        Assert.Contains("Header: inherited from the template", prompt);
        Assert.Contains("Footer: inherited from the template", prompt);
        Assert.Contains("\"pageWidth\"", prompt); // the optional JSON keys are taught
        Assert.Contains("\"columns\"", prompt);
    }
}
