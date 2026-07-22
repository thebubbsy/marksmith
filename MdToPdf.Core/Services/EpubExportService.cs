using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using MdToPdf.Models;

namespace MdToPdf.Services;

// EPUB 3 export. Markdown → themed XHTML chapters (split on each top-level heading), packaged as a
// valid EPUB container (mimetype first + uncompressed, META-INF/container.xml, an OPF manifest/spine,
// and an EPUB3 nav document), zipped with System.IO.Compression. No external dependency.
public sealed class EpubExportService
{
    public const string Extension = "epub";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseMathematics().Build();

    private static readonly ThemeCatalog Themes = new();

    private static readonly string[] VoidTags =
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings) => Task.Run(() =>
    {
        markdown = TextNormalizer.Newlines(markdown);
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown, settings.DashMode);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);

        var theme = Themes.GetOrDefault(settings.Theme);
        var bodyHtml = XhtmlSafe(Markdown.ToHtml(markdown, Pipeline));
        var bookTitle = HistoryEntry.ExtractTitle(markdown) ?? "Marksmith Export";

        // Split into chapters on each <h1>. Content before the first heading becomes its own chapter.
        var segments = Regex.Split(bodyHtml, @"(?=<h1[ >])")
                            .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (segments.Count == 0) segments.Add(bodyHtml.Length == 0 ? "<p></p>" : bodyHtml);

        var chapters = segments.Select((html, i) => new Chapter(
            Id: $"ch{i + 1:000}",
            File: $"ch{i + 1:000}.xhtml",
            Title: FirstHeading(html) ?? (i == 0 ? bookTitle : $"Section {i + 1}"),
            Html: html)).ToList();

        var dir = Path.GetDirectoryName(epubPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(epubPath)) File.Delete(epubPath);

        using var zip = ZipFile.Open(epubPath, ZipArchiveMode.Create);

        // 1) mimetype — MUST be first and stored uncompressed.
        WriteEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        WriteEntry(zip, "META-INF/container.xml", ContainerXml());
        WriteEntry(zip, "OEBPS/style.css", Css(theme));
        WriteEntry(zip, "OEBPS/content.opf", Opf(bookTitle, chapters));
        WriteEntry(zip, "OEBPS/nav.xhtml", Nav(chapters));
        foreach (var c in chapters)
            WriteEntry(zip, $"OEBPS/{c.File}", ChapterXhtml(c));
    });

    private sealed record Chapter(string Id, string File, string Title, string Html);

    private static void WriteEntry(ZipArchive zip, string path, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string XhtmlSafe(string html)
    {
        foreach (var t in VoidTags)
            html = Regex.Replace(html, $@"<{t}\b((?:[^>""']|""[^""]*""|'[^']*')*?)\s*/?>", $"<{t}$1 />", RegexOptions.IgnoreCase);
        return html;
    }

    private static string? FirstHeading(string html)
    {
        var m = Regex.Match(html, @"<h[1-6][^>]*>(.*?)</h[1-6]>", RegexOptions.Singleline);
        if (!m.Success) return null;
        var text = Regex.Replace(m.Groups[1].Value, "<.*?>", "").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : System.Net.WebUtility.HtmlDecode(text);
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string ContainerXml() =>
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string Opf(string title, List<Chapter> chapters)
    {
        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        foreach (var c in chapters)
        {
            manifest.Append($"    <item id=\"{c.Id}\" href=\"{c.File}\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append($"    <itemref idref=\"{c.Id}\"/>\n");
        }
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var uid = Guid.NewGuid().ToString();
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:identifier id="bookid">urn:uuid:{uid}</dc:identifier>
            <dc:title>{Esc(title)}</dc:title>
            <dc:language>en</dc:language>
            <dc:creator>Marksmith</dc:creator>
            <meta property="dcterms:modified">{modified}</meta>
          </metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="css" href="style.css" media-type="text/css"/>
        {manifest}  </manifest>
          <spine>
        {spine}  </spine>
        </package>
        """;
    }

    private static string Nav(List<Chapter> chapters)
    {
        var items = new StringBuilder();
        foreach (var c in chapters)
            items.Append($"      <li><a href=\"{c.File}\">{Esc(c.Title)}</a></li>\n");
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Contents</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
          <nav epub:type="toc" id="toc">
            <h1>Contents</h1>
            <ol>
        {items}    </ol>
          </nav>
        </body>
        </html>
        """;
    }

    private static string ChapterXhtml(Chapter c) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
        <head><title>{Esc(c.Title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
        {c.Html}
        </body>
        </html>
        """;

    private static string Css(ThemeDefinition t) =>
        $$"""
        body { background: {{t.Background}}; color: {{t.Text}}; font-family: Georgia, serif; line-height: 1.6; padding: 1em; }
        h1, h2, h3, h4, h5, h6 { color: {{t.Heading}}; line-height: 1.25; }
        a { color: {{t.Primary}}; }
        code, pre { font-family: "Cascadia Mono", Consolas, monospace; background: {{t.Secondary}}; color: {{t.Code}}; }
        pre { padding: .8em; border: 1px solid {{t.Border}}; border-radius: 6px; overflow-x: auto; }
        code { padding: .1em .3em; border-radius: 4px; }
        blockquote { border-left: 4px solid {{t.Heading}}; margin: 1em 0; padding: .2em 1em; opacity: .9; }
        table { border-collapse: collapse; } td, th { border: 1px solid {{t.Border}}; padding: .4em .7em; }
        hr { border: none; border-top: 1px solid {{t.Border}}; }
        img { max-width: 100%; }
        """;
}
