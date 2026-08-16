using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkSmith.Models;

namespace MarkSmith.Services;

// Native Google Docs export builder (Task: "use their native stuff"). Parses Markdown with Markdig
// and emits a single Docs API batchUpdate request list that builds a REAL Google Doc:
//
//   * headings  -> named styles HEADING_1..6  (native outline / TOC)
//   * paragraphs -> native paragraphs with bold/italic/strike/code/link runs
//   * lists      -> createParagraphBullets (native bullets, nested by indentation)
//   * code blocks-> monospace paragraphs with background shading
//   * images     -> inserted as native inline images (uploaded to Drive by the service)
//   * tables     -> native tables (insertTable + per-cell text)
//   * blockquotes-> indented paragraphs;  HR -> insertHorizontalRule; page setup -> updateDocumentStyle
//
// Everything is append-only via endOfSegmentLocation, so the builder's running index is exact for
// every style range it emits — no index guessing. Images and tables are emitted as fixed text
// tokens ("[[IMG_N]]" / "[[TBL_N]]") that the service replaces with real inline images / tables in
// later phases (Google Docs API has no way to create shapes or equations, so diagrams are images).
public static class GoogleDocsDocumentBuilder
{
    private const string ImageTokenPrefix = "[[IMG_";
    private const string TableTokenPrefix = "[[TBL_";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record GoogleImage(int Order, string Source, string AltText);

    public sealed class GoogleTable
    {
        public int Order { get; init; }
        public List<List<string>> Rows { get; } = new();
    }

    public sealed class GoogleDocsBuildResult
    {
        public List<object> Requests { get; } = new();
        public List<GoogleImage> Images { get; } = new();
        public List<GoogleTable> Tables { get; } = new();
        public int FinalIndex { get; set; }
    }

