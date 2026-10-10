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

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings) =>
        ExportAsync(markdown, epubPath, settings, null, null);

    public Task ExportAsync(string markdown, string epubPath, AppSettings settings, EpubMetadata? meta) =>
        ExportAsync(markdown, epubPath, settings, meta, null);

    /// <param name="mermaidPngs">
    /// One rendered PNG per ```mermaid fence, in document order (MermaidHarvestService), or null
    /// when no renderer is available (CLI, API without a host). Readers have no JavaScript, so
    /// without a PNG a diagram ships as its labelled source instead.
    /// </param>
    public Task ExportAsync(string markdown, string epubPath, AppSettings settings, EpubMetadata? meta,
                            IReadOnlyList<byte[]?>? mermaidPngs) => Task.Run(() =>
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
        var blockPictures = new List<EpubDirectives.Picture>();
        markdown = EpubDirectives.Lift(markdown, theme, out var coverPage, blockPictures);
        var doc = XhtmlWriter.Parse(Markdown.ToHtml(markdown, Pipeline));

        var bookTitle = NonEmpty(meta?.Title)
                        ?? NonEmpty(frontMatter, "title")
                        ?? NonEmpty(coverPage?.Title)
                        ?? HistoryEntry.ExtractTitle(markdown) ?? "Marksmith Export";

        // The person, never the tool: a reader's library lists books by dc:creator, and every
        // book used to be filed under "Marksmith". Settings' author name is the one Word exports
        // stamp too; with none, the book simply has no creator (EPUB doesn't require one).
        var author = NonEmpty(meta?.Author)
                     ?? NonEmpty(frontMatter, "author")
                     ?? NonEmpty(coverPage?.Author)
                     ?? NonEmpty(settings.AuthorName);

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
        var images = new List<PackagedImage>();
        var embedded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var drawn = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, png) in blockPictures)
        {
            images.Add(new($"block{drawn.Count + 1:000}", file, "image/png", png));
            drawn.Add(file);
        }
        foreach (var img in doc.QuerySelectorAll("img[src]"))
        {
            var src = img.GetAttribute("src")!;
            if (drawn.Contains(src)) continue;      // a chart or diagram drawn above, already packaged
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = ResolveImagePath(src);
            if (resolved is null) continue;          // missing on disk: leave the src as authored
            var media = MediaTypeFor(resolved);
            if (media is null) continue;             // not an image type EPUB 3 requires support for

            if (!embedded.TryGetValue(resolved, out var file))
            {
                file = $"images/{images.Count + 1:000}{Path.GetExtension(resolved).ToLowerInvariant()}";
                try { images.Add(new($"img{images.Count + 1:000}", file, media, File.ReadAllBytes(resolved))); }
                catch { continue; }
                embedded[resolved] = file;
            }
            img.SetAttribute("src", file);
            if (!img.HasAttribute("alt")) img.SetAttribute("alt", "");
        }

        // Diagrams and equations. Readers run no JavaScript, so a ```mermaid fence would ship as
        // raw "flowchart LR" text and math as literal \(E = mc^2\). Diagrams become the PNGs the
        // app rendered; equations become MathML, which EPUB 3 readers draw natively.
        var diagrams = doc.QuerySelectorAll("pre.mermaid, div.mermaid").ToList();
        string? Replace(AngleSharp.Dom.IElement el)
        {
            if (el.ClassList.Contains("mermaid") && el.LocalName is "pre" or "div")
                return DiagramFigure(el.TextContent, diagrams.IndexOf(el), mermaidPngs, images);
            if (el.ClassList.Contains("math") && el.LocalName is "span" or "div")
                return LatexToMathMl.Convert(StripMathDelimiters(el.TextContent), display: el.LocalName == "div");
            return null;
        }

        // Task lists. The app's own pipeline writes a bare <li><input type="checkbox"> without
        // Markdig's task-list classes, so the stylesheet couldn't hide the bullet and every task
        // showed a dot *and* a box. Tag them here, and freeze the boxes: a book can't be ticked.
        foreach (var box in doc.QuerySelectorAll("li > input[type=checkbox]"))
        {
            var li = box.ParentElement!;
            if (li.FirstElementChild != box) continue;
            box.SetAttribute("disabled", "disabled");
            li.ClassList.Add("task-list-item");
            li.ParentElement?.ClassList.Add("contains-task-list");
        }

        // Footnotes that readers can show as pop-ups (Apple Books, Kobo, Thorium) instead of a jump
        // to the end of the book.
        foreach (var a in doc.QuerySelectorAll("a.footnote-ref"))
        {
            a.SetAttribute("epub:type", "noteref");
            a.SetAttribute("role", "doc-noteref");
        }
        foreach (var notes in doc.QuerySelectorAll("div.footnotes"))
        {
            notes.SetAttribute("epub:type", "footnotes");
            foreach (var li in notes.QuerySelectorAll("li[id]"))
            {
                li.SetAttribute("epub:type", "footnote");
                li.SetAttribute("role", "doc-footnote");
            }
        }

        // Split into chapters on each top-level <h1>. Content before the first heading is its own chapter.
        var segments = XhtmlWriter.Chapters(doc, raw: Replace);
        if (segments.All(string.IsNullOrWhiteSpace)) segments = new() { "<p></p>" };

        var chapters = segments.Select((html, i) => new Chapter(
            Id: $"ch{i + 1:000}",
            File: $"ch{i + 1:000}.xhtml",
            Title: FirstHeading(html) ?? (i == 0 ? bookTitle : $"Section {i + 1}"),
            Html: html)).ToList();
        chapters = RetargetCrossChapterLinks(chapters);

        var dir = Path.GetDirectoryName(epubPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(epubPath)) File.Delete(epubPath);

        // A book without a cover opens on a title page instead of straight into the text, once
        // it is more than one chapter or the brand cover page is switched on (Settings › Branding,
        // the same switch that gives Word exports a title page).
        bool titlePage = coverFile is null && (chapters.Count > 1 || settings.BrandCoverPage || coverPage is not null);

        using var zip = ZipFile.Open(epubPath, ZipArchiveMode.Create);

        // 1) mimetype — MUST be first and stored uncompressed.
        WriteEntry(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        WriteEntry(zip, "META-INF/container.xml", ContainerXml());
        WriteEntry(zip, "OEBPS/style.css", Css(theme));
        WriteEntry(zip, "OEBPS/content.opf", Opf(bookTitle, author, language, publisher, identifier ?? StableIdentifier(bookTitle, author, Path.GetFileNameWithoutExtension(epubPath)), description, rights, chapters, coverFile, coverMediaType, images, titlePage));
        if (titlePage)
            WriteEntry(zip, "OEBPS/title.xhtml", TitleXhtml(bookTitle, author, publisher == ExportBranding.Tag ? null : publisher, language, coverPage));
        WriteEntry(zip, "OEBPS/nav.xhtml", Nav(chapters, language));
        if (coverFile is not null && coverBytes is not null)
        {
            WriteEntry(zip, $"OEBPS/{coverFile}", coverBytes);
            WriteEntry(zip, "OEBPS/cover.xhtml", CoverXhtml(coverFile, language));
        }
        foreach (var (_, file, _, bytes) in images)
            WriteEntry(zip, $"OEBPS/{file}", bytes);
        foreach (var c in chapters)
            WriteEntry(zip, $"OEBPS/{c.File}", ChapterXhtml(c, language));
    });

    private sealed record Chapter(string Id, string File, string Title, string Html);
    private sealed record PackagedImage(string Id, string File, string Media, byte[] Bytes);

    private static readonly Regex IdAttr = new(@"\bid=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex FragmentHref = new(@"\bhref=""#([^""]+)""", RegexOptions.Compiled);

    /// <summary>
    /// A same-page link ("#fn:1", "#setup") only resolves inside its own file, and chapters are
    /// separate files: a footnote reference in chapter 1 pointing at a note in chapter 3 went
    /// nowhere. Point every fragment link whose target lives in another chapter at that chapter.
    /// </summary>
    private static List<Chapter> RetargetCrossChapterLinks(List<Chapter> chapters)
    {
        if (chapters.Count < 2) return chapters;
        var home = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in chapters)
            foreach (Match m in IdAttr.Matches(c.Html))
                home.TryAdd(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), c.File);

        return chapters.Select(c => c with
        {
            Html = FragmentHref.Replace(c.Html, m =>
                home.TryGetValue(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), out var file) && file != c.File
                    ? $"href=\"{file}#{m.Groups[1].Value}\""
                    : m.Value),
        }).ToList();
    }

    /// <summary>
    /// A diagram as a figure. With a rendered PNG it's an image packaged into the book; without
    /// one (no renderer, or Mermaid rejected the source) the reader gets the source in a labelled
    /// block rather than an unexplained run of "A --> B" text.
    /// </summary>
    private static string DiagramFigure(string source, int index, IReadOnlyList<byte[]?>? pngs, List<PackagedImage> images)
    {
        var label = DiagramLabel(source, index + 1);
        var png = pngs is not null && index >= 0 && index < pngs.Count ? pngs[index] : null;
        if (png is { Length: > 0 })
        {
            var file = $"images/diagram-{index + 1:000}.png";
            images.Add(new($"diagram{index + 1:000}", file, "image/png", png));
            return $"<figure class=\"diagram\"><img src=\"{file}\" alt=\"{XhtmlWriter.Escape(label, attribute: true)}\" /></figure>";
        }
        return "<figure class=\"diagram diagram-source\">"
             + $"<pre><code>{XhtmlWriter.Escape(source.TrimEnd(), attribute: false)}</code></pre>"
             + $"<figcaption>{XhtmlWriter.Escape(label, attribute: false)} (diagram source; export from the MarkSmith app to draw it)</figcaption>"
             + "</figure>";
    }

    /// <summary>Alt text for a diagram: its accTitle/title when it has one, else its kind and number.</summary>
    internal static string DiagramLabel(string source, int number)
    {
        string? kind = null;
        foreach (var raw in source.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            foreach (var key in new[] { "accTitle:", "title:", "title " })
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase) && line.Length > key.Length)
                    return line[key.Length..].Trim().Trim('"');
            }
            if (kind is null)
            {
                var word = line.Split(' ', 2)[0];
                kind = word switch
                {
                    "flowchart" or "graph" => "Flowchart",
                    "sequenceDiagram" => "Sequence diagram",
                    "classDiagram" => "Class diagram",
                    "stateDiagram" or "stateDiagram-v2" => "State diagram",
                    "erDiagram" => "Entity relationship diagram",
                    "gantt" => "Gantt chart",
                    "pie" => "Pie chart",
                    "mindmap" => "Mind map",
                    "journey" => "User journey",
                    "gitGraph" => "Git graph",
                    "timeline" => "Timeline",
                    "quadrantChart" => "Quadrant chart",
                    _ => "Diagram",
                };
            }
        }
        return $"{kind ?? "Diagram"} {number}";
    }

    /// <summary>Markdig wraps math in \( \) or \[ \]; the converter wants the bare LaTeX.</summary>
    internal static string StripMathDelimiters(string text)
    {
        var t = text.Trim();
        foreach (var (open, close) in new[] { ("\\(", "\\)"), ("\\[", "\\]"), ("$$", "$$"), ("$", "$") })
        {
            if (t.Length >= open.Length + close.Length && t.StartsWith(open, StringComparison.Ordinal)
                && t.EndsWith(close, StringComparison.Ordinal))
                return t[open.Length..^close.Length].Trim();
        }
        return t;
    }

    /// <summary>Locates a local image the same way every other renderer does (DocumentImages):
    /// absolute paths, then relative to the document's folder.</summary>
    private static string? ResolveImagePath(string src) => DocumentImages.Resolve(src);

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
                               IReadOnlyList<PackagedImage> images, bool titlePage = false)
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
        if (titlePage)
        {
            manifest.Append("    <item id=\"titlepage\" href=\"title.xhtml\" media-type=\"application/xhtml+xml\"/>\n");
            spine.Append("    <itemref idref=\"titlepage\"/>\n");
        }
        foreach (var c in chapters)
        {
            var props = ChapterProperties(c.Html);
            var propsAttr = props.Length == 0 ? "" : $" properties=\"{props}\"";
            manifest.Append($"    <item id=\"{c.Id}\" href=\"{c.File}\" media-type=\"application/xhtml+xml\"{propsAttr}/>\n");
            spine.Append($"    <itemref idref=\"{c.Id}\"/>\n");
        }
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var uid = string.IsNullOrWhiteSpace(identifier) ? $"urn:uuid:{Guid.NewGuid()}" : identifier;
        
        var metaXml = new StringBuilder();
        metaXml.AppendLine($"    <dc:identifier id=\"bookid\">{Esc(uid)}</dc:identifier>");
        metaXml.AppendLine($"    <dc:title>{Esc(title)}</dc:title>");
        metaXml.AppendLine($"    <dc:language>{Esc(language)}</dc:language>");
        if (!string.IsNullOrWhiteSpace(author)) metaXml.AppendLine($"    <dc:creator>{Esc(author)}</dc:creator>");
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

    /// <summary>
    /// EPUB 3 makes a chapter declare what it contains: MathML, inline SVG, and anything loaded
    /// from the web. A reader may skip its MathML engine for an undeclared chapter, and epubcheck
    /// fails the book.
    /// </summary>
    internal static string ChapterProperties(string html)
    {
        var props = new List<string>();
        if (html.Contains("<math", StringComparison.Ordinal)) props.Add("mathml");
        if (html.Contains("<svg", StringComparison.Ordinal)) props.Add("svg");
        if (Regex.IsMatch(html, @"\b(?:src|poster)=""https?://", RegexOptions.IgnoreCase)) props.Add("remote-resources");
        return string.Join(' ', props);
    }

    /// <summary>
    /// A book with no ISBN/identifier gets one derived from its title and author (a name-based
    /// UUID), not a random one per export: readers key their library on it, so re-exporting the
    /// same book used to add a duplicate instead of replacing it. The file name is part of it:
    /// re-exporting a book to the same file keeps its identity, while two different books that
    /// share a title (two "Meeting Notes", or two untitled exports with no author) aren't taken
    /// for one another and replaced in the library.
    /// </summary>
    internal static string StableIdentifier(string title, string? author, string? fileStem = null)
    {
        var key = "marksmith-epub\n" + title.Trim() + "\n" + (author ?? "").Trim()
                  + (string.IsNullOrWhiteSpace(fileStem) ? "" : "\n" + fileStem.Trim().ToLowerInvariant());
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5 (name-based, SHA-1)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        var hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return $"urn:uuid:{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    // A :::cover-page adds its subtitle under the title and its organisation, date and version
    // at the foot, the way the Word export's cover page sets them.
    private static string TitleXhtml(string title, string? author, string? publisher, string language,
                                     DocxExportService.CoverPageInfo? cover = null)
    {
        var lines = new StringBuilder();
        lines.Append($"    <h1 class=\"title\">{Esc(title)}</h1>\n");
        if (!string.IsNullOrWhiteSpace(cover?.Subtitle)) lines.Append($"    <p class=\"subtitle\">{Esc(cover.Subtitle)}</p>\n");
        if (!string.IsNullOrWhiteSpace(author)) lines.Append($"    <p class=\"author\">{Esc(author)}</p>\n");
        var org = NonEmpty(cover?.Organization) ?? NonEmpty(publisher);
        if (org is not null) lines.Append($"    <p class=\"publisher\">{Esc(org)}</p>\n");
        var stamp = string.Join(" · ", new[] { cover?.Date, cover?.Version }.Where(v => !string.IsNullOrWhiteSpace(v)));
        if (stamp.Length > 0) lines.Append($"    <p class=\"edition\">{Esc(stamp)}</p>\n");
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{Esc(language)}" xml:lang="{Esc(language)}">
        <head><meta charset="utf-8"/><title>{Esc(title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
          <section epub:type="titlepage" class="titlepage">
        {lines}  </section>
        </body>
        </html>
        """;
    }

    private static string CoverXhtml(string coverFile, string language) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{Esc(language)}" xml:lang="{Esc(language)}">
        <head><title>Cover</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
        <body>
          <section epub:type="cover">
            <img src="{coverFile}" alt="Cover" style="max-width:100%;max-height:100%;"/>
          </section>
        </body>
        </html>
        """;

    private static string Nav(List<Chapter> chapters, string language)
    {
        var items = new StringBuilder();
        foreach (var c in chapters)
            items.Append($"      <li><a href=\"{c.File}\">{Esc(c.Title)}</a></li>\n");
        return $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{Esc(language)}" xml:lang="{Esc(language)}">
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

    private static string ChapterXhtml(Chapter c, string language) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops" lang="{Esc(language)}" xml:lang="{Esc(language)}">
        <head><meta charset="utf-8"/><title>{Esc(c.Title)}</title><link rel="stylesheet" type="text/css" href="style.css"/></head>
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
        code, pre { font-family: "Cascadia Mono", Consolas, monospace; background: {{t.Code}}; color: {{t.Text}}; }
        pre { padding: .8em; border: 1px solid {{t.Border}}; border-radius: 6px; overflow-x: auto; }
        code { padding: .1em .3em; border-radius: 4px; }
        blockquote { border-left: 4px solid {{t.Heading}}; margin: 1em 0; padding: .2em 1em; opacity: .9; }
        table { border-collapse: collapse; } td, th { border: 1px solid {{t.Border}}; padding: .4em .7em; }
        hr { border: none; border-top: 1px solid {{t.Border}}; }
        img { max-width: 100%; }
        pre code { padding: 0; background: none; }
        figure.diagram { margin: 1.2em 0; text-align: center; page-break-inside: avoid; break-inside: avoid; }
        figure.diagram img { max-width: 100%; height: auto; }
        figure.diagram-source { text-align: left; }
        figure.diagram-source pre { margin: 0; }
        figcaption { font-size: .85em; opacity: .75; margin-top: .4em; }
        math[display="block"] { margin: .8em 0; }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        li.task-list-item input { margin-right: .5em; }
        .footnotes { font-size: .85em; margin-top: 2em; }
        a.footnote-ref { text-decoration: none; }
        details > summary { font-weight: bold; }
        section.titlepage { text-align: center; margin-top: 30%; page-break-after: always; break-after: page; }
        section.titlepage h1.title { font-size: 2.2em; margin-bottom: .6em; }
        section.titlepage p.author { font-size: 1.25em; font-style: italic; margin: 0 0 2em; }
        section.titlepage p.subtitle { font-size: 1.2em; margin: -.2em 0 1.6em; opacity: .85; }
        section.titlepage p.publisher { font-size: .9em; opacity: .75; }
        section.titlepage p.edition { font-size: .85em; opacity: .65; margin-top: .4em; }
        figure.ms-block { margin: 1.2em 0; text-align: center; page-break-inside: avoid; break-inside: avoid; }
        figure.ms-block img { max-width: 100%; height: auto; border-radius: 6px; }
        div.ms-metrics { margin: 1.2em 0; text-align: center; }
        div.ms-metric { display: inline-block; vertical-align: top; min-width: 8em; margin: .3em; padding: .7em 1em; border: 1px solid {{t.Border}}; border-radius: 8px; background: {{t.Code}}; page-break-inside: avoid; break-inside: avoid; }
        p.ms-metric-value { margin: 0; font-size: 1.6em; font-weight: bold; line-height: 1.2; color: {{t.Heading}}; }
        p.ms-metric-label { margin: .2em 0 0; font-size: .85em; opacity: .8; }
        """;
}
