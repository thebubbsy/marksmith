using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MarkSmith.Core.Composer;
using MarkSmith.Mermaid.Ast;
using MarkSmith.Mermaid.Generator;
using MarkSmith.Services;
using SkiaSharp;

namespace MarkSmith.Ocr;

/// <summary>Options controlling how visual elements (pictures, structured diagrams, and shape mosaics)
/// are extracted from a scanned page during OCR.</summary>
public sealed class OcrVisualOptions
{
    public int PageNumber { get; init; } = 1;
    public string? MediaDirectory { get; init; }
    public string? MediaLink { get; init; }
    public int StartingPictureCount { get; init; }
    /// <summary>When true, non-diagram graphic regions are always composed into native Word shape
    /// mosaics (<c>:::shapes compact=true</c>) rather than saved as external PNG files.</summary>
    public bool PreferShapeMosaicForPictures { get; init; }
}

/// <summary>The unified reading-order blocks (text + pictures + diagrams + shape mosaics) extracted from a scanned page.</summary>
public sealed record OcrVisualExtractionResult(
    IReadOnlyList<OcrMarkdownBlock> Blocks,
    string Markdown,
    int PicturesSaved);

/// <summary>
/// Inspects a deskewed OCR page bitmap alongside the OCR engine's recognised words to extract:
/// <list type="bullet">
/// <item><description><see cref="OcrBlockKind.Diagram"/>: Box-and-connector flowcharts reconstructed via
/// <see cref="FlowchartDiagramAst"/> / <see cref="MermaidCodeGenerator"/> and ShapeForge's
/// <see cref="ComposedShape"/> / <see cref="ShapeMarkdownCodec"/> as native Word shapes.</description></item>
/// <item><description><see cref="OcrBlockKind.ShapeMosaic"/>: Illustrations and vector-style graphics
/// composed into multi-shape Word mosaics via <see cref="ImageShapeComposer"/>.</description></item>
/// <item><description><see cref="OcrBlockKind.Picture"/>: Continuous-tone photographs cropped and saved
/// to the document's media directory as Markdown image links.</description></item>
/// </list>
/// Pure-text pages are left untouched so character accuracy on text scans is 100% preserved.
/// </summary>
public static class OcrVisualElements
{
    private const int MaxGridEdge = 640;

    public static OcrVisualExtractionResult ExtractPage(
        SKBitmap? page,
        OcrPageResult recognized,
        OcrVisualOptions? options = null)
    {
        options ??= new OcrVisualOptions();
        if (page is null || page.Width < 64 || page.Height < 64)
        {
            var textOnly = OcrMarkdown.Blocks(recognized);
            return new OcrVisualExtractionResult(
                textOnly,
                string.Join("\n\n", textOnly.Select(b => b.Markdown)).Trim(),
                0);
        }

        int pageW = page.Width;
        int pageH = page.Height;
        var nonEmptyLines = recognized.Lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        float bodyHeight = nonEmptyLines.Count > 0
            ? Math.Max(12f, OcrLayout.Median(nonEmptyLines.Select(l => l.Height)))
            : 20f;

        // 1. Build downsampled analysis grid so connected-component analysis runs in a few milliseconds.
        float scale = Math.Min(1.0f, MaxGridEdge / (float)Math.Max(pageW, pageH));
        int gw = Math.Max(32, (int)Math.Round(pageW * scale));
        int gh = Math.Max(32, (int)Math.Round(pageH * scale));
        float invScaleX = pageW / (float)gw;
        float invScaleY = pageH / (float)gh;

        var rGrid = new byte[gw * gh];
        var gGrid = new byte[gw * gh];
        var bGrid = new byte[gw * gh];
        var lumGrid = new byte[gw * gh];
        var satGrid = new byte[gw * gh];

        SampleBitmapToGrid(page, gw, gh, rGrid, gGrid, bGrid, lumGrid, satGrid);
        byte bgLum = EstimateBackgroundLuminance(lumGrid, gw, gh);
        int fgLumThreshold = Math.Max(40, bgLum - 38);

        // Mark cells covered by recognised OCR words (with a 2px page-space margin).
        var wordMask = new bool[gw * gh];
        var allWords = recognized.Lines.SelectMany(l => l.Words).Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        foreach (var w in allWords)
        {
            int x0 = Math.Clamp((int)Math.Floor((w.X - 2f) * scale), 0, gw - 1);
            int y0 = Math.Clamp((int)Math.Floor((w.Y - 2f) * scale), 0, gh - 1);
            int x1 = Math.Clamp((int)Math.Ceiling((w.X + w.Width + 2f) * scale), 0, gw - 1);
            int y1 = Math.Clamp((int)Math.Ceiling((w.Y + w.Height + 2f) * scale), 0, gh - 1);
            for (int y = y0; y <= y1; y++)
            {
                int row = y * gw;
                for (int x = x0; x <= x1; x++)
                    wordMask[row + x] = true;
            }
        }

        // Foreground pixels that are NOT part of recognised word glyphs.
        var nonWordFg = new bool[gw * gh];
        for (int i = 0; i < nonWordFg.Length; i++)
        {
            bool isFg = lumGrid[i] < fgLumThreshold || (satGrid[i] > 32 && lumGrid[i] < bgLum - 14);
            nonWordFg[i] = isFg && !wordMask[i];
        }

        // Ignore thin scanner shadow rules along the outer 1.5% border of the page.
        int marginX = Math.Max(1, (int)(gw * 0.015f));
        int marginY = Math.Max(1, (int)(gh * 0.015f));
        for (int y = 0; y < gh; y++)
        {
            int row = y * gw;
            for (int x = 0; x < gw; x++)
            {
                if (x < marginX || x >= gw - marginX || y < marginY || y >= gh - marginY)
                    nonWordFg[row + x] = false;
            }
        }

        var visualBlocks = new List<OcrMarkdownBlock>();
        var claimedWords = new HashSet<OcrWord>();
        var claimedGrid = new bool[gw * gh];

        // 2. Phase A: Detect structured box-and-connector diagrams (Mermaid + ShapeForge).
        var diagrams = DetectStructuredDiagrams(
            gw, gh, invScaleX, invScaleY, bodyHeight, pageW, pageH,
            nonWordFg, rGrid, gGrid, bGrid, satGrid,
            recognized.Lines, claimedWords, claimedGrid);
        visualBlocks.AddRange(diagrams);

        // 3. Phase B: Detect non-text picture & shape-mosaic regions on the remaining canvas.
        int picturesSaved = DetectPicturesAndMosaics(
            page, gw, gh, invScaleX, invScaleY, bodyHeight, pageW, pageH,
            nonWordFg, satGrid, claimedGrid,
            allWords, claimedWords, options, visualBlocks);

        // 4. Phase C: Build text blocks from remaining unclaimed OCR lines and merge in reading order.
        if (visualBlocks.Count == 0)
        {
            var plainBlocks = OcrMarkdown.Blocks(recognized);
            return new OcrVisualExtractionResult(
                plainBlocks,
                string.Join("\n\n", plainBlocks.Select(b => b.Markdown)).Trim(),
                0);
        }

        var remainingLines = new List<OcrLine>();
        foreach (var line in recognized.Lines)
        {
            var keptWords = line.Words.Where(w => !claimedWords.Contains(w)).ToList();
            if (keptWords.Count == 0) continue;
            if (keptWords.Count == line.Words.Count)
            {
                remainingLines.Add(line);
            }
            else
            {
                float ly = keptWords.Min(w => w.Y);
                float lh = keptWords.Max(w => w.Y + w.Height) - ly;
                string ltext = string.Join(" ", keptWords.Select(w => w.Text));
                remainingLines.Add(new OcrLine(ltext, keptWords, ly, lh));
            }
        }

        var filteredPage = new OcrPageResult(
            remainingLines,
            recognized.ImageWidth,
            recognized.ImageHeight);

        var textBlocks = OcrMarkdown.Blocks(filteredPage);
        var combined = textBlocks
            .Concat(visualBlocks)
            .OrderBy(b => b.Bounds.IsEmpty ? float.MaxValue : b.Bounds.Top)
            .ThenBy(b => b.Bounds.IsEmpty ? 0f : b.Bounds.Left)
            .ToList();

        string markdown = string.Join("\n\n", combined.Select(b => b.Markdown).Where(m => !string.IsNullOrWhiteSpace(m))).Trim();
        return new OcrVisualExtractionResult(combined, markdown, picturesSaved);
    }

