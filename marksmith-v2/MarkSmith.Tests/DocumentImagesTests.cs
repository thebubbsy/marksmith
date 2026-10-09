using System.IO.Compression;
using MarkSmith.Models;
using MarkSmith.Services;
using Xunit;

namespace MarkSmith.Core.Tests;

/// <summary>
/// Images referenced relative to the document ("images/chart.png", the normal Markdown
/// convention) used to resolve against the APP folder, so they were missing from the preview,
/// the PDF, Word and EPUB. And a picked file under a folder with a space in its name produced
/// "![x](C:/My Pictures/x.png)", which isn't an image at all.
/// </summary>
public sealed class DocumentImagesTests : IDisposable
{
    // 1x1 white PNG.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _dir;
    private readonly string _image;

    public DocumentImagesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ms-docimg-" + Guid.NewGuid().ToString("N"), "My Report");
        Directory.CreateDirectory(Path.Combine(_dir, "images"));
        _image = Path.Combine(_dir, "images", "team photo.png");
        File.WriteAllBytes(_image, Png);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
    }

    private static string Render(string md, bool interactive = false) =>
        new MarkdownHtmlService().Render(md, new AppSettings(), new ThemeCatalog().GetOrDefault("GitHub Light"), null, interactive);

    // ---- resolving ---------------------------------------------------------------------------

    [Fact]
    public void Relative_path_resolves_against_the_given_folder()
    {
        Assert.Equal(_image, DocumentImages.Resolve("images/team photo.png", _dir));
        Assert.Equal(_image, DocumentImages.Resolve("images/team%20photo.png", _dir));
        Assert.Equal(_image, DocumentImages.Resolve("./images/team photo.png", _dir));
        Assert.Equal(_image, DocumentImages.Resolve("<images/team photo.png>", _dir));
    }

    [Fact]
    public void Ambient_folder_applies_and_is_restored()
    {
        Assert.Null(DocumentImages.Resolve("images/team photo.png"));
        using (DocumentImages.UseFolder(_dir))
        {
            Assert.Equal(_image, DocumentImages.Resolve("images/team photo.png"));
            using (DocumentImages.UseFolder(null)) // a pasted-text scope keeps the outer folder
                Assert.Equal(_image, DocumentImages.Resolve("images/team photo.png"));
        }
        Assert.Null(DocumentImages.CurrentFolder);
    }

    [Fact]
    public void Absolute_paths_and_file_uris_resolve()
    {
        Assert.Equal(_image, DocumentImages.Resolve(_image));
        Assert.Equal(_image, DocumentImages.Resolve(_image.Replace('\\', '/')));
        Assert.Equal(_image, DocumentImages.Resolve(new Uri(_image).AbsoluteUri));
    }

    [Fact]
    public void Web_data_and_missing_sources_are_not_local_files()
    {
        Assert.Null(DocumentImages.Resolve("https://example.com/a.png", _dir));
        Assert.Null(DocumentImages.Resolve("data:image/png;base64,AAAA", _dir));
        Assert.Null(DocumentImages.Resolve("images/missing.png", _dir));
        Assert.Null(DocumentImages.Resolve("", _dir));
        Assert.False(DocumentImages.LooksLocal("https://example.com/a.png"));
        Assert.True(DocumentImages.LooksLocal("images/a.png"));
    }

    // ---- writing a destination ---------------------------------------------------------------

    [Fact]
    public void Image_in_the_document_folder_is_written_relative_and_bracketed_for_spaces()
    {
        Assert.Equal("<images/team photo.png>", DocumentImages.Destination(_image, _dir));
        Assert.Equal("\n![team photo](<images/team photo.png>)\n", InsertSnippetBuilder.Image("", _image, _dir));
    }

    [Fact]
    public void Image_outside_the_document_folder_keeps_its_full_path_with_forward_slashes()
    {
        var other = Path.Combine(Path.GetTempPath(), "elsewhere", "chart.png");
        Assert.Equal(other.Replace('\\', '/'), DocumentImages.Destination(other, _dir));
        Assert.Equal(other.Replace('\\', '/'), DocumentImages.Destination(other, null));
    }

    [Fact]
    public void Web_addresses_pass_through_and_odd_brackets_are_wrapped()
    {
        Assert.Equal("https://example.com/a.png", DocumentImages.Destination("https://example.com/a.png", _dir));
        Assert.Equal("<C:/pics/a).png>", DocumentImages.Destination(@"C:\pics\a).png"));
        Assert.Equal("C:/pics/a(1).png", DocumentImages.Destination(@"C:\pics\a(1).png"));
    }

    [Theory]
    [InlineData(@"C:\Pictures\team-photo_2024.png", "team photo 2024")]
    [InlineData("https://example.com/img/hero%20shot.jpg?w=200", "hero shot")]
    [InlineData("", "image")]
    [InlineData("data:image/png;base64,AAAA", "image")]
    public void Alt_text_comes_from_the_file_name(string source, string expected) =>
        Assert.Equal(expected, DocumentImages.AltFromFileName(source));

    [Fact]
    public void Alt_text_brackets_are_escaped()
    {
        Assert.Equal(@"Fig \[1\]", DocumentImages.EscapeAlt("Fig [1]"));
        Assert.Equal("\n![Fig \\[1\\]](https://example.com/a.png)\n", InsertSnippetBuilder.Image("Fig [1]", "https://example.com/a.png"));
    }

    // ---- every renderer agrees ---------------------------------------------------------------

    [Fact]
    public void Preview_inlines_a_relative_image_with_spaces_from_the_document_folder()
    {
        var md = InsertSnippetBuilder.Image("", _image, _dir);
        Assert.DoesNotContain("data:image/png;base64", Render(md));            // no folder: not found
        using (DocumentImages.UseFolder(_dir))
            Assert.Contains("data:image/png;base64", Render(md));
    }

    [Fact]
    public void Live_preview_shows_a_card_for_a_missing_image_and_exports_keep_the_img()
    {
        var live = Render("![Q3 chart](images/q3.png)", interactive: true);
        Assert.Contains("ms-img-missing", live);
        Assert.Contains("Q3 chart · image not found", live);
        Assert.Contains("images/q3.png", live);

        var export = Render("![Q3 chart](images/q3.png)");
        Assert.DoesNotContain("ms-img-missing", export);
        Assert.Contains("<img src=\"images/q3.png\"", export);
    }

    [Fact]
    public void Small_svg_is_inlined_instead_of_left_as_an_unloadable_path()
    {
        var svg = Path.Combine(_dir, "logo.svg");
        File.WriteAllText(svg, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"4\" height=\"4\"/>");
        using (DocumentImages.UseFolder(_dir))
            Assert.Contains("data:image/svg+xml;base64", Render("![logo](logo.svg)"));
    }

    [Fact]
    public void Served_images_round_trip_and_standalone_html_embeds_them()
    {
        var previous = DocumentImages.ServedHost;
        DocumentImages.ServedHost = "marksmith.images";
        try
        {
            var url = DocumentImages.ServeUrl(_image)!;
            Assert.StartsWith("https://marksmith.images/", url);
            Assert.Equal(_image, DocumentImages.ServedPath(url));
            Assert.Null(DocumentImages.ServedPath("https://marksmith.images/000000000000000000000000/x.png"));

            var html = StandaloneHtml.Inline($"<p><img src=\"{url}\" alt=\"x\"></p>", Path.GetTempPath());
            Assert.Contains("data:image/png;base64", html);
            Assert.DoesNotContain("marksmith.images", html);
        }
        finally { DocumentImages.ServedHost = previous; }
    }

    [Fact]
    public async Task Word_export_embeds_a_relative_image()
    {
        var docx = Path.Combine(_dir, "out.docx");
        using (DocumentImages.UseFolder(_dir))
            await new DocxExportService().ExportAsync("# Report\n\n![team](<images/team photo.png>)\n", docx, new AppSettings());
        using var zip = ZipFile.OpenRead(docx);
        Assert.True(zip.Entries.Any(e => e.FullName.Contains("media/", StringComparison.Ordinal)), string.Join(", ", zip.Entries.Select(e => e.FullName)));
    }

    [Fact]
    public async Task Epub_export_packages_a_relative_image()
    {
        var epub = Path.Combine(_dir, "out.epub");
        using (DocumentImages.UseFolder(_dir))
            await new EpubExportService().ExportAsync("# Report\n\n![team](images/team%20photo.png)\n", epub, new AppSettings());
        using var zip = ZipFile.OpenRead(epub);
        Assert.NotNull(zip.GetEntry("OEBPS/images/001.png"));
    }
}
