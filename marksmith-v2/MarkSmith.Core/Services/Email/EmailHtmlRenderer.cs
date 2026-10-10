using System.Globalization;
using System.Net;
using System.Text;
using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.CustomContainers;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Footers;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkSmith.Models;
using SkiaSharp;

namespace MarkSmith.Services.Email;

public sealed class EmailRenderOptions
{
    /// <summary>Keep the document's opening H1 in the body. Off by default: it becomes the subject,
    /// and a message that repeats its subject as a giant heading reads like a newsletter.</summary>
    public bool RepeatTitleInBody { get; init; }

    /// <summary>Folder relative image paths resolve against (the document's folder).</summary>
    public string? BaseDirectory { get; init; }

    /// <summary>One PNG per <c>```mermaid</c> fence of the prepared Markdown, in document order
    /// (null where a diagram failed). Rendered by the desktop app's web host; without it each
    /// diagram's source is shown as code instead.</summary>
    public IReadOnlyList<byte[]?>? MermaidPngs { get; init; }

    /// <summary>Preview only: write each diagram without a PNG as a placeholder the preview page
    /// draws live with mermaid.js (the export harvests real PNGs instead). Never set for a file
    /// that leaves the app — the placeholder relies on script.</summary>
    public bool LiveMermaidPlaceholders { get; init; }
}

public sealed class EmailRenderResult
{
    public string Html { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Title { get; init; }
    public List<EmailInlineImage> Images { get; init; } = new();
    public List<string> Notes { get; init; } = new();
}

/// <summary>Markdown → email-safe HTML. Writes the markup itself from Markdig's syntax tree instead
/// of post-processing the preview HTML, because every rule here comes from what classic Outlook's
/// Word engine can show: layout is tables, every style is inline, pictures are CID PNGs, and
/// nothing relies on script, SVG, form controls, data: URIs, flexbox or grid. Output is
/// well-formed XHTML, so strict parsers (and the tests) can read it back.</summary>
public sealed class EmailHtmlRenderer
{
    public const int ContentWidth = 680;
    private const int MaxImageWidth = ContentWidth - 8;
    private const string FontStack = "Aptos, Calibri, 'Segoe UI', Helvetica, Arial, sans-serif";
    private const string MonoStack = "Consolas, 'Cascadia Mono', 'Courier New', monospace";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseCalloutTitles().UseMathematics()
        .UseEmojiAndSmiley(enableSmileys: false).UsePlainAbbreviations().Build();

    private static readonly MarkdownPipeline PipelineNoEmoji = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions().UseYamlFrontMatter().UseAlertBlocks().UseCalloutTitles().UseMathematics().UsePlainAbbreviations().Build();

    private static readonly Dictionary<string, (string Color, string Tint, string Label)> AlertStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["note"] = ("#0969da", "#eef5ff", "Note"),
        ["tip"] = ("#1a7f37", "#eefbf1", "Tip"),
        ["important"] = ("#8250df", "#f6f0ff", "Important"),
        ["warning"] = ("#9a6700", "#fff8e5", "Warning"),
        ["caution"] = ("#cf222e", "#ffeff0", "Caution"),
    };

    private readonly AppSettings _settings;
    private readonly ThemeDefinition _theme;
    private readonly EmailPalette _p;
    private readonly EmailRenderOptions _options;
    private readonly List<EmailInlineImage> _images = new();
    private readonly List<string> _notes = new();
    private readonly string _cidToken = Guid.NewGuid().ToString("N")[..10];
    private List<MarkdownHtmlService.EmailFigureFragment> _figures = new();
    private int _mermaidIndex;
    private int _missingDiagrams;
    private Block? _omittedTitle;

    public EmailHtmlRenderer(AppSettings settings, ThemeDefinition theme, EmailRenderOptions? options = null)
    {
        _settings = settings;
        _theme = theme;
        _p = EmailPalette.From(theme);
        _options = options ?? new EmailRenderOptions();
    }

    public EmailPalette Palette => _p;

    /// <summary>The Markdown the renderer actually draws (normalized, with the self-drawn blocks
    /// lifted out). Mermaid PNGs must be rendered from THIS text so their order matches.</summary>
    public static string Prepare(string markdown, AppSettings settings, ThemeDefinition theme) =>
        MarkdownHtmlService.PrepareForEmail(markdown, settings, theme, out _);

    /// <summary>The title the email's subject is made from (front matter or the first H1), the
    /// same one <see cref="Render"/> finds, so the Settings preview matches the real subject.</summary>
    public static string? TitleOf(string markdown, AppSettings settings, ThemeDefinition theme)
    {
        var prepared = MarkdownHtmlService.PrepareForEmail(markdown, settings, theme, out _);
        return FindTitle(Markdig.Markdown.Parse(prepared, Pipeline), out _);
    }