    // ---- Phase A: Structured Diagram Detection & Mermaid + ShapeForge Reconstruction ------------

    private sealed class DetectedNode
    {
        public int GridX0, GridY0, GridX1, GridY1;
        public SKRect PageBounds;
        public string ShapePrst = "roundRect";
        public FlowNodeShape MermaidShape = FlowNodeShape.RoundedRectangle;
        public string FillHex = "2563EB";
        public string TextColorHex = "FFFFFF";
        public string Label = string.Empty;
        public List<OcrWord> Words = new();
        public float CenterX => PageBounds.MidX;
        public float CenterY => PageBounds.MidY;
    }

    private static List<OcrMarkdownBlock> DetectStructuredDiagrams(
        int gw, int gh, float invScaleX, float invScaleY, float bodyHeight, int pageW, int pageH,
        bool[] nonWordFg, byte[] rGrid, byte[] gGrid, byte[] bGrid, byte[] satGrid,
        IReadOnlyList<OcrLine> lines,
        HashSet<OcrWord> claimedWords,
        bool[] claimedGrid)
    {
        var results = new List<OcrMarkdownBlock>();

        // Close 1-cell gaps in stroke outlines so enclosed box cavities are watertight.
        var closedStroke = Dilate(nonWordFg, gw, gh, 1);
        closedStroke = Erode(closedStroke, gw, gh, 1);

        // Flood-fill background from the outer boundary of the grid.
        var exterior = new bool[gw * gh];
        var queue = new Queue<int>();
        for (int x = 0; x < gw; x++)
        {
            int top = x;
            int bot = (gh - 1) * gw + x;
            if (!closedStroke[top]) { exterior[top] = true; queue.Enqueue(top); }
            if (!closedStroke[bot]) { exterior[bot] = true; queue.Enqueue(bot); }
        }
        for (int y = 1; y < gh - 1; y++)
        {
            int left = y * gw;
            int right = y * gw + (gw - 1);
            if (!closedStroke[left]) { exterior[left] = true; queue.Enqueue(left); }
            if (!closedStroke[right]) { exterior[right] = true; queue.Enqueue(right); }
        }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            int cx = cur % gw, cy = cur / gw;
            if (cx > 0) TryEnqueueLight(cur - 1);
            if (cx + 1 < gw) TryEnqueueLight(cur + 1);
            if (cy > 0) TryEnqueueLight(cur - gw);
            if (cy + 1 < gh) TryEnqueueLight(cur + gw);
        }
        void TryEnqueueLight(int idx)
        {
            if (!exterior[idx] && !closedStroke[idx])
            {
                exterior[idx] = true;
                queue.Enqueue(idx);
            }
        }

        // Find enclosed interior cavities (hollow rectangles, rounded rects, diamonds, ellipses).
        var visited = new bool[gw * gh];
        var candidateNodes = new List<DetectedNode>();
        float minNodeW = Math.Max(46f, bodyHeight * 1.6f);
        float minNodeH = Math.Max(24f, bodyHeight * 1.05f);