    private sealed record Run(string Text, bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

    private sealed class BuilderState
    {
        public GoogleDocsBuildResult Result { get; } = new();
        public int Index = 1; // the body starts with one empty paragraph [0,1)
        public int ImageCount;   // unique token order ([[IMG_N]] across ALL images)
        public int MermaidCount; // pairing index into the harvested mermaid-PNG list
        public int TableCount;
    }

    // ---- public entry -------------------------------------------------------------------------

    public static GoogleDocsBuildResult Build(string markdown, AppSettings settings, ThemeDefinition? theme = null)
    {
        var st = new BuilderState();
        var doc = Markdown.Parse(markdown ?? "", Pipeline);

        foreach (var block in doc)
            EmitBlock(block, st, quoteDepth: 0, listDepth: 0);

        // Theme-aware page setup + base typography, applied first so per-range styles win.
        st.Result.Requests.Insert(0, PageSetup(settings, theme));
        if (st.Index > 1)
            st.Result.Requests.Insert(1, BaseBodyStyle(settings, theme, st.Index));
        st.Result.FinalIndex = st.Index;
        return st.Result;
    }

    // ---- block renderer -----------------------------------------------------------------------

    private static void EmitBlock(Block block, BuilderState st, int quoteDepth, int listDepth)
    {
        switch (block)
        {
            case HeadingBlock h:
                InsertParagraph(st, Runs(h.Inline, st), namedStyle: $"HEADING_{Math.Clamp(h.Level, 1, 6)}", indentPt: quoteDepth * 36);
                break;

            case ParagraphBlock p:
                InsertParagraph(st, Runs(p.Inline, st), indentPt: quoteDepth * 36 + listDepth * 36);
                break;

            case QuoteBlock q:
                foreach (var child in q) EmitBlock(child, st, quoteDepth + 1, listDepth);
                break;

            case ListBlock l:
            {
                var preset = l.IsOrdered ? "NUMBERED_DECIMAL_ALPHA_ROMAN" : "BULLET_DISC_CIRCLE_SQUARE";
                foreach (var item in l.OfType<ListItemBlock>())
                {
                    // Direct paragraphs of the item become bullet paragraphs; nested blocks recurse.
                    foreach (var child in item)
                    {
                        if (child is ParagraphBlock)
                            InsertParagraph(st, Runs(((ParagraphBlock)child).Inline, st), indentPt: (listDepth + 1) * 36, bulletPreset: preset);
                        else
                            EmitBlock(child, st, quoteDepth, listDepth + 1);
                    }
                }
                break;
            }

            case FencedCodeBlock f when f.Info?.TrimStart('`').StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true:
            {
                var tokenOrder = st.ImageCount++;
                var mermaidIndex = st.MermaidCount++;
                AddImageToken(st, new GoogleImage(tokenOrder, $"mermaid:{mermaidIndex}", "Diagram"));
                break;
            }

            case FencedCodeBlock f:
                EmitCodeLines(st, f.Lines.ToString(), quoteDepth, listDepth);
                break;

            case CodeBlock c:
                EmitCodeLines(st, c.Lines.ToString(), quoteDepth, listDepth);
                break;

            case Table t:
            {
                var table = new GoogleTable { Order = st.TableCount };
                foreach (var rowObj in t)
                {
                    if (rowObj is not TableRow row) continue;
                    var cells = new List<string>();
                    foreach (var cellObj in row)
                    {
                        if (cellObj is not TableCell cell) continue;
                        var text = string.Concat(cell.OfType<ParagraphBlock>()
                            .SelectMany(pb => Runs(pb.Inline, st)).Select(r => r.Text));
                        cells.Add(text.Trim());
                    }
                    if (cells.Count > 0) table.Rows.Add(cells);
                }
                st.Result.Tables.Add(table);
                AddTableToken(st, table.Order);
                break;
            }

            case ThematicBreakBlock:
                st.Result.Requests.Add(new { insertHorizontalRule = new { location = EndOfSegment } });
                break;

            case Markdig.Extensions.DefinitionLists.DefinitionList dl:
                // "Term\n: definition" — Docs has no native <dl>; the term becomes a bold
                // paragraph and each definition an indented one (same shape as DOCX and HTML).
                foreach (var item in dl.OfType<Markdig.Extensions.DefinitionLists.DefinitionItem>())
                {
                    foreach (var child in item)
                    {
                        if (child is Markdig.Extensions.DefinitionLists.DefinitionTerm term)
                            InsertParagraph(st, Runs(term.Inline, st).Select(r => r with { Bold = true }).ToList(),
                                indentPt: quoteDepth * 36);
                        else if (child is ParagraphBlock def) // the ": definition" bodies arrive as paragraphs
                            InsertParagraph(st, Runs(def.Inline, st), indentPt: quoteDepth * 36 + 36);
                    }
                }
                break;

            default:
                // HtmlBlock, YamlFrontMatter, ListItemBlock handled above — ignore the rest.
                break;
        }
    }

    private static void EmitCodeLines(BuilderState st, string code, int quoteDepth, int listDepth)
    {
        var lines = code.Replace("\r", "").Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                st.Result.Requests.Add(InsertText("\n"));
                st.Index += 1;
                continue;
            }
            InsertParagraph(st, new List<Run> { new(line, Code: true) },
                indentPt: quoteDepth * 36 + listDepth * 36, codeBlock: true);
        }
    }

    // ---- paragraph insertion (append-only; tracked index is exact) ------------------------------

    private static void InsertParagraph(BuilderState st, List<Run> runs, string? namedStyle = null, int indentPt = 0, bool codeBlock = false, string? bulletPreset = null)
    {
        var text = string.Concat(runs.Select(r => r.Text));
        if (text.Length == 0) return;

        var start = st.Index;
        st.Result.Requests.Add(InsertText(text + "\n"));
        st.Index += text.Length + 1;
        var end = start + text.Length + 1;

        // Inline run styling.
        int off = 0;
        foreach (var r in runs)
        {
            var len = r.Text.Length;
            if (len == 0) continue;
            var rs = start + off;
            if (r.Code)
                st.Result.Requests.Add(UpdateTextStyle(rs, rs + len, code: true));
            else if (r.Bold || r.Italic || r.Strike || r.Link is not null)
                st.Result.Requests.Add(UpdateTextStyle(rs, rs + len, bold: r.Bold, italic: r.Italic, strike: r.Strike, link: r.Link));
            off += len;
        }

        if (namedStyle is not null || indentPt > 0 || codeBlock)
            st.Result.Requests.Add(UpdateParagraphStyle(start, end, namedStyle, indentPt, codeBlock));
        if (bulletPreset is not null)
            st.Result.Requests.Add(CreateParagraphBullets(start, end, bulletPreset));
    }

    private static void AddImageToken(BuilderState st, GoogleImage img)
    {
        st.Result.Images.Add(img);
        var token = $"{ImageTokenPrefix}{img.Order}]]";
        st.Result.Requests.Add(InsertText(token));
        st.Index += token.Length;
    }

    private static void AddTableToken(BuilderState st, int order)
    {
        var token = $"{TableTokenPrefix}{order}]]";
        st.Result.Requests.Add(InsertText(token + "\n"));
        st.Index += token.Length + 1;
    }

    // ---- inline runs --------------------------------------------------------------------------

    private static List<Run> Runs(IEnumerable<MarkdownObject>? inlines, BuilderState st)
    {
        var runs = new List<Run>();
        if (inlines is null) return runs;
        foreach (var inline in inlines) EmitInline(inline, runs, new Style(), st);
        return runs;
    }

    private static void EmitInline(MarkdownObject inline, List<Run> runs, Style ctx, BuilderState st)
    {
        switch (inline)
        {
            case LiteralInline lit:
                AddRun(runs, lit.Content.ToString(), ctx);
                break;
            case CodeInline code:
                AddRun(runs, code.Content, ctx with { Code = true });
                break;
            case EmphasisInline em:
            {
                var next = em.DelimiterChar == '~'
                    ? ctx with { Strike = true }
                    : em.DelimiterCount >= 2 ? ctx with { Bold = true } : ctx with { Italic = true };
                foreach (var c in em) EmitInline(c, runs, next, st);
                break;
            }
            case LinkInline link:
            {
                if (link.IsImage)
                {
                    // Native inline image: register it and drop a token into the text stream that
                    // the service replaces with a real image after the text batch is applied.
                    var img = new GoogleImage(st.ImageCount, link.Url ?? "", link.FirstChild?.ToString() ?? "");
                    st.Result.Images.Add(img);
                    st.ImageCount++;
                    AddRun(runs, $"[[IMG_{img.Order}]]", ctx);
                    return;
                }
                var next = ctx with { Link = link.Url };
                if (link.IsAutoLink) AddRun(runs, link.Url, next);
                else foreach (var c in link) EmitInline(c, runs, next, st);
                break;
            }
            case AutolinkInline auto:
                AddRun(runs, auto.Url, ctx with { Link = auto.Url });
                break;
            case LineBreakInline:
                AddRun(runs, "\n", ctx);
                break;
            case Markdig.Extensions.Mathematics.MathInline math:
                AddRun(runs, "$" + math.Content + "$", ctx with { Code = true });
                break;
            case HtmlInline:
                break; // raw HTML has no native Docs equivalent
            default:
                if (inline is ContainerInline container)
                    foreach (var c in container) EmitInline(c, runs, ctx, st);
                break;
        }
    }

    private static void AddRun(List<Run> runs, string text, Style ctx) =>
        runs.Add(new Run(text, ctx.Bold, ctx.Italic, ctx.Strike, ctx.Code, ctx.Link));

    private sealed record Style(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

    // ---- Docs API request shapes ---------------------------------------------------------------

    private static object EndOfSegment => new { location = new { endOfSegmentLocation = new { } } };

    private static object InsertText(string text) =>
        new { insertText = new { location = new { endOfSegmentLocation = new { } }, text } };

    private static object UpdateParagraphStyle(int start, int end, string? namedStyle, int indentPt, bool codeBlock) =>
        new
        {
            updateParagraphStyle = new
            {
                range = new { startIndex = start, endIndex = end },
                paragraphStyle = new
                {
                    namedStyleType = namedStyle,
                    indentStart = indentPt > 0 ? Dimension(indentPt) : null,
                    shading = codeBlock ? new { backgroundColor = new { color = new { rgbColor = Rgb("#f2f2f2") } } } : null,
                },
            },
        };

    private static object UpdateTextStyle(int start, int end, bool bold = false, bool italic = false, bool strike = false, bool code = false, string? link = null) =>
        new
        {
            updateTextStyle = new
            {
                range = new { startIndex = start, endIndex = end },
                textStyle = new
                {
                    bold = bold ? (bool?)true : null,
                    italic = italic ? (bool?)true : null,
                    strikethrough = strike ? (bool?)true : null,
                    fontFamily = code ? "Courier New" : null,
                    backgroundColor = code ? new { color = new { rgbColor = Rgb("#ececec") } } : null,
                    link = link is null ? null : new { url = link },
                },
            },
        };

    private static object CreateParagraphBullets(int start, int end, string preset) =>
        new { createParagraphBullets = new { range = new { startIndex = start, endIndex = end }, bulletPreset = preset } };

    private static object PageSetup(AppSettings settings, ThemeDefinition? theme)
    {
        var widthPt = settings.A4FixedWidth ? 595.3 : Math.Clamp(settings.ContentWidth, 400, 2400) * 0.75;
        return new
        {
            updateDocumentStyle = new
            {
                documentStyle = new
                {
                    pageSize = new { width = Dimension(widthPt), height = Dimension(841.9) },
                    marginTop = Dimension(36), marginBottom = Dimension(36),
                    marginLeft = Dimension(36), marginRight = Dimension(36),
                    background = theme is null ? null : new { color = new { rgbColor = Rgb(theme.Background) } },
                },
            },
        };
    }

    private static object BaseBodyStyle(AppSettings settings, ThemeDefinition? theme, int endIndex)
    {
        var font = settings.FontPreset switch
        {
            "Serif" => "Georgia",
            "Monospace" => "Courier New",
            "Dyslexic-friendly" => "Comic Sans MS",
            _ => "Arial", // System / Sans-Serif
        };
        return new
        {
            updateTextStyle = new
            {
                range = new { startIndex = 1, endIndex },
                textStyle = new
                {
                    fontFamily = font,
                    foregroundColor = theme is null ? null : new { color = new { rgbColor = Rgb(theme.Text) } },
                },
            },
        };
    }

    private static object Dimension(double pt) => new { magnitude = Math.Round(pt, 1), unit = "PT" };

    private static object Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => new string(c, 2)));
        if (h.Length != 6 || !int.TryParse(h.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !int.TryParse(h.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !int.TryParse(h.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new { red = 0.0, green = 0.0, blue = 0.0 };
        return new { red = r / 255.0, green = g / 255.0, blue = b / 255.0 };
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseMathematics().Build();
}
