using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Lifts :::shapes and :::mosaic blocks into HTML-comment placeholders before Markdig + Sanitizer run,
    /// then injects the trusted rendered SVG or flattened Skia PNG post-sanitization.
    /// Incorporates:
    /// - Trick 1: Skia Flattened Bitmap Preview when shape count > 150 (O(1) DOM footprint).
    /// - Trick 3: Compound Path Grouping by Color when shape count is 31..150.
    /// - Trick 4: LOD Clamping for In-App Preview (recipes clamped to max 32x32 in preview).
    /// </summary>
    public static class ShapeMarkdownHtml
    {
        public const int BitmapPreviewThreshold = 150;
        public const int CompoundPathThreshold = 30;

        private static readonly Regex ShapesBlock = new(
            @"(?:\A\uFEFF?|(?<=\r?\n))\s*:::(?<tag>shapes|mosaic)(?<header>[^\r\n]*)\r?\n(?<inner>[\s\S]*?)\r?\n:::\s*",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static (string CleanMarkdown, List<string> SvgBlocks) LiftShapes(string markdown)
        {
            var svgs = new List<string>();
            if (string.IsNullOrEmpty(markdown) ||
                (!markdown.Contains(":::shapes", StringComparison.OrdinalIgnoreCase) &&
                 !markdown.Contains(":::mosaic", StringComparison.OrdinalIgnoreCase)))
            {
                return (markdown, svgs);
            }

            string clean = ShapesBlock.Replace(markdown, m =>
            {
                try
                {
                    string tag = m.Groups["tag"].Value;
                    string header = m.Groups["header"].Value;
                    string inner = m.Groups["inner"].Value;

                    List<ComposedShape> shapes;
                    bool isRecipe = tag.Equals("mosaic", StringComparison.OrdinalIgnoreCase) ||
                                    header.Contains("recipe", StringComparison.OrdinalIgnoreCase) ||
                                    MosaicRecipe.IsRecipe(inner);

                    if (isRecipe)
                    {
                        var recipe = MosaicRecipe.Parse(header, inner);
                        // Trick 4: LOD Clamping for in-app preview!
                        shapes = recipe.Synthesize(isPreview: true);
                    }
                    else
                    {
                        shapes = ShapeMarkdownCodec.Parse(inner);
                    }

                    if (shapes.Count == 0) return m.Value;

                    var (w, h) = ShapeMarkdownCodec.CanvasSize(shapes);

                    string visualContent;
                    if (shapes.Count > BitmapPreviewThreshold)
                    {
                        // Trick 1: Skia Flattened Bitmap Preview — paints shapes onto an in-memory SKBitmap
                        // returning a crisp, lightweight base64 PNG data URI. DOM overhead is O(1)!
                        string pngUri = ImageShapeComposer.RenderBitmapPreviewPng(shapes, w, h);
                        visualContent = $"<img src=\"{pngUri}\" style=\"width:100%;height:auto;display:block;border-radius:4px;\" alt=\"Shape composition\" />";
                    }
                    else if (shapes.Count > CompoundPathThreshold)
                    {
                        // Trick 3: Compound Path Grouping by Color — groups shapes sharing the same palette fill color
                        visualContent = ImageShapeComposer.RenderSvg(shapes, w, h, compoundPaths: true);
                    }
                    else
                    {
                        // Individual SVG vector nodes
                        visualContent = ImageShapeComposer.RenderSvg(shapes, w, h, compoundPaths: false);
                    }

                    svgs.Add($"<div class=\"shapes-diagram\" style=\"width:100%;max-width:900px;background:#ffffff;border:1px solid #d0d0d0;border-radius:8px;padding:8px;margin:12px 0;\">{visualContent}</div>");
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