        for (int y = 1; y < gh - 1; y++)
        {
            int row = y * gw;
            for (int x = 1; x < gw - 1; x++)
            {
                int start = row + x;
                if (closedStroke[start] || exterior[start] || visited[start]) continue;

                // BFS this enclosed cavity
                int minX = x, maxX = x, minY = y, maxY = y;
                int cellCount = 0;
                var cells = new List<int>();
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    cells.Add(cur);
                    cellCount++;
                    int cx = cur % gw, cy = cur / gw;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    if (cx > 0) TryVisitCavity(cur - 1);
                    if (cx + 1 < gw) TryVisitCavity(cur + 1);
                    if (cy > 0) TryVisitCavity(cur - gw);
                    if (cy + 1 < gh) TryVisitCavity(cur + gw);
                }
                void TryVisitCavity(int idx)
                {
                    if (!visited[idx] && !closedStroke[idx] && !exterior[idx])
                    {
                        visited[idx] = true;
                        queue.Enqueue(idx);
                    }
                }

                int cw = maxX - minX + 1;
                int ch = maxY - minY + 1;
                if (cw < 6 || ch < 4) continue;

                // Expand by 2 grid cells to include the enclosing stroke border.
                int bx0 = Math.Max(0, minX - 2);
                int by0 = Math.Max(0, minY - 2);
                int bx1 = Math.Min(gw - 1, maxX + 2);
                int by1 = Math.Min(gh - 1, maxY + 2);

                float pageX0 = bx0 * invScaleX;
                float pageY0 = by0 * invScaleY;
                float pageX1 = (bx1 + 1) * invScaleX;
                float pageY1 = (by1 + 1) * invScaleY;
                float pw = pageX1 - pageX0;
                float ph = pageY1 - pageY0;

                if (pw < minNodeW || ph < minNodeH) continue;
                if (pw > pageW * 0.86f || ph > pageH * 0.60f) continue;
                float aspect = pw / Math.Max(1f, ph);
                if (aspect < 0.35f || aspect > 8.5f) continue;

                float compactness = cellCount / (float)(cw * ch);
                if (compactness < 0.36f) continue;

                // Check that the interior isn't filled with non-word clutter (e.g. a framed photo).
                int interiorNonWordFg = 0;
                for (int iy = minY + 1; iy <= maxY - 1; iy++)
                {
                    int irow = iy * gw;
                    for (int ix = minX + 1; ix <= maxX - 1; ix++)
                        if (nonWordFg[irow + ix]) interiorNonWordFg++;
                }
                float interiorNoise = interiorNonWordFg / (float)Math.Max(1, (cw - 2) * (ch - 2));
                if (interiorNoise > 0.22f) continue;

                // Classify shape geometry (diamond vs ellipse vs roundRect vs rect) from corner occupancy.
                var (prst, mShape) = ClassifyCavityShape(cells, minX, minY, cw, ch, compactness, aspect, gw);
                var (fillHex, textHex) = SampleNodeColors(bx0, by0, bx1, by1, gw, closedStroke, rGrid, gGrid, bGrid, satGrid);

                candidateNodes.Add(new DetectedNode
                {
                    GridX0 = bx0,
                    GridY0 = by0,
                    GridX1 = bx1,
                    GridY1 = by1,
                    PageBounds = new SKRect(pageX0, pageY0, pageX1, pageY1),
                    ShapePrst = prst,
                    MermaidShape = mShape,
                    FillHex = fillHex,
                    TextColorHex = textHex
                });
            }
        }

        // Deduplicate nested/overlapping candidate nodes (keep outer node when one is inside another).
        candidateNodes = DeduplicateNodes(candidateNodes);
        if (candidateNodes.Count < 2) return results;

        // Group candidate nodes into spatially connected diagram clusters.
        float maxClusterGap = Math.Max(160f, Math.Min(pageW, pageH) * 0.26f);
        var clusters = ClusterNodes(candidateNodes, maxClusterGap);

        var allLines = lines.Where(l => l.Words.Count > 0).ToList();
        foreach (var cluster in clusters)
        {
            if (cluster.Count < 2) continue;

            // Assign OCR words inside each node as its label.
            var clusterLines = new List<OcrLine>();
            foreach (var node in cluster)
            {
                var insideWords = new List<OcrWord>();
                foreach (var line in allLines)
                {
                    foreach (var w in line.Words)
                    {
                        if (claimedWords.Contains(w)) continue;
                        float wx = w.X + w.Width * 0.5f;
                        float wy = w.Y + w.Height * 0.5f;
                        if (wx >= node.PageBounds.Left - 6f && wx <= node.PageBounds.Right + 6f &&
                            wy >= node.PageBounds.Top - 6f && wy <= node.PageBounds.Bottom + 6f)
                        {
                            insideWords.Add(w);
                        }
                    }
                }
                node.Words = insideWords
                    .OrderBy(w => Math.Round(w.Y / Math.Max(6f, bodyHeight * 0.45f)))
                    .ThenBy(w => w.X)
                    .ToList();
                node.Label = string.Join(" ", node.Words.Select(w => w.Text.Trim())).Trim();
            }

            // Compute cluster bounding box in grid and page coordinates.
            int gx0 = Math.Max(0, cluster.Min(n => n.GridX0) - 6);
            int gy0 = Math.Max(0, cluster.Min(n => n.GridY0) - 6);
            int gx1 = Math.Min(gw - 1, cluster.Max(n => n.GridX1) + 6);
            int gy1 = Math.Min(gh - 1, cluster.Max(n => n.GridY1) + 6);

            // Detect connector edges between nodes in this cluster.
            var edges = DetectConnectorEdges(cluster, nonWordFg, gw, gh, gx0, gy0, gx1, gy1);

            // Require either at least one node with text OR at least one detected connector stroke
            // so two random empty boxes on a form without text or connectors aren't misread.
            if (cluster.All(n => string.IsNullOrWhiteSpace(n.Label)) && edges.Count == 0)
                continue;

            // Claim all words inside the diagram nodes (and any short edge labels inside the diagram box).
            var diagramBounds = new SKRect(gx0 * invScaleX, gy0 * invScaleY, (gx1 + 1) * invScaleX, (gy1 + 1) * invScaleY);
            foreach (var node in cluster)
            {
                foreach (var w in node.Words)
                    claimedWords.Add(w);
                if (node.Words.Count > 0)
                {
                    float ly = node.Words.Min(w => w.Y);
                    float lh = node.Words.Max(w => w.Y + w.Height) - ly;
                    clusterLines.Add(new OcrLine(node.Label, node.Words, ly, lh));
                }
            }

            // Also check for short unclaimed edge labels sitting near edge midpoints inside diagramBounds.
            foreach (var line in allLines)
            {
                var unclaimedInLine = line.Words.Where(w => !claimedWords.Contains(w)).ToList();
                if (unclaimedInLine.Count is 0 or > 4) continue;
                float lx = unclaimedInLine.Average(w => w.X + w.Width * 0.5f);
                float ly = unclaimedInLine.Average(w => w.Y + w.Height * 0.5f);
                if (!diagramBounds.Contains(lx, ly)) continue;

                string labelText = string.Join(" ", unclaimedInLine.Select(w => w.Text.Trim())).Trim();
                int bestEdge = -1;
                float bestDist = float.MaxValue;
                for (int e = 0; e < edges.Count; e++)
                {
                    var from = cluster[edges[e].From];
                    var to = cluster[edges[e].To];
                    float mx = (from.CenterX + to.CenterX) * 0.5f;
                    float my = (from.CenterY + to.CenterY) * 0.5f;
                    float dist = MathF.Sqrt((lx - mx) * (lx - mx) + (ly - my) * (ly - my));
                    if (dist < bestDist && dist < Math.Max(diagramBounds.Width, diagramBounds.Height) * 0.25f)
                    {
                        bestDist = dist;
                        bestEdge = e;
                    }
                }
                if (bestEdge >= 0 && string.IsNullOrEmpty(edges[bestEdge].Label))
                {
                    edges[bestEdge] = (edges[bestEdge].From, edges[bestEdge].To, labelText);
                    foreach (var w in unclaimedInLine) claimedWords.Add(w);
                    clusterLines.Add(new OcrLine(labelText, unclaimedInLine, line.Y, line.Height));
                }
            }

            // Mark the diagram's grid area as claimed so Phase B won't re-detect it as a picture.
            for (int y = gy0; y <= gy1; y++)
            {
                int row = y * gw;
                for (int x = gx0; x <= gx1; x++)
                    claimedGrid[row + x] = true;
            }

            string diagramMd = BuildDiagramShapesAndMermaidMarkdown(cluster, edges, diagramBounds);
            results.Add(new OcrMarkdownBlock(diagramMd, OcrBlockKind.Diagram, clusterLines, diagramBounds));
        }

        return results;
    }

    private static (string Prst, FlowNodeShape MermaidShape) ClassifyCavityShape(
        List<int> cells, int minX, int minY, int cw, int ch, float compactness, float aspect, int gw)
    {
        // Measure occupancy in the 4 corners (outer 22% on each axis) of the cavity bounding box.
        int cornerTotal = 0, cornerFilled = 0;
        float xCut = cw * 0.22f, yCut = ch * 0.22f;
        var cellSet = new HashSet<int>(cells);
        for (int dy = 0; dy < ch; dy++)
        {
            bool inCornerY = dy < yCut || dy >= ch - yCut;
            if (!inCornerY) continue;
            for (int dx = 0; dx < cw; dx++)
            {
                bool inCornerX = dx < xCut || dx >= cw - xCut;
                if (!inCornerX) continue;
                cornerTotal++;
                if (cellSet.Contains((minY + dy) * gw + (minX + dx)))
                    cornerFilled++;
            }
        }
        float cornerRatio = cornerTotal > 0 ? cornerFilled / (float)cornerTotal : 1f;

        if (compactness < 0.64f && cornerRatio < 0.20f)
            return ("diamond", FlowNodeShape.RhombusDiamond);
        if (cornerRatio < 0.58f && compactness < 0.86f)
        {
            if (aspect >= 0.72f && aspect <= 1.38f)
                return ("ellipse", FlowNodeShape.Circle);
            return ("roundRect", FlowNodeShape.RoundedRectangle);
        }
        return ("roundRect", FlowNodeShape.Rectangle);
    }

    private static (string FillHex, string TextColorHex) SampleNodeColors(
        int bx0, int by0, int bx1, int by1, int gw,
        bool[] closedStroke, byte[] rGrid, byte[] gGrid, byte[] bGrid, byte[] satGrid)
    {
        long sr = 0, sg = 0, sb = 0, count = 0;
        for (int y = by0; y <= by1; y++)
        {
            int row = y * gw;
            for (int x = bx0; x <= bx1; x++)
            {
                int idx = row + x;
                if (satGrid[idx] > 28)
                {
                    sr += rGrid[idx];
                    sg += gGrid[idx];
                    sb += bGrid[idx];
                    count++;
                }
            }
        }
        if (count >= 6)
        {
            int r = (int)(sr / count), g = (int)(sg / count), b = (int)(sb / count);
            string hex = $"{r:X2}{g:X2}{b:X2}";
            double lum = 0.299 * r + 0.587 * g + 0.114 * b;
            return (hex, lum > 155 ? "0F172A" : "FFFFFF");
        }
        // Clean monochrome scan: give the reconstructed Word shape a crisp slate-blue fill.
        return ("2563EB", "FFFFFF");
    }

    private static List<DetectedNode> DeduplicateNodes(List<DetectedNode> nodes)
    {
        var sorted = nodes.OrderByDescending(n => n.PageBounds.Width * n.PageBounds.Height).ToList();
        var kept = new List<DetectedNode>();
        foreach (var cand in sorted)
        {
            bool insideExisting = kept.Any(k =>
            {
                float ix0 = Math.Max(k.PageBounds.Left, cand.PageBounds.Left);
                float iy0 = Math.Max(k.PageBounds.Top, cand.PageBounds.Top);
                float ix1 = Math.Min(k.PageBounds.Right, cand.PageBounds.Right);
                float iy1 = Math.Min(k.PageBounds.Bottom, cand.PageBounds.Bottom);
                if (ix1 <= ix0 || iy1 <= iy0) return false;
                float inter = (ix1 - ix0) * (iy1 - iy0);
                float candArea = Math.Max(1f, cand.PageBounds.Width * cand.PageBounds.Height);
                return inter / candArea > 0.65f;
            });
            if (!insideExisting)
                kept.Add(cand);
        }
        return kept
            .OrderBy(n => n.PageBounds.Top)
            .ThenBy(n => n.PageBounds.Left)
            .ToList();
    }

    private static List<List<DetectedNode>> ClusterNodes(List<DetectedNode> nodes, float maxGap)
    {
        int n = nodes.Count;
        var visited = new bool[n];
        var clusters = new List<List<DetectedNode>>();
        for (int i = 0; i < n; i++)
        {
            if (visited[i]) continue;
            var cluster = new List<DetectedNode>();
            var q = new Queue<int>();
            visited[i] = true;
            q.Enqueue(i);
            while (q.Count > 0)
            {
                int cur = q.Dequeue();
                cluster.Add(nodes[cur]);
                for (int j = 0; j < n; j++)
                {
                    if (visited[j]) continue;
                    if (RectDistance(nodes[cur].PageBounds, nodes[j].PageBounds) <= maxGap)
                    {
                        visited[j] = true;
                        q.Enqueue(j);
                    }
                }
            }
            clusters.Add(cluster
                .OrderBy(x => Math.Round(x.PageBounds.Top / 24.0))
                .ThenBy(x => x.PageBounds.Left)
                .ToList());
        }
        return clusters;
    }

    private static float RectDistance(SKRect a, SKRect b)
    {
        float dx = Math.Max(0f, Math.Max(a.Left - b.Right, b.Left - a.Right));
        float dy = Math.Max(0f, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static List<(int From, int To, string? Label)> DetectConnectorEdges(
        List<DetectedNode> nodes, bool[] nonWordFg, int gw, int gh, int gx0, int gy0, int gx1, int gy1)
    {
        var edges = new List<(int From, int To, string? Label)>();
        var edgeSet = new HashSet<(int, int)>();

        // For each pair of nodes, sample the corridor outside the two node interiors to see if
        // connector stroke pixels bridge them without passing through a third node.
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var a = nodes[i];
                var b = nodes[j];

                // Reject if another node sits directly between a and b.
                bool blocked = false;
                for (int k = 0; k < nodes.Count; k++)
                {
                    if (k == i || k == j) continue;
                    if (SegmentIntersectsRect(a.CenterX, a.CenterY, b.CenterX, b.CenterY, nodes[k].PageBounds))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked) continue;

                if (HasConnectorCorridor(a, b, nonWordFg, gw, gh))
                {
                    // Orient edge in reading/flow order (top-to-bottom or left-to-right).
                    bool aFirst = a.CenterY + a.PageBounds.Height * 0.35f < b.CenterY
                        || (Math.Abs(a.CenterY - b.CenterY) <= Math.Max(a.PageBounds.Height, b.PageBounds.Height) * 0.7f && a.CenterX <= b.CenterX);
                    int from = aFirst ? i : j;
                    int to = aFirst ? j : i;
                    if (edgeSet.Add((from, to)))
                        edges.Add((from, to, null));
                }
            }
        }

        // If nodes are arranged in a clear sequence with subtle/dashed connectors, link sequential neighbours
        // that have stroke pixels in the gap between them.
        if (edges.Count == 0 && nodes.Count >= 2)
        {
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                edges.Add((i, i + 1, null));
            }
        }

        return edges;
    }

    private static bool HasConnectorCorridor(DetectedNode a, DetectedNode b, bool[] nonWordFg, int gw, int gh)
    {
        // Anchor points on the facing edges of node A and node B in grid space.
        float ax = (a.GridX0 + a.GridX1) * 0.5f, ay = (a.GridY0 + a.GridY1) * 0.5f;
        float bx = (b.GridX0 + b.GridX1) * 0.5f, by = (b.GridY0 + b.GridY1) * 0.5f;

        int steps = Math.Max(8, (int)Math.Ceiling(MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay))));
        int outsideSamples = 0, hitSamples = 0;
        for (int s = 1; s < steps; s++)
        {
            float t = s / (float)steps;
            int gx = Math.Clamp((int)Math.Round(ax + (bx - ax) * t), 0, gw - 1);
            int gy = Math.Clamp((int)Math.Round(ay + (by - ay) * t), 0, gh - 1);

            bool inA = gx >= a.GridX0 + 1 && gx <= a.GridX1 - 1 && gy >= a.GridY0 + 1 && gy <= a.GridY1 - 1;
            bool inB = gx >= b.GridX0 + 1 && gx <= b.GridX1 - 1 && gy >= b.GridY0 + 1 && gy <= b.GridY1 - 1;
            if (inA || inB) continue;

            outsideSamples++;
            bool hit = false;
            for (int dy = -3; dy <= 3 && !hit; dy++)
            {
                int ny = Math.Clamp(gy + dy, 0, gh - 1) * gw;
                for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = Math.Clamp(gx + dx, 0, gw - 1);
                    if (nonWordFg[ny + nx]) { hit = true; break; }
                }
            }
            if (hit) hitSamples++;
        }
        return outsideSamples >= 2 && (hitSamples / (float)outsideSamples) >= 0.35f;
    }

    private static bool SegmentIntersectsRect(float x0, float y0, float x1, float y1, SKRect r)
    {
        for (int s = 1; s <= 9; s++)
        {
            float t = s / 10f;
            float x = x0 + (x1 - x0) * t;
            float y = y0 + (y1 - y0) * t;
            if (r.Contains(x, y)) return true;
        }
        return false;
    }

    private static string BuildDiagramShapesAndMermaidMarkdown(
        List<DetectedNode> nodes,
        List<(int From, int To, string? Label)> edges,
        SKRect diagramBounds)
    {
        float spanX = nodes.Max(n => n.CenterX) - nodes.Min(n => n.CenterX);
        float spanY = nodes.Max(n => n.CenterY) - nodes.Min(n => n.CenterY);
        bool isHorizontal = spanX >= spanY * 1.15f;

        // 1. Construct the Mermaid FlowchartDiagramAst and generate canonical Mermaid syntax.
        var ast = new FlowchartDiagramAst
        {
            Direction = isHorizontal ? FlowDirection.LR : FlowDirection.TD
        };

        for (int i = 0; i < nodes.Count; i++)
        {
            string id = $"N{i + 1}";
            string label = string.IsNullOrWhiteSpace(nodes[i].Label) ? $"Step {i + 1}" : nodes[i].Label;
            ast.Nodes[id] = new FlowNode
            {
                Id = id,
                Text = label,
                Shape = nodes[i].MermaidShape
            };
        }

        foreach (var (from, to, edgeLabel) in edges)
        {
            ast.Edges.Add(new FlowEdge
            {
                FromId = $"N{from + 1}",
                ToId = $"N{to + 1}",
                LineStyle = FlowLineStyle.Solid,
                StartHead = FlowArrowHead.None,
                EndHead = FlowArrowHead.Normal,
                Label = edgeLabel
            });
        }

        string mermaidCode = MermaidCodeGenerator.Generate(ast);

        // 2. Construct ShapeForge ComposedShapes (connectors + arrowheads + labelled node shapes)
        // in Word canvas inches (up to 6.0" wide).
        float rawW = Math.Max(1f, diagramBounds.Width);
        float rawH = Math.Max(1f, diagramBounds.Height);
        double canvasW = 5.8;
        double canvasH = Math.Clamp(canvasW * (rawH / rawW), 1.2, 4.6);
        const double pad = 0.15;

        double MapX(float px) => pad + ((px - diagramBounds.Left) / rawW) * (canvasW - pad * 2);
        double MapY(float py) => pad + ((py - diagramBounds.Top) / rawH) * (canvasH - pad * 2);
        double MapW(float pw) => Math.Max(0.75, (pw / rawW) * (canvasW - pad * 2));
        double MapH(float ph) => Math.Max(0.42, (ph / rawH) * (canvasH - pad * 2));

        var composedShapes = new List<ComposedShape>();

        // Emit connector lines and arrowheads first so node boxes sit cleanly on top.
        foreach (var (fromIdx, toIdx, edgeLabel) in edges)
        {
            var fn = nodes[fromIdx];
            var tn = nodes[toIdx];

            double fx = MapX(fn.PageBounds.Left), fy = MapY(fn.PageBounds.Top);
            double fw = MapW(fn.PageBounds.Width), fh = MapH(fn.PageBounds.Height);
            double tx = MapX(tn.PageBounds.Left), ty = MapY(tn.PageBounds.Top);
            double tw = MapW(tn.PageBounds.Width), th = MapH(tn.PageBounds.Height);

            var (x1, y1, x2, y2) = ComputeConnectorEndpoints(fx, fy, fw, fh, tx, ty, tw, th);
            double minX = Math.Min(x1, x2), minY = Math.Min(y1, y2);
            double boxW = Math.Max(0.08, Math.Abs(x2 - x1));
            double boxH = Math.Max(0.08, Math.Abs(y2 - y1));

            double p1x = Math.Clamp(((x1 - minX) / boxW) * 100.0, 0.0, 100.0);
            double p1y = Math.Clamp(((y1 - minY) / boxH) * 100.0, 0.0, 100.0);
            double p2x = Math.Clamp(((x2 - minX) / boxW) * 100.0, 0.0, 100.0);
            double p2y = Math.Clamp(((y2 - minY) / boxH) * 100.0, 0.0, 100.0);

            composedShapes.Add(new ComposedShape
            {
                Prst = "line",
                X = minX,
                Y = minY,
                W = boxW,
                H = boxH,
                Fill = "475569",
                StrokeWidthPt = 1.8,
                PathPoints = new List<(double X, double Y)> { (p1x, p1y), (p2x, p2y) }
            });

            // Arrowhead at (x2, y2) oriented along the connector vector.
            double angleDeg = Math.Atan2(y2 - y1, x2 - x1) * (180.0 / Math.PI);
            int rot = (((int)Math.Round(angleDeg + 90.0) % 360) + 360) % 360;
            const double arrowSize = 0.11;
            composedShapes.Add(new ComposedShape
            {
                Prst = "triangle",
                X = Math.Max(0, x2 - arrowSize * 0.5),
                Y = Math.Max(0, y2 - arrowSize * 0.5),
                W = arrowSize,
                H = arrowSize,
                Fill = "475569",
                Rot = rot
            });

            if (!string.IsNullOrWhiteSpace(edgeLabel))
            {
                double mx = (x1 + x2) * 0.5;
                double my = (y1 + y2) * 0.5;
                composedShapes.Add(new ComposedShape
                {
                    Prst = "roundRect",
                    X = Math.Max(0, mx - 0.42),
                    Y = Math.Max(0, my - 0.14),
                    W = 0.84,
                    H = 0.28,
                    Fill = "F1F5F9",
                    Text = edgeLabel,
                    TextColor = "0F172A"
                });
            }
        }

        // Emit each diagram node shape with its OCR'd label.
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            string label = string.IsNullOrWhiteSpace(n.Label) ? $"Step {i + 1}" : n.Label;
            composedShapes.Add(new ComposedShape
            {
                Prst = n.ShapePrst,
                X = MapX(n.PageBounds.Left),
                Y = MapY(n.PageBounds.Top),
                W = MapW(n.PageBounds.Width),
                H = MapH(n.PageBounds.Height),
                Fill = n.FillHex,
                Text = label,
                TextColor = n.TextColorHex
            });
        }

        // Serialize via ShapeMarkdownCodec and embed the Mermaid graph metadata at the top of the block.
        string serialized = ShapeMarkdownCodec.Serialize(composedShapes, compact: false).Trim();
        var sb = new StringBuilder();
        sb.AppendLine(ShapeMarkdownCodec.BlockTag);
        foreach (var mLine in mermaidCode.Split('\n'))
        {
            var trimmed = mLine.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(trimmed))
                sb.AppendLine($"# mermaid: {trimmed}");
        }
        foreach (var sLine in serialized.Split('\n').Skip(1))
        {
            sb.AppendLine(sLine.TrimEnd('\r'));
        }
        return sb.ToString().Trim();
    }

    private static (double X1, double Y1, double X2, double Y2) ComputeConnectorEndpoints(
        double fx, double fy, double fw, double fh,
        double tx, double ty, double tw, double th)
    {
        double fcx = fx + fw * 0.5, fcy = fy + fh * 0.5;
        double tcx = tx + tw * 0.5, tcy = ty + th * 0.5;
        double dx = tcx - fcx, dy = tcy - fcy;

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            if (dx >= 0)
                return (fx + fw, fcy, tx, tcy);
            return (fx, fcy, tx + tw, tcy);
        }
        else
        {
            if (dy >= 0)
                return (fcx, fy + fh, tcx, ty);
            return (fcx, fy, tcx, ty + th);
        }
    }

    // ---- Phase B: Picture & ShapeForge Mosaic Detection -----------------------------------------

    private static int DetectPicturesAndMosaics(
        SKBitmap page,
        int gw, int gh, float invScaleX, float invScaleY, float bodyHeight, int pageW, int pageH,
        bool[] nonWordFg, byte[] satGrid, bool[] claimedGrid,
        List<OcrWord> allWords, HashSet<OcrWord> claimedWords,
        OcrVisualOptions options,
        List<OcrMarkdownBlock> visualBlocks)
    {
        int picturesSaved = 0;

        // Remaining non-word foreground cells not already claimed by a structured diagram.
        var available = new bool[gw * gh];
        for (int i = 0; i < available.Length; i++)
            available[i] = nonWordFg[i] && !claimedGrid[i];

        // Dilate by 3 grid cells so adjacent parts of a graphic or photograph coalesce.
        var coalesced = Dilate(available, gw, gh, 3);
        var visited = new bool[gw * gh];
        var queue = new Queue<int>();

        float minPicW = Math.Max(76f, bodyHeight * 2.8f);
        float minPicH = Math.Max(64f, bodyHeight * 2.5f);
        float minArea = pageW * pageH * 0.012f;

        for (int y = 0; y < gh; y++)
        {
            int row = y * gw;
            for (int x = 0; x < gw; x++)
            {
                int start = row + x;
                if (!coalesced[start] || visited[start]) continue;

                int minX = x, maxX = x, minY = y, maxY = y;
                visited[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int cur = queue.Dequeue();
                    int cx = cur % gw, cy = cur / gw;
                    if (cx < minX) minX = cx;
                    if (cx > maxX) maxX = cx;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;

                    if (cx > 0) TryVisit(cur - 1);
                    if (cx + 1 < gw) TryVisit(cur + 1);
                    if (cy > 0) TryVisit(cur - gw);
                    if (cy + 1 < gh) TryVisit(cur + gw);
                }
                void TryVisit(int idx)
                {
                    if (!visited[idx] && coalesced[idx])
                    {
                        visited[idx] = true;
                        queue.Enqueue(idx);
                    }
                }

                float px0 = minX * invScaleX;
                float py0 = minY * invScaleY;
                float px1 = (maxX + 1) * invScaleX;
                float py1 = (maxY + 1) * invScaleY;
                float pw = px1 - px0;
                float ph = py1 - py0;

                if (pw < minPicW || ph < minPicH || pw * ph < minArea) continue;
                if (pw > pageW * 0.94f && ph > pageH * 0.94f) continue;

                var rect = new SKRect(px0, py0, px1, py1);

                // Ensure this region isn't mostly covered by recognised body text.
                float wordArea = 0f;
                var wordsInside = new List<OcrWord>();
                foreach (var w in allWords)
                {
                    if (claimedWords.Contains(w)) continue;
                    var wRect = new SKRect(w.X, w.Y, w.X + w.Width, w.Y + w.Height);
                    if (rect.IntersectsWith(wRect))
                    {
                        var inter = SKRect.Intersect(rect, wRect);
                        wordArea += Math.Max(0f, inter.Width * inter.Height);
                        wordsInside.Add(w);
                    }
                }
                if (wordArea / (pw * ph) > 0.14f) continue;

                // Measure foreground density, colour richness, and 2D solid-patch density inside the region.
                // Text glyphs and scanner noise are thin 1D strokes with 0 colour and 0 solid 3x3 patches.
                int rw = maxX - minX + 1, rh = maxY - minY + 1;
                int totalCells = Math.Max(1, rw * rh);
                int fgCells = 0, colorfulCells = 0, solidPatchCells = 0;

                for (int gy = minY; gy <= maxY; gy++)
                {
                    int rRow = gy * gw;
                    for (int gx = minX; gx <= maxX; gx++)
                    {
                        int idx = rRow + gx;
                        if (available[idx]) fgCells++;
                        if (satGrid[idx] > 28) colorfulCells++;
                        if (gy > minY && gy < maxY && gx > minX && gx < maxX && IsSolid3x3(available, gw, gx, gy))
                            solidPatchCells++;
                    }
                }

                float fgRatio = fgCells / (float)totalCells;
                float colorRatio = colorfulCells / (float)totalCells;
                float solidRatio = solidPatchCells / (float)totalCells;

                if (fgRatio < 0.10f) continue;
                if (colorRatio < 0.04f && solidRatio < 0.08f) continue;

                using var cropped = CropBitmap(page, rect);
                if (cropped is null) continue;

                foreach (var w in wordsInside)
                    claimedWords.Add(w);

                // Decide between a continuous-tone raster Picture vs. a ShapeForge Mosaic of Word shapes.
                bool isContinuousTonePhoto = !options.PreferShapeMosaicForPictures
                    && !string.IsNullOrWhiteSpace(options.MediaDirectory)
                    && fgRatio > 0.78f
                    && CountDistinctColors(cropped) >= 18;

                if (isContinuousTonePhoto)
                {
                    Directory.CreateDirectory(options.MediaDirectory!);
                    int picIndex = options.StartingPictureCount + picturesSaved + 1;
                    string fileName = $"page{options.PageNumber}-picture{picIndex}.png";
                    string fullPath = Path.Combine(options.MediaDirectory!, fileName);
                    using (var fs = File.Create(fullPath))
                    {
                        cropped.Encode(SKEncodedImageFormat.Png, 100).SaveTo(fs);
                    }
                    string linkPrefix = !string.IsNullOrWhiteSpace(options.MediaLink)
                        ? options.MediaLink!
                        : Path.GetFileName(options.MediaDirectory!);
                    string relLink = (linkPrefix + "/" + fileName).Replace(" ", "%20");
                    string picMd = $"![Picture from page {options.PageNumber}]({relLink})";
                    visualBlocks.Add(new OcrMarkdownBlock(picMd, OcrBlockKind.Picture, Array.Empty<OcrLine>(), rect));
                    picturesSaved++;
                }
                else
                {
                    int mosaicGrid = Math.Clamp((int)Math.Round(rect.Width / 14.0), 16, 28);
                    var mosaicShapes = ImageShapeComposer.Compose(cropped, new ShapeComposerOptions
                    {
                        Grid = mosaicGrid,
                        Shapes = new List<string> { "roundRect", "ellipse", "diamond" },
                        Dither = true,
                        PaletteColors = 16
                    });
                    if (mosaicShapes.Count > 0)
                    {
                        string mosaicMd = ShapeMarkdownCodec.Serialize(mosaicShapes, compact: true).Trim();
                        visualBlocks.Add(new OcrMarkdownBlock(mosaicMd, OcrBlockKind.ShapeMosaic, Array.Empty<OcrLine>(), rect));
                    }
                }
            }
        }

        return picturesSaved;
    }

    private static bool IsSolid3x3(bool[] mask, int gw, int cx, int cy)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int row = (cy + dy) * gw;
            for (int dx = -1; dx <= 1; dx++)
                if (!mask[row + cx + dx]) return false;
        }
        return true;
    }

    private static int CountDistinctColors(SKBitmap bmp)
    {
        int pw = bmp.Width, ph = bmp.Height;
        if (pw <= 0 || ph <= 0) return 0;
        var pixels = bmp.Pixels;
        int stepX = Math.Max(1, pw / 48);
        int stepY = Math.Max(1, ph / 48);
        var buckets = new Dictionary<int, int>();
        int samples = 0;
        for (int y = 0; y < ph; y += stepY)
        {
            int rowOffset = y * pw;
            for (int x = 0; x < pw; x += stepX)
            {
                var c = pixels[rowOffset + x];
                int key = ((c.Red >> 5) << 6) | ((c.Green >> 5) << 3) | (c.Blue >> 5);
                buckets[key] = buckets.GetValueOrDefault(key) + 1;
                samples++;
            }
        }
        int minCount = Math.Max(1, (int)(samples * 0.006));
        return buckets.Count(kv => kv.Value >= minCount);
    }

    private static SKBitmap? CropBitmap(SKBitmap src, SKRect rect)
    {
        int x0 = Math.Clamp((int)Math.Floor(rect.Left), 0, src.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(rect.Top), 0, src.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(rect.Right), x0 + 1, src.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom), y0 + 1, src.Height);
        int w = x1 - x0, h = y1 - y0;
        if (w < 8 || h < 8) return null;

        var cropped = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(cropped);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, new SKRectI(x0, y0, x1, y1), new SKRect(0, 0, w, h));
        return cropped;
    }

    // ---- Grid Sampling & Morphological Helpers --------------------------------------------------

    private static void SampleBitmapToGrid(
        SKBitmap page, int gw, int gh,
        byte[] rGrid, byte[] gGrid, byte[] bGrid, byte[] lumGrid, byte[] satGrid)
    {
        int pw = page.Width, ph = page.Height;
        var pixels = page.Pixels;
        for (int gy = 0; gy < gh; gy++)
        {
            int sy = Math.Clamp((int)((gy + 0.5f) * ph / gh), 0, ph - 1);
            int srcRow = sy * pw;
            int dstRow = gy * gw;
            for (int gx = 0; gx < gw; gx++)
            {
                int sx = Math.Clamp((int)((gx + 0.5f) * pw / gw), 0, pw - 1);
                var c = pixels[srcRow + sx];
                int idx = dstRow + gx;
                rGrid[idx] = c.Red;
                gGrid[idx] = c.Green;
                bGrid[idx] = c.Blue;
                lumGrid[idx] = (byte)((c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8);
                byte max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
                byte min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                satGrid[idx] = (byte)(max - min);
            }
        }
    }

    private static byte EstimateBackgroundLuminance(byte[] lumGrid, int gw, int gh)
    {
        var hist = new int[256];
        for (int i = 0; i < lumGrid.Length; i++)
            hist[lumGrid[i]]++;
        int target = (int)(lumGrid.Length * 0.80);
        int acc = 0;
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (acc >= target) return (byte)Math.Max(180, v);
        }
        return 240;
    }

    private static bool[] Dilate(bool[] src, int gw, int gh, int radius)
    {
        var dst = new bool[gw * gh];
        for (int y = 0; y < gh; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(gh - 1, y + radius);
            for (int x = 0; x < gw; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(gw - 1, x + radius);
                bool any = false;
                for (int ny = y0; ny <= y1 && !any; ny++)
                {
                    int row = ny * gw;
                    for (int nx = x0; nx <= x1; nx++)
                        if (src[row + nx]) { any = true; break; }
                }
                dst[y * gw + x] = any;
            }
        }
        return dst;
    }

    private static bool[] Erode(bool[] src, int gw, int gh, int radius)
    {
        var dst = new bool[gw * gh];
        for (int y = 0; y < gh; y++)
        {
            int y0 = Math.Max(0, y - radius), y1 = Math.Min(gh - 1, y + radius);
            for (int x = 0; x < gw; x++)
            {
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(gw - 1, x + radius);
                bool all = true;
                for (int ny = y0; ny <= y1 && all; ny++)
                {
                    int row = ny * gw;
                    for (int nx = x0; nx <= x1; nx++)
                        if (!src[row + nx]) { all = false; break; }
                }
                dst[y * gw + x] = all;
            }
        }
        return dst;
    }
}