    private static readonly System.Text.RegularExpressions.Regex MermaidFence =
        new(@"^[ \t]*(```|~~~)[ \t]*mermaid\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Whether the document has a Mermaid diagram to draw: ```mermaid, ~~~mermaid or
    /// ``` mermaid, as the renderer and the diagram harvest both accept.</summary>
    public static bool HasMermaid(string? markdown) => markdown is not null && MermaidFence.IsMatch(markdown);

    public EmailRenderResult Render(string markdown)
    {
        var prepared = MarkdownHtmlService.PrepareForEmail(markdown, _settings, _theme, out _figures);
        var doc = Markdig.Markdown.Parse(prepared, _settings.NoEmoji ? PipelineNoEmoji : Pipeline);

        var title = FindTitle(doc, out var titleBlock);
        if (!_options.RepeatTitleInBody) _omittedTitle = titleBlock;

        var body = new StringBuilder();
        foreach (var block in doc) WriteBlock(body, block);

        if (_missingDiagrams > 0)
            _notes.Add(_missingDiagrams == 1
                ? "One diagram couldn't be drawn as a picture, so the email shows its source instead."
                : $"{_missingDiagrams} diagrams couldn't be drawn as pictures, so the email shows their source instead.");

        var bodyText = _settings.NoEmoji ? EmojiStripper.Strip(body.ToString()) : body.ToString();
        return new EmailRenderResult
        {
            Html = WrapDocument(bodyText, title),
            Text = EmailTextRenderer.Render(doc, _omittedTitle, _figures, _settings.MermaidEnabled),
            Title = title,
            Images = _images,
            Notes = _notes,
        };
    }

    // ── document frame ────────────────────────────────────────────────────────

    private string WrapDocument(string body, string? title)
    {
        var sb = new StringBuilder(body.Length + 1600);
        sb.Append("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">\n");
        sb.Append("<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">\n<head>\n");
        sb.Append("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\" />\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />\n");
        // "light" only: we draw a light message on purpose; clients that force dark mode invert it.
        sb.Append("<meta name=\"color-scheme\" content=\"light\" />\n<meta name=\"supported-color-schemes\" content=\"light\" />\n");
        sb.Append("<title>").Append(Enc(title ?? "")).Append("</title>\n");
        // Word engine: stop it from adding paragraph spacing around tables and scaling images.
        sb.Append("<!--[if mso]><xml><o:OfficeDocumentSettings><o:AllowPNG/><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml><![endif]-->\n");
        sb.Append("</head>\n");
        sb.Append($"<body style=\"margin:0;padding:0;background-color:{_p.Page};\">\n");
        // Outlook ignores max-width on a div, so it gets a fixed-width "ghost" table instead.
        sb.Append($"<!--[if mso]><table role=\"presentation\" width=\"{ContentWidth}\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr><td><![endif]-->\n");
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"max-width:{ContentWidth}px;border-collapse:collapse;\">\n<tr>\n");
        sb.Append($"<td style=\"padding:4px 2px;font-family:{FontStack};font-size:15px;line-height:1.55;color:{_p.Text};text-align:left;\">\n");
        sb.Append(body);
        sb.Append("</td>\n</tr>\n</table>\n<!--[if mso]></td></tr></table><![endif]-->\n</body>\n</html>\n");
        return sb.ToString();
    }

    internal static string? FindTitle(MarkdownDocument doc, out Block? leadingH1)
    {
        leadingH1 = null;
        string? frontTitle = null;
        foreach (var b in doc)
        {
            if (b is YamlFrontMatterBlock y)
            {
                foreach (var line in y.Lines.Lines)
                {
                    var s = line.ToString().Trim();
                    if (s.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                        frontTitle = s[6..].Trim().Trim('"', '\'').Trim();
                }
                continue;
            }
            if (b is LinkReferenceDefinitionGroup) continue;
            if (b is HeadingBlock { Level: 1 } h)
            {
                leadingH1 = h;
                var t = EmailTextRenderer.InlineText(h.Inline).Trim();
                return string.IsNullOrEmpty(frontTitle) ? (t.Length > 0 ? t : null) : frontTitle;
            }
            break;
        }
        if (!string.IsNullOrEmpty(frontTitle)) return frontTitle;
        var firstH1 = doc.Descendants<HeadingBlock>().FirstOrDefault(h => h.Level == 1);
        var text = firstH1 is null ? "" : EmailTextRenderer.InlineText(firstH1.Inline).Trim();
        return text.Length > 0 ? text : null;
    }

    // ── blocks ────────────────────────────────────────────────────────────────

    private void WriteBlocks(StringBuilder sb, ContainerBlock container)
    {
        foreach (var b in container) WriteBlock(sb, b);
    }

    private void WriteBlock(StringBuilder sb, Block block)
    {
        if (ReferenceEquals(block, _omittedTitle)) return;
        switch (block)
        {
            case YamlFrontMatterBlock:
            case LinkReferenceDefinitionGroup:
            case BlankLineBlock:
                return;
            case HeadingBlock h:
                WriteHeading(sb, h);
                return;
            case ParagraphBlock para:
                sb.Append($"<p style=\"margin:0 0 14px 0;\">");
                WriteInlines(sb, para.Inline);
                sb.Append("</p>\n");
                return;
            case MathBlock math:
                WriteMathBlock(sb, math);
                return;
            case FencedCodeBlock fenced when _settings.MermaidEnabled && IsMermaid(fenced):
                WriteMermaid(sb, fenced);
                return;
            case CodeBlock code:
                WriteCode(sb, code.Lines.ToString(), (code as FencedCodeBlock)?.Info ?? "");
                return;
            case AlertBlock alert:
                WriteAlert(sb, alert);
                return;
            case QuoteBlock quote:
                sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 14px 0;\"><tr>");
                sb.Append($"<td style=\"font-family:{FontStack};border-left:4px solid {_p.QuoteBar};padding:2px 0 2px 14px;color:{_p.Muted};\">\n");
                WriteBlocks(sb, quote);
                sb.Append("</td></tr></table>\n");
                return;
            case ListBlock list:
                WriteList(sb, list);
                return;
            case ThematicBreakBlock:
                sb.Append($"<hr style=\"border:0;border-top:1px solid {_p.Border};height:1px;margin:20px 0;\" />\n");
                return;
            case Table table:
                WriteTable(sb, table);
                return;
            case FootnoteGroup group:
                WriteFootnotes(sb, group);
                return;
            case HtmlBlock html:
                WriteHtmlBlock(sb, html);
                return;
            case DefinitionList dl:
                WriteDefinitionList(sb, dl);
                return;
            case Figure figure:
                WriteBlocks(sb, figure);
                return;
            case FigureCaption caption:
                sb.Append($"<p style=\"margin:-6px 0 14px 0;font-size:13px;color:{_p.Muted};\">");
                WriteInlines(sb, caption.Inline);
                sb.Append("</p>\n");
                return;
            case FooterBlock footer:
                sb.Append($"<div style=\"font-size:13px;color:{_p.Muted};\">\n");
                WriteBlocks(sb, footer);
                sb.Append("</div>\n");
                return;
            case CustomContainer custom:
                WriteCustomContainer(sb, custom);
                return;
            case LeafBlock leaf when leaf.Inline is not null:
                sb.Append("<p style=\"margin:0 0 14px 0;\">");
                WriteInlines(sb, leaf.Inline);
                sb.Append("</p>\n");
                return;
            case ContainerBlock other:
                WriteBlocks(sb, other);
                return;
        }
    }

    private void WriteHeading(StringBuilder sb, HeadingBlock h)
    {
        var (size, top) = h.Level switch
        {
            1 => (26, 6),
            2 => (21, 22),
            3 => (17, 18),
            4 => (15, 16),
            _ => (14, 14),
        };
        var id = h.GetAttributes().Id;
        var idAttr = string.IsNullOrEmpty(id) ? "" : $" id=\"{Enc(id)}\"";
        var colour = h.Level >= 5 ? _p.Muted : _p.Heading;
        var rule = h.Level <= 2 ? $"padding-bottom:4px;border-bottom:1px solid {_p.Border};" : "";
        sb.Append($"<h{h.Level}{idAttr} style=\"margin:{top}px 0 10px 0;{rule}font-family:{FontStack};font-size:{size}px;line-height:1.3;font-weight:600;color:{colour};\">");
        WriteInlines(sb, h.Inline);
        sb.Append($"</h{h.Level}>\n");
    }

    private static bool IsMermaid(FencedCodeBlock f) =>
        f.Info?.Trim().StartsWith("mermaid", StringComparison.OrdinalIgnoreCase) == true;

    private void WriteMermaid(StringBuilder sb, FencedCodeBlock fenced)
    {
        var index = _mermaidIndex++;
        var source = fenced.Lines.ToString();
        var png = _options.MermaidPngs is { } list && index < list.Count ? list[index] : null;
        var firstLine = source.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "diagram";
        if (png is { Length: > 0 } && AddImage(png, "image/png", $"diagram-{index + 1}.png", 2.0, out var img))
        {
            WriteFigureImage(sb, img, $"Diagram: {firstLine}");
            return;
        }
        if (_options.LiveMermaidPlaceholders)
        {
            sb.Append("<div data-ms-mermaid=\"1\" style=\"margin:0 0 16px 0;\"><pre style=\"display:none;\">")
              .Append(Enc(source)).Append("</pre></div>\n");
            return;
        }
        _missingDiagrams++;
        WriteCode(sb, source, "mermaid");
    }

    private void WriteCode(StringBuilder sb, string code, string info)
    {
        code = code.TrimEnd('\n', '\r');
        var lang = (info ?? "").Trim().Split(' ', '{').FirstOrDefault() ?? "";
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 16px 0;\"><tr>");
        sb.Append($"<td bgcolor=\"{_p.CodeBackground}\" style=\"background-color:{_p.CodeBackground};border:1px solid {_p.Border};padding:10px 14px;\">");
        sb.Append($"<pre style=\"margin:0;font-family:{MonoStack};font-size:13px;line-height:1.45;color:{_p.CodeText};white-space:pre-wrap;word-wrap:break-word;\">");
        foreach (var (text, hex, italic, bold) in OpenXmlSyntaxHighlighter.GetHighlightedSpans(code, lang, _p.CodeBackground))
        {
            if (hex is null) { sb.Append(Enc(text)); continue; }
            sb.Append($"<span style=\"color:{hex};{(italic ? "font-style:italic;" : "")}{(bold ? "font-weight:bold;" : "")}\">").Append(Enc(text)).Append("</span>");
        }
        sb.Append("</pre></td></tr></table>\n");
    }

    private void WriteMathBlock(StringBuilder sb, MathBlock math)
    {
        var tex = math.Lines.ToString().Trim();
        sb.Append($"<p style=\"margin:0 0 14px 0;text-align:center;font-family:'Cambria Math',Cambria,Georgia,serif;font-size:16px;\">")
          .Append(Enc(LatexText.ToReadable(tex))).Append("</p>\n");
    }

    private void WriteAlert(StringBuilder sb, AlertBlock alert)
    {
        var kind = alert.Kind.ToString();
        var (colour, tint, label) = AlertStyles.TryGetValue(kind, out var s) ? s : AlertStyles["note"];
        label = CalloutTitles.Get(alert) ?? label;
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 16px 0;\"><tr>");
        sb.Append($"<td bgcolor=\"{tint}\" style=\"font-family:{FontStack};background-color:{tint};border-left:4px solid {colour};padding:10px 14px;\">\n");
        sb.Append($"<p style=\"margin:0 0 6px 0;font-weight:600;color:{colour};\">{Enc(label)}</p>\n");
        var inner = new StringBuilder();
        WriteBlocks(inner, alert);
        sb.Append(TrimLastMargin(inner.ToString()));
        sb.Append("</td></tr></table>\n");
    }

