using System.Text.RegularExpressions;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;
using MarkSmith.Core.Preview;
using MarkSmith.Models;

namespace MarkSmith.Services;

// The email half of the lift/placeholder model. An email can't run the preview's scripts or show
// its SVG, so every block the preview draws itself (SmartArt, :::shapes, charts, metrics, the
// engineering diagrams, tabs, columns…) is lifted here, rendered with the SAME code the preview
// uses, and handed to EmailHtmlRenderer as a numbered figure: the renderer rasterizes the SVG ones
// to inline PNGs and flattens the rest to plain tables. Page furniture that makes no sense in a
// message (watermarks, cover pages, the concordance index) is dropped.
public sealed partial class MarkdownHtmlService
{
    /// <summary>A lifted block: preview HTML to flatten or rasterize, or (from the shared
    /// <c>:::</c> lift) a ready picture with its alt text, or KPI cards.</summary>
    internal sealed record EmailFigureFragment(string Kind, string Html, byte[]? Png = null, string? Alt = null,
        IReadOnlyList<(string Value, string Label)>? Metrics = null);

    [GeneratedRegex(@"<!--(?<kind>EMAILFIG|SHAPES|SMARTART|ENGDIAGRAM|MSBLOCK|CHART|METRICS|COLUMNS|PARALLEL|WATERMARK|COVERPAGE|INDEX):(?<n>\d+)-->")]
    private static partial Regex EmailPlaceholderRe();

    /// <summary>Normalizes <paramref name="markdown"/> exactly as the preview does, then swaps each
    /// self-drawn block for a <c>&lt;!--MSFIG:n--&gt;</c> placeholder whose rendered HTML is
    /// <paramref name="figures"/>[n].</summary>
    internal static string PrepareForEmail(string markdown, AppSettings settings, ThemeDefinition theme,
        out List<EmailFigureFragment> figures)
    {
        markdown = NormalizeForRender(markdown ?? "", settings);
        markdown = LiftEmailDirectives(markdown, theme, out var direct);
        var (clean, shapes) = MarkSmith.Core.Composer.ShapeMarkdownHtml.LiftShapes(markdown);
        markdown = clean;

        var smartArt = new List<string>();
        var fences = FencedSpans(markdown);
        markdown = SmartArtBlockRe().Replace(markdown, m =>
        {
            foreach (var f in fences)
                if (m.Index >= f.Start && m.Index < f.End) return m.Value;
            string alias = SmartArtBlockHeader.Layout(m.Groups["header"].Value)?.ToLowerInvariant() ?? "";
            if (alias.Length == 0 && m.Groups["kind"].Value is "workflow" or "timeline") alias = m.Groups["kind"].Value;
            smartArt.Add(RenderSmartArtForEmail(alias, m.Groups["inner"].Value.Trim()));
            return $"\n\n<!--SMARTART:{smartArt.Count - 1}-->\n\n";
        });

        markdown = LiftEngineeringDiagrams(markdown, fences, out var engineering);
        markdown = LiftContainerBlocks(markdown, theme, out var containers);
        markdown = LiftWatermarks(markdown, fences, isDark: false, out _);
        markdown = LiftCoverPages(markdown, fences, out _);
        markdown = LiftIndexBlocks(markdown, fences, out _);
        markdown = LiftColumnsBlocks(markdown, fences, out var columns);
        markdown = LiftParallelBlocks(markdown, fences, out var parallel);
        markdown = LiftChartBlocks(markdown, fences, theme, out var charts);
        markdown = LiftMetricsBlocks(markdown, fences, theme, out var metrics);
        markdown = TableFormulaEvaluator.EvaluateTableMarkdown(markdown);

        var found = new List<EmailFigureFragment>();
        markdown = EmailPlaceholderRe().Replace(markdown, m =>
        {
            var kind = m.Groups["kind"].Value;
            var n = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (kind == "EMAILFIG")
            {
                if (n >= direct.Count) return "";
                found.Add(direct[n]);
                return $"<!--MSFIG:{found.Count - 1}-->";
            }
            List<string>? source = kind switch
            {
                "SHAPES" => shapes,
                "SMARTART" => smartArt,
                "ENGDIAGRAM" => engineering,
                "MSBLOCK" => containers,
                "CHART" => charts,
                "METRICS" => metrics,
                "COLUMNS" => columns,
                "PARALLEL" => parallel,
                _ => null, // watermark, cover page, index: not part of a message
            };
            if (source is null || n >= source.Count) return "";
            found.Add(new EmailFigureFragment(kind, source[n]));
            return $"<!--MSFIG:{found.Count - 1}-->";
        });
        figures = found;
        return markdown;
    }

