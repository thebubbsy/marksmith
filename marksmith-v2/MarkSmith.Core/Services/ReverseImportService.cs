using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using W = DocumentFormat.OpenXml.Wordprocessing;
using M = DocumentFormat.OpenXml.Math;
using A = DocumentFormat.OpenXml.Drawing;
using MarkSmith.Models;

namespace MarkSmith.Services;

// The Smart Dual-Mode DOCX-to-Markdown engine — the inverse of DocxExportService.
//
//   Tier 1 (Lossless): a Marksmith-made .docx carries the original source in a private custom-XML
//     part (MarksmithSourceStore). When that marker is present we hand the exact source back —
//     byte-for-byte, no reconstruction. A staleness flag names the one leak: the user edited the
//     file in Word after export.
//
//   Tier 2 (Universal): ANY .docx in the world — Word, Pandoc, Google Docs, LibreOffice — is parsed
//     into clean Markdown. Images are extracted to a media/ folder, headings/lists/tables/quotes/
//     code/HR are generalized well beyond Marksmith's own signatures, OMML becomes LaTeX math, and
//     shapes become Mermaid (or a rasterized PNG fallback) so nothing is silently dropped.
//
//   Tier 3 (Pandoc): if the native engine produces nothing and a Pandoc importer plugin is
//     installed, we defer to it.
//
// The class is single-use per import: instance fields hold per-document state (image map, footnotes,
// media counters) and are reset at the start of each Universal Engine run.
public enum ImportTier { None, EmbeddedSource, UniversalEngine, Pandoc }

public sealed record ReverseImportResult(
    string Markdown,
    ImportTier Tier,
    bool IsStale,
    string? Warning,
    IReadOnlyList<string> ExtractedMedia);

public sealed record DocxCommentInfo(
    string Id,
    string Author,
    string? Initials,
    DateTime? Date,
    string Text);

public sealed class ReverseImportService : IReverseImportService
{
    private const string StaleWarning =
        "This document was modified in Word after Marksmith exported it. The recovered source may not reflect the current visible content.";

