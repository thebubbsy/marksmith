using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Core.AdvancedFeatures;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Models;
using MarkSmith.Services.Presentation;

namespace MarkSmith.Services;

/// <summary>
/// The <c>:::</c> blocks (cover page, chart, metrics, SmartArt, tabs, columns…) in an e-book.
///
/// Markdig reads these as plain paragraphs, so a book printed "title: … subtitle: …" for a cover
/// page, "Q1,10 Q2,25" for a chart and "=== Option A" for tabs. Readers run no JavaScript and
/// draw little CSS, so each block becomes what a book can hold: charts and SmartArt are drawn
/// to packaged pictures (with the data in their alt text), metrics become KPI cards, and the
/// text-shaped blocks take the same rewrites the slides use (tabs as headed sections, a data grid
/// as a table, references as a list). The cover page feeds the book's title page.
/// </summary>
internal static class EpubDirectives
{
    internal sealed record Picture(string File, byte[] Png);

    public static string Lift(string markdown, ThemeDefinition theme,
                              out DocxExportService.CoverPageInfo? cover, List<Picture> pictures)
    {
        cover = null;
        if (markdown.IndexOf(":::", StringComparison.Ordinal) < 0) return markdown;

        var slideBlocks = new List<SlideBlock>();
        var lifted = SlideDirectives.Lift(markdown, out cover, slideBlocks, node => Native(node, theme, pictures));

        // What the slide rewrite drew as a picture (canvas, shapes, engineering diagrams).
        return Regex.Replace(lifted, @"<!--ms-slide-block:(\d+)-->", m =>
        {
            var i = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return i < slideBlocks.Count && slideBlocks[i] is PictureSlideBlock p && p.Data.Length > 0
                ? Figure(pictures, p.Data, p.Description)
                : "";
        });
    }

    private static string? Native(FeatureNode node, ThemeDefinition theme, List<Picture> pictures)
    {
        var inner = (node.InnerContent ?? "").Replace("\r", "");
        switch (node.Detector.FeatureName)
        {
            case "Chart":
            {
                var chart = SlideDirectives.Chart(node, inner);
                if (chart is null) return null;
                var kind = node.Attributes.GetValueOrDefault("type", "bar").Trim().ToLowerInvariant();
                kind = kind switch { "donut" => "doughnut", "area" => "line", "pie" or "doughnut" or "line" => kind, _ => "bar" };
                var svg = MarkdownHtmlService.BuildChartSvg(kind, chart.Labels.ToList(), chart.Values.ToList(), theme);
                var png = Rasterize(svg, theme.Background);
                if (png is null) return null;
                var name = kind switch { "pie" => "Pie chart", "doughnut" => "Doughnut chart", "line" => "Line chart", _ => "Bar chart" };
                var data = string.Join(", ", chart.Labels.Zip(chart.Values, (l, v) => $"{l} {v.ToString("0.##", CultureInfo.InvariantCulture)}"));
                return Figure(pictures, png, $"{name}: {data}");
            }
            case "Metrics":
            {
                var metrics = SlideDirectives.Metrics(inner);
                if (metrics is null) return null;
                var sb = new StringBuilder("<div class=\"ms-metrics\">");
                foreach (var (value, label) in metrics.Items)
                {
                    sb.Append("<div class=\"ms-metric\"><p class=\"ms-metric-value\">").Append(Html(Plain(value))).Append("</p>");
                    if (label.Length > 0) sb.Append("<p class=\"ms-metric-label\">").Append(Html(Plain(label))).Append("</p>");
                    sb.Append("</div>");
                }
                return sb.Append("</div>").ToString();
            }
            case "SmartArt" or "Workflow" or "Timeline":
            {
                var block = SlideDirectives.SmartArt(node, inner);
                if (block is null) return null;
                var first = DetectorHelpers.SplitLines(node.Block.RawText)[0].TrimStart();
                int sp = first.IndexOfAny(new[] { ' ', '\t' });
                var asked = SmartArtBlockHeader.Layout(sp < 0 ? "" : first[sp..])
                            ?? (node.Detector.FeatureName is "Workflow" or "Timeline" ? node.Detector.FeatureName.ToLowerInvariant() : null);
                var ast = MarkdownAstParser.Parse(block.Body);
                var pkg = SmartArtLayoutCatalog.Shared.TryResolve(asked);
                var alias = pkg != null ? asked! : (SmartArtLayoutSuggester.Suggest(ast) ?? "list");
                var title = (pkg ?? SmartArtLayoutCatalog.Shared.TryResolve(alias))?.Title ?? alias;
                var html = HtmlPreviewRenderer.RenderHtml(ast, alias, title);
                var open = html.IndexOf("<svg", StringComparison.Ordinal);
                var close = html.LastIndexOf("</svg>", StringComparison.Ordinal);
                if (open < 0 || close < open) return null;
                var png = Rasterize(html[open..(close + 6)], "#f8f9fa");
                if (png is null) return null;
                var items = block.Body.Split('\n')
                    .Where(l => Regex.IsMatch(l, @"^\s*[-*+]\s+\S"))
                    .Select(l => Plain(l.TrimStart().TrimStart('-', '*', '+', ' ', '\t')));
                return Figure(pictures, png, "Diagram: " + string.Join(" · ", items));
            }
            default:
                return null;
        }
    }

    /// <summary>A packaged picture as an HTML block Markdig passes through untouched.</summary>
    private static string Figure(List<Picture> pictures, byte[] png, string alt)
    {
        var file = $"images/ms-block-{pictures.Count + 1:000}.png";
        pictures.Add(new(file, png));
        // Drawn at 2x: its natural width is half the pixels, so a 200 px sketch isn't blown up to
        // the page width (the stylesheet still shrinks a wide one to fit).
        var width = png.Length >= 24 ? ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]) / 2 : 0;
        var size = width > 0 ? $" width=\"{width}\"" : "";
        return $"<figure class=\"ms-block\"><img src=\"{file}\" alt=\"{Html(alt)}\"{size} /></figure>";
    }

    /// <summary>
    /// Our generated SVGs size themselves to their container (width 100%, or no size at all),
    /// which a rasterizer can't; give them the viewBox's size, a solid ground (readers can't be
    /// relied on to show a theme's page colour behind a transparent picture), and draw at 2x.
    /// </summary>
    internal static byte[]? Rasterize(string svg, string background)
    {
        var tagEnd = svg.IndexOf('>');
        if (tagEnd < 0) return null;
        var tag = svg[..tagEnd];
        var vb = Regex.Match(tag, @"viewBox=""\s*[-\d.]+\s+[-\d.]+\s+([\d.]+)\s+([\d.]+)\s*""");
        if (!vb.Success) return null;
        string w = vb.Groups[1].Value, h = vb.Groups[2].Value;
        tag = Regex.Replace(tag, @"\s(?:width|height|style)=""[^""]*""", "");
        if (!tag.Contains("xmlns=", StringComparison.Ordinal)) tag += " xmlns=\"http://www.w3.org/2000/svg\"";
        tag += $" width=\"{w}\" height=\"{h}\"";
        var body = svg[(tagEnd + 1)..];
        var ground = $"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"{Html(background)}\" />";
        return SvgRasterizer.ToPng(tag + ">" + ground + body, 2.0);
    }

    // "**99.9%**" or "`code`" in a card or alt text: the words, not the markup.
    private static string Plain(string s) => Regex.Replace(s, @"(\*\*|__|`)", "").Trim();

    private static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);
}
