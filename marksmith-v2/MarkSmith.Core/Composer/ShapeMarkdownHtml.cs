using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkSmith.Core.Composer
{
    /// <summary>
    /// Turns :::shapes blocks into inline SVG before Markdig runs, so the main preview pane
    /// shows the MLShape composition exactly like the design (the SVG mirrors Word's render of
    /// the same shape set — see ImageShapeComposer.RenderSvg).
    /// </summary>
    public static class ShapeMarkdownHtml
    {
        private static readonly Regex ShapesBlock = new(
            @"(?<open>^[ \t]*:::shapes[ \t]*\r?\n)(?<body>.*?)(?<close>^[ \t]*:::[ \t]*\r?$)",
            RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string PreTransform(string markdown)
        {
            if (string.IsNullOrEmpty(markdown) || !markdown.Contains(":::shapes", StringComparison.OrdinalIgnoreCase))
            {
                return markdown;
            }

            return ShapesBlock.Replace(markdown, m =>
            {
                try
                {
                    var shapes = ShapeMarkdownCodec.Parse(m.Groups["body"].Value);
                    if (shapes.Count == 0) return m.Value;

                    var (w, h) = ShapeMarkdownCodec.CanvasSize(shapes);
                    string svg = ImageShapeComposer.RenderSvg(shapes, w, h);
                    return $"<div style=\"width:100%;max-width:900px;background:#ffffff;border:1px solid #d0d0d0;border-radius:8px;padding:8px;margin:12px 0;\">{svg}</div>";
                }
                catch
                {
                    return m.Value;
                }
            });
        }
    }
}
