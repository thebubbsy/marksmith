using System;
using System.Collections.Generic;
using MarkSmith.Core.AST;

namespace MarkSmith.Core.Mosaic
{
    public class RasterMosaicOptions
    {
        public int GridWidth { get; set; } = 8;
        public int GridHeight { get; set; } = 8;
        public int PaletteColors { get; set; } = 16;
        public bool EnableDithering { get; set; } = true;
        public string TargetLayout { get; set; } = "picturelist";
    }

    public static class RasterMosaicEngine
    {
        public static CanonicalAst GenerateMosaicAst(string imagePath, RasterMosaicOptions options)
        {
            var ast = new CanonicalAst
            {
                RequestedLayout = options.TargetLayout
            };

            int totalNodes = options.GridWidth * options.GridHeight;
            int counter = 1;

            for (int y = 0; y < options.GridHeight; y++)
            {
                for (int x = 0; x < options.GridWidth; x++)
                {
                    string hexColor = QuantizeSampleColor(x, y, options.GridWidth, options.GridHeight);

                    var node = new AstNode
                    {
                        NodeId = $"mosaic_{x}_{y}",
                        Depth = 1,
                        ParentId = ast.Root.NodeId,
                        NodeType = AstNodeType.Image,
                        Text = $"({x},{y})",
                        ImagePath = imagePath,
                        SemanticTags = new List<string> { "mosaic", "picture" }
                    };

                    node.Attributes["hexColor"] = hexColor;
                    node.Attributes["gridX"] = x.ToString();
                    node.Attributes["gridY"] = y.ToString();

                    ast.Root.Children.Add(node);
                }
            }

            return ast;
        }

        private static string QuantizeSampleColor(int x, int y, int w, int h)
        {
            // Simple deterministic palette color generator based on grid coords
            byte r = (byte)((x * 255) / Math.Max(1, w - 1));
            byte g = (byte)((y * 255) / Math.Max(1, h - 1));
            byte b = (byte)(255 - r);
            return $"{r:X2}{g:X2}{b:X2}";
        }
    }
}
