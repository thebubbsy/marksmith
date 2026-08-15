using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Lifts :::shapes blocks into HTML-comment placeholders before Markdig + Sanitizer run,
    /// then injects the trusted rendered SVG post-sanitization so preview matches DOCX export 1:1.
    /// </summary>
    public static class ShapeMarkdownHtml
    {
        private static readonly Regex ShapesBlock = new(
            @"(?:\A\uFEFF?|(?<=\r?\n))\s*:::shapes[^\r\n]*\r?\n([\s\S]*?)\r?\n:::\s*",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static (string CleanMarkdown, List<string> SvgBlocks) LiftShapes(string markdown)
        {
            var svgs = new List<string>();
            if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::shapes", StringComparison.OrdinalIgnoreCase))
            {
                return (markdown, svgs);
            }

            string clean = ShapesBlock.Replace(markdown, m =>
            {
                try
                {
                    var shapes = ShapeMarkdownCodec.Parse(m.Groups[1].Value);
                    if (shapes.Count == 0) return m.Value;

                    var (w, h) = ShapeMarkdownCodec.CanvasSize(shapes);
                    string svg = ImageShapeComposer.RenderSvg(shapes, w, h);
                    svgs.Add($"<div class=\"shapes-diagram\" style=\"width:100%;max-width:900px;background:#ffffff;border:1px solid #d0d0d0;border-radius:8px;padding:8px;margin:12px 0;\">{svg}</div>");
                    return $"\n\n<!--SHAPES:{svgs.Count - 1}-->\n\n";
                }
                catch
                {
                    return m.Value;
                }
            });

            return (clean, svgs);
        }

        public static string PostInject(string html, List<string> svgBlocks)
        {
            if (svgBlocks == null || svgBlocks.Count == 0) return html;
            for (int i = 0; i < svgBlocks.Count; i++)
            {
                html = html.Replace($"<!--SHAPES:{i}-->", svgBlocks[i]);
            }
            return html;
        }

        public static string PreTransform(string markdown)
        {
            var (clean, svgs) = LiftShapes(markdown);
            return PostInject(clean, svgs);
        }
    }
}
