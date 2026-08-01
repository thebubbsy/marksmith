using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace MarkSmith.Core.Preview
{
    public static class HtmlPreviewRenderer
    {
        public static string RenderHtml(AST.CanonicalAst ast, string layoutAlias, string layoutTitle = "SmartArt Diagram")
        {
            var dataNodes = ast.Root.Children.Count > 0 ? ast.Root.Children : new List<AST.AstNode> { ast.Root };

            int width = 800;
            int height = 500;
            string shapeType = "roundRect";

            string svgContent;
            string aliasLower = (layoutAlias ?? "").ToLower();

            if (aliasLower.Contains("hierarchy") || aliasLower.Contains("tree"))
            {
                svgContent = RenderHierarchy(dataNodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("cycle"))
            {
                svgContent = RenderCycle(dataNodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("matrix") || aliasLower.Contains("grid"))
            {
                svgContent = RenderGrid(dataNodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("pyramid"))
            {
                svgContent = RenderPyramid(dataNodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("venn"))
            {
                svgContent = RenderVenn(dataNodes, width, height);
            }
            else
            {
                svgContent = RenderLinear(dataNodes, width, height, shapeType);
            }

            return $@"
<div class=""smartart-container"" style=""width: 100%; max-width: 800px; height: 500px; background: #f8f9fa; border: 1px solid #e0e0e0; border-radius: 8px; position: relative; overflow: hidden; font-family: system-ui, -apple-system, sans-serif; box-shadow: 0 2px 8px rgba(0,0,0,0.05);"">
  <div style=""position: absolute; top: 10px; left: 10px; background: rgba(0,0,0,0.06); padding: 4px 10px; border-radius: 4px; font-size: 12px; font-weight: bold; color: #333;"">
    Layout: {WebUtility.HtmlEncode(layoutTitle)} ({layoutAlias})
  </div>
  {svgContent}
</div>";
        }

        private static string RenderHierarchy(List<AST.AstNode> level1, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");

            int y1 = 60, y2 = 220, y3 = 380;
            int n1 = level1.Count;
            double step1 = (double)w / Math.Max(1, n1 + 1);

            for (int i = 0; i < n1; i++)
            {
                var node = level1[i];
                int cx = (int)(step1 * (i + 1));
                int cy = y1;

                sb.Append(DrawShape(cx, cy, 140, 60, node.Text, shapeType, "#0078d4"));

                int n2 = node.Children.Count;
                if (n2 > 0)
                {
                    double childStep = 160;
                    double startX = cx - ((n2 - 1) * childStep) / 2;
                    for (int j = 0; j < n2; j++)
                    {
                        var child = node.Children[j];
                        int chX = (int)(startX + j * childStep);
                        int chY = y2;

                        sb.Append($@"<line x1=""{cx}"" y1=""{cy + 30}"" x2=""{chX}"" y2=""{chY - 30}"" stroke=""#0078d4"" stroke-width=""2""/>");
                        sb.Append(DrawShape(chX, chY, 130, 50, child.Text, shapeType, "#107c41"));

                        int n3 = child.Children.Count;
                        if (n3 > 0)
                        {
                            double gcStep = 110;
                            double gcStartX = chX - ((n3 - 1) * gcStep) / 2;
                            for (int k = 0; k < n3; k++)
                            {
                                var gc = child.Children[k];
                                int gcX = (int)(gcStartX + k * gcStep);
                                int gcY = y3;

                                sb.Append($@"<line x1=""{chX}"" y1=""{chY + 25}"" x2=""{gcX}"" y2=""{gcY - 25}"" stroke=""#107c41"" stroke-width=""1.5"" stroke-dasharray=""4""/>");
                                sb.Append(DrawShape(gcX, gcY, 100, 45, gc.Text, shapeType, "#5c2d91"));
                            }
                        }
                    }
                }
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderCycle(List<AST.AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            double cx = w / 2.0, cy = h / 2.0;
            double r = Math.Min(w, h) * 0.32;
            int n = nodes.Count;

            var coords = new List<(double x, double y, AST.AstNode node)>();
            for (int i = 0; i < n; i++)
            {
                double angle = (2 * Math.PI * i / Math.Max(1, n)) - (Math.PI / 2);
                double nx = cx + r * Math.Cos(angle);
                double ny = cy + r * Math.Sin(angle);
                coords.Add((nx, ny, nodes[i]));
            }

            for (int i = 0; i < n; i++)
            {
                var (x1, y1, _) = coords[i];
                var (x2, y2, _) = coords[(i + 1) % n];
                sb.Append($@"<line x1=""{x1}"" y1=""{y1}"" x2=""{x2}"" y2=""{y2}"" stroke=""#d13438"" stroke-width=""3"" marker-end=""url(#arrow)""/>");
            }

            sb.Append(@"<defs>
              <marker id=""arrow"" viewBox=""0 0 10 10"" refX=""5"" refY=""5"" markerWidth=""6"" markerHeight=""6"" orient=""auto-start-reverse"">
                <path d=""M 0 0 L 10 5 L 0 10 z"" fill=""#d13438""/>
              </marker>
            </defs>");

            string[] colors = { "#0078d4", "#107c41", "#d13438", "#ff8c00", "#5c2d91", "#008272" };
            for (int i = 0; i < coords.Count; i++)
            {
                var (nx, ny, node) = coords[i];
                string c = colors[i % colors.Length];
                sb.Append(DrawShape((int)nx, (int)ny, 110, 55, node.Text, shapeType, c));
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderGrid(List<AST.AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            int n = nodes.Count;
            int cols = (int)Math.Ceiling(Math.Sqrt(n));
            if (cols < 1) cols = 1;
            int rows = (int)Math.Ceiling((double)n / cols);
            if (rows < 1) rows = 1;

            double cellW = w / (double)cols;
            double cellH = h / (double)rows;
            string[] colors = { "#0078d4", "#107c41", "#5c2d91", "#ff8c00", "#d13438", "#008272" };

            for (int i = 0; i < n; i++)
            {
                int r = i / cols;
                int c = i % cols;
                double cx = (c + 0.5) * cellW;
                double cy = (r + 0.5) * cellH;
                string bg = colors[i % colors.Length];
                sb.Append(DrawShape((int)cx, (int)cy, (int)(cellW * 0.8), (int)(cellH * 0.75), nodes[i].Text, shapeType, bg));
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderPyramid(List<AST.AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            int n = nodes.Count;
            double layerH = (h - 80) / (double)Math.Max(1, n);
            double cx = w / 2.0;

            string[] colors = { "#d13438", "#ff8c00", "#107c41", "#0078d4", "#5c2d91" };

            for (int i = 0; i < n; i++)
            {
                double y = 40 + i * layerH;
                double topW = (i / (double)Math.Max(1, n)) * 400 + 100;
                double botW = ((i + 1) / (double)Math.Max(1, n)) * 400 + 100;

                string points = $"{cx - topW / 2},{y} {cx + topW / 2},{y} {cx + botW / 2},{y + layerH - 4} {cx - botW / 2},{y + layerH - 4}";
                string bg = colors[i % colors.Length];

                sb.Append($@"<polygon points=""{points}"" fill=""{bg}"" stroke=""#ffffff"" stroke-width=""2""/>");
                sb.Append($@"<text x=""{cx}"" y=""{y + layerH / 2}"" fill=""#ffffff"" font-weight=""bold"" font-size=""14"" text-anchor=""middle"" dominant-baseline=""middle"">{WebUtility.HtmlEncode(nodes[i].Text)}</text>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderVenn(List<AST.AstNode> nodes, int w, int h)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            int n = Math.Min(nodes.Count, 4);
            double cx = w / 2.0, cy = h / 2.0;
            double r = 110;
            string[] colors = { "rgba(0,120,212,0.6)", "rgba(16,124,65,0.6)", "rgba(209,52,56,0.6)", "rgba(255,140,0,0.6)" };
            (double ox, double oy)[] offsets = { (-60, -30), (60, -30), (0, 60), (0, -70) };

            for (int i = 0; i < n; i++)
            {
                var (ox, oy) = offsets[i % offsets.Length];
                double nx = cx + ox, ny = cy + oy;
                string c = colors[i % colors.Length];
                sb.Append($@"<circle cx=""{nx}"" cy=""{ny}"" r=""{r}"" fill=""{c}"" stroke=""#ffffff"" stroke-width=""2""/>");
                sb.Append($@"<text x=""{nx}"" y=""{ny}"" fill=""#ffffff"" font-weight=""bold"" font-size=""14"" text-anchor=""middle"" dominant-baseline=""middle"">{WebUtility.HtmlEncode(nodes[i].Text)}</text>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderLinear(List<AST.AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            int n = nodes.Count;
            double step = w / (double)Math.Max(1, n + 1);
            double cy = h / 2.0;

            string[] colors = { "#0078d4", "#107c41", "#ff8c00", "#d13438", "#5c2d91" };

            for (int i = 0; i < n; i++)
            {
                double cx = step * (i + 1);
                string bg = colors[i % colors.Length];

                if (i < n - 1)
                {
                    double nextX = step * (i + 2);
                    sb.Append($@"<line x1=""{cx}"" y1=""{cy}"" x2=""{nextX}"" y2=""{cy}"" stroke=""#0078d4"" stroke-width=""3""/>");
                }

                sb.Append(DrawShape((int)cx, (int)cy, 120, 60, nodes[i].Text, shapeType, bg));
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string DrawShape(int cx, int cy, int sw, int sh, string text, string shapeType, string bg)
        {
            int x = cx - sw / 2;
            int y = cy - sh / 2;
            string escText = WebUtility.HtmlEncode(text ?? "");

            int rx = (shapeType == "roundRect") ? 8 : 0;
            return $@"<g>
              <rect x=""{x}"" y=""{y}"" width=""{sw}"" height=""{sh}"" rx=""{rx}"" fill=""{bg}"" stroke=""#ffffff"" stroke-width=""2""/>
              <text x=""{cx}"" y=""{cy}"" fill=""#ffffff"" font-weight=""bold"" font-size=""13"" text-anchor=""middle"" dominant-baseline=""middle"">{escText}</text>
            </g>";
        }
    }
}
