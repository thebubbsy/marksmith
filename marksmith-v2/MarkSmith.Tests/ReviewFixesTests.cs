using System.IO;
using System.Linq;
using DocumentFormat.OpenXml.Packaging;
using MarkSmith.Models;
using MarkSmith.Services;
using MarkSmith.ViewModels.Mermaid;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace MarkSmith.Core.Tests;

// Regression suite for the external "reality check" review. Every claim it made is either FIXED
// here (with a test proving the fix) or verified-not-present (with a test pinning the safe
// behavior so it can never regress).
public class ReviewFixesTests
{
    private const string CanvasBg = "1E1E2E"; // MermaidCanvasControl.xaml canvas background

    private static string RenderHtml(string md, AppSettings? s = null) =>
        new MarkdownHtmlService().Render(md, s ?? new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"));

    private static string ExportDocxText(string md, AppSettings? s = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_review_{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync(md, path, s ?? new AppSettings { BrandCoverPage = true }).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(path, false);
            return string.Concat(doc.MainDocumentPart!.Document.Descendants<W.Text>().Select(t => t.Text));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- XSS claim: mermaid fence content must stay escaped -------------------------------------
    [Fact]
    public void Mermaid_Fence_Content_Is_Escaped_Not_Executable()
    {
        var evil = RenderHtml("```mermaid\n<script>alert(1)</script><img src=x onerror=alert(2)>\n```");
        // The fence content is HTML-escaped (and the HtmlSanitizer round-trips it into well-formed
        // text). The hard guarantee: a raw '<' from the payload can never survive into the markup,
        // so the browser can never parse the payload as a live tag/event handler.
        Assert.DoesNotContain("<script>alert(1)", evil);
        Assert.DoesNotContain("<img src=x", evil);
        Assert.Contains("&lt;script", evil); // escaped for display — inert text, decodes back for mermaid

        Assert.Contains("mermaid", RenderHtml("```mermaid\ngraph TD;\nA-->B;\n```")); // normal fences still render
    }

    // ---- definition lists: the "spacing trap" — real dl/dt/dd layout now -----------------------
    [Fact]
    public void Definition_List_Renders_Native_Dl_Structure_In_Html()
    {
        var html = RenderHtml("Term\n:   the definition");
        Assert.Contains("<dl>", html);
        Assert.Contains("<dt>", html);
        Assert.Contains("<dd>", html);
        Assert.Contains("the definition", html);
    }

    [Fact]
    public void Definition_List_In_Docx_Has_Bold_Term_And_Indented_Definitions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ms_review_dl_{Guid.NewGuid():N}.docx");
        try
        {
            new DocxExportService().ExportAsync("Term\n:   first definition\n:   second definition", path, new AppSettings()).GetAwaiter().GetResult();
            using var doc = WordprocessingDocument.Open(path, false);
            var paras = doc.MainDocumentPart!.Document.Descendants<W.Paragraph>().ToList();

            var boldTerms = paras.Where(p => p.Descendants<W.Bold>().Any()).Select(p => p.InnerText).ToList();
            Assert.Contains("Term", boldTerms);

            var indented = paras.Where(p => p.ParagraphProperties?.Indentation?.Left?.Value is not null).Select(p => p.InnerText).ToList();
            Assert.Contains("first definition", indented);
            Assert.Contains("second definition", indented);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Definition_List_In_Google_Docs_Has_Bold_Term()
    {
        var r = GoogleDocsDocumentBuilder.Build("Term\n:   the definition", new AppSettings(), null);
        var json = string.Join("\n", r.Requests.Select(o => System.Text.Json.JsonSerializer.Serialize(o, GoogleDocsDocumentBuilder.JsonOpts)));
        Assert.Contains("\"bold\":true", json);
        Assert.Contains("Term", json);
        Assert.Contains("the definition", json);
    }

    // ---- nested <details> claim: outer block + tail must not be truncated ------------------------
    [Fact]
    public void Nested_Details_Preserve_Outer_And_Sibling_Content_In_Docx()
    {
        var md =
            "<details>\n<summary>Outer</summary>\n<div>Inner start\n" +
            "<details>\n<summary>Inner</summary>\n<div>Inner secret body</div>\n" +
            "</details>\nOuter tail</div>\n</details>\n\nAfter everything.";
        var text = ExportDocxText(md);
        Assert.Contains("Outer", text);
        Assert.Contains("Inner", text);
        Assert.Contains("Inner secret body", text);
        Assert.Contains("Outer tail", text);          // was dropped by the old first-</details> regex
        Assert.Contains("After everything", text);    // sibling content after the block was dropped too
    }

    // ---- black-on-black claim: a fresh node must be visible on the dark canvas ------------------
    [Fact]
    public void New_Mermaid_Node_Is_Visible_On_The_Dark_Canvas_For_Any_Theme()
    {
        foreach (var themeName in new[] { "GitHub Light", "Dark", "Nord", "Sepia" })
        {
            AppServices.Settings.Current.Theme = themeName;
            var node = new DiagramNodeViewModel();
            var ratio = ContrastGuard.GetContrastRatio(node.FillColor, CanvasBg);
            Assert.True(ratio >= 1.8,
                $"theme '{themeName}' produced fill {node.FillColor} with contrast {ratio:F2} vs the canvas — below the 1.8:1 floor");
        }
    }

    // ---- A4 lock claim: width edits can't desync the page model while the lock is on -------------
    [Fact]
    public void A4_Lock_Is_Authoritative_Over_Manual_Width_Edits()
    {
        var vm = new MarkSmith.ViewModels.MainViewModel();
        vm.A4FixedWidth = true;
        vm.ContentWidth = 1200; // user types a custom width while locked
        Assert.Equal(794, vm.ContentWidth); // reverted — canvas/PDF/DOCX/Google all agree
        Assert.Equal(794, vm.ContentWidth);
    }
}
