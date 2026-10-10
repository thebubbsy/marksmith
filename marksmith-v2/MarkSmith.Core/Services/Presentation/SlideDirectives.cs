using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MarkSmith.Core.AdvancedFeatures;

namespace MarkSmith.Services.Presentation;

/// <summary>
/// The <c>:::</c> blocks (cover page, chart, metrics, SmartArt, tabs, columns…) on slides.
///
/// Markdig sees these as plain paragraphs, so without this pass a slide printed
/// "title: … subtitle: …" for a cover page and ":::chart type=bar Q1,10" for a chart. Each block
/// is found with the same tokenizer and detectors the Word export uses, then either rewritten as
/// the Markdown it means on a slide (tabs and columns become headed sections, a data grid a
/// table) or swapped for a placeholder the deck builder turns into a native block (a PowerPoint
/// chart, SmartArt, KPI cards, a picture). Page furniture with no slide meaning (watermark, line
/// numbers, index, AI provenance) is dropped.
/// </summary>
internal static class SlideDirectives
{
    /// <summary>The placeholder an HTML comment carries: <c>&lt;!--ms-slide-block:3--&gt;</c>.</summary>
    internal static readonly Regex PlaceholderRe = new(@"^\s*<!--ms-slide-block:(\d+)-->\s*$", RegexOptions.Compiled);

    public static string Lift(string markdown, out DocxExportService.CoverPageInfo? cover, List<SlideBlock> blocks)
    {
        cover = null;
        if (markdown.IndexOf(":::", StringComparison.Ordinal) < 0) return markdown;

        List<FeatureNode> nodes;
        try { nodes = AdvancedFeaturePipeline.Shared.Process(markdown, "slides"); }
        catch { return markdown; }
        if (nodes.Count == 0) return markdown;

        var sb = new StringBuilder(markdown.Length);
        int at = 0;
        foreach (var node in nodes.OrderBy(n => n.Block.Start))
        {
            if (node.Block.Start < at) continue;
            sb.Append(markdown, at, node.Block.Start - at);
            at = node.Block.End;

            string? replacement;
            try { replacement = Replace(node, ref cover, blocks); }
            catch { replacement = null; }
            // Unknown or unreadable: keep the author's text rather than lose it.
            sb.Append(replacement is null ? markdown[node.Block.Start..node.Block.End] : "\n\n" + replacement.Trim('\n') + "\n\n");
        }
        sb.Append(markdown, at, markdown.Length - at);
        return sb.ToString();
    }

    private static string Placeholder(List<SlideBlock> blocks, SlideBlock block)
    {
        blocks.Add(block);
        return $"<!--ms-slide-block:{blocks.Count - 1}-->";
    }

