using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using MarkSmith.Models;

namespace MarkSmith.Services;

// EPUB 3 export. Markdown → themed XHTML chapters (split on each top-level heading), packaged as a
// valid EPUB container (mimetype first + uncompressed, META-INF/container.xml, an OPF manifest/spine,
// and an EPUB3 nav document), zipped with System.IO.Compression. No external dependency.
public sealed class EpubExportService
{
    public const string Extension = "epub";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseMathematics().Build();

    // Shared AppServices.Themes singleton instead of a private instance (see DocxExportService).
    private static ThemeCatalog Themes => AppServices.Themes;

    // Single alternation covering every HTML void tag, used to self-close them in ONE pass. The
    // previous form was a string[] VoidTags iterated into 14 sequential Regex.Replace calls (one
    // per tag) — each allocated a Regex and re-scanned the entire document.
    private static readonly Regex VoidTagRegex = new(
        @"<(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)\b((?:[^>""']|""[^""]*""|'[^']*')*?)\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings) =>
        ExportAsync(markdown, epubPath, settings, null);

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings, EpubMetadata? meta) => Task.Run(() =>
    {
        // Front matter is metadata for the OPF (Dublin Core) — read it before normalization.
        var frontMatter = ExtractFrontMatter(markdown);

        markdown = TextNormalizer.Newlines(markdown);
        markdown = AdmonitionNormalizer.Apply(markdown);
        markdown = DialectNormalizer.Apply(markdown, settings.DashMode);
        if (settings.NoEmoji) markdown = EmojiStripper.Strip(markdown);
        markdown = DashReplacer.Apply(markdown, settings.DashMode, settings.DashCustom);
        markdown = FormattingService.Apply(markdown, settings);

        var theme = Themes.GetOrDefault(settings.Theme);
        var bodyHtml = XhtmlSafe(Markdown.ToHtml(markdown, Pipeline));
        
        var bookTitle = NonEmpty(meta?.Title)
                        ?? NonEmpty(frontMatter, "title")
                        ?? HistoryEntry.ExtractTitle(markdown) ?? "Marksmith Export";

        var author = NonEmpty(meta?.Author)
                     ?? NonEmpty(frontMatter, "author") ?? "Marksmith";

        var language = NonEmpty(meta?.Language)
                       ?? NonEmpty(frontMatter, "language")
                       ?? (string.IsNullOrWhiteSpace(settings.ContentLanguage) ? "en" : settings.ContentLanguage);

        var publisher = NonEmpty(meta?.Publisher) ?? NonEmpty(frontMatter, "publisher") ?? ExportBranding.Tag;
        var identifier = NonEmpty(meta?.Identifier)
                          ?? NonEmpty(frontMatter, "isbn")
                          ?? NonEmpty(frontMatter, "identifier");
        var description = NonEmpty(meta?.Description) ?? NonEmpty(frontMatter, "description");
        var rights = NonEmpty(meta?.Rights) ?? NonEmpty(frontMatter, "rights");

        // Cover Image resolution: explicit EpubMetadata path -> Front matter cover -> BrandLogoPath
        var explicitCoverPath = NonEmpty(meta?.CoverImagePath)
                                ?? NonEmpty(frontMatter, "cover")
                                ?? NonEmpty(frontMatter, "cover_image");

        string? logoPath = null;
        if (!string.IsNullOrWhiteSpace(explicitCoverPath) && File.Exists(explicitCoverPath))
        {
            logoPath = explicitCoverPath;
        }
        else if (settings.BrandCoverPage && !string.IsNullOrWhiteSpace(settings.BrandLogoPath) && File.Exists(settings.BrandLogoPath))
        {
            logoPath = settings.BrandLogoPath;
        }

        string? coverFile = null, coverMediaType = null;
        byte[]? coverBytes = null;
        if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
        {
            var ext = Path.GetExtension(logoPath).ToLowerInvariant();
            coverMediaType = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".svg" => "image/svg+xml",
                _ => null,
            };
            if (coverMediaType is not null)
            {
                coverFile = ext switch
                {
                    ".png" => "cover.png",
                    ".jpg" or ".jpeg" => "cover.jpg",
                    ".svg" => "cover.svg",
                    _ => "cover.img"
                };
                coverBytes = File.ReadAllBytes(logoPath);
            }
        }

        // Pull local images into the package. A referenced file that is not in the manifest is
        // both a broken image in every reader and an EPUB spec violation, and every local image
        // in a document hit exactly that: the src was written through untouched and nothing was
        // ever embedded. Remote and data: URIs are left alone — the first is the reader's problem
        // and the second needs no manifest entry.
        var images = new List<(string Id, string File, string Media, byte[] Bytes)>();
        bodyHtml = ImgSrcRe().Replace(bodyHtml, m =>
        {
            var src = m.Groups["src"].Value;
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            var resolved = ResolveImagePath(src);
            if (resolved is null) return m.Value;   // missing on disk: leave the href as authored

            var media = MediaTypeFor(resolved);
            if (media is null) return m.Value;      // not an image type EPUB 3 requires support for

            var existing = images.FirstOrDefault(i => string.Equals(i.File, "images/" + Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase));
            string file = existing.File ?? $"images/{images.Count + 1:000}{Path.GetExtension(resolved).ToLowerInvariant()}";
            if (existing.File is null)
            {
                try { images.Add(($"img{images.Count + 1:000}", file, media, File.ReadAllBytes(resolved))); }
                catch { return m.Value; }
            }
            else file = existing.File;

            return m.Value.Replace(m.Groups["src"].Value, file);
        });

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
        WriteEntry(zip, "OEBPS/content.opf", Opf(bookTitle, author, language, publisher, identifier, description, rights, chapters, coverFile, coverMediaType, images));
        WriteEntry(zip, "OEBPS/nav.xhtml", Nav(chapters));
        if (coverFile is not null && coverBytes is not null)
        {
            WriteEntry(zip, $"OEBPS/{coverFile}", coverBytes);
            WriteEntry(zip, "OEBPS/cover.xhtml", CoverXhtml(coverFile));
        }
        foreach (var (_, file, _, bytes) in images)
            WriteEntry(zip, $"OEBPS/{file}", bytes);
        foreach (var c in chapters)
            WriteEntry(zip, $"OEBPS/{c.File}", ChapterXhtml(c));
    });

    private sealed record Chapter(string Id, string File, string Title, string Html);

    private static readonly Regex ImgSrc = new(
        @"<img\b[^>]*?\bsrc=""(?<src>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static Regex ImgSrcRe() => ImgSrc;

    /// <summary>
    /// Locates a local image the same way the DOCX exporter does — absolute path, then relative to
    /// the app base directory, then to the working directory — so the two exporters agree on where
    /// a document's images live instead of each inventing a rule.
    /// </summary>
    private static string? ResolveImagePath(string src)
    {
        var raw = src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? src[8..] : src;
        raw = Uri.UnescapeDataString(raw);
        var candidates = new[]
        {
            raw,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, raw.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(Directory.GetCurrentDirectory(), raw.Replace('/', Path.DirectorySeparatorChar)),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
        }
        return null;
    }

    /// <summary>The EPUB 3 core image media types; anything else is left as an external reference.</summary>
    private static string? MediaTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => null,
    };

    private static void WriteEntry(ZipArchive zip, string path, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(path, level);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] bytes)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static Dictionary<string, string> ExtractFrontMatter(string markdown)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = markdown.Split('\n');
        int i = 0;
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Length || lines[i].Trim() != "---") return map;
        for (i++; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t == "---" || t == "...") break;
            var colon = t.IndexOf(':');
            if (colon <= 0) continue;
            var key = t[..colon].Trim();
            var value = t[(colon + 1)..].Trim().Trim('"', '\'');
            if (key.Length > 0 && value.Length > 0) map[key] = value;
        }
        return map;
    }

    private static string? NonEmpty(string? val) => string.IsNullOrWhiteSpace(val) ? null : val;
    private static string? NonEmpty(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static string XhtmlSafe(string html) =>
        VoidTagRegex.Replace(html, m => $"<{m.Groups[1].Value.ToLowerInvariant()}{m.Groups[2].Value} />");

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

    private static string Opf(string title, string author, string language, string? publisher, string? identifier,
                               string? description, string? rights, List<Chapter> chapters,
                               string? coverFile, string? coverMediaType,
                               IReadOnlyList<(string Id, string File, string Media, byte[] Bytes)> images)
    {
        var manifest = new StringBuilder();
        // Every embedded image needs its own manifest item, or the package fails validation even
        // though the bytes are present.
        foreach (var img in images)
            manifest.Append($"    <item id=\"{img.Id}\" href=\"{img.File}\" media-type=\"{img.Media}\"/>\n");
        var spine = new StringBuilder();
        if (coverFile is not null)
        {
            manifest.Append("    <item id=\"cover\" href=\"cover.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            manifest.Append($"    <item id=\"cover-image\" href=\"{coverFile}\" media-type=\"{coverMediaType}\" properties=\"cover-image\"/>\n");
            spine.Append("    <itemref idref=\"cover\"/>\n");
        }
        foreach (var c in chapters)
        {
            manifest.Append($"    <item id=\"{c.Id}\" href=\"{c.File}\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append($"    <itemref idref=\"{c.Id}\"/>\n");
        }
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var uid = string.IsNullOrWhiteSpace(identifier) ? $"urn:uuid:{Guid.NewGuid()}" : identifier;
        
        var metaXml = new StringBuilder();
        metaXml.AppendLine($"    <dc:identifier id=\"bookid\">{Esc(uid)}</dc:identifier>");
        metaXml.AppendLine($"    <dc:title>{Esc(title)}</dc:title>");
        metaXml.AppendLine($"    <dc:language>{Esc(language)}</dc:language>");
        metaXml.AppendLine($"    <dc:creator>{Esc(author)}</dc:creator>");
        if (!string.IsNullOrWhiteSpace(publisher)) metaXml.AppendLine($"    <dc:publisher>{Esc(publisher)}</dc:publisher>");
        if (!string.IsNullOrWhiteSpace(description)) metaXml.AppendLine($"    <dc:description>{Esc(description)}</dc:description>");
        if (!string.IsNullOrWhiteSpace(rights)) metaXml.AppendLine($"    <dc:rights>{Esc(rights)}</dc:rights>");
        metaXml.AppendLine($"    <meta property=\"dcterms:modified\">{modified}</meta>");

        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
        {metaXml.ToString().TrimEnd()}
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

    private static string CoverXhtml(string coverFile) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
        <head><title>Cover</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
          <section epub:type="cover">
            <img src="{coverFile}" alt="Cover" style="max-width:100%;max-height:100%;"/>
          </section>
        </body>
        </html>
        """;

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
