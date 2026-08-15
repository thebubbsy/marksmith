using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using MarkSmith.Core.AST;

namespace MarkSmith.Core.Preview
{
    public static class HtmlPreviewRenderer
    {
        public static string RenderHtml(CanonicalAst ast, string layoutAlias, string layoutTitle = "SmartArt Diagram")
        {
            int width = 800;
            int height = 500;
            string shapeType = "roundRect";

            string svgContent;
            string aliasLower = (layoutAlias ?? "").ToLowerInvariant();

            if (aliasLower.Contains("hierarchy") || aliasLower.Contains("tree") || aliasLower.Contains("org"))
            {
                svgContent = RenderHierarchy(ast, width, height, shapeType);
            }
            else if (aliasLower.Contains("cycle"))
            {
                var nodes = FlattenNodes(ast.Root);
                svgContent = RenderCycle(nodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("matrix") || aliasLower.Contains("grid") || aliasLower.Contains("swot"))
            {
                svgContent = RenderMatrix(ast, width, height, shapeType);
            }
            else if (aliasLower.Contains("pyramid"))
            {
                var nodes = FlattenNodes(ast.Root);
                svgContent = RenderPyramid(nodes, width, height, shapeType);
            }
            else if (aliasLower.Contains("venn"))
            {
                var nodes = FlattenNodes(ast.Root);
                svgContent = RenderVenn(nodes, width, height);
            }
            else
            {
                var nodes = FlattenNodes(ast.Root);
                svgContent = RenderLinear(nodes, width, height, shapeType);
            }

            return $@"
<div class=""smartart-container"" style=""width: 100%; max-width: 800px; height: 500px; background: #f8f9fa; border: 1px solid #e0e0e0; border-radius: 8px; position: relative; overflow: hidden; font-family: system-ui, -apple-system, sans-serif; box-shadow: 0 2px 8px rgba(0,0,0,0.05);"">
  <div style=""position: absolute; top: 10px; left: 10px; background: rgba(0,0,0,0.06); padding: 4px 10px; border-radius: 4px; font-size: 12px; font-weight: bold; color: #333;"">
    Layout: {WebUtility.HtmlEncode(layoutTitle)} ({WebUtility.HtmlEncode(layoutAlias)})
  </div>
  {svgContent}
</div>";
        }

        private static List<AstNode> FlattenNodes(AstNode root)
        {
            var list = new List<AstNode>();
            void Walk(AstNode node)
            {
                if (node != root && !string.IsNullOrWhiteSpace(node.Text))
                {
                    list.Add(node);
                }
                foreach (var child in node.Children)
                {
                    Walk(child);
                }
            }
            Walk(root);
            return list.Count > 0 ? list : (root.Children.Count > 0 ? root.Children : new List<AstNode> { root });
        }

        private static string RenderPyramid(List<AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            
            int n = Math.Max(1, nodes.Count);
            double topMargin = 50;
            double botMargin = 30;
            double availableH = h - topMargin - botMargin;
            double layerH = availableH / n;
            double cx = w / 2.0;
            double maxBaseW = Math.Min(650, w - 80);
            double minApexW = 120;

            string[] colors = { "#d13438", "#ff8c00", "#107c41", "#0078d4", "#5c2d91", "#008272", "#8764b8", "#e3008c" };

            for (int i = 0; i < n; i++)
            {
                double yTop = topMargin + i * layerH;
                double yBot = yTop + layerH - 3;

                double topRatio = (double)i / n;
                double botRatio = (double)(i + 1) / n;

                double wTop = i == 0 ? minApexW : minApexW + topRatio * (maxBaseW - minApexW);
                double wBot = minApexW + botRatio * (maxBaseW - minApexW);

                double xTopL = cx - wTop / 2.0;
                double xTopR = cx + wTop / 2.0;
                double xBotL = cx - wBot / 2.0;
                double xBotR = cx + wBot / 2.0;

                string points = $"{xTopL:F1},{yTop:F1} {xTopR:F1},{yTop:F1} {xBotR:F1},{yBot:F1} {xBotL:F1},{yBot:F1}";
                string bg = colors[i % colors.Length];

                sb.Append($@"<polygon points=""{points}"" fill=""{bg}"" stroke=""#ffffff"" stroke-width=""2"" filter=""drop-shadow(0 1px 2px rgba(0,0,0,0.15))""/>");

                string label = nodes[i].Text ?? "";
                int fontSize = n > 7 ? 11 : (n > 5 ? 12 : 14);
                sb.Append($@"<text x=""{cx}"" y=""{yTop + layerH / 2.0}"" fill=""#ffffff"" font-weight=""bold"" font-size=""{fontSize}"" text-anchor=""middle"" dominant-baseline=""middle"">{WebUtility.HtmlEncode(label)}</text>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderHierarchy(CanonicalAst ast, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");

            var roots = ast.Root.Children.Count > 0 ? ast.Root.Children : new List<AstNode> { ast.Root };

            // Determine levels
            var level0 = roots;
            var level1 = level0.SelectMany(r => r.Children).ToList();
            var level2 = level1.SelectMany(c => c.Children).ToList();

            // One child→parent map instead of a FirstOrDefault(p => p.Children.Contains(child))
            // scan per node on each level (which was O(parents × children) per level).
            var parentOf = new Dictionary<AstNode, AstNode>(level1.Count + level2.Count);
            foreach (var p in level0) foreach (var c in p.Children) parentOf[c] = p;
            foreach (var p in level1) foreach (var c in p.Children) parentOf[c] = p;

            int y0 = 60;
            int y1 = level2.Count > 0 ? 210 : 300;
            int y2 = 380;

            // Draw Level 0
            double step0 = (double)w / (level0.Count + 1);
            var l0Positions = new Dictionary<AstNode, (int X, int Y)>();
            for (int i = 0; i < level0.Count; i++)
            {
                int cx = (int)(step0 * (i + 1));
                int cy = y0;
                l0Positions[level0[i]] = (cx, cy);
                sb.Append(DrawShape(cx, cy, 140, 50, level0[i].Text, shapeType, "#0078d4"));
            }

            // Draw Level 1
            if (level1.Count > 0)
            {
                double step1 = (double)(w - 40) / Math.Max(1, level1.Count);
                int cardW = Math.Max(70, Math.Min(120, (int)(step1 * 0.85)));
                var l1Positions = new Dictionary<AstNode, (int X, int Y)>();

                for (int i = 0; i < level1.Count; i++)
                {
                    var child = level1[i];
                    int chX = (int)(20 + (i + 0.5) * step1);
                    int chY = y1;
                    l1Positions[child] = (chX, chY);

                    // Find parent in level0
                    parentOf.TryGetValue(child, out var parent0);
                    var parent = parent0 ?? level0.FirstOrDefault();
                    if (parent != null && l0Positions.TryGetValue(parent, out var pPos))
                    {
                        sb.Append($@"<line x1=""{pPos.X}"" y1=""{pPos.Y + 25}"" x2=""{chX}"" y2=""{chY - 25}"" stroke=""#0078d4"" stroke-width=""2""/>");
                    }

                    sb.Append(DrawShape(chX, chY, cardW, 45, child.Text, shapeType, "#107c41"));
                }

                // Draw Level 2
                if (level2.Count > 0)
                {
                    double step2 = (double)(w - 40) / Math.Max(1, level2.Count);
                    int cardW2 = Math.Max(60, Math.Min(100, (int)(step2 * 0.85)));

                    for (int k = 0; k < level2.Count; k++)
                    {
                        var gc = level2[k];
                        int gcX = (int)(20 + (k + 0.5) * step2);
                        int gcY = y2;

                        parentOf.TryGetValue(gc, out var parent1);
                        var parentL1 = parent1 ?? level1.FirstOrDefault();
                        if (parentL1 != null && l1Positions.TryGetValue(parentL1, out var pPos))
                        {
                            sb.Append($@"<line x1=""{pPos.X}"" y1=""{pPos.Y + 22}"" x2=""{gcX}"" y2=""{gcY - 20}"" stroke=""#107c41"" stroke-width=""1.5"" stroke-dasharray=""3""/>");
                        }

                        sb.Append(DrawShape(gcX, gcY, cardW2, 40, gc.Text, shapeType, "#5c2d91"));
                    }
                }
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderCycle(List<AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            double cx = w / 2.0, cy = h / 2.0;
            double r = Math.Min(w, h) * 0.32;
            int n = nodes.Count;

            var coords = new List<(double x, double y, AstNode node)>();
            for (int i = 0; i < n; i++)
            {
                double angle = (2 * Math.PI * i / Math.Max(1, n)) - (Math.PI / 2);
                double nx = cx + r * Math.Cos(angle);
                double ny = cy + r * Math.Sin(angle);
                coords.Add((nx, ny, nodes[i]));
            }

            sb.Append(@"<defs>
  <marker id=""arrow"" viewBox=""0 0 10 10"" refX=""5"" refY=""5"" markerWidth=""6"" markerHeight=""6"" orient=""auto-start-reverse"">
    <path d=""M 0 0 L 10 5 L 0 10 z"" fill=""#d13438""/>
  </marker>
</defs>");

            for (int i = 0; i < n; i++)
            {
                var (x1, y1, _) = coords[i];
                var (x2, y2, _) = coords[(i + 1) % n];
                sb.Append($@"<line x1=""{x1}"" y1=""{y1}"" x2=""{x2}"" y2=""{y2}"" stroke=""#d13438"" stroke-width=""2.5"" stroke-dasharray=""4"" marker-end=""url(#arrow)""/>");
            }

            string[] colors = { "#0078d4", "#107c41", "#d13438", "#ff8c00", "#5c2d91", "#008272" };
            int cardW = n > 5 ? 90 : 110;
            int cardH = n > 5 ? 45 : 55;

            for (int i = 0; i < coords.Count; i++)
            {
                var (nx, ny, node) = coords[i];
                string c = colors[i % colors.Length];
                sb.Append(DrawShape((int)nx, (int)ny, cardW, cardH, node.Text, shapeType, c));
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderMatrix(CanonicalAst ast, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");

            var nodes = ast.Root.Children.Count >= 2 ? ast.Root.Children : FlattenNodes(ast.Root);
            int n = nodes.Count;

            int cols = 2;
            int rows = 2;
            double pad = 30;
            double cellW = (w - pad * 3) / 2.0;
            double cellH = (h - pad * 3 - 30) / 2.0;

            string[] colors = { "#0078d4", "#107c41", "#d13438", "#ff8c00" };
            string[] defaultTitles = { "Strengths / Q1", "Weaknesses / Q2", "Opportunities / Q3", "Threats / Q4" };

            for (int i = 0; i < Math.Min(4, n); i++)
            {
                int r = i / cols;
                int c = i % cols;
                double x = pad + c * (cellW + pad);
                double y = 40 + pad + r * (cellH + pad);
                string bg = colors[i % colors.Length];

                var node = nodes[i];
                string title = !string.IsNullOrWhiteSpace(node.Text) ? node.Text : defaultTitles[i];

                sb.Append($@"<g>
  <rect x=""{x}"" y=""{y}"" width=""{cellW}"" height=""{cellH}"" rx=""8"" fill=""{bg}"" stroke=""#ffffff"" stroke-width=""2"" opacity=""0.9""/>
  <text x=""{x + 15}"" y=""{y + 25}"" fill=""#ffffff"" font-weight=""bold"" font-size=""14"">{WebUtility.HtmlEncode(title)}</text>");

                if (node.Children.Count > 0)
                {
                    for (int j = 0; j < Math.Min(4, node.Children.Count); j++)
                    {
                        double bulletY = y + 50 + j * 20;
                        sb.Append($@"<text x=""{x + 20}"" y=""{bulletY}"" fill=""#f0f0f0"" font-size=""12"">• {WebUtility.HtmlEncode(node.Children[j].Text)}</text>");
                    }
                }

                sb.Append("</g>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderLinear(List<AstNode> nodes, int w, int h, string shapeType)
        {
            var sb = new StringBuilder();
            sb.Append($@"<svg width=""100%"" height=""100%"" viewBox=""0 0 {w} {h}"" xmlns=""http://www.w3.org/2000/svg"">");
            int n = Math.Max(1, nodes.Count);
            double pad = 40;
            double step = (w - pad * 2) / (double)n;
            double cy = h / 2.0;

            int cardW = Math.Max(60, Math.Min(120, (int)(step * 0.75)));
            int cardH = 55;

            string[] colors = { "#0078d4", "#107c41", "#ff8c00", "#d13438", "#5c2d91", "#008272" };

            for (int i = 0; i < n; i++)
            {
                double cx = pad + (i + 0.5) * step;
                string bg = colors[i % colors.Length];

                if (i < n - 1)
                {
                    double nextX = pad + (i + 1.5) * step;
                    sb.Append($@"<line x1=""{cx + cardW / 2.0}"" y1=""{cy}"" x2=""{nextX - cardW / 2.0}"" y2=""{cy}"" stroke=""#0078d4"" stroke-width=""3""/>");
                }

                sb.Append(DrawShape((int)cx, (int)cy, cardW, cardH, nodes[i].Text, shapeType, bg));
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string RenderVenn(List<AstNode> nodes, int w, int h)
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
                sb.Append($@"<text x=""{nx}"" y=""{ny}"" fill=""#ffffff"" font-weight=""bold"" font-size=""13"" text-anchor=""middle"" dominant-baseline=""middle"">{WebUtility.HtmlEncode(nodes[i].Text)}</text>");
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        private static string DrawShape(int cx, int cy, int sw, int sh, string text, string shapeType, string bg)
        {
            int x = cx - sw / 2;
            int y = cy - sh / 2;
            string escText = WebUtility.HtmlEncode(text ?? "");

            int rx = (shapeType == "roundRect") ? 6 : 0;
            return $@"<g>
  <rect x=""{x}"" y=""{y}"" width=""{sw}"" height=""{sh}"" rx=""{rx}"" fill=""{bg}"" stroke=""#ffffff"" stroke-width=""1.5"" filter=""drop-shadow(0 1px 2px rgba(0,0,0,0.12))""/>
  <text x=""{cx}"" y=""{cy}"" fill=""#ffffff"" font-weight=""bold"" font-size=""12"" text-anchor=""middle"" dominant-baseline=""middle"">{escText}</text>
</g>";
        }
    }
}