    private static string? Replace(FeatureNode node, ref DocxExportService.CoverPageInfo? cover, List<SlideBlock> blocks)
    {
        var inner = (node.InnerContent ?? "").Replace("\r", "");
        switch (node.Detector.FeatureName)
        {
            case "CoverPage":
                cover ??= DocxExportService.ExtractCoverPage(node);
                return "";
            case "Watermark" or "LineNumbers" or "Index" or "AI Context":
                return "";
            case "DropCap":
                return inner;
            case "Columns":
                return string.Join("\n\n", SplitOn(inner, @"(?:\n|^)[ \t]*===+[ \t]*(?:\n|$)"));
            case "Tabs":
            {
                var tabs = DocxExportService.ParseTabsFromContent(inner);
                if (tabs.Count == 0) return inner;
                return string.Join("\n\n", tabs.Select(t => $"#### {OneLine(t.Title)}\n\n{t.Content.Trim('\n')}"));
            }
            case "Parallel":
                return Parallel(node, inner);
            case "Datagrid":
                return Datagrid(inner);
            case "Kanban":
                return Kanban(node, inner);
            case "References":
                return References(inner);
            case "Embed":
            {
                var src = node.Attributes.GetValueOrDefault("src", "").Trim();
                if (src.Length == 0) return "";
                var provider = node.Attributes.GetValueOrDefault("provider", "").Trim();
                var label = inner.Trim().Length > 0 ? OneLine(inner) : provider.ToLowerInvariant() switch
                {
                    "" => src,
                    "youtube" => "YouTube",
                    var other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other),
                };
                return $"▶ [{EscapeLinkText(label)}](<{src}>)";
            }
            case "Chart":
                return Chart(node, inner) is { } chart ? Placeholder(blocks, chart) : null;
            case "Metrics":
                return Metrics(inner) is { } metrics ? Placeholder(blocks, metrics) : null;
            case "SmartArt" or "Workflow" or "Timeline":
                return SmartArt(node, inner) is { } art ? Placeholder(blocks, art) : null;
            case "Canvas":
                return SvgPicture(inner.Trim(), "Drawing", transparent: true) is { } canvas ? Placeholder(blocks, canvas) : null;
            case "Shapes":
            {
                var shapes = MarkSmith.Core.Composer.ShapeMarkdownCodec.Parse(inner);
                if (shapes.Count == 0) return "";
                var (w, h) = MarkSmith.Core.Composer.ShapeMarkdownCodec.CanvasSize(shapes);
                var svg = MarkSmith.Core.Composer.ImageShapeComposer.RenderSvg(shapes, w, h);
                return SvgPicture(svg, "Shapes", transparent: false) is { } pic ? Placeholder(blocks, pic) : null;
            }
            case "EngineeringDiagram":
            {
                MarkdownHtmlService.TryGetEngineeringFenceName(node.Block.RawText, out var name);
                return MarkdownHtmlService.TryRenderEngineeringFence(node.Block.RawText, out var svg)
                    && SvgPicture(svg, string.IsNullOrEmpty(name) ? "Diagram" : name, transparent: false) is { } pic
                    ? Placeholder(blocks, pic) : null;
            }
            default:
                return null;
        }
    }

    private static IEnumerable<string> SplitOn(string text, string pattern) =>
        Regex.Split(text, pattern).Select(s => s.Trim('\n')).Where(s => s.Trim().Length > 0);

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static string EscapeLinkText(string s) => s.Replace("[", "\\[").Replace("]", "\\]");

    private static string Cell(string s) => OneLine(s).Replace("|", "\\|");

    private static string Table(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", header.Select(Cell))).Append(" |\n");
        sb.Append('|').Append(string.Concat(header.Select(_ => "---|"))).Append('\n');
        foreach (var row in rows)
        {
            var cells = Enumerable.Range(0, header.Count).Select(i => i < row.Count ? Cell(row[i]) : "");
            sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
        }
        return sb.ToString();
    }

    // First line is the header, like the Word export.
    private static string Datagrid(string inner)
    {
        var lines = inner.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return "";
        var rows = lines.Select(l => (IReadOnlyList<string>)l.Split(',', '\t').Select(c => c.Trim()).ToList()).ToList();
        var width = rows.Max(r => r.Count);
        var header = rows[0].Concat(Enumerable.Repeat("", width - rows[0].Count)).ToList();
        return Table(header, rows.Skip(1));
    }

    private static string Kanban(FeatureNode node, string inner)
    {
        var board = MarkSmith.Core.Kanban.KanbanParser.Parse(node.Block.RawText, node.InnerContent ?? "", node.Attributes);
        if (board.Columns.Count == 0) return inner;
        var header = board.Columns.Select(c => c.Title).ToList();
        int depth = board.Columns.Max(c => c.Cards.Count);
        var rows = Enumerable.Range(0, depth).Select(r => (IReadOnlyList<string>)board.Columns
            .Select(c => r < c.Cards.Count ? (c.Cards[r].IsCompleted == true ? "✓ " : "") + c.Cards[r].Text : "").ToList());
        var title = string.IsNullOrWhiteSpace(board.Title) ? "" : $"#### {OneLine(board.Title)}\n\n";
        return title + Table(header, rows);
    }

    // ":::parallel English | Français" then rows separated by "---", columns by "===" or "|".
    private static string Parallel(FeatureNode node, string inner)
    {
        var first = DetectorHelpers.SplitLines(node.Block.RawText)[0];
        var m = Regex.Match(first, @"^\s*:::parallel\s+(.*)$", RegexOptions.IgnoreCase);
        var headers = m.Success
            ? m.Groups[1].Value.Split('|').Select(h => h.Trim().Trim('"', '\'')).Where(h => h.Length > 0).ToList()
            : new List<string>();
        var rowsText = Regex.Split(inner, @"(?:\n|^)---(?:\n|$)").Where(r => r.Trim().Length > 0).ToList();
        int cols = Math.Max(2, headers.Count);
        var rows = rowsText.Select(r => (IReadOnlyList<string>)ParallelRowParser.SplitColumns(r, cols).ToList()).ToList();
        if (rows.Count == 0) return "";
        while (headers.Count < cols) headers.Add("");
        return Table(headers, rows);
    }

    // "@id / author: / title: / year:" entries, one paragraph each: Author (Year). *Title*. Journal.
    private static string References(string inner)
    {
        var sb = new StringBuilder();
        foreach (var block in Regex.Split(inner, @"\n\s*\n|(?=^\s*@)", RegexOptions.Multiline))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in block.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith('@') || t.Length == 0) continue;
                var colon = t.IndexOf(':');
                if (colon > 0) fields[t[..colon].Trim()] = t[(colon + 1)..].Trim();
            }
            if (fields.Count == 0) continue;
            var parts = new List<string>();
            var author = fields.GetValueOrDefault("author");
            var year = fields.GetValueOrDefault("year");
            if (!string.IsNullOrEmpty(author)) parts.Add(author + (string.IsNullOrEmpty(year) ? "." : $" ({year})."));
            else if (!string.IsNullOrEmpty(year)) parts.Add($"({year}).");
            if (fields.GetValueOrDefault("title") is { Length: > 0 } title) parts.Add($"*{title.TrimEnd('.')}*.");
            if ((fields.GetValueOrDefault("journal") ?? fields.GetValueOrDefault("publisher")) is { Length: > 0 } where) parts.Add(where.TrimEnd('.') + ".");
            if ((fields.GetValueOrDefault("url") ?? fields.GetValueOrDefault("doi")) is { Length: > 0 } url) parts.Add(url);
            if (parts.Count > 0) sb.Append("- ").Append(string.Join(" ", parts)).Append('\n');
        }
        return sb.ToString();
    }

    internal static ChartSlideBlock? Chart(FeatureNode node, string inner)
    {
        var chart = new ChartSlideBlock
        {
            Kind = node.Attributes.GetValueOrDefault("type", "bar").Trim().ToLowerInvariant() switch
            {
                "line" or "area" => ChartKind.Line,
                "pie" or "doughnut" or "donut" => ChartKind.Pie,
                _ => ChartKind.Bar,
            },
        };
        if (inner.TrimStart().StartsWith('{'))
        {
            using var j = System.Text.Json.JsonDocument.Parse(inner);
            var data = j.RootElement.GetProperty("data");
            foreach (var l in data.GetProperty("labels").EnumerateArray()) chart.Labels.Add(l.ToString());
            foreach (var v in data.GetProperty("values").EnumerateArray()) chart.Values.Add(v.GetDouble());
        }
        else
        {
            bool first = true;
            foreach (var line in inner.Split('\n').Where(l => l.Trim().Length > 0))
            {
                // A header row is optional (the Insert menu writes none).
                if (first) { first = false; if (!ChartDetector.IsLabelValueLine(line)) continue; }
                var comma = line.LastIndexOf(',');
                if (comma > 0 && double.TryParse(line[(comma + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    chart.Labels.Add(line[..comma].Trim());
                    chart.Values.Add(value);
                }
            }
        }
        return chart.Labels.Count > 0 && chart.Labels.Count == chart.Values.Count ? chart : null;
    }

    // "- **99.9%** System Uptime" or "- 99.9%: System Uptime".
    internal static MetricsSlideBlock? Metrics(string inner)
    {
        var block = new MetricsSlideBlock();
        foreach (var raw in inner.Split('\n'))
        {
            var t = raw.Trim();
            var bullet = Regex.Match(t, @"^[-*+]\s+");
            if (!bullet.Success) continue;
            t = t[bullet.Length..].Trim();
            if (t.Length == 0) continue;
            var bold = Regex.Match(t, @"^\*\*(.+?)\*\*\s*[:\-–—]?\s*(.*)$");
            if (bold.Success) { block.Items.Add((bold.Groups[1].Value.Trim(), bold.Groups[2].Value.Trim())); continue; }
            var colon = t.IndexOf(':');
            block.Items.Add(colon > 0 ? (t[..colon].Trim(), t[(colon + 1)..].Trim()) : (t, ""));
        }
        return block.Items.Count > 0 ? block : null;
    }

    private static SmartArtSlideBlock? SmartArt(FeatureNode node, string inner)
    {
        var body = node.Detector.FeatureName is "Timeline" or "Workflow" && !inner.Split('\n').Any(l => l.TrimStart().StartsWith('-') || l.TrimStart().StartsWith('*'))
            ? string.Join('\n', inner.Split('\n').Select(l => l.Trim().Length == 0 ? l : "- " + l.Trim()))
            : inner;
        var entries = body.Split('\n').Where(l => Regex.IsMatch(l, @"^\s*[-*+]\s+\S")).ToList();
        if (entries.Count == 0) return null;
        int top = entries.Count(l => !char.IsWhiteSpace(l[0]));
        int depth = entries.Any(l => char.IsWhiteSpace(l[0])) ? 2 : 1;

        var first = DetectorHelpers.SplitLines(node.Block.RawText)[0].TrimStart();
        int sp = first.IndexOfAny(new[] { ' ', '\t' });
        var layout = MarkSmith.Core.Glox.SmartArtBlockHeader.Layout(sp < 0 ? "" : first[sp..]);
        if (layout is null)
        {
            var ast = MarkSmith.Core.AST.MarkdownAstParser.Parse(body);
            layout = MarkSmith.Core.Glox.SmartArtLayoutSuggester.Suggest(ast) ?? "list";
        }
        return new SmartArtSlideBlock { Layout = layout, Body = body, TopLevelCount = Math.Max(1, top), Depth = depth };
    }

    private static PictureSlideBlock? SvgPicture(string svg, string description, bool transparent)
    {
        if (!svg.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) && !svg.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return null;
        var png = SvgRasterizer.ToPng(svg, 2.0, transparent);
        if (png is null || png.Length < 24) return null;
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        if (w <= 0 || h <= 0) return null;
        return new PictureSlideBlock
        {
            Data = png,
            ContentType = "image/png",
            PixelWidth = w,
            PixelHeight = h,
            NaturalWidthPt = w * 0.375,
            NaturalHeightPt = h * 0.375,
            IsDiagram = true,
            Description = description,
        };
    }
}