    private void WriteCustomContainer(StringBuilder sb, CustomContainer custom)
    {
        // ::: note / ::: warning … that the normalizers didn't already turn into alerts.
        var kind = (custom.Info ?? "").Trim().ToLowerInvariant();
        if (AlertStyles.TryGetValue(kind, out var s))
        {
            sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 16px 0;\"><tr>");
            sb.Append($"<td bgcolor=\"{s.Tint}\" style=\"font-family:{FontStack};background-color:{s.Tint};border-left:4px solid {s.Color};padding:10px 14px;\">\n");
            sb.Append($"<p style=\"margin:0 0 6px 0;font-weight:600;color:{s.Color};\">{Enc(s.Label)}</p>\n");
            var inner = new StringBuilder();
            WriteBlocks(inner, custom);
            sb.Append(TrimLastMargin(inner.ToString()));
            sb.Append("</td></tr></table>\n");
            return;
        }
        sb.Append($"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 16px 0;\"><tr>");
        sb.Append($"<td style=\"font-family:{FontStack};border:1px solid {_p.Border};padding:10px 14px;\">\n");
        var body = new StringBuilder();
        WriteBlocks(body, custom);
        sb.Append(TrimLastMargin(body.ToString()));
        sb.Append("</td></tr></table>\n");
    }

    private static string TrimLastMargin(string html)
    {
        const string m = "margin:0 0 14px 0;";
        var i = html.LastIndexOf(m, StringComparison.Ordinal);
        return i < 0 ? html : html[..i] + "margin:0;" + html[(i + m.Length)..];
    }

    private void WriteList(StringBuilder sb, ListBlock list)
    {
        bool isTaskList = list.OfType<ListItemBlock>().Any(IsTaskItem);
        var tag = list.IsOrdered ? "ol" : "ul";
        var start = list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 1
            ? $" start=\"{n}\"" : "";
        var type = list.IsOrdered && list.BulletType is 'a' or 'A' or 'i' or 'I' ? $" type=\"{list.BulletType}\"" : "";
        var listStyle = isTaskList ? "list-style-type:none;padding-left:4px;" : "padding-left:26px;";
        sb.Append($"<{tag}{start}{type} style=\"margin:0 0 14px 0;{listStyle}\">\n");
        foreach (var item in list.OfType<ListItemBlock>())
        {
            sb.Append(isTaskList ? "<li style=\"margin:0 0 4px 0;list-style-type:none;\">" : "<li style=\"margin:0 0 4px 0;\">");
            // Tight list items hold their text in a paragraph; drawing it as <p> would add a gap
            // under every bullet, so the first paragraph is written inline.
            bool first = true;
            foreach (var child in item)
            {
                if (first && child is ParagraphBlock p)
                {
                    WriteInlines(sb, p.Inline);
                    first = false;
                    continue;
                }
                first = false;
                if (child is ParagraphBlock para)
                {
                    sb.Append("<p style=\"margin:6px 0 0 0;\">");
                    WriteInlines(sb, para.Inline);
                    sb.Append("</p>");
                    continue;
                }
                if (child is ListBlock nested)
                {
                    var inner = new StringBuilder();
                    WriteList(inner, nested);
                    sb.Append(inner.ToString().Replace("margin:0 0 14px 0;", "margin:4px 0 0 0;"));
                    continue;
                }
                WriteBlock(sb, child);
            }
            sb.Append("</li>\n");
        }
        sb.Append($"</{tag}>\n");
    }

    private static bool IsTaskItem(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline.FirstChild: { } first } && CheckboxState(first) is not null;

    /// <summary>True / false for a ticked / empty task box, null for anything else. The preview's
    /// DialectNormalizer has already turned "[x]" into an &lt;input type="checkbox"&gt; by the
    /// time the email parses the text, so both forms count.</summary>
    internal static bool? CheckboxState(Inline inline) => inline switch
    {
        TaskList t => t.Checked,
        HtmlInline h when System.Text.RegularExpressions.Regex.IsMatch(h.Tag, @"^<input\b[^>]*type\s*=\s*[""']?checkbox", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            => System.Text.RegularExpressions.Regex.IsMatch(h.Tag, @"\schecked\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
        _ => null,
    };

    private void WriteTable(StringBuilder sb, Table table)
    {
        sb.Append($"<table cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"border-collapse:collapse;margin:0 0 16px 0;font-size:14px;\">\n");
        foreach (var row in table.OfType<TableRow>())
        {
            sb.Append("<tr>");
            // Markdig only fills ColumnIndex for grid tables; pipe tables need the running position.
            var column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var index = cell.ColumnIndex >= 0 ? cell.ColumnIndex : column;
                column = index + Math.Max(1, cell.ColumnSpan);
                var align = index < table.ColumnDefinitions.Count
                    ? table.ColumnDefinitions[index].Alignment : null;
                var alignName = align switch
                {
                    TableColumnAlign.Center => "center",
                    TableColumnAlign.Right => "right",
                    _ => "left",
                };
                var tag = row.IsHeader ? "th" : "td";
                var fill = row.IsHeader ? $" bgcolor=\"{_p.HeaderFill}\"" : "";
                var fillStyle = row.IsHeader ? $"background-color:{_p.HeaderFill};font-weight:600;color:{_p.Heading};" : "";
                var span = cell.ColumnSpan > 1 ? $" colspan=\"{cell.ColumnSpan}\"" : "";
                sb.Append($"<{tag}{span} align=\"{alignName}\" valign=\"top\"{fill} style=\"font-family:{FontStack};color:{_p.Text};border:1px solid {_p.Border};padding:6px 10px;text-align:{alignName};vertical-align:top;{fillStyle}\">");
                var first = true;
                foreach (var child in cell)
                {
                    if (child is ParagraphBlock p)
                    {
                        if (!first) sb.Append("<br />");
                        WriteInlines(sb, p.Inline);
                    }
                    else
                    {
                        var inner = new StringBuilder();
                        WriteBlock(inner, child);
                        sb.Append(inner.ToString());
                    }
                    first = false;
                }
                sb.Append($"</{tag}>");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</table>\n");
    }

    private void WriteDefinitionList(StringBuilder sb, DefinitionList dl)
    {
        foreach (var item in dl.OfType<DefinitionItem>())
        {
            foreach (var child in item)
            {
                if (child is DefinitionTerm term)
                {
                    sb.Append($"<p style=\"margin:0 0 2px 0;font-weight:600;color:{_p.Heading};\">");
                    WriteInlines(sb, term.Inline);
                    sb.Append("</p>\n");
                }
                else
                {
                    sb.Append("<div style=\"margin:0 0 10px 0;padding-left:22px;\">");
                    var inner = new StringBuilder();
                    WriteBlock(inner, child);
                    sb.Append(TrimLastMargin(inner.ToString()));
                    sb.Append("</div>\n");
                }
            }
        }
    }

    private void WriteFootnotes(StringBuilder sb, FootnoteGroup group)
    {
        // A "---" right before the notes already drew the rule; two in a row look like a glitch.
        if (!sb.ToString().TrimEnd().EndsWith("margin:20px 0;\" />", StringComparison.Ordinal))
            sb.Append($"<hr style=\"border:0;border-top:1px solid {_p.Border};height:1px;margin:24px 0 10px 0;\" />\n");
        sb.Append($"<ol style=\"margin:0 0 14px 0;padding-left:22px;font-size:13px;color:{_p.Muted};\">\n");
        foreach (var fn in group.OfType<Footnote>().OrderBy(f => f.Order))
        {
            sb.Append($"<li id=\"fn-{fn.Order}\" style=\"margin:0 0 4px 0;\">");
            bool first = true;
            foreach (var child in fn)
            {
                if (child is ParagraphBlock p)
                {
                    if (!first) sb.Append("<br />");
                    WriteInlines(sb, p.Inline);
                }
                else
                {
                    WriteBlock(sb, child);
                }
                first = false;
            }
            sb.Append("</li>\n");
        }
        sb.Append("</ol>\n");
    }

    private void WriteHtmlBlock(StringBuilder sb, HtmlBlock html)
    {
        var raw = html.Lines.ToString().Trim();
        var fig = System.Text.RegularExpressions.Regex.Match(raw, @"^<!--MSFIG:(\d+)-->$");
        if (fig.Success)
        {
            var n = int.Parse(fig.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n < _figures.Count) WriteFigure(sb, _figures[n]);
            return;
        }
        var scrubbed = EmailHtmlScrubber.Scrub(raw);
        if (scrubbed.Length > 0) sb.Append(scrubbed).Append('\n');
    }

    private void WriteFigure(StringBuilder sb, MarkdownHtmlService.EmailFigureFragment figure)
    {
        if (figure.Metrics is { Count: > 0 } metrics) { WriteMetrics(sb, metrics); return; }
        if (figure.Png is { Length: > 0 } drawn)
        {
            if (AddImage(drawn, "image/png", $"{figure.Kind.ToLowerInvariant()}-{_images.Count + 1}.png", 2.0, out var pic))
                WriteFigureImage(sb, pic, figure.Alt ?? FigureAlt(figure));
            return;
        }
        var svg = EmailHtmlScrubber.FirstSvg(figure.Html);
        if (svg is not null && SvgRasterizer.ToPng(svg, 2.0) is { Length: > 0 } png
            && AddImage(png, "image/png", $"{figure.Kind.ToLowerInvariant()}-{_images.Count + 1}.png", 2.0, out var img))
        {
            WriteFigureImage(sb, img, FigureAlt(figure));
            return;
        }
        var flat = EmailHtmlScrubber.Scrub(figure.Html);
        if (flat.Length == 0) return;
        sb.Append($"<div style=\"margin:0 0 16px 0;\">").Append(flat).Append("</div>\n");
    }

    /// <summary>KPI cards: a table, since Outlook draws neither grid nor flex. Up to four to a row
    /// (three for five, six or nine, so no row ends with one lonely card); cellspacing, which Outlook
    /// honours where it ignores margins, keeps the gutters.</summary>
    private void WriteMetrics(StringBuilder sb, IReadOnlyList<(string Value, string Label)> items)
    {
        int cols = items.Count <= 4 ? items.Count : items.Count is 5 or 6 or 9 ? 3 : 4;
        int width = (int)Math.Floor(100.0 / cols);
        sb.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"8\" border=\"0\" style=\"margin:0 0 12px -8px;width:100%;\">\n");
        for (int r = 0; r < items.Count; r += cols)
        {
            sb.Append("<tr>\n");
            for (int c = 0; c < cols; c++)
            {
                if (r + c >= items.Count) { sb.Append($"<td width=\"{width}%\" style=\"width:{width}%;\"></td>\n"); continue; }
                var (value, label) = items[r + c];
                sb.Append($"<td width=\"{width}%\" valign=\"top\" bgcolor=\"{_p.CodeBackground}\" style=\"width:{width}%;background-color:{_p.CodeBackground};border:1px solid {_p.Border};border-top:3px solid {_p.Accent};padding:12px 14px;\">");
                sb.Append($"<div style=\"font-family:{FontStack};font-size:24px;line-height:1.2;font-weight:bold;color:{_p.Accent};\">").Append(Enc(value)).Append("</div>");
                if (label.Length > 0)
                    sb.Append($"<div style=\"font-family:{FontStack};font-size:13px;line-height:1.4;color:{_p.Muted};padding-top:4px;\">").Append(Enc(label)).Append("</div>");
                sb.Append("</td>\n");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</table>\n");
    }

    private static string FigureAlt(MarkdownHtmlService.EmailFigureFragment f) => f.Kind switch
    {
        "SMARTART" => "SmartArt diagram",
        "SHAPES" => "Shapes diagram",
        "CHART" => "Chart",
        "METRICS" => "Metrics",
        _ => "Diagram",
    };

    private void WriteFigureImage(StringBuilder sb, EmailInlineImage img, string alt)
    {
        sb.Append($"<p style=\"margin:0 0 16px 0;\"><img src=\"cid:{img.ContentId}\" width=\"{img.Width}\" height=\"{img.Height}\" alt=\"{Enc(alt)}\" style=\"display:block;border:0;width:{img.Width}px;max-width:100%;height:auto;\" /></p>\n");
    }

    // ── inlines ───────────────────────────────────────────────────────────────

    private int _inlineDepth;
    private List<HtmlInline> _blockHtml = new();

    private void WriteInlines(StringBuilder sb, ContainerInline? container)
    {
        if (container is null) return;
        // A block's inlines start here: note its HTML tags, so a <select> or <textarea> only
        // hides what follows when it's really closed in the same block. ("Use a <select>
        // element" in prose used to empty every paragraph after it, to the end of the email.)
        bool top = _inlineDepth++ == 0;
        if (top) _blockHtml = container.Descendants<HtmlInline>().ToList();
        try
        {
            foreach (var inline in container) WriteInline(sb, inline);
        }
        finally
        {
            if (--_inlineDepth == 0) _skipUntilTag = null;
        }
    }

    private bool ClosedLater(HtmlInline open, string tag)
    {
        int i = _blockHtml.IndexOf(open);
        return i >= 0 && _blockHtml.Skip(i + 1).Any(h => IsClosingTag(h.Tag, tag));
    }

    private void WriteInline(StringBuilder sb, Inline inline)
    {
        // Inside <script>/<style>/… everything up to the closing tag is dropped, text included.
        if (_skipUntilTag is not null)
        {
            if (inline is HtmlInline end && IsClosingTag(end.Tag, _skipUntilTag)) _skipUntilTag = null;
            return;
        }
        switch (inline)
        {
            case var box when CheckboxState(box) is { } ticked:
                sb.Append(ticked
                    ? $"<span style=\"font-family:'Segoe UI Symbol',{FontStack};color:{_p.Accent};\">&#9745;</span>&#160;"
                    : $"<span style=\"font-family:'Segoe UI Symbol',{FontStack};color:{_p.Muted};\">&#9744;</span>&#160;");
                return;
            case LiteralInline lit:
                sb.Append(Enc(lit.Content.ToString()));
                return;
            case HtmlEntityInline entity:
                sb.Append(Enc(entity.Transcoded.ToString()));
                return;
            case CodeInline code:
                sb.Append($"<code style=\"font-family:{MonoStack};font-size:90%;background-color:{_p.CodeBackground};color:{_p.CodeText};padding:1px 4px;border:1px solid {_p.Border};\">")
                  .Append(Enc(code.Content)).Append("</code>");
                return;
            case MathInline math:
                sb.Append("<span style=\"font-family:'Cambria Math',Cambria,Georgia,serif;font-style:italic;\">")
                  .Append(Enc(LatexText.ToReadable(math.Content.ToString()))).Append("</span>");
                return;
            case LineBreakInline br:
                sb.Append(br.IsHard ? "<br />\n" : "\n");
                return;
            case AutolinkInline auto:
            {
                var href = auto.IsEmail && !auto.Url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + auto.Url : auto.Url;
                sb.Append($"<a href=\"{Enc(SafeUrl(href))}\" style=\"color:{_p.Accent};text-decoration:underline;\">").Append(Enc(auto.Url)).Append("</a>");
                return;
            }
            case FootnoteLink fl:
                if (fl.IsBackLink)
                    sb.Append($"&#160;<a href=\"#fnref-{fl.Footnote.Order}\" style=\"color:{_p.Accent};text-decoration:none;\">&#8617;</a>");
                else
                    sb.Append($"<sup id=\"fnref-{fl.Footnote.Order}\"><a href=\"#fn-{fl.Footnote.Order}\" style=\"color:{_p.Accent};text-decoration:none;font-size:11px;\">{fl.Footnote.Order}</a></sup>");
                return;
            case LinkInline link when link.IsImage:
                WriteImage(sb, link);
                return;
            case LinkInline link:
            {
                var url = SafeUrl(link.GetDynamicUrl?.Invoke() ?? link.Url ?? "");
                var titleAttr = string.IsNullOrEmpty(link.Title) ? "" : $" title=\"{Enc(link.Title)}\"";
                if (url.Length == 0) { WriteInlines(sb, link); return; }
                sb.Append($"<a href=\"{Enc(url)}\"{titleAttr} style=\"color:{_p.Accent};text-decoration:underline;\">");
                WriteInlines(sb, link);
                sb.Append("</a>");
                return;
            }
            case EmphasisInline em:
                WriteEmphasis(sb, em);
                return;
            case HtmlInline html:
                if (OpeningTagName(html.Tag) is { } opened && SkippedContentTags.Contains(opened)
                    && !html.Tag.TrimEnd().EndsWith("/>", StringComparison.Ordinal))
                {
                    // Never closed: it's prose about a tag, so show it as written.
                    if (!ClosedLater(html, opened)) { sb.Append(Enc(html.Tag)); return; }
                    _skipUntilTag = opened;
                    return;
                }
                WriteHtmlInline(sb, html.Tag);
                return;
            case ContainerInline container:
                WriteInlines(sb, container);
                return;
            default:
                sb.Append(Enc(inline.ToString() ?? ""));
                return;
        }
    }

    private void WriteEmphasis(StringBuilder sb, EmphasisInline em)
    {
        var (open, close) = (em.DelimiterChar, em.DelimiterCount) switch
        {
            ('*' or '_', >= 2) => ("<strong style=\"font-weight:bold;\">", "</strong>"),
            ('*' or '_', _) => ("<em style=\"font-style:italic;\">", "</em>"),
            ('~', >= 2) => ("<del style=\"text-decoration:line-through;\">", "</del>"),
            ('~', _) => ("<sub>", "</sub>"),
            ('^', _) => ("<sup>", "</sup>"),
            ('+', _) => ("<ins style=\"text-decoration:underline;\">", "</ins>"),
            ('=', _) => ("<span style=\"background-color:#fff3a3;\">", "</span>"),
            _ => ("", ""),
        };
        sb.Append(open);
        WriteInlines(sb, em);
        sb.Append(close);
    }

    // Inline HTML in AI output is almost always <br>, <sub>/<sup>, <kbd> or <u>. Those are kept
    // (self-closed / balanced for XHTML); anything else is dropped rather than risk unbalanced tags.
    private static readonly HashSet<string> InlineTagsKept = new(StringComparer.OrdinalIgnoreCase)
        { "sub", "sup", "kbd", "u", "b", "i", "strong", "em", "s", "del", "ins", "mark", "small", "abbr", "span" };

    internal static readonly HashSet<string> SkippedContentTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "iframe", "object", "noscript", "template", "textarea", "select" };

    private string? _skipUntilTag;

    internal static string? OpeningTagName(string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"^<\s*([a-zA-Z][a-zA-Z0-9]*)");
        return m.Success ? m.Groups[1].Value : null;
    }

    internal static bool IsClosingTag(string tag, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(tag, @"^<\s*/\s*" + name + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static void WriteHtmlInline(StringBuilder sb, string tag)
    {
        var m = System.Text.RegularExpressions.Regex.Match(tag, @"^<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)");
        if (!m.Success) return;
        var closing = m.Groups[1].Value == "/";
        var name = m.Groups[2].Value.ToLowerInvariant();
        if (name == "br") { if (!closing) sb.Append("<br />"); return; }
        if (!InlineTagsKept.Contains(name)) return;
        // A CriticMarkup substitution is <del>old</del><ins>new</ins>: without a gap it read "oldnew".
        if (name == "ins" && !closing && sb.Length > 6 && sb.ToString(sb.Length - 6, 6) == "</del>") sb.Append(' ');
        if (name == "mark") name = "span"; // <mark> (CriticMarkup {==highlight==}) styled like ==text==
        sb.Append(closing ? $"</{name}>" : name switch
        {
            "kbd" => $"<{name} style=\"font-family:{MonoStack};font-size:90%;border:1px solid #d0d7de;padding:0 3px;\">",
            "span" when tag.StartsWith("<mark", StringComparison.OrdinalIgnoreCase) => "<span style=\"background-color:#fff3a3;\">",
            "del" or "s" => $"<{name} style=\"text-decoration:line-through;\">",
            "ins" or "u" => $"<{name} style=\"text-decoration:underline;\">",
            _ => $"<{name}>",
        });
    }

    private void WriteImage(StringBuilder sb, LinkInline link)
    {
        var alt = EmailTextRenderer.InlineText(link).Trim();
        var url = link.Url ?? "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            // Remote pictures stay remote: mail clients block them until the reader allows it,
            // and silently downloading them here would ship whatever the URL served today.
            sb.Append($"<img src=\"{Enc(url)}\" alt=\"{Enc(alt)}\" style=\"border:0;max-width:100%;height:auto;\" />");
            return;
        }
        if (LoadLocalImage(url, out var bytes, out var mime, out var name) &&
            AddImage(bytes, mime, name, 1.0, out var img))
        {
            sb.Append($"<img src=\"cid:{img.ContentId}\" width=\"{img.Width}\" height=\"{img.Height}\" alt=\"{Enc(alt)}\" style=\"border:0;width:{img.Width}px;max-width:100%;height:auto;\" />");
            return;
        }
        _notes.Add($"Couldn't find the picture \"{(alt.Length > 0 ? alt : Path.GetFileName(url))}\", so the email shows its description instead.");
        sb.Append($"<span style=\"color:{_p.Muted};\">[{Enc(alt.Length > 0 ? alt : "image")}]</span>");
    }

    private bool LoadLocalImage(string url, out byte[] bytes, out string mime, out string name)
    {
        bytes = Array.Empty<byte>();
        mime = "image/png";
        name = "image.png";
        try
        {
            if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0 || !url[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return false;
                bytes = Convert.FromBase64String(url[(comma + 1)..]);
                mime = url[5..url.IndexOf(';')].ToLowerInvariant();
                name = "image" + MimeExtension(mime);
                return bytes.Length > 0;
            }
            // The same lookup as the preview and every other exporter (relative to the document).
            var path = DocumentImages.Resolve(url, _options.BaseDirectory);
            if (path is null) return false;
            bytes = File.ReadAllBytes(path);
            name = Path.GetFileName(path);
            mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".svg" => "image/svg+xml",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "image/png",
            };
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string MimeExtension(string mime) => mime switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/svg+xml" => ".svg",
        _ => ".png",
    };

    /// <summary>Adds an inline picture, converting anything Outlook can't show (SVG, WebP, BMP) to
    /// PNG. <paramref name="density"/> is how many image pixels make one CSS pixel (2 for the 2x
    /// diagram renders), so diagrams stay crisp at their natural size.</summary>
    private bool AddImage(byte[] bytes, string mime, string fileName, double density, out EmailInlineImage image)
    {
        image = null!;
        try
        {
            if (mime == "image/svg+xml")
            {
                var png = SvgRasterizer.ToPng(Encoding.UTF8.GetString(bytes), 2.0);
                if (png is null) return false;
                bytes = png;
                mime = "image/png";
                density = 2.0;
                fileName = Path.ChangeExtension(fileName, ".png");
            }
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));
            if (codec is null) return false;
            int pxW = codec.Info.Width, pxH = codec.Info.Height;
            if (mime is not ("image/png" or "image/jpeg" or "image/gif"))
            {
                using var bmp = SKBitmap.Decode(bytes);
                if (bmp is null) return false;
                using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 90);
                bytes = data.ToArray();
                mime = "image/png";
                fileName = Path.ChangeExtension(fileName, ".png");
            }
            double w = pxW / density, h = pxH / density;
            if (w > MaxImageWidth) { h = h * MaxImageWidth / w; w = MaxImageWidth; }
            var cid = $"ms{_images.Count + 1}.{_cidToken}@marksmith";
            image = new EmailInlineImage(cid, bytes, mime, SafeFileName(fileName),
                Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
            _images.Add(image);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
        return clean.Length == 0 ? "image.png" : clean;
    }

    internal static string SafeUrl(string url)
    {
        var compact = new string(WebUtility.HtmlDecode(url ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c)).ToArray());
        return compact.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || compact.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? "" : url ?? "";
    }

    /// <summary>Escapes only the markup characters. WebUtility.HtmlEncode also turns every Latin-1
    /// letter (é, ², Ä…) into a numeric entity, which bloats the body and garbles the source when
    /// someone views it; the message is UTF-8, so those characters can stay as they are.</summary>
    internal static string Enc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default:
                    if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) break;
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