    // The wordprocessingml namespace URI, used for generic attribute reads (e.g. w:ilvl/@w:val).
    private const string WNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly HashSet<string> MonoFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier New", "Lucida Console", "Source Code Pro", "Fira Code", "JetBrains Mono",
    };

    // ---- per-import state (reset in RunUniversalEngine) --------------------------------------
    private MainDocumentPart? _main;
    private string? _mediaDir;
    private Dictionary<string, DocxImageExtractor.ExtractedImage> _imageMap = new();
    private readonly List<string> _rasterizedPaths = new();
    private int _diagramCounter;
    private Dictionary<string, string> _footnotes = new();
    private readonly HashSet<string> _usedFootnotes = new();
    private Dictionary<string, DocxCommentInfo> _comments = new();
    private ReverseImportOptions _options = new();

    // ---- public API --------------------------------------------------------------------------

    /// <summary>Converts a DOCX file to Markdown with customizable reverse-import options.</summary>
    public string ConvertDocxToMarkdown(string docxPath, ReverseImportOptions? options = null)
    {
        using var stream = File.OpenRead(docxPath);
        return ConvertDocxToMarkdown(stream, options);
    }

    /// <summary>Converts a DOCX stream to Markdown with customizable reverse-import options.</summary>
    public string ConvertDocxToMarkdown(Stream docxStream, ReverseImportOptions? options = null) =>
        ImportCore(docxStream, mediaDir: null, pandocPath: null, options: options).Markdown;

    /// <summary>Full async cascade: Tier 1 (embedded) -> Tier 2 (Universal) -> Tier 3 (Pandoc).</summary>
    public Task<ReverseImportResult> ImportFromDocxAsync(string docxPath, string? mediaDir = null, ReverseImportOptions? options = null) =>
        Task.Run(() =>
        {
            mediaDir ??= DefaultMediaDir(docxPath);
            using var stream = File.OpenRead(docxPath);
            return ImportCore(stream, mediaDir, docxPath, options);
        });

    /// <summary>Stream overload. Pandoc fallback is unavailable without a path; Tier 1 -> Tier 2.</summary>
    public Task<ReverseImportResult> ImportFromDocxAsync(Stream stream, string? mediaDir = null, ReverseImportOptions? options = null) =>
        Task.Run(() => ImportCore(stream, mediaDir, pandocPath: null, options: options));

    /// <summary>Back-compat sync entry (used by DocxRoundTripTests): Tier 1 -> Tier 2, markdown only.</summary>
    public string ImportFromDocx(string docxPath)
    {
        using var stream = File.OpenRead(docxPath);
        return ImportFromDocx(stream);
    }

    /// <summary>Back-compat sync entry: Tier 1 -> Tier 2 (no Pandoc, no media write), markdown only.</summary>
    public string ImportFromDocx(Stream stream) =>
        ImportCore(stream, mediaDir: null, pandocPath: null).Markdown;

    // ---- PDF → Markdown (D1 extension) ---------------------------------------------------------

    /// <summary>
    /// Imports a PDF file into Markdown by extracting text from each page's content stream.
    /// Digital PDFs (with selectable text) yield clean paragraphs; scanned/image-only PDFs
    /// yield empty pages (use OcrEngineService for those — D2).
    /// </summary>
    public Task<ReverseImportResult> ImportFromPdfAsync(string pdfPath) =>
        Task.Run(() => ImportFromPdf(pdfPath));

    /// <summary>Synchronous PDF import entry point.</summary>
    public ReverseImportResult ImportFromPdf(string pdfPath)
    {
        using var stream = File.OpenRead(pdfPath);
        return ImportFromPdf(stream);
    }

    /// <summary>Stream-based PDF import.</summary>
    public ReverseImportResult ImportFromPdf(Stream stream)
    {
        // Use PdfDocumentOpenMode.Import for reading/extracting PDF document streams (ReadOnly is obsolete CS0618)
        using var doc = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        // Tier 1: embedded source (lossless) — Marksmith-made PDFs carry the exact Markdown in the
        // Info dictionary (PdfSourceStore), so a PDF-only workflow recovers the source byte-for-byte.
        var embedded = PdfSourceStore.Read(doc);
        if (embedded is not null)
        {
            return new ReverseImportResult(
                embedded.Markdown,
                ImportTier.EmbeddedSource,
                embedded.IsStale,
                embedded.IsStale ? StaleWarning : null,
                Array.Empty<string>());
        }

        var markdown = ExtractPdfMarkdown(doc);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new ReverseImportResult("", ImportTier.None, false,
                "No extractable text found. This PDF may be scanned/image-only — try OCR (D2).",
                Array.Empty<string>());
        }
        return new ReverseImportResult(markdown, ImportTier.UniversalEngine, false, null, Array.Empty<string>());
    }

    /// <summary>
    /// Extracts structured Markdown from a PDF document by parsing page content streams for
    /// text-showing operators (Tj, TJ, ', "). Handles font-size based heading detection and
    /// paragraph grouping via text-position tracking.
    /// </summary>
    private static string ExtractPdfMarkdown(PdfDocument doc)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            var pageText = ExtractPageText(page);
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(pageText);
            }
        }
        return sb.ToString().Trim();
    }

    private static string ExtractPageText(PdfPage page)
    {
        var contents = page.Contents;
        if (contents is null) return "";

        var sb = new StringBuilder();
        foreach (var item in contents.Elements)
        {
            if (item is PdfSharp.Pdf.PdfDictionary dict)
            {
                var stream = dict.Stream;
                if (stream is null) continue;
                var bytes = stream.Value;
                var content = Encoding.Latin1.GetString(bytes);
                AppendContentStreamText(sb, content);
            }
        }
        return sb.ToString().Trim();
    }

    // Regex patterns for PDF text-showing operators in content streams.
    private static readonly Regex TjPattern = new(@"\(([^)]*)\)\s*Tj", RegexOptions.Compiled);
    private static readonly Regex TJArrayPattern = new(@"\[(.*?)\]\s*TJ", RegexOptions.Compiled);
    private static readonly Regex TJStringPattern = new(@"\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex QuotePattern = new(@"\(([^)]*)\)\s*'", RegexOptions.Compiled);
    // Line-break detection used to be rebuilt per reverse-import call.
    private static readonly Regex TdPattern = new(@"([\d.-]+)\s+([\d.-]+)\s+Td|([\d.-]+)\s+([\d.-]+)\s+TD|T\*", RegexOptions.Compiled);

    private static void AppendContentStreamText(StringBuilder sb, string content)
    {
        // Track text positioning for line breaks. Td/TD with significant Y-offset = new line.
        var lines = new List<string>();
        var currentLine = new StringBuilder();

        // Process the content stream in order using a combined tokenizer approach.
        // We look for text operators and positioning operators.
        var allMatches = new List<(int pos, string type, string value)>();

        foreach (Match m in TjPattern.Matches(content))
            allMatches.Add((m.Index, "Tj", m.Groups[1].Value));
        foreach (Match m in QuotePattern.Matches(content))
            allMatches.Add((m.Index, "'", m.Groups[1].Value));
        foreach (Match m in TJArrayPattern.Matches(content))
        {
            // Concatenate all string fragments within the TJ array.
            var fragments = TJStringPattern.Matches(m.Groups[1].Value);
            var combined = string.Concat(fragments.Select(f => f.Groups[1].Value));
            allMatches.Add((m.Index, "TJ", combined));
        }

        // Detect line breaks via Td/TD/T* operators.
        var newLinePositions = new HashSet<int>();
        foreach (Match m in TdPattern.Matches(content))
        {
            newLinePositions.Add(m.Index);
            // If Y offset is significant (negative = move down), it's a paragraph break.
            var yStr = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value;
            if (double.TryParse(yStr, out double y) && Math.Abs(y) > 2)
                newLinePositions.Add(m.Index); // paragraph-level break
        }

        // Sort all text fragments by position and build lines.
        allMatches.Sort((a, b) => a.pos.CompareTo(b.pos));
        int lastNewline = -1;
        foreach (var (pos, type, value) in allMatches)
        {
            // Check if there's a newline operator between the last text and this one.
            bool hasNewline = newLinePositions.Any(p => p > lastNewline && p < pos);
            if (hasNewline && currentLine.Length > 0)
            {
                lines.Add(currentLine.ToString());
                currentLine.Clear();
            }
            lastNewline = pos;

            var decoded = DecodePdfString(value);
            currentLine.Append(decoded);
            if (type == "'") // ' operator moves to next line after showing text
            {
                lines.Add(currentLine.ToString());
                currentLine.Clear();
            }
        }
        if (currentLine.Length > 0)
            lines.Add(currentLine.ToString());

        // Join lines into paragraphs (heuristic: short gap = same paragraph).
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                sb.AppendLine(trimmed);
        }
    }

    /// <summary>Decodes PDF string escape sequences (\n, \r, \t, \(, \), \\, octal).</summary>
    private static string DecodePdfString(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                switch (raw[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '(': sb.Append('('); break;
                    case ')': sb.Append(')'); break;
                    case '\\': sb.Append('\\'); break;
                    case >= '0' and <= '7':
                        // Octal escape (up to 3 digits).
                        var octal = raw[i].ToString();
                        while (octal.Length < 3 && i + 1 < raw.Length && raw[i + 1] >= '0' && raw[i + 1] <= '7')
                            octal += raw[++i];
                        sb.Append((char)Convert.ToInt32(octal, 8));
                        break;
                    default: sb.Append(raw[i]); break;
                }
            }
            else
            {
                sb.Append(raw[i]);
            }
        }
        return sb.ToString();
    }

    // ---- cascade core ------------------------------------------------------------------------

    private ReverseImportResult ImportCore(Stream stream, string? mediaDir, string? pandocPath, ReverseImportOptions? options = null)
    {
        options ??= new ReverseImportOptions();
        using var doc = WordprocessingDocument.Open(stream, false);

        // Tier 1: embedded source (lossless).
        var embedded = MarksmithSourceStore.Read(doc);
        if (embedded is not null)
        {
            return new ReverseImportResult(
                embedded.Markdown,
                ImportTier.EmbeddedSource,
                embedded.IsStale,
                embedded.IsStale ? StaleWarning : null,
                Array.Empty<string>());
        }

        // Tier 2: native Universal Engine.
        try
        {
            var (md, media) = RunUniversalEngine(doc, mediaDir, options);
            if (!string.IsNullOrWhiteSpace(md))
                return new ReverseImportResult(md, ImportTier.UniversalEngine, false, null, media);
        }
        catch
        {
            // fall through to Pandoc
        }

        // Tier 3: Pandoc importer plugin (path-based only).
        if (pandocPath is not null)
        {
            try
            {
                var md = AppServices.Plugins.FindImporter("docx")?.ImportToMarkdown(pandocPath);
                if (!string.IsNullOrWhiteSpace(md))
                    return new ReverseImportResult(md!, ImportTier.Pandoc, false, null, Array.Empty<string>());
            }
            catch
            {
                // fall through to None
            }
        }

        return new ReverseImportResult("", ImportTier.None, false, null, Array.Empty<string>());
    }

    private static string DefaultMediaDir(string docxPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(docxPath));
        if (string.IsNullOrEmpty(dir)) dir = ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(docxPath) + "_media");
    }

    // ---- Universal Engine --------------------------------------------------------------------

    private (string Markdown, IReadOnlyList<string> Media) RunUniversalEngine(WordprocessingDocument doc, string? mediaDir, ReverseImportOptions options)
    {
        var main = doc.MainDocumentPart ?? throw new InvalidDataException("Not a valid .docx (no main document part).");
        var body = main.Document?.Body ?? throw new InvalidDataException("Not a valid .docx (no body).");

        // Reset per-import state.
        _main = main;
        _mediaDir = mediaDir;
        _diagramCounter = 0;
        _rasterizedPaths.Clear();
        _usedFootnotes.Clear();
        _footnotes = LoadFootnotes(doc);
        _comments = LoadComments(doc);
        _options = options;
        _imageMap = mediaDir is not null
            ? DocxImageExtractor.ExtractAll(main, mediaDir)
            : new Dictionary<string, DocxImageExtractor.ExtractedImage>();

        var numMap = LoadNumbering(doc);
        var items = new List<Block>();

        foreach (var child in body.ChildElements)
        {
            switch (child)
            {
                case W.Table t:
                    items.Add(ConvertTable(t));
                    break;
                case W.Paragraph p:
                    var block = ConvertParagraph(p);
                    if (block is not null) items.Add(block);
                    break;
                // sectPr and other structural parts carry no content.
            }
        }

        var markdown = Assemble(items, numMap);
        var media = _imageMap.Values.Select(v => v.RelativePath)
            .Concat(_rasterizedPaths)
            .Distinct()
            .ToList();
        return (markdown, media);
    }

    // ---- intermediate model ------------------------------------------------------------------

    private abstract record Block;
    private sealed record HeadingBlock(int Level, string Text) : Block;
    private sealed record ParagraphBlock(string Text) : Block;
    private sealed record HrBlock : Block;
    private sealed record TableBlock(string Markdown) : Block;
    private sealed record AlertBlock(string Markdown) : Block;
    private sealed record CodeBlock(string Lang, string Content, bool Mergeable) : Block;
    private sealed record DisplayMathBlock(string Latex) : Block;
    private sealed record MermaidBlock(string Content) : Block;
    private sealed record ListItemBlock(int NumId, bool IsTask, bool Checked, string Text, int Level) : Block;
    private sealed record ImageBlock(string Alt, string Path) : Block;
    private sealed record BlockquoteBlock(string Text) : Block;
    private sealed record RawBlock(string Markdown) : Block;

    // ---- assembly ----------------------------------------------------------------------------

    private string Assemble(List<Block> items, Dictionary<int, bool> numMap)
    {
        var blocks = new List<string>();
        int i = 0;
        while (i < items.Count)
        {
            if (items[i] is ListItemBlock first)
            {
                // A "list" is a run of consecutive list paragraphs sharing (numId, task-ness).
                var group = new List<ListItemBlock>();
                while (i < items.Count && items[i] is ListItemBlock li &&
                       li.NumId == first.NumId && li.IsTask == first.IsTask)
                {
                    group.Add(li);
                    i++;
                }
                blocks.Add(RenderListGroup(group, numMap));
            }
            else if (items[i] is CodeBlock { Mergeable: true } firstCode)
            {
                // Merge consecutive generalized monospace paragraphs into a single fence.
                var lines = new List<string> { firstCode.Content };
                var lang = firstCode.Lang;
                i++;
                while (i < items.Count && items[i] is CodeBlock { Mergeable: true } cc)
                {
                    lines.Add(cc.Content);
                    if (string.IsNullOrEmpty(lang)) lang = cc.Lang;
                    i++;
                }
                blocks.Add("```" + lang + "\n" + string.Join("\n", lines) + "\n```");
            }
            else
            {
                blocks.Add(RenderBlock(items[i]));
                i++;
            }
        }

        var result = string.Join("\n\n", blocks) + "\n";

        // Footnote definitions (C7) — appended once, in id order, for every reference actually used.
        if (_usedFootnotes.Count > 0 && _footnotes.Count > 0)
        {
            var defs = new List<string>();
            foreach (var id in _usedFootnotes.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (_footnotes.TryGetValue(id, out var text) && text.Length > 0)
                    defs.Add($"[^{id}]: {text}");
            }
            if (defs.Count > 0) result += "\n" + string.Join("\n", defs) + "\n";
        }

        return result;
    }

    private string RenderBlock(Block b) => b switch
    {
        HeadingBlock h => new string('#', h.Level) + " " + h.Text,
        ParagraphBlock p => p.Text,
        HrBlock => "---",
        TableBlock t => t.Markdown,
        AlertBlock a => a.Markdown,
        CodeBlock c => "```" + c.Lang + "\n" + c.Content + "\n```",
        DisplayMathBlock m => "$$\n" + m.Latex + "\n$$",
        MermaidBlock mm => "```mermaid\n" + mm.Content + "\n```",
        ImageBlock img => "![" + img.Alt + "](" + img.Path + ")",
        BlockquoteBlock q => RenderBlockquote(q.Text),
        RawBlock raw => raw.Markdown,
        _ => "",
    };

    private static string RenderBlockquote(string text) =>
        string.Join("\n", text.Split('\n').Select(l => "> " + l));

    private string RenderListGroup(List<ListItemBlock> group, Dictionary<int, bool> numMap)
    {
        var isOrdered = numMap.TryGetValue(group[0].NumId, out var ord) && ord;
        var lines = new List<string>();
        var counters = new Dictionary<int, int>(); // per-level ordered counters (C5 nesting)
        foreach (var item in group)
        {
            var indent = new string(' ', Math.Max(0, item.Level) * 2);
            string prefix;
            if (item.IsTask)
            {
                prefix = item.Checked ? "- [x] " : "- [ ] ";
            }
            else if (isOrdered)
            {
                counters.TryGetValue(item.Level, out var n);
                counters[item.Level] = n + 1;
                prefix = counters[item.Level] + ". ";
            }
            else
            {
                prefix = "- ";
            }
            lines.Add(indent + prefix + item.Text);
        }
        return string.Join("\n", lines);
    }

    // ---- numbering ---------------------------------------------------------------------------

    // numId -> isOrdered (decimal vs bullet), from numbering.xml.
    private static Dictionary<int, bool> LoadNumbering(WordprocessingDocument doc)
    {
        var map = new Dictionary<int, bool>();
        var numbering = doc.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
        if (numbering is null) return map;

        var abstractOrdered = new Dictionary<int, bool>();
        foreach (var an in numbering.Elements<W.AbstractNum>())
        {
            var absId = an.AbstractNumberId?.Value ?? 0;
            var lvl0 = an.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == 0);
            // OpenXml 3.x: NumberFormatValues is a struct — compare the value, never ToString().
            var fmt = lvl0?.GetFirstChild<W.NumberingFormat>()?.Val?.Value;
            abstractOrdered[absId] = fmt == W.NumberFormatValues.Decimal;
        }
        foreach (var num in numbering.Elements<W.NumberingInstance>())
        {
            var numId = num.NumberID?.Value ?? 0;
            var absId = num.GetFirstChild<W.AbstractNumId>()?.Val?.Value ?? 0;
            map[numId] = abstractOrdered.TryGetValue(absId, out var ord) && ord;
        }
        return map;
    }

    // ---- footnotes ---------------------------------------------------------------------------

    private static Dictionary<string, string> LoadFootnotes(WordprocessingDocument doc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var footnotes = doc.MainDocumentPart?.FootnotesPart?.Footnotes;
        if (footnotes is null) return map;
        foreach (var fn in footnotes.Elements<W.Footnote>())
        {
            // Footnote ids are numeric; the auto-generated separator (-1) and continuation-separator
            // (0) footnotes carry id <= 0, while real author footnotes start at 1.
            long? idVal = fn.Id?.Value;
            if (idVal is null or <= 0) continue;
            var id = idVal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var text = string.Concat(fn.Descendants<W.Text>().Select(t => t.Text)).Trim();
            if (text.Length > 0) map[id] = text;
        }
        return map;
    }

    // ---- comments ----------------------------------------------------------------------------

    private static Dictionary<string, DocxCommentInfo> LoadComments(WordprocessingDocument doc)
    {
        var map = new Dictionary<string, DocxCommentInfo>(StringComparer.Ordinal);
        var comments = doc.MainDocumentPart?.WordprocessingCommentsPart?.Comments;
        if (comments is null) return map;
        foreach (var c in comments.Elements<W.Comment>())
        {
            var id = c.Id?.Value ?? "";
            if (string.IsNullOrEmpty(id)) continue;
            var author = c.Author?.Value ?? "Reviewer";
            var initials = c.Initials?.Value;
            var date = c.Date?.Value;
            var text = string.Concat(c.Descendants<W.Text>().Select(t => t.Text)).Trim();
            map[id] = new DocxCommentInfo(id, author, initials, date, text);
        }
        return map;
    }

    // ---- paragraphs --------------------------------------------------------------------------

    private Block? ConvertParagraph(W.Paragraph p)
    {
        var pPr = p.GetFirstChild<W.ParagraphProperties>();
        var styleId = pPr?.GetFirstChild<W.ParagraphStyleId>()?.Val?.Value ?? "";

        // Drawings (diagrams & pictures). A standalone drawing paragraph — no text outside the
        // drawing — becomes a block; inline images within text are handled inside ConvertRun.
        var drawing = p.Descendants<W.Drawing>().FirstOrDefault();
        if (drawing is not null)
        {
            bool standalone = !p.Descendants<W.Text>()
                .Any(t => t.Text.Length > 0 && !t.Ancestors<W.Drawing>().Any());
            if (standalone)
            {
                var drawn = ConvertDrawing(drawing);
                if (drawn is not null) return drawn;
            }
        }

        // Heading (generalized cascade: style name -> outline level -> font-size heuristic).
        var headingLevel = DetectHeadingLevel(p, pPr, styleId);
        if (headingLevel > 0)
            return new HeadingBlock(headingLevel, ConvertInlines(p).Trim());

        // Horizontal rule: an empty paragraph carrying ANY bottom border (C4).
        var bottomBorder = pPr?.GetFirstChild<W.ParagraphBorders>()?.GetFirstChild<W.BottomBorder>();
        var hasAnyText = p.Descendants<W.Text>().Any(t => t.Text.Length > 0);
        if (!hasAnyText && bottomBorder?.Val?.Value is not null)
            return new HrBlock();

        // Code block — Marksmith form: keepLines + a full border + Consolas runs.
        if (pPr?.GetFirstChild<W.KeepLines>() is not null)
        {
            var lang = styleId.StartsWith("MSCode_", StringComparison.Ordinal)
                ? styleId.Substring("MSCode_".Length)
                : "";
            return new CodeBlock(lang, ExtractCodeContent(p), Mergeable: false);
        }

        // Code block — generalized: all runs monospace AND (shading OR a code-like style) (C3).
        if (IsGeneralizedCodeParagraph(p, pPr, styleId, out var codeLang))
            return new CodeBlock(codeLang, ExtractCodeContent(p), Mergeable: true);

        // Display math: a centered paragraph whose ONLY content is an OMML equation (C8).
        var omml = p.GetFirstChild<M.OfficeMath>();
        var isCentered = pPr?.GetFirstChild<W.Justification>()?.Val?.Value == W.JustificationValues.Center;
        if (omml is not null && !hasAnyText && isCentered)
            return new DisplayMathBlock(OmmlToLatex.Convert(omml));

        // Blockquote: a "Quote" style, or an indented italic paragraph (C2).
        if (IsBlockquote(p, pPr, styleId, out var quoteText))
            return new BlockquoteBlock(quoteText);

        // List item (with nesting via w:ilvl) (C5).
        var numPr = pPr?.GetFirstChild<W.NumberingProperties>();
        if (numPr is not null)
        {
            var numId = numPr.GetFirstChild<W.NumberingId>()?.Val?.Value ?? 0;
            // w:ilvl (nesting level), read generically — 0-based, drives 2-space indentation (C5).
            int ilvl = 0;
            var ilvlVal = numPr.Elements().FirstOrDefault(e => e.LocalName == "ilvl")?.GetAttribute("val", WNamespace).Value;
            int.TryParse(ilvlVal, out ilvl);

            // Task list: the forward engine emits the checkbox as a w14:checkbox sdt whose first run
            // is a ballot glyph — ☒ (U+2612) checked / ☐ (U+2610) unchecked. Detect it in the text.
            var textParts = p.Descendants<W.Text>().Select(t => t.Text).ToList();
            bool isTask = false, isChecked = false;
            if (textParts.Count > 0 && textParts[0].Length > 0 &&
                (textParts[0][0] == '\u2612' || textParts[0][0] == '\u2610'))
            {
                isTask = true;
                isChecked = textParts[0][0] == '\u2612';
                textParts[0] = textParts[0].Substring(1);
            }
            var text = string.Concat(textParts).TrimStart();
            return new ListItemBlock(numId, isTask, isChecked, text, ilvl);
        }

        // Plain paragraph (may contain inline math / links / images / formatting).
        var inline = ConvertInlines(p);
        if (string.IsNullOrWhiteSpace(inline)) return null; // drop empty spacer paragraphs
        return new ParagraphBlock(inline);
    }

    // ---- drawings (diagrams & pictures) ------------------------------------------------------

    private Block? ConvertDrawing(W.Drawing drawing)
    {
        // 1. Tagged / geometry Mermaid recovery (Marksmith ShapeForge + structured foreign shapes).
        if (DocxShapeParser.TryParseMermaid(drawing) is { } mermaid)
            return new MermaidBlock(mermaid);

        // 2. Picture (pic:pic / a:blip) → image reference resolved through the extraction map.
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        var relId = blip?.Embed?.Value;
        if (!string.IsNullOrEmpty(relId) && _imageMap.TryGetValue(relId, out var img))
            return new ImageBlock(img.Alt, img.RelativePath);

        // 3. Shape group / SmartArt with no Mermaid recovery → raster fallback (Part D). Never drop.
        bool hasShapes = drawing.Descendants().Any(e => e.LocalName == "wsp");
        bool isSmartArt = drawing.Descendants().Any(e =>
            e.LocalName == "relIds" ||
            (e.LocalName == "graphicData" && (e.GetAttribute("uri", "").Value ?? "").Contains("diagram", StringComparison.Ordinal)));
        if (hasShapes || isSmartArt)
        {
            if (_mediaDir is not null)
            {
                var path = DocxDrawingRasterizer.TryRasterize(drawing, _mediaDir, ref _diagramCounter);
                if (path is not null)
                {
                    _rasterizedPaths.Add(path);
                    return new ImageBlock("Diagram", path);
                }
            }
            var shapeCount = drawing.Descendants().Count(e => e.LocalName == "wsp");
            return new RawBlock($"<!-- Unconvertible diagram: {shapeCount} shapes -->");
        }

        return null;
    }

    // ---- heading detection (C1) --------------------------------------------------------------

    private int DetectHeadingLevel(W.Paragraph p, W.ParagraphProperties? pPr, string styleId)
    {
        // 1. Style id/name: Heading1..6 (any casing, with/without space), Title -> H1, Subtitle -> H2.
        var normalized = styleId.Replace(" ", "").ToLowerInvariant();
        if (normalized == "title") return 1;
        if (normalized == "subtitle") return 2;
        if (normalized.StartsWith("heading", StringComparison.Ordinal))
        {
            var rest = normalized.Substring("heading".Length);
            if (int.TryParse(rest, out var lvl) && lvl is >= 1 and <= 6) return lvl;
        }

        // 2. w:outlineLvl — Word's semantic heading level (0-based).
        var outline = pPr?.GetFirstChild<W.OutlineLevel>()?.Val?.Value;
        if (outline is not null)
        {
            var lvl = outline.Value + 1;
            if (lvl is >= 1 and <= 6) return lvl;
        }

        // 3. Font-size heuristic: a SHORT, fully-bold, large paragraph is almost certainly a heading.
        var text = string.Concat(p.Descendants<W.Text>().Select(t => t.Text));
        if (text.Length is > 0 and < 200)
        {
            var runs = p.Descendants<W.Run>().Where(r => r.Elements<W.Text>().Any()).ToList();
            bool allBold = runs.Count > 0 && runs.All(r =>
                r.GetFirstChild<W.RunProperties>()?.GetFirstChild<W.Bold>() is not null);
            if (allBold)
            {
                double maxPt = runs.Max(RunFontSizePt);
                if (maxPt >= 28) return 1;
                if (maxPt >= 24) return 2;
                if (maxPt >= 20) return 3;
            }
        }

        return 0;
    }

    // w:sz is expressed in half-points.
    private static double RunFontSizePt(W.Run r)
    {
        var sz = r.GetFirstChild<W.RunProperties>()?.GetFirstChild<W.FontSize>()?.Val?.Value;
        return double.TryParse(sz, out var halfPts) ? halfPts / 2.0 : 0;
    }

    // ---- blockquote detection (C2) -----------------------------------------------------------

    private bool IsBlockquote(W.Paragraph p, W.ParagraphProperties? pPr, string styleId, out string text)
    {
        text = "";
        bool quoteStyle = styleId.ToLowerInvariant().Contains("quote", StringComparison.Ordinal);

        bool indentItalic = false;
        var leftIndent = pPr?.GetFirstChild<W.Indentation>()?.Left?.Value;
        if (int.TryParse(leftIndent, out var twips) && twips >= 720)
        {
            var runs = p.Descendants<W.Run>().Where(r => r.Elements<W.Text>().Any()).ToList();
            indentItalic = runs.Count > 0 && runs.All(r =>
                r.GetFirstChild<W.RunProperties>()?.GetFirstChild<W.Italic>() is not null);
        }

        if (!quoteStyle && !indentItalic) return false;
        text = ConvertInlines(p).Trim();
        return text.Length > 0;
    }

    // ---- generalized code detection (C3) -----------------------------------------------------

    private static bool IsGeneralizedCodeParagraph(W.Paragraph p, W.ParagraphProperties? pPr, string styleId, out string lang)
    {
        lang = "";
        var runs = p.Elements<W.Run>().Where(r => r.Elements<W.Text>().Any()).ToList();
        if (runs.Count == 0) return false;

        bool allMono = runs.All(r =>
        {
            var font = r.GetFirstChild<W.RunProperties>()?.GetFirstChild<W.RunFonts>()?.Ascii?.Value;
            return font is not null && MonoFonts.Contains(font);
        });
        if (!allMono) return false;

        bool hasShading = pPr?.GetFirstChild<W.Shading>() is not null;
        var styleLower = styleId.ToLowerInvariant();
        bool codeStyle = styleLower.Contains("code", StringComparison.Ordinal) ||
                         styleLower.Contains("html", StringComparison.Ordinal) ||
                         styleLower.Contains("plaintext", StringComparison.Ordinal);
        if (!hasShading && !codeStyle) return false;

        // Best-effort language from a style suffix like "Code_cs" / "HTML_py".
        var us = styleId.LastIndexOf('_');
        if (codeStyle && us > 0 && us < styleId.Length - 1)
            lang = styleId.Substring(us + 1);
        return true;
    }

    // ---- inline content ----------------------------------------------------------------------

    private enum InlineNodeKind
    {
        Plain,
        Insertion,
        Deletion,
        CommentRangeStart,
        CommentRangeEnd,
        CommentReference
    }

    private sealed class InlineSegment
    {
        public InlineNodeKind Kind { get; init; }
        public string Text { get; init; } = "";
        public string? CommentId { get; init; }
        public bool IsHighlighted { get; init; }
    }

    private List<InlineSegment> ExtractInlineSegments(W.Paragraph p)
    {
        var list = new List<InlineSegment>();
        foreach (var child in p.ChildElements)
        {
            switch (child)
            {
                case W.CommentRangeStart crs:
                    if (!string.IsNullOrEmpty(crs.Id?.Value))
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.CommentRangeStart,
                            CommentId = crs.Id.Value
                        });
                    }
                    break;

                case W.CommentRangeEnd cre:
                    if (!string.IsNullOrEmpty(cre.Id?.Value))
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.CommentRangeEnd,
                            CommentId = cre.Id.Value
                        });
                    }
                    break;

                case W.InsertedRun ins:
                    var insText = ConvertInsertedRun(ins);
                    if (!string.IsNullOrEmpty(insText))
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.Insertion,
                            Text = insText
                        });
                    }
                    break;

                case W.DeletedRun del:
                    var delText = ConvertDeletedRun(del);
                    if (!string.IsNullOrEmpty(delText))
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.Deletion,
                            Text = delText
                        });
                    }
                    break;

                case W.Run r:
                    var cr = r.GetFirstChild<W.CommentReference>();
                    if (cr?.Id?.Value is { Length: > 0 } crId)
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.CommentReference,
                            CommentId = crId
                        });
                    }

                    var runText = ConvertRun(r);
                    if (!string.IsNullOrEmpty(runText))
                    {
                        bool isHl = r.GetFirstChild<W.RunProperties>()?.GetFirstChild<W.Highlight>() is not null;
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.Plain,
                            Text = runText,
                            IsHighlighted = isHl
                        });
                    }
                    break;

                case M.OfficeMath om:
                    list.Add(new InlineSegment
                    {
                        Kind = InlineNodeKind.Plain,
                        Text = "$" + OmmlToLatex.Convert(om) + "$"
                    });
                    break;

                case W.Hyperlink h:
                    list.Add(new InlineSegment
                    {
                        Kind = InlineNodeKind.Plain,
                        Text = ConvertHyperlink(h)
                    });
                    break;

                case W.SdtBlock sdt:
                    list.Add(new InlineSegment
                    {
                        Kind = InlineNodeKind.Plain,
                        Text = GetSdtText(sdt)
                    });
                    break;

                case W.SdtRun sdtRun:
                    var sdtText = string.Concat(sdtRun.Descendants<W.Text>().Select(t => t.Text));
                    if (!string.IsNullOrEmpty(sdtText))
                    {
                        list.Add(new InlineSegment
                        {
                            Kind = InlineNodeKind.Plain,
                            Text = sdtText
                        });
                    }
                    break;
            }
        }
        return list;
    }

    private string ConvertInsertedRun(W.InsertedRun ins)
    {
        var sb = new StringBuilder();
        foreach (var r in ins.Elements<W.Run>())
        {
            var text = string.Concat(r.Elements<W.Text>().Select(t => t.Text));
            if (text.Length == 0) continue;

            var rPr = r.GetFirstChild<W.RunProperties>();
            bool bold = rPr?.GetFirstChild<W.Bold>() is not null;
            bool italic = rPr?.GetFirstChild<W.Italic>() is not null;
            bool isCode = rPr?.GetFirstChild<W.RunFonts>()?.Ascii?.Value == "Consolas";

            var s = text;
            if (isCode) s = "`" + s + "`";
            else if (bold && italic) s = "***" + s + "***";
            else if (bold) s = "**" + s + "**";
            else if (italic) s = "*" + s + "*";
            sb.Append(s);
        }
        return sb.ToString();
    }

    private string ConvertDeletedRun(W.DeletedRun del)
    {
        var sb = new StringBuilder();
        foreach (var r in del.Elements<W.Run>())
        {
            var delText = string.Concat(r.Elements<W.DeletedText>().Select(t => t.Text));
            if (string.IsNullOrEmpty(delText))
            {
                delText = string.Concat(r.Elements<W.Text>().Select(t => t.Text));
            }
            if (delText.Length == 0) continue;

            var rPr = r.GetFirstChild<W.RunProperties>();
            bool bold = rPr?.GetFirstChild<W.Bold>() is not null;
            bool italic = rPr?.GetFirstChild<W.Italic>() is not null;
            bool isCode = rPr?.GetFirstChild<W.RunFonts>()?.Ascii?.Value == "Consolas";

            var s = delText;
            if (isCode) s = "`" + s + "`";
            else if (bold && italic) s = "***" + s + "***";
            else if (bold) s = "**" + s + "**";
            else if (italic) s = "*" + s + "*";
            sb.Append(s);
        }
        return sb.ToString();
    }

    private string ConvertInlines(W.Paragraph p)
    {
        var segments = ExtractInlineSegments(p);
        if (segments.Count == 0) return "";

        var sb = new StringBuilder();
        var activeCommentRanges = new Dictionary<string, int>(StringComparer.Ordinal);
        var handledComments = new HashSet<string>(StringComparer.Ordinal);

        int i = 0;
        while (i < segments.Count)
        {
            var seg = segments[i];

            if (seg.Kind == InlineNodeKind.CommentRangeStart && seg.CommentId is not null)
            {
                activeCommentRanges[seg.CommentId] = sb.Length;
                i++;
                continue;
            }

            if (seg.Kind == InlineNodeKind.CommentRangeEnd && seg.CommentId is not null)
            {
                HandleCommentAttachment(seg.CommentId, sb, activeCommentRanges, handledComments);
                i++;
                continue;
            }

            if (seg.Kind == InlineNodeKind.CommentReference && seg.CommentId is not null)
            {
                HandleCommentAttachment(seg.CommentId, sb, activeCommentRanges, handledComments);
                i++;
                continue;
            }

            // Check for coalesced substitution: Deletion immediately followed by Insertion
            if (seg.Kind == InlineNodeKind.Deletion)
            {
                int nextIdx = i + 1;
                while (nextIdx < segments.Count &&
                       (segments[nextIdx].Kind == InlineNodeKind.CommentRangeStart ||
                        segments[nextIdx].Kind == InlineNodeKind.CommentRangeEnd ||
                        segments[nextIdx].Kind == InlineNodeKind.CommentReference))
                {
                    nextIdx++;
                }

                if (_options.CoalesceSubstitutions &&
                    _options.PreserveRevisionsAsCriticMarkup &&
                    nextIdx < segments.Count &&
                    segments[nextIdx].Kind == InlineNodeKind.Insertion)
                {
                    var delText = seg.Text;
                    var insText = segments[nextIdx].Text;
                    sb.Append("{~~").Append(delText).Append("~>").Append(insText).Append("~~}");

                    for (int k = i + 1; k < nextIdx; k++)
                    {
                        var inter = segments[k];
                        if (inter.Kind == InlineNodeKind.CommentRangeStart && inter.CommentId is not null)
                            activeCommentRanges[inter.CommentId] = sb.Length;
                        else if ((inter.Kind == InlineNodeKind.CommentRangeEnd || inter.Kind == InlineNodeKind.CommentReference) && inter.CommentId is not null)
                            HandleCommentAttachment(inter.CommentId, sb, activeCommentRanges, handledComments);
                    }
                    i = nextIdx + 1;
                    continue;
                }
                else
                {
                    if (_options.PreserveRevisionsAsCriticMarkup)
                    {
                        sb.Append("{--").Append(seg.Text).Append("--}");
                    }
                    i++;
                    continue;
                }
            }

            if (seg.Kind == InlineNodeKind.Insertion)
            {
                if (_options.PreserveRevisionsAsCriticMarkup)
                {
                    sb.Append("{++").Append(seg.Text).Append("++}");
                }
                else
                {
                    sb.Append(seg.Text);
                }
                i++;
                continue;
            }

            // Plain segment
            sb.Append(seg.Text);
            i++;
        }

        foreach (var (cId, _) in activeCommentRanges.ToList())
        {
            HandleCommentAttachment(cId, sb, activeCommentRanges, handledComments);
        }

        return sb.ToString();
    }

    private void HandleCommentAttachment(
        string commentId,
        StringBuilder sb,
        Dictionary<string, int> activeCommentRanges,
        HashSet<string> handledComments)
    {
        if (handledComments.Contains(commentId)) return;
        handledComments.Add(commentId);

        if (!_options.PreserveCommentsAsCriticMarkup)
        {
            activeCommentRanges.Remove(commentId);
            return;
        }

        if (!_comments.TryGetValue(commentId, out var commentInfo))
        {
            activeCommentRanges.Remove(commentId);
            return;
        }

        if (activeCommentRanges.TryGetValue(commentId, out int startPos))
        {
            activeCommentRanges.Remove(commentId);
            int len = sb.Length - startPos;
            if (len > 0)
            {
                var enclosed = sb.ToString(startPos, len);
                if (enclosed.StartsWith("==") && enclosed.EndsWith("==") && enclosed.Length >= 4)
                {
                    var inner = enclosed[2..^2];
                    var formatted = "{==" + inner + "==}" + FormatCriticComment(commentInfo);
                    sb.Remove(startPos, len);
                    sb.Insert(startPos, formatted);
                }
                else
                {
                    var formatted = enclosed + FormatReviewerComment(commentInfo);
                    sb.Remove(startPos, len);
                    sb.Insert(startPos, formatted);
                }
                return;
            }
        }

        sb.Append(FormatReviewerComment(commentInfo));
    }

    private static string FormatCriticComment(DocxCommentInfo c)
    {
        if (!string.IsNullOrWhiteSpace(c.Author) && c.Author != "Reviewer")
        {
            if (c.Date.HasValue)
                return $"{{>>{c.Author} ({c.Date.Value:yyyy-MM-dd}): {c.Text}<<}}";
            return $"{{>>{c.Author}: {c.Text}<<}}";
        }
        return $"{{>>{c.Text}<<}}";
    }

    private static string FormatReviewerComment(DocxCommentInfo c)
    {
        var author = !string.IsNullOrWhiteSpace(c.Author) ? c.Author : "Reviewer";
        if (c.Date.HasValue)
            return $"^[{author} ({c.Date.Value:yyyy-MM-dd}): \"{c.Text}\"]";
        return $"^[{author}: \"{c.Text}\"]";
    }

    private static string GetSdtText(W.SdtBlock sdt) =>
        string.Concat(sdt.Descendants<W.Text>().Select(t => t.Text));

    private string ConvertRun(W.Run r)
    {
        // Inline picture within a text run → inline image reference.
        var drawing = r.GetFirstChild<W.Drawing>();
        if (drawing is not null)
        {
            var relId = drawing.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value;
            if (!string.IsNullOrEmpty(relId) && _imageMap.TryGetValue(relId, out var img))
                return "![" + img.Alt + "](" + img.RelativePath + ")";
        }

        // Footnote reference → [^N] (definition appended at assembly) (C7).
        var fnRef = r.GetFirstChild<W.FootnoteReference>();
        if (fnRef is not null)
        {
            var id = fnRef.Id?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (id.Length > 0) _usedFootnotes.Add(id);
            return "[^" + id + "]";
        }

        var rPr = r.GetFirstChild<W.RunProperties>();
        var text = string.Concat(r.Elements<W.Text>().Select(t => t.Text));
        if (text.Length == 0) return "";

        bool bold = rPr?.GetFirstChild<W.Bold>() is not null;
        bool italic = rPr?.GetFirstChild<W.Italic>() is not null;
        bool strike = rPr?.GetFirstChild<W.Strike>() is not null;
        bool highlight = rPr?.GetFirstChild<W.Highlight>() is not null;
        bool isCode = rPr?.GetFirstChild<W.RunFonts>()?.Ascii?.Value == "Consolas";
        // Underline (C7): Markdown has no native underline, so emit HTML. A present <w:u val="none"/>
        // means "explicitly no underline" and must not wrap the text.
        var underlineVal = rPr?.GetFirstChild<W.Underline>()?.Val?.Value;
        bool underline = underlineVal is not null && underlineVal != W.UnderlineValues.None;
        // OpenXml 3.x models these as structs, not C# enums — compare the values, never ToString().
        var vertAlign = rPr?.GetFirstChild<W.VerticalTextAlignment>()?.Val?.Value;

        if (isCode) return "`" + text + "`";

        var s = text;
        if (bold && italic) s = "***" + s + "***";
        else if (bold) s = "**" + s + "**";
        else if (italic) s = "*" + s + "*";
        if (underline) s = "<u>" + s + "</u>";
        if (strike) s = "~~" + s + "~~";
        if (highlight) s = "==" + s + "==";
        if (vertAlign == W.VerticalPositionValues.Subscript) s = "~" + s + "~";
        else if (vertAlign == W.VerticalPositionValues.Superscript) s = "^" + s + "^";
        return s;
    }

    private string ConvertHyperlink(W.Hyperlink h)
    {
        var text = string.Concat(h.Descendants<W.Text>().Select(t => t.Text));
        var anchor = h.Anchor?.Value;
        if (!string.IsNullOrEmpty(anchor))
        {
            var slug = anchor.StartsWith("H_", StringComparison.Ordinal)
                ? anchor.Substring(2).Replace('_', '-')
                : anchor;
            return "[" + text + "](#" + slug + ")";
        }
        var relId = h.Id?.Value;
        if (!string.IsNullOrEmpty(relId) && _main is not null)
        {
            var rel = _main.HyperlinkRelationships.FirstOrDefault(x => x.Id == relId);
            if (rel is not null) return "[" + text + "](" + rel.Uri + ")";
        }
        return text;
    }

    // ---- code blocks -------------------------------------------------------------------------

    private static string ExtractCodeContent(W.Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var child in p.ChildElements)
        {
            if (child is not W.Run r) continue;
            foreach (var el in r.ChildElements)
            {
                if (el is W.Break) sb.Append('\n');
                else if (el is W.Text t) sb.Append(t.Text);
            }
        }
        return sb.ToString();
    }

    // ---- tables & alerts ---------------------------------------------------------------------

    private Block ConvertTable(W.Table t)
    {
        var rows = t.Elements<W.TableRow>().ToList();

        // GitHub alert callout: a single-cell table whose first paragraph is "{icon} {KIND}".
        if (rows.Count == 1)
        {
            var cells = rows[0].Elements<W.TableCell>().ToList();
            if (cells.Count == 1)
            {
                var paras = cells[0].Elements<W.Paragraph>().ToList();
                if (paras.Count >= 1)
                {
                    var title = string.Concat(paras[0].Descendants<W.Text>().Select(x => x.Text)).Trim();
                    var kind = AlertKindFromTitle(title);
                    if (kind is not null)
                    {
                        var sb = new StringBuilder("> [!" + kind + "]");
                        foreach (var cp in paras.Skip(1))
                        {
                            var line = string.Concat(cp.Descendants<W.Text>().Select(x => x.Text));
                            if (line.Length > 0) sb.Append("\n> ").Append(line);
                        }
                        return new AlertBlock(sb.ToString());
                    }
                }
            }
        }

        // Regular table (C6): honor gridSpan merges, detect a header row, pad short rows.
        var mdRows = new List<List<string>>();
        int colCount = 0;
        foreach (var row in rows)
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements<W.TableCell>())
            {
                var span = cell.GetFirstChild<W.TableCellProperties>()?.GetFirstChild<W.GridSpan>()?.Val?.Value ?? 1;
                var paras = cell.Elements<W.Paragraph>().Select(ConvertInlines).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                var cellText = paras.Count > 0 ? string.Join("<br>", paras).Trim() : string.Concat(cell.Descendants<W.Text>().Select(x => x.Text)).Trim();
                cells.Add(cellText);
                for (int s = 1; s < span; s++) cells.Add(""); // merged cell → empty filler columns
            }
            colCount = Math.Max(colCount, cells.Count);
            mdRows.Add(cells);
        }

        if (mdRows.Count == 0) return new TableBlock("");
        foreach (var c in mdRows)
            while (c.Count < colCount) c.Add("");

        // GFM requires a header row; use the first row (bold/shading detection only informs future
        // enhancements — the first row is the header regardless, matching the prior behavior).
        var lines = new List<string>
        {
            "| " + string.Join(" | ", mdRows[0]) + " |",
            "| " + string.Join(" | ", Enumerable.Repeat("---", colCount)) + " |",
        };
        foreach (var c in mdRows.Skip(1))
            lines.Add("| " + string.Join(" | ", c) + " |");
        return new TableBlock(string.Join("\n", lines));
    }

    private static string? AlertKindFromTitle(string title)
    {
        // Title is "{icon} {KIND}" (e.g. "💡 TIP"); the kind is the trailing word.
        var lastSpace = title.LastIndexOf(' ');
        var kind = lastSpace >= 0 ? title.Substring(lastSpace + 1) : title;
        return kind.ToUpperInvariant() switch
        {
            "NOTE" or "TIP" or "IMPORTANT" or "WARNING" or "CAUTION" => kind.ToUpperInvariant(),
            _ => null,
        };
    }
}