    /// <summary>
    /// The <c>:::</c> blocks, through the same lift EPUB and the slides use (Core
    /// <see cref="Presentation.SlideDirectives"/>): tabs become headed sections, a data grid, kanban
    /// board or parallel text a table, references a reading list, an embed a link; the AI context,
    /// cover page, watermark, line numbers and index stay out of the message. Charts and SmartArt
    /// are drawn as pictures in the email's light colours with their data in the alt text, metrics
    /// become KPI cards, a canvas its own picture. Shapes, engineering diagrams and columns are left
    /// for the preview-renderer lifts that follow.
    /// </summary>
    private static string LiftEmailDirectives(string markdown, ThemeDefinition theme, out List<EmailFigureFragment> figures)
    {
        var found = figures = new List<EmailFigureFragment>();
        if (markdown.IndexOf(":::", StringComparison.Ordinal) < 0) return markdown;

        var light = Email.EmailPalette.From(theme);
        var drawTheme = light.DiagramTheme();
        string Add(EmailFigureFragment f)
        {
            found.Add(f);
            return $"<!--EMAILFIG:{found.Count - 1}-->";
        }

        var slideBlocks = new List<Presentation.SlideBlock>();
        var lifted = Presentation.SlideDirectives.Lift(markdown, out _, slideBlocks, node =>
        {
            switch (node.Detector.FeatureName)
            {
                case "Shapes" or "EngineeringDiagram" or "Columns":
                    return markdown[node.Block.Start..node.Block.End];
                case "Metrics":
                    return Presentation.SlideDirectives.Metrics((node.InnerContent ?? "").Replace("\r", "")) is { } m
                        ? Add(new EmailFigureFragment("KPI", "", Metrics: m.Items
                            .Select(i => (EpubDirectives.Plain(i.Value), EpubDirectives.Plain(i.Label))).ToList()))
                        : null;
                case "Chart" or "SmartArt" or "Workflow" or "Timeline":
                    return EpubDirectives.Draw(node, drawTheme, light.Page) is { } pic
                        ? Add(new EmailFigureFragment(node.Detector.FeatureName.ToUpperInvariant(), "", pic.Png, pic.Alt))
                        : null;
                default:
                    return null;
            }
        });

        // What the slide rewrite drew as a picture (a canvas).
        return Regex.Replace(lifted, @"<!--ms-slide-block:(\d+)-->", m =>
        {
            var i = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            return i < slideBlocks.Count && slideBlocks[i] is Presentation.PictureSlideBlock p && p.Data.Length > 0
                ? Add(new EmailFigureFragment("PICTURE", "", p.Data, p.Description))
                : "";
        });
    }

    private static string RenderSmartArtForEmail(string alias, string inner)
    {
        try
        {
            var ast = MarkdownAstParser.Parse(inner);
            var resolved = !string.IsNullOrWhiteSpace(alias) ? alias : (SmartArtLayoutSuggester.Suggest(ast) ?? "list");
            var title = SmartArtLayoutCatalog.Shared.TryResolve(resolved)?.Title ?? resolved;
            return HtmlPreviewRenderer.RenderHtml(ast, resolved, title);
        }
        catch
        {
            return "";
        }
    }
}
