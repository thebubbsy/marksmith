using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using MarkSmith.Core.AST;
using MarkSmith.Core.Glox;

namespace MarkSmith.Core.Preview
{
    /// <summary>The drawing a SmartArt layout previews as. Word lays out the real diagram from the
    /// layout definition at export; the preview approximates each family's look so picking
    /// "Step Up Process" or "Basic Venn" in the gallery shows something recognisably that layout.</summary>
    public enum SmartArtPreviewFamily
    {
        Hierarchy,
        HorizontalHierarchy,
        BlockHierarchy,
        HierarchyList,
        BlockList,
        HorizontalList,
        VerticalList,
        Process,
        Chevron,
        VerticalProcess,
        BendingProcess,
        StepsUp,
        StepsDown,
        Timeline,
        Cycle,
        Radial,
        Matrix,
        Pyramid,
        InvertedPyramid,
        Venn,
        LinearVenn,
        Target,
        Balance,
        Equation,
        Pictures,
    }

    /// <summary>
    /// Renders a SmartArt block (an outline parsed to a <see cref="CanonicalAst"/>) as an inline
    /// SVG card for the document preview and the SmartArt Design Studio. Top-level items are the
    /// diagram's shapes and their sub-items are the shape's bullet text — the same data model the
    /// DOCX export hands to Word — except for hierarchies, where every level is a shape.
    /// </summary>
    public static class HtmlPreviewRenderer
    {
        // Fluent accent ramp; every one carries white bold text at >= 3:1.
        private static readonly string[] Accents = { "#0078d4", "#107c41", "#ca5010", "#8764b8", "#038387", "#c239b3", "#986f0b", "#d13438" };
        private static readonly string[] LevelColors = { "#0078d4", "#107c41", "#8764b8", "#038387", "#ca5010" };
        private const string Ink = "#201f1e";
        private const string SubInk = "#484644";
        private const string Connector = "#8a8886";
        private const double BaseW = 800;
        private const double Pad = 28;

        private sealed class Item
        {
            public string Text = string.Empty;
            public List<Item> Children = new();
            public IReadOnlyList<string> Bullets => Children.SelectMany(c => new[] { c.Text }.Concat(c.Children.Select(g => "–\u00A0" + g.Text))).ToList();
        }

        public static string RenderHtml(CanonicalAst ast, string layoutAlias, string layoutTitle = "SmartArt Diagram")
        {
            var family = ResolveFamily(layoutAlias);
            var items = ToItems(ast?.Root);

            string svg;
            if (items.Count == 0)
            {
                svg = Svg(BaseW, 240, $"<text x=\"400\" y=\"120\" text-anchor=\"middle\" font-size=\"15\" fill=\"#605e5c\">Add items to the outline to see this layout.</text>", out _);
            }
            else
            {
                svg = DrawUniform(family, items);
            }

            // Only append the alias when it adds something (an untitled layout's title *is* its alias).
            // A blank title used to print as "Layout:  (process)".
            var layoutLabel = string.IsNullOrWhiteSpace(layoutTitle) ? layoutAlias
                : string.Equals(layoutTitle, layoutAlias, StringComparison.OrdinalIgnoreCase)
                ? layoutTitle
                : $"{layoutTitle} ({layoutAlias})";

            return $@"
<div class=""smartart-container"" data-family=""{family}"" style=""width: 100%; max-width: 800px; background: #f8f9fa; border: 1px solid #e0e0e0; border-radius: 8px; overflow: hidden; font-family: 'Segoe UI', system-ui, -apple-system, sans-serif; box-shadow: 0 2px 8px rgba(0,0,0,0.05); box-sizing: border-box;"">
  <div class=""smartart-caption"" style=""padding: 10px 14px 0; font-size: 12px; font-weight: 600; color: #605e5c; text-align: left;"">Layout: {WebUtility.HtmlEncode(layoutLabel)}</div>
  {svg}
</div>";
        }

        // Word syncs the text size across shapes of the same kind, so a row of process boxes never
        // mixes 15 pt with 9 pt. The drawing runs twice: the first pass records the size every text
        // slot fits at, the second caps each slot at the smallest size of its group.
        [ThreadStatic] private static Dictionary<string, double>? _seenSizes;
        [ThreadStatic] private static Dictionary<string, double>? _sizeCaps;

        private static string DrawUniform(SmartArtPreviewFamily family, List<Item> items)
        {
            try
            {
                _seenSizes = new Dictionary<string, double>();
                _sizeCaps = null;
                DrawFamily(family, items);
                _sizeCaps = _seenSizes;
                _seenSizes = null;
                return DrawFamily(family, items);
            }
            finally
            {
                _seenSizes = null;
                _sizeCaps = null;
            }
        }

        private const double MinSharedFs = 10;

        private static readonly Dictionary<SmartArtPreviewFamily, string> _thumbnails = new();

        /// <summary>A miniature of the family's drawing for the layout gallery: the real shapes
        /// drawn from a small sample outline, with the text, tooltips and hover styles removed (the
        /// gallery renders it through Direct2D's SVG support, which has no text, and words at
        /// thumbnail size would only be noise). Sized by explicit width/height so an image source can
        /// rasterize it. Cached per family.</summary>
        public static string RenderThumbnailSvg(SmartArtPreviewFamily family)
        {
            lock (_thumbnails)
            {
                if (_thumbnails.TryGetValue(family, out var cached)) return cached;
                var svg = DrawFamily(family, ThumbnailSample(family));
                svg = System.Text.RegularExpressions.Regex.Replace(svg,
                    "<text\\b[^>]*>.*?</text>|<title>.*?</title>|<style>.*?</style>", string.Empty,
                    System.Text.RegularExpressions.RegexOptions.Singleline);
                // Picture placeholders are pale grey on white; at thumbnail size on a light tile they
                // vanished, so the miniature draws them mid-grey with a white glyph.
                if (family == SmartArtPreviewFamily.Pictures)
                    svg = svg.Replace("fill=\"#c8c6c4\"/>", "fill=\"#ffffff\"/>").Replace("fill=\"#edebe9\"", "fill=\"#a19f9d\"");
                // White cards (timeline labels, picture frames, empty matrix cells) disappear on the
                // light tile; a miniature fills them with their own outline colour instead.
                svg = System.Text.RegularExpressions.Regex.Replace(svg, "(<rect\\b[^>]*?)fill=\"#ffffff\"([^>]*?)stroke=\"(#[0-9a-fA-F]{6})\"",
                    m => $"{m.Groups[1].Value}fill=\"{m.Groups[3].Value}\"{m.Groups[2].Value}stroke=\"{m.Groups[3].Value}\"");
                // Frame the shapes themselves: the drawing's 800-wide page left a three-box process as
                // a sliver in the middle of the tile.
                var (x0, y0, x1, y1) = ShapeBounds(svg);
                double pad = Math.Max(x1 - x0, y1 - y0) * 0.04;
                x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
                svg = System.Text.RegularExpressions.Regex.Replace(svg, "viewBox=\"[^\"]*\"", $"viewBox=\"{F(x0)} {F(y0)} {F(x1 - x0)} {F(y1 - y0)}\"", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                svg = svg.Replace("width=\"100%\" style=\"display:block;width:100%;height:auto\"", $"width=\"{F(x1 - x0)}\" height=\"{F(y1 - y0)}\"");
                _thumbnails[family] = svg;
                return svg;
            }
        }

        /// <summary>The bounding box of the shapes this renderer emits (rect, circle, polygon, line and
        /// absolute M/L/H/V/A paths), so a thumbnail can frame them.</summary>
        internal static (double x0, double y0, double x1, double y1) ShapeBounds(string svg)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            void Add(double x, double y) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
            static double N(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            string body = System.Text.RegularExpressions.Regex.Replace(svg, "<defs>.*?</defs>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, "<(rect|circle|polygon|line|path)\\b([^>]*)>"))
            {
                string a = m.Groups[2].Value;
                string? Attr(string name) { var am = System.Text.RegularExpressions.Regex.Match(a, "\\b" + name + "=\"([^\"]*)\""); return am.Success ? am.Groups[1].Value : null; }
                switch (m.Groups[1].Value)
                {
                    case "rect":
                        double rx = N(Attr("x") ?? "0"), ry = N(Attr("y") ?? "0");
                        Add(rx, ry); Add(rx + N(Attr("width") ?? "0"), ry + N(Attr("height") ?? "0"));
                        break;
                    case "circle":
                        double cx = N(Attr("cx") ?? "0"), cy = N(Attr("cy") ?? "0"), r = N(Attr("r") ?? "0");
                        Add(cx - r, cy - r); Add(cx + r, cy + r);
                        break;
                    case "polygon":
                        foreach (var p in (Attr("points") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var xy = p.Split(',');
                            if (xy.Length == 2) Add(N(xy[0]), N(xy[1]));
                        }
                        break;
                    case "line":
                        Add(N(Attr("x1") ?? "0"), N(Attr("y1") ?? "0")); Add(N(Attr("x2") ?? "0"), N(Attr("y2") ?? "0"));
                        break;
                    case "path":
                        double px = 0, py = 0;
                        foreach (System.Text.RegularExpressions.Match c in System.Text.RegularExpressions.Regex.Matches(Attr("d") ?? "", "([MLHVA])([^MLHVAZz]*)"))
                        {
                            var v = c.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Select(N).ToArray();
                            switch (c.Groups[1].Value)
                            {
                                case "M": case "L": if (v.Length >= 2) { px = v[0]; py = v[1]; } break;
                                case "H": if (v.Length >= 1) px = v[0]; break;
                                case "V": if (v.Length >= 1) py = v[0]; break;
                                case "A": if (v.Length >= 7) { px = v[5]; py = v[6]; } break;
                            }
                            Add(px, py);
                        }
                        break;
                }
            }
            return x0 == double.MaxValue ? (0, 0, BaseW, 400) : (x0, y0, x1, y1);
        }

        /// <summary>The outline a thumbnail draws: enough items to show the family's shape (a bend, a
        /// ring, a tree with two levels) and no more. Single letters keep every box at its smallest.</summary>
        private static List<Item> ThumbnailSample(SmartArtPreviewFamily family)
        {
            static Item I(string t, params Item[] kids) => new() { Text = t, Children = kids.ToList() };
            static List<Item> Flat(int n) => Enumerable.Range(0, n).Select(i => I(((char)('A' + i)).ToString())).ToList();
            return family switch
            {
                SmartArtPreviewFamily.Hierarchy or SmartArtPreviewFamily.HorizontalHierarchy or SmartArtPreviewFamily.BlockHierarchy
                    => new() { I("A", I("B", I("D"), I("E")), I("C", I("F"), I("G"))) },
                SmartArtPreviewFamily.HierarchyList => new() { I("A", I("a"), I("b")), I("B", I("c"), I("d")), I("C", I("e"), I("f")) },
                SmartArtPreviewFamily.HorizontalList or SmartArtPreviewFamily.VerticalList
                    => new() { I("A", I("a")), I("B", I("b")), I("C", I("c")) },
                SmartArtPreviewFamily.BendingProcess => Flat(6),
                SmartArtPreviewFamily.Cycle or SmartArtPreviewFamily.Radial => Flat(6),
                SmartArtPreviewFamily.Matrix or SmartArtPreviewFamily.Pyramid or SmartArtPreviewFamily.InvertedPyramid
                    or SmartArtPreviewFamily.Target or SmartArtPreviewFamily.BlockList or SmartArtPreviewFamily.Chevron
                    or SmartArtPreviewFamily.StepsUp or SmartArtPreviewFamily.StepsDown or SmartArtPreviewFamily.Timeline => Flat(4),
                SmartArtPreviewFamily.Pictures => Flat(2), // two cards stay recognisable at tile size; four were specks
                SmartArtPreviewFamily.Balance => Flat(3),
                _ => Flat(3),
            };
        }

        private static string DrawFamily(SmartArtPreviewFamily family, List<Item> items)
        {
            var sb = new StringBuilder();
            var (w, h) = family switch
            {
                SmartArtPreviewFamily.Hierarchy => DrawTree(sb, items, horizontal: false),
                SmartArtPreviewFamily.HorizontalHierarchy => DrawTree(sb, items, horizontal: true),
                SmartArtPreviewFamily.BlockHierarchy => DrawBlockHierarchy(sb, items),
                SmartArtPreviewFamily.HierarchyList => DrawHierarchyList(sb, items),
                SmartArtPreviewFamily.BlockList => DrawBlockList(sb, items),
                SmartArtPreviewFamily.HorizontalList => DrawHorizontalList(sb, items),
                SmartArtPreviewFamily.VerticalList => DrawVerticalList(sb, items),
                SmartArtPreviewFamily.Process => DrawProcess(sb, items),
                SmartArtPreviewFamily.Chevron => DrawChevrons(sb, items),
                SmartArtPreviewFamily.VerticalProcess => DrawVerticalProcess(sb, items),
                SmartArtPreviewFamily.BendingProcess => DrawBending(sb, items),
                SmartArtPreviewFamily.StepsUp => DrawSteps(sb, items, up: true),
                SmartArtPreviewFamily.StepsDown => DrawSteps(sb, items, up: false),
                SmartArtPreviewFamily.Timeline => DrawTimeline(sb, items),
                SmartArtPreviewFamily.Cycle => DrawCycle(sb, items),
                SmartArtPreviewFamily.Radial => DrawRadial(sb, items),
                SmartArtPreviewFamily.Matrix => DrawMatrix(sb, items),
                SmartArtPreviewFamily.Pyramid => DrawPyramid(sb, Flatten(items), inverted: false),
                SmartArtPreviewFamily.InvertedPyramid => DrawPyramid(sb, Flatten(items), inverted: true),
                SmartArtPreviewFamily.Venn => DrawVenn(sb, items),
                SmartArtPreviewFamily.LinearVenn => DrawLinearVenn(sb, items),
                SmartArtPreviewFamily.Target => DrawTarget(sb, items),
                SmartArtPreviewFamily.Balance => DrawBalance(sb, items),
                SmartArtPreviewFamily.Equation => DrawEquation(sb, items),
                SmartArtPreviewFamily.Pictures => DrawPictures(sb, items),
                _ => DrawBlockList(sb, items),
            };
            return Svg(w, h, sb.ToString(), out _);
        }

        // ------------------------------------------------------------------ family resolution

        /// <summary>Which drawing a layout alias, URN or title previews as.</summary>
        public static SmartArtPreviewFamily ResolveFamily(string? layoutAlias)
        {
            var alias = (layoutAlias ?? string.Empty).Trim();
            GloxPackage? pkg = null;
            try { pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias); } catch { /* catalog unavailable: classify by name */ }
            var tail = Tail(pkg?.UniqueId ?? alias);
            return Classify(tail, pkg?.Category);
        }

        private static readonly Dictionary<string, SmartArtPreviewFamily> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            // Hierarchies
            ["orgChart1"] = SmartArtPreviewFamily.Hierarchy,
            ["hierarchy1"] = SmartArtPreviewFamily.Hierarchy,
            ["hierarchy6"] = SmartArtPreviewFamily.Hierarchy,
            ["NameandTitleOrganizationalChart"] = SmartArtPreviewFamily.Hierarchy,
            ["HalfCircleOrganizationChart"] = SmartArtPreviewFamily.Hierarchy,
            ["pictureOrgChart+Icon"] = SmartArtPreviewFamily.Hierarchy,
            ["CirclePictureHierarchy"] = SmartArtPreviewFamily.Hierarchy,
            ["hierarchy2"] = SmartArtPreviewFamily.HorizontalHierarchy,
            ["hierarchy5"] = SmartArtPreviewFamily.HorizontalHierarchy,
            ["HorizontalMultiLevelHierarchy"] = SmartArtPreviewFamily.HorizontalHierarchy,
            ["HorizontalOrganizationChart"] = SmartArtPreviewFamily.HorizontalHierarchy,
            ["architecture"] = SmartArtPreviewFamily.BlockHierarchy,
            ["hierarchy4"] = SmartArtPreviewFamily.BlockHierarchy,
            ["hierarchy3"] = SmartArtPreviewFamily.HierarchyList,
            ["LinedList"] = SmartArtPreviewFamily.HierarchyList,
            ["lProcess2"] = SmartArtPreviewFamily.HierarchyList,
            ["hList7"] = SmartArtPreviewFamily.HierarchyList,

            // Lists
            ["default"] = SmartArtPreviewFamily.BlockList,
            ["AlternatingHexagons"] = SmartArtPreviewFamily.BlockList,
            ["SquareAccentList"] = SmartArtPreviewFamily.BlockList,
            ["hList1"] = SmartArtPreviewFamily.HorizontalList,
            ["hList2"] = SmartArtPreviewFamily.HorizontalList,
            ["hList3"] = SmartArtPreviewFamily.HorizontalList,
            ["hList6"] = SmartArtPreviewFamily.HorizontalList,
            ["hList9"] = SmartArtPreviewFamily.HorizontalList,
            ["list1"] = SmartArtPreviewFamily.HorizontalList,
            ["TabList"] = SmartArtPreviewFamily.HorizontalList,
            ["HorizontalActionList"] = SmartArtPreviewFamily.HorizontalList,
            ["ReverseList"] = SmartArtPreviewFamily.VerticalList,

            // Processes
            ["chevron1"] = SmartArtPreviewFamily.Chevron,
            ["chevron2"] = SmartArtPreviewFamily.Chevron,
            ["hChevron3"] = SmartArtPreviewFamily.Chevron,
            ["chevronAccent+Icon"] = SmartArtPreviewFamily.Chevron,
            ["IncreasingArrowsProcess"] = SmartArtPreviewFamily.Chevron,
            ["NumberedLinearArrowProcess"] = SmartArtPreviewFamily.Chevron,
            ["process2"] = SmartArtPreviewFamily.VerticalProcess,
            ["vProcess5"] = SmartArtPreviewFamily.VerticalProcess,
            ["vList6"] = SmartArtPreviewFamily.VerticalProcess,
            ["lProcess1"] = SmartArtPreviewFamily.VerticalProcess,
            ["lProcess3"] = SmartArtPreviewFamily.VerticalProcess,
            ["bProcess2"] = SmartArtPreviewFamily.BendingProcess,
            ["bProcess3"] = SmartArtPreviewFamily.BendingProcess,
            ["bProcess4"] = SmartArtPreviewFamily.BendingProcess,
            ["process5"] = SmartArtPreviewFamily.BendingProcess,
            ["StepUpProcess"] = SmartArtPreviewFamily.StepsUp,
            ["AscendingPictureAccentProcess"] = SmartArtPreviewFamily.StepsUp,
            ["arrow2"] = SmartArtPreviewFamily.StepsUp,
            ["IncreasingCircleProcess"] = SmartArtPreviewFamily.StepsUp,
            ["StepDownProcess"] = SmartArtPreviewFamily.StepsDown,
            ["DescendingProcess"] = SmartArtPreviewFamily.StepsDown,
            ["BlockDescendingList"] = SmartArtPreviewFamily.StepsDown,
            ["hProcess11"] = SmartArtPreviewFamily.Timeline,
            ["AlternatingCircleProcess"] = SmartArtPreviewFamily.Timeline,
            ["funnel1"] = SmartArtPreviewFamily.InvertedPyramid,
            ["equation1"] = SmartArtPreviewFamily.Equation,
            ["equation2"] = SmartArtPreviewFamily.Equation,
            ["ConvergingText"] = SmartArtPreviewFamily.Equation,
            ["RandomtoResultProcess"] = SmartArtPreviewFamily.Equation,

            // Cycles and relationships
            ["CircleArrowProcess"] = SmartArtPreviewFamily.Cycle,
            ["gear1"] = SmartArtPreviewFamily.Cycle,
            ["chart3"] = SmartArtPreviewFamily.Cycle,
            ["TabbedArc+Icon"] = SmartArtPreviewFamily.Cycle,
            ["cycle4"] = SmartArtPreviewFamily.Matrix,
            ["HexagonRadial"] = SmartArtPreviewFamily.Radial,
            ["CircleRelationship"] = SmartArtPreviewFamily.Radial,
            ["RadialCluster"] = SmartArtPreviewFamily.Radial,
            ["RadialPictureList"] = SmartArtPreviewFamily.Radial,
            ["CircularPictureCallout"] = SmartArtPreviewFamily.Radial,
            ["balance1"] = SmartArtPreviewFamily.Balance,
            ["OpposingIdeas"] = SmartArtPreviewFamily.Balance,
            ["PlusandMinus"] = SmartArtPreviewFamily.Balance,
            ["arrow1"] = SmartArtPreviewFamily.Balance,
            ["arrow3"] = SmartArtPreviewFamily.Balance,
            ["arrow4"] = SmartArtPreviewFamily.Balance,
            ["arrow5"] = SmartArtPreviewFamily.Balance,
            ["arrow6"] = SmartArtPreviewFamily.Balance,
            ["venn1"] = SmartArtPreviewFamily.Venn,
            ["venn3"] = SmartArtPreviewFamily.LinearVenn,
            ["venn2"] = SmartArtPreviewFamily.Target,
            ["rings+Icon"] = SmartArtPreviewFamily.LinearVenn,
            ["pyramid3"] = SmartArtPreviewFamily.InvertedPyramid,
            ["ThemePictureGrid"] = SmartArtPreviewFamily.Pictures,
            ["pList1"] = SmartArtPreviewFamily.Pictures,
            ["pList2"] = SmartArtPreviewFamily.Pictures,
            ["bList2"] = SmartArtPreviewFamily.Pictures,
            ["vList4"] = SmartArtPreviewFamily.Pictures,
            ["SubStepProcess"] = SmartArtPreviewFamily.Process,
        };

        internal static SmartArtPreviewFamily Classify(string tail, string? category)
        {
            if (Known.TryGetValue(tail, out var known)) return known;

            var t = tail.ToLowerInvariant();
            if (t.Contains("timeline") || t.Contains("dots")) return SmartArtPreviewFamily.Timeline;
            if (t.Contains("chevron")) return SmartArtPreviewFamily.Chevron;
            if (t.Contains("picture") || t.Contains("meettheteam") || t.Contains("photo") || t.Contains("mosaic")) return SmartArtPreviewFamily.Pictures;
            if (t.Contains("pyramid")) return SmartArtPreviewFamily.Pyramid;
            if (t.Contains("funnel")) return SmartArtPreviewFamily.InvertedPyramid;
            if (t.Contains("venn")) return SmartArtPreviewFamily.Venn;
            if (t.Contains("target")) return SmartArtPreviewFamily.Target;
            if (t.Contains("radial") || t.Contains("relationship") || t.Contains("composite")) return SmartArtPreviewFamily.Radial;
            if (t.Contains("cycle")) return SmartArtPreviewFamily.Cycle;
            if (t.Contains("matrix") || t.Contains("grid") || t.Contains("swot") || t.Contains("quadrant")) return SmartArtPreviewFamily.Matrix;
            if (t.StartsWith("horizontal") && (t.Contains("hier") || t.Contains("org"))) return SmartArtPreviewFamily.HorizontalHierarchy;
            if (t.Contains("hier") || t.Contains("org") || t.Contains("tree")) return SmartArtPreviewFamily.Hierarchy;
            if (t.Contains("step")) return SmartArtPreviewFamily.StepsUp;
            if (t.StartsWith("vprocess") || t.Contains("vertical") && t.Contains("process")) return SmartArtPreviewFamily.VerticalProcess;
            if (t.StartsWith("bprocess") || t.Contains("bending")) return SmartArtPreviewFamily.BendingProcess;
            if (t.Contains("process") || t.Contains("workflow") || t.Contains("arrow")) return SmartArtPreviewFamily.Process;
            if (t.StartsWith("hlist")) return SmartArtPreviewFamily.HorizontalList;
            if (t.StartsWith("vlist") || t.StartsWith("blist") || t.Contains("list") || t.StartsWith("textcard") || t.Contains("titlecard")) return SmartArtPreviewFamily.VerticalList;

            return (category ?? string.Empty).ToLowerInvariant() switch
            {
                "hierarchy" => SmartArtPreviewFamily.Hierarchy,
                "process" => SmartArtPreviewFamily.Process,
                "cycle" => SmartArtPreviewFamily.Cycle,
                "matrix" => SmartArtPreviewFamily.Matrix,
                "pyramid" => SmartArtPreviewFamily.Pyramid,
                "relationship" => SmartArtPreviewFamily.Radial,
                "timeline" => SmartArtPreviewFamily.Timeline,
                "picture" or "meettheteam" => SmartArtPreviewFamily.Pictures,
                "textcard" or "list" => SmartArtPreviewFamily.VerticalList,
                _ => SmartArtPreviewFamily.BlockList,
            };
        }

        private static string Tail(string urn)
        {
            int idx = urn.LastIndexOf('/');
            return idx >= 0 ? urn[(idx + 1)..] : urn;
        }

        // ------------------------------------------------------------------ model

        private static List<Item> ToItems(AstNode? root)
        {
            var list = new List<Item>();
            if (root == null) return list;
            foreach (var child in root.Children) Collect(child, list);
            return list;
        }

        // Text-less nodes (wrappers) hoist their children so nothing the user typed disappears.
        private static void Collect(AstNode node, List<Item> into)
        {
            var text = (node.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                foreach (var c in node.Children) Collect(c, into);
                return;
            }
            var item = new Item { Text = text };
            foreach (var c in node.Children) Collect(c, item.Children);
            into.Add(item);
        }

        private static List<Item> Flatten(List<Item> items)
        {
            var flat = new List<Item>();
            void Walk(Item i) { flat.Add(new Item { Text = i.Text }); foreach (var c in i.Children) Walk(c); }
            foreach (var i in items) Walk(i);
            return flat;
        }

        // ------------------------------------------------------------------ svg primitives

        private static string F(double d) => d.ToString("0.#", CultureInfo.InvariantCulture);
        private static string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

        private static string Svg(double w, double h, string inner, out string viewBox)
        {
            viewBox = $"0 0 {F(w)} {F(h)}";
            return $"<svg class=\"smartart-svg\" viewBox=\"{viewBox}\" width=\"100%\" style=\"display:block;width:100%;height:auto\" font-family=\"'Segoe UI', system-ui, sans-serif\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\">"
                 + "<style>.sa-s{transition:filter .15s ease}.sa-s:hover{filter:brightness(1.08) drop-shadow(0 2px 4px rgba(0,0,0,.18))}</style>"
                 + "<defs><marker id=\"sa-arrow\" viewBox=\"0 0 10 10\" refX=\"8\" refY=\"5\" markerUnits=\"userSpaceOnUse\" markerWidth=\"13\" markerHeight=\"13\" orient=\"auto-start-reverse\"><path d=\"M0 0L10 5L0 10z\" fill=\"#8a8886\"/></marker></defs>"
                 + inner + "</svg>";
        }

        /// <summary>Mixes a #rrggbb colour toward white (t = 0 keeps it, 1 is white).</summary>
        private static string Tint(string hex, double t)
        {
            if (hex.Length != 7 || hex[0] != '#') return hex;
            int r = Convert.ToInt32(hex.Substring(1, 2), 16), g = Convert.ToInt32(hex.Substring(3, 2), 16), b = Convert.ToInt32(hex.Substring(5, 2), 16);
            int M(int c) => (int)Math.Round(c + (255 - c) * t);
            return $"#{M(r):x2}{M(g):x2}{M(b):x2}";
        }

        private static string Accent(int i) => Accents[((i % Accents.Length) + Accents.Length) % Accents.Length];

        private static double CharW(double fs, bool bold) => fs * (bold ? 0.58 : 0.52);

        private static List<string> Wrap(string text, double maxWidth, double fs, bool bold, int maxLines) =>
            Wrap(text, maxWidth, fs, bold, maxLines, out _);

        /// <summary>Greedy word wrap at an estimated glyph width. <paramref name="clean"/> is false when
        /// a word had to be hyphenated or lines were cut with "…" — a smaller font may avoid both.</summary>
        private static List<string> Wrap(string text, double maxWidth, double fs, bool bold, int maxLines, out bool clean)
        {
            clean = true;
            var lines = new List<string>();
            int maxChars = Math.Max(3, (int)(maxWidth / CharW(fs, bold)));
            var line = new StringBuilder();
            foreach (var raw in (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw;
                while (word.Length > maxChars)
                {
                    if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                    // A compound word breaks after its own hyphen ("Self-" / "actualisation")
                    // before it is ever cut mid-syllable with an added one.
                    int own = word.LastIndexOf('-', maxChars - 1);
                    if (own > 0 && own < word.Length - 1)
                    {
                        lines.Add(word[..(own + 1)]);
                        word = word[(own + 1)..];
                        continue;
                    }
                    clean = false;
                    lines.Add(word[..(maxChars - 1)] + "-");
                    word = word[(maxChars - 1)..];
                }
                if (line.Length > 0 && line.Length + 1 + word.Length > maxChars) { lines.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) lines.Add(line.ToString());
            if (lines.Count > maxLines)
            {
                clean = false;
                lines = lines.Take(maxLines).ToList();
                var last = lines[^1];
                lines[^1] = (last.Length >= maxChars ? last[..Math.Max(1, maxChars - 1)] : last).TrimEnd('-', ' ') + "…";
            }
            return lines;
        }

        // A no-break space keeps the bullet mark on the same line as its first word.
        // Grandchildren (already marked with an en dash) indent under their parent instead.
        private static string Bullet(string text) =>
            text.StartsWith("–", StringComparison.Ordinal) ? NBSP + NBSP + NBSP + text : "•" + NBSP + text;

        private static readonly string NBSP = ((char)160).ToString();

        /// <summary>A bullet wrapped onto at most two lines, the second hanging under the first word
        /// rather than back under the bullet mark. No-break spaces carry the indent because SVG
        /// collapses ordinary leading spaces; "• " is about two and a half of them wide, the
        /// grandchild "   – " about five and a half (U+202F is the half).</summary>
        private static List<string> WrapBullet(string bullet, double w, double fs, out bool clean)
        {
            bool dash = bullet.StartsWith("–", StringComparison.Ordinal);
            string hang = dash ? NBSP + NBSP + NBSP + NBSP + NBSP + " " : NBSP + NBSP + " ";
            double hangW = (dash ? 5.5 : 2.5) * fs * 0.27;
            var lines = Wrap(Bullet(bullet), Math.Max(20, w - hangW), fs, false, 2, out clean);
            for (int i = 1; i < lines.Count; i++) lines[i] = hang + lines[i];
            return lines;
        }

        private sealed record TextFit(double Fs, List<string> Title, double Bfs, List<string> Bullets, double Height);

        /// <summary>Largest font (maxFs down to 9) at which the title (≤ 3 lines) and the bullets fit
        /// <paramref name="h"/>; when nothing fits, the smallest size with bullets cut short.</summary>
        private static TextFit FitText(string title, IReadOnlyList<string> bullets, double w, double h, double maxFs = 15)
        {
            TextFit? best = null, roomy = null;
            for (double fs = maxFs; fs >= 9; fs -= 1)
            {
                var t = Wrap(title, w, fs, true, 3, out bool clean);
                // Bullets sit a step below a title; on their own (a list body) they get the full size.
                double bfs = t.Count == 0 ? fs : Math.Max(9, fs - 2);
                var b = new List<string>();
                foreach (var bullet in bullets)
                {
                    b.AddRange(WrapBullet(bullet, w, bfs, out bool bulletClean));
                    clean &= bulletClean;
                }
                double height = t.Count * fs * 1.22 + (b.Count > 0 ? 6 + b.Count * bfs * 1.3 : 0);
                best = new TextFit(fs, t, bfs, b, height);
                // First choice: the largest size with the whole title, unbroken. Failing that, the
                // largest size that at least fits the height.
                if (height <= h && clean) return best;
                if (height <= h) roomy ??= best;
            }
            if (roomy != null) return roomy;
            // Drop bullet lines until it fits, marking the cut.
            var fit = best!;
            var lines = fit.Bullets.ToList();
            double H() => fit.Title.Count * fit.Fs * 1.22 + (lines.Count > 0 ? 6 + lines.Count * fit.Bfs * 1.3 : 0);
            bool cut = false;
            while (lines.Count > 0 && H() > h) { lines.RemoveAt(lines.Count - 1); cut = true; }
            if (cut && lines.Count > 0) lines[^1] = lines[^1].TrimEnd('…') + " …";
            return fit with { Bullets = lines, Height = H() };
        }

        /// <summary>True when the title fits w × h at <paramref name="minFs"/> or larger without a word
        /// being hyphenated or the text cut short.</summary>
        private static bool FitsClean(string title, double w, double h, double minFs, int maxLines = 3)
        {
            if (w < 12) return false;
            for (double fs = 14; fs >= minFs; fs -= 1)
            {
                var lines = Wrap(title, w, fs, true, maxLines, out bool clean);
                if (clean && lines.Count * fs * 1.22 <= h) return true;
            }
            return false;
        }

        /// <summary>Height the title + bullets need at a comfortable size in a box of width w.</summary>
        private static double Measure(string title, IReadOnlyList<string> bullets, double w, double fs = 14)
        {
            var t = Wrap(title, w, fs, true, 3);
            double bfs = t.Count == 0 ? fs : fs - 2;
            int b = bullets.Sum(x => WrapBullet(x, w, bfs, out _).Count);
            return t.Count * fs * 1.22 + (b > 0 ? 6 + b * bfs * 1.3 : 0);
        }

        /// <summary>Title (bold, centred) and bullets (left-aligned when there's room) inside a box,
        /// centred vertically.</summary>
        /// <param name="group">Text slots in one group share a font size (see <see cref="DrawUniform"/>).
        /// By default a slot's group is its box size, which matches "shapes of the same kind" for every
        /// family but the pyramid, whose tiers differ in width and pass a group of their own.</param>
        /// <param name="halo">Paints a pale outline behind the letters so labels stay legible where
        /// translucent shapes overlap (Venn).</param>
        private static void Text(StringBuilder sb, double x, double y, double w, double h, string title, IReadOnlyList<string> bullets, string color, double maxFs = 15, bool alignLeft = false, double inset = 10, string? group = null, bool halo = false)
        {
            string key = group ?? $"{Math.Round(w)}x{Math.Round(h)}|{F(maxFs)}|{(title.Length == 0 ? "body" : "title")}|{color}";
            // The shared size never drops below 10 pt for the group's sake: a slot that only fits smaller
            // (one box crammed with bullets) shrinks on its own rather than taking every sibling with it.
            if (_sizeCaps != null && _sizeCaps.TryGetValue(key, out var cap)) maxFs = Math.Min(maxFs, Math.Max(cap, MinSharedFs));
            var fit = FitText(title, bullets, Math.Max(20, w - inset * 2), Math.Max(10, h - 8), maxFs);
            if (_seenSizes != null)
                _seenSizes[key] = _seenSizes.TryGetValue(key, out var seen) ? Math.Min(seen, fit.Fs) : fit.Fs;
            double top = y + (h - fit.Height) / 2;
            bool left = alignLeft;
            double tx = left ? x + inset : x + w / 2;
            string anchor = left ? "start" : "middle";
            string haloAttrs = halo ? " stroke=\"#ffffff\" stroke-opacity=\"0.75\" stroke-width=\"3\" stroke-linejoin=\"round\" paint-order=\"stroke\"" : "";
            sb.Append($"<text fill=\"{color}\" font-weight=\"600\" font-size=\"{F(fit.Fs)}\" text-anchor=\"{anchor}\"{haloAttrs}>");
            for (int i = 0; i < fit.Title.Count; i++)
                sb.Append($"<tspan x=\"{F(tx)}\" y=\"{F(top + fit.Fs * (0.95 + i * 1.22))}\">{E(fit.Title[i])}</tspan>");
            sb.Append("</text>");
            if (fit.Bullets.Count == 0) return;
            // Bullets read left-aligned; in a centred box the bullet block itself is centred.
            bool bulletsLeft = left || w >= 120;
            double groupW = fit.Bullets.Max(l => l.Length) * CharW(fit.Bfs, false);
            double bx = !bulletsLeft ? x + w / 2 : left ? x + inset : x + Math.Max(inset, (w - groupW) / 2);
            double by = top + fit.Title.Count * fit.Fs * 1.22 + 6;
            sb.Append($"<text fill=\"{color}\" font-size=\"{F(fit.Bfs)}\" text-anchor=\"{(bulletsLeft ? "start" : "middle")}\" opacity=\"0.92\"{haloAttrs}>");
            for (int i = 0; i < fit.Bullets.Count; i++)
                sb.Append($"<tspan x=\"{F(bx)}\" y=\"{F(by + fit.Bfs * (0.95 + i * 1.3))}\">{E(fit.Bullets[i])}</tspan>");
            sb.Append("</text>");
        }

        private static string Tooltip(Item item) =>
            "<title>" + E(item.Children.Count == 0 ? item.Text : item.Text + "\n" + string.Join("\n", item.Bullets.Select(b => "• " + b.Replace('\u00A0', ' ')))) + "</title>";

        private static void Box(StringBuilder sb, double x, double y, double w, double h, string fill, Item item, bool withBullets = true, double rx = 6, string? textColor = null, bool alignLeft = false, string? stroke = null)
        {
            sb.Append("<g class=\"sa-s\">").Append(Tooltip(item));
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(rx)}\" fill=\"{fill}\" stroke=\"{stroke ?? "#ffffff"}\" stroke-width=\"1.5\"/>");
            Text(sb, x, y, w, h, item.Text, withBullets ? item.Bullets : Array.Empty<string>(), textColor ?? "#ffffff", alignLeft: alignLeft);
            sb.Append("</g>");
        }

        private static void Circle(StringBuilder sb, double cx, double cy, double r, string fill, Item item, bool withBullets = true, string? textColor = null, double opacity = 1)
        {
            sb.Append("<g class=\"sa-s\">").Append(Tooltip(item));
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{fill}\" fill-opacity=\"{F(opacity)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            // A 1.5r × 1.3r box still sits inside the circle (its corner is at 0.99r) and gives a
            // line of text more room than the inscribed square did.
            double tw = r * CircleTextW, th = r * 1.3;
            Text(sb, cx - tw / 2, cy - th / 2, tw, th, item.Text, withBullets ? item.Bullets : Array.Empty<string>(), textColor ?? "#ffffff", maxFs: 14, inset: 3);
            sb.Append("</g>");
        }

        private const double CircleTextW = 1.5;

        /// <summary>Radius at which the longest word of any label fits a circle on one line at 10 pt,
        /// so "Internationalisation" grows its circle instead of being cut into "Internati-onalisati-on…".</summary>
        private static double RadiusForWords(IEnumerable<Item> items) =>
            (items.SelectMany(i => (i.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                  .Select(word => word.Length).DefaultIfEmpty(0).Max() * CharW(10, true) + 10) / CircleTextW;

        private static void RightArrow(StringBuilder sb, double x, double cy, double w, double h)
        {
            double sh = h * 0.45;
            sb.Append($"<polygon points=\"{F(x)},{F(cy - sh / 2)} {F(x + w * 0.55)},{F(cy - sh / 2)} {F(x + w * 0.55)},{F(cy - h / 2)} {F(x + w)},{F(cy)} {F(x + w * 0.55)},{F(cy + h / 2)} {F(x + w * 0.55)},{F(cy + sh / 2)} {F(x)},{F(cy + sh / 2)}\" fill=\"#c8c6c4\"/>");
        }

        private static void DownArrow(StringBuilder sb, double cx, double y, double w, double h)
        {
            double sw = w * 0.45;
            sb.Append($"<polygon points=\"{F(cx - sw / 2)},{F(y)} {F(cx + sw / 2)},{F(y)} {F(cx + sw / 2)},{F(y + h * 0.5)} {F(cx + w / 2)},{F(y + h * 0.5)} {F(cx)},{F(y + h)} {F(cx - w / 2)},{F(y + h * 0.5)} {F(cx - sw / 2)},{F(y + h * 0.5)}\" fill=\"#c8c6c4\"/>");
        }

        // ------------------------------------------------------------------ hierarchies

        private sealed class TNode
        {
            public Item Item = null!;
            public List<TNode> Kids = new();
            public int Depth;
            public double Pos;   // along the sibling axis, in leaf units
            public double Leaves;
            public double Start; // first leaf slot this node's subtree covers
            public bool Hung;    // children stack vertically under it instead of spreading out
        }

        /// <param name="hang">Org-chart "hanging" layout: a parent whose children are all leaves
        /// stacks them beneath itself, so a wide chart keeps readable boxes instead of shrinking.</param>
        private static (List<TNode> roots, int leaves, int depth) BuildTree(List<Item> items, bool hang = false)
        {
            int maxDepth = 0;
            TNode Make(Item i, int d)
            {
                maxDepth = Math.Max(maxDepth, d);
                var n = new TNode { Item = i, Depth = d };
                foreach (var c in i.Children) n.Kids.Add(Make(c, d + 1));
                return n;
            }
            var roots = items.Select(i => Make(i, 0)).ToList();
            double cursor = 0;
            void Place(TNode n)
            {
                n.Start = cursor;
                if (n.Kids.Count == 0) { n.Pos = cursor + 0.5; n.Leaves = 1; cursor += 1; return; }
                if (hang && n.Kids.All(k => k.Kids.Count == 0))
                {
                    n.Hung = true; n.Pos = cursor + 0.5; n.Leaves = 1; cursor += 1;
                    return;
                }
                foreach (var k in n.Kids) Place(k);
                n.Pos = (n.Kids[0].Pos + n.Kids[^1].Pos) / 2;
                n.Leaves = n.Kids.Sum(k => k.Leaves);
            }
            foreach (var r in roots) Place(r);
            if (hang)
            {
                // Hung children don't add a level of their own.
                maxDepth = 0;
                void Deepest(TNode n) { maxDepth = Math.Max(maxDepth, n.Depth); if (!n.Hung) foreach (var k in n.Kids) Deepest(k); }
                foreach (var r in roots) Deepest(r);
            }
            return (roots, (int)cursor, maxDepth + 1);
        }

        // Hung children are drawn by their parent, so the walk stops there.
        private static IEnumerable<TNode> All(IEnumerable<TNode> roots) => roots.SelectMany(r => new[] { r }.Concat(r.Hung ? Enumerable.Empty<TNode>() : All(r.Kids)));

        private static (double, double) DrawTree(StringBuilder sb, List<Item> items, bool horizontal)
        {
            var edges = new StringBuilder();
            var nodes = new StringBuilder();

            if (!horizontal)
            {
                var (roots, leaves, depth) = BuildTree(items);
                // More than six side-by-side leaves won't stay legible at 800 px: hang them instead.
                if (leaves > 6) (roots, leaves, depth) = BuildTree(items, hang: true);
                double slot = Math.Max(118, (BaseW - Pad * 2) / Math.Max(1, leaves));
                double w = Math.Max(BaseW, Pad * 2 + leaves * slot);
                double boxW = Math.Min(170, slot - 14), boxH = 56, gapY = 44, kidH = 40, kidGap = 8, indent = 18;
                double ox = (w - leaves * slot) / 2;
                double X(TNode n) => ox + n.Pos * slot;
                double Y(TNode n) => Pad + n.Depth * (boxH + gapY);
                double bottom = Pad + depth * boxH + (depth - 1) * gapY;
                foreach (var n in All(roots))
                {
                    if (n.Hung)
                    {
                        double left = X(n) - boxW / 2;
                        for (int j = 0; j < n.Kids.Count; j++)
                        {
                            double ky = Y(n) + boxH + kidGap + j * (kidH + kidGap);
                            edges.Append($"<path d=\"M{F(left + indent / 2)} {F(Y(n) + boxH)}V{F(ky + kidH / 2)}H{F(left + indent)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                            Box(nodes, left + indent, ky, boxW - indent, kidH, LevelColors[(n.Depth + 1) % LevelColors.Length], n.Kids[j].Item, withBullets: false);
                            bottom = Math.Max(bottom, ky + kidH);
                        }
                        Box(nodes, left, Y(n), boxW, boxH, LevelColors[n.Depth % LevelColors.Length], n.Item, withBullets: false);
                        continue;
                    }
                    foreach (var k in n.Kids)
                    {
                        double midY = Y(n) + boxH + gapY / 2;
                        edges.Append($"<path d=\"M{F(X(n))} {F(Y(n) + boxH)}V{F(midY)}H{F(X(k))}V{F(Y(k))}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                    }
                    Box(nodes, X(n) - boxW / 2, Y(n), boxW, boxH, LevelColors[n.Depth % LevelColors.Length], n.Item, withBullets: false);
                }
                sb.Append(edges).Append(nodes);
                return (w, bottom + Pad);
            }
            else
            {
                var (roots, leaves, depth) = BuildTree(items);
                double slot = 64;
                double h = Math.Max(240, Pad * 2 + leaves * slot);
                double colW = (BaseW - Pad * 2) / Math.Max(1, depth);
                double boxW = Math.Min(190, colW - 36), boxH = 48;
                double oy = (h - leaves * slot) / 2;
                double X(TNode n) => Pad + n.Depth * colW + (colW - boxW) / 2;
                double Y(TNode n) => oy + n.Pos * slot;
                foreach (var n in All(roots))
                {
                    foreach (var k in n.Kids)
                    {
                        double midX = X(n) + boxW + (colW - boxW) / 2;
                        edges.Append($"<path d=\"M{F(X(n) + boxW)} {F(Y(n))}H{F(midX)}V{F(Y(k))}H{F(X(k))}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                    }
                    Box(nodes, X(n), Y(n) - boxH / 2, boxW, boxH, LevelColors[n.Depth % LevelColors.Length], n.Item, withBullets: false);
                }
                sb.Append(edges).Append(nodes);
                return (BaseW, h);
            }
        }

        /// <summary>Architecture / table hierarchy: each item is a block spanning the columns of its
        /// descendants, one row per level.</summary>
        private static (double, double) DrawBlockHierarchy(StringBuilder sb, List<Item> items)
        {
            var (roots, leaves, depth) = BuildTree(items);
            double slot = Math.Max(96, (BaseW - Pad * 2) / Math.Max(1, leaves));
            double w = Math.Max(BaseW, Pad * 2 + leaves * slot);
            double rowH = 58, gap = 8;
            double h = Pad * 2 + depth * rowH + (depth - 1) * gap;
            double ox = (w - leaves * slot) / 2;
            foreach (var n in All(roots))
            {
                double x = ox + n.Start * slot + gap / 2;
                double y = Pad + n.Depth * (rowH + gap);
                // Leaves stretch down to the bottom row so the blocks read as a solid stack.
                double bh = n.Kids.Count == 0 ? rowH + (depth - 1 - n.Depth) * (rowH + gap) : rowH;
                Box(sb, x, y, n.Leaves * slot - gap, bh, LevelColors[n.Depth % LevelColors.Length], n.Item, withBullets: false, rx: 4);
            }
            return (w, h);
        }

        /// <summary>Hierarchy list: each top-level item heads a column; its children stack under it.</summary>
        private static (double, double) DrawHierarchyList(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double gap = 18;
            double colW = Math.Max(120, (BaseW - Pad * 2 - (n - 1) * gap) / n);
            double w = Math.Max(BaseW, Pad * 2 + n * colW + (n - 1) * gap);
            double headH = 52, childGap = 10, maxBottom = 0;
            var childHeights = items.Select(i => i.Children.Select(c => Math.Max(44, Measure(c.Text, c.Bullets, colW - 40) + 16)).ToList()).ToList();
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * (colW + gap);
                string color = Accent(i);
                Box(sb, x, Pad, colW, headH, color, items[i], withBullets: false);
                double y = Pad + headH + 14;
                for (int c = 0; c < items[i].Children.Count; c++)
                {
                    double ch = childHeights[i][c];
                    sb.Append($"<path d=\"M{F(x + 12)} {F(y - 14)}V{F(y + ch / 2)}H{F(x + 22)}\" fill=\"none\" stroke=\"{Tint(color, 0.35)}\" stroke-width=\"2\"/>");
                    Box(sb, x + 22, y, colW - 22, ch, Tint(color, 0.82), items[i].Children[c], textColor: Ink, stroke: Tint(color, 0.55));
                    y += ch + childGap;
                }
                maxBottom = Math.Max(maxBottom, y);
            }
            return (w, Math.Max(maxBottom, Pad + headH) + Pad);
        }

        // ------------------------------------------------------------------ lists

        private static int Columns(int n) => n <= 3 ? n : n == 4 ? 2 : n <= 6 ? 3 : 4;

        private static (double, double) DrawBlockList(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = Columns(n);
            double gap = 16;
            double bw = (BaseW - Pad * 2 - (cols - 1) * gap) / cols;
            double y = Pad;
            for (int r = 0; r * cols < n; r++)
            {
                var row = items.Skip(r * cols).Take(cols).ToList();
                double bh = Math.Min(220, Math.Max(bw * 0.55, row.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 24));
                double rowW = row.Count * bw + (row.Count - 1) * gap;
                double x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < row.Count; c++)
                    Box(sb, x0 + c * (bw + gap), y, bw, bh, Accent(r * cols + c), row[c]);
                y += bh + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        private static (double, double) DrawHorizontalList(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            int rows = (int)Math.Ceiling(items.Count / (double)n);
            double gap = 14;
            double cw = (BaseW - Pad * 2 - (n - 1) * gap) / n;
            double y = Pad;
            for (int r = 0; r < rows; r++)
            {
                var row = items.Skip(r * n).Take(n).ToList();
                double headH = Math.Max(48, row.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 20)) + 20);
                bool anyBody = row.Any(i => i.Children.Count > 0);
                double bodyH = anyBody ? Math.Max(70, row.Max(i => Measure("", i.Bullets, cw - 20)) + 24) : 0;
                for (int c = 0; c < row.Count; c++)
                {
                    double x = Pad + c * (cw + gap);
                    string color = Accent(r * n + c);
                    if (anyBody)
                    {
                        sb.Append("<g class=\"sa-s\">").Append(Tooltip(row[c]));
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + headH - 6)}\" width=\"{F(cw)}\" height=\"{F(bodyH + 6)}\" rx=\"4\" fill=\"{Tint(color, 0.85)}\" stroke=\"{Tint(color, 0.6)}\"/>");
                        Text(sb, x, y + headH, cw, bodyH, "", row[c].Bullets, Ink, maxFs: 13, alignLeft: true);
                        sb.Append("</g>");
                    }
                    Box(sb, x, y, cw, headH, color, row[c], withBullets: false, rx: 4);
                }
                y += headH + bodyH + gap * 1.5;
            }
            return (BaseW, y - gap * 1.5 + Pad);
        }

        private static (double, double) DrawVerticalList(StringBuilder sb, List<Item> items)
        {
            double gap = 10, w = BaseW - Pad * 2;
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double tabW = anyBody ? 210 : w;
            double y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string color = Accent(i);
                double rh = Math.Max(50, Math.Max(Measure(item.Text, Array.Empty<string>(), tabW - 24), anyBody ? Measure("", item.Bullets, w - tabW - 30) : 0) + 22);
                if (anyBody)
                {
                    sb.Append("<g class=\"sa-s\">").Append(Tooltip(item));
                    sb.Append($"<rect x=\"{F(Pad + tabW - 8)}\" y=\"{F(y)}\" width=\"{F(w - tabW + 8)}\" height=\"{F(rh)}\" rx=\"4\" fill=\"{Tint(color, 0.86)}\" stroke=\"{Tint(color, 0.6)}\"/>");
                    Text(sb, Pad + tabW + 6, y, w - tabW - 6, rh, "", item.Bullets, Ink, maxFs: 13, alignLeft: true);
                    sb.Append("</g>");
                }
                Box(sb, Pad, y, tabW, rh, color, item, withBullets: false, rx: 4, alignLeft: !anyBody);
                y += rh + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        // ------------------------------------------------------------------ processes

        private static (double, double) DrawProcess(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double arrowW = 30, gap = 10;
            double bw = Math.Min(200, (BaseW - Pad * 2 - (n - 1) * (arrowW + gap * 2)) / n);
            if (bw < 92) return DrawBending(sb, items);
            double bh = Math.Min(260, Math.Max(80, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 26));
            double total = n * bw + (n - 1) * (arrowW + gap * 2);
            double x = (BaseW - total) / 2, y = Pad;
            for (int i = 0; i < n; i++)
            {
                Box(sb, x, y, bw, bh, Accent(i), items[i]);
                if (i < n - 1) RightArrow(sb, x + bw + gap, y + bh / 2, arrowW, 30);
                x += bw + arrowW + gap * 2;
            }
            return (BaseW, bh + Pad * 2);
        }

        private static (double, double) DrawChevrons(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double point = 22, overlap = 8;
            double cw = Math.Min(220, (BaseW - Pad * 2 - point + (n - 1) * overlap) / n);
            double ch = 64;
            double total = n * cw - (n - 1) * overlap + point;
            double x0 = (BaseW - total) / 2, y = Pad;
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(220, items.Max(i => Measure("", i.Bullets, cw - overlap - 26, 13)) + 20) : 0;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (cw - overlap);
                string notch = i == 0 ? $"{F(x)},{F(y + ch)}" : $"{F(x)},{F(y + ch)} {F(x + point)},{F(y + ch / 2)}";
                string pts = $"{F(x)},{F(y)} {F(x + cw)},{F(y)} {F(x + cw + point)},{F(y + ch / 2)} {F(x + cw)},{F(y + ch)} {notch}";
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(items[i]));
                sb.Append($"<polygon points=\"{pts}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, x + (i == 0 ? 4 : point), y, cw - (i == 0 ? 4 : point), ch, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                if (anyBody && items[i].Children.Count > 0)
                {
                    double bodyW = cw - overlap - 6;
                    sb.Append($"<rect x=\"{F(x + point / 2)}\" y=\"{F(y + ch + 10)}\" width=\"{F(bodyW)}\" height=\"{F(bodyH)}\" rx=\"4\" fill=\"{Tint(Accent(i), 0.86)}\" stroke=\"{Tint(Accent(i), 0.6)}\"/>");
                    Text(sb, x + point / 2, y + ch + 10, bodyW, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true);
                }
                sb.Append("</g>");
            }
            return (BaseW, y + ch + (anyBody ? bodyH + 10 : 0) + Pad);
        }

        private static (double, double) DrawVerticalProcess(StringBuilder sb, List<Item> items)
        {
            double bw = 420, arrowH = 26, gap = 8;
            double x = (BaseW - bw) / 2, y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                double bh = Math.Max(54, Measure(items[i].Text, items[i].Bullets, bw - 20) + 22);
                Box(sb, x, y, bw, bh, Accent(i), items[i]);
                y += bh;
                if (i < items.Count - 1) { DownArrow(sb, BaseW / 2, y + gap, 34, arrowH); y += arrowH + gap * 2; }
            }
            return (BaseW, y + Pad);
        }

        private static (double, double) DrawBending(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = n <= 4 ? Math.Max(1, n) : n <= 9 ? 3 : 4;
            double gapX = 44, gapY = 46;
            double bw = (BaseW - Pad * 2 - (cols - 1) * gapX) / cols;
            double bh = Math.Min(200, Math.Max(76, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 24));
            var pos = new List<(double x, double y)>();
            for (int i = 0; i < n; i++) pos.Add((Pad + (i % cols) * (bw + gapX), Pad + (i / cols) * (bh + gapY)));
            for (int i = 0; i < n - 1; i++)
            {
                var (ax, ay) = pos[i];
                var (bx, by) = pos[i + 1];
                if (by == ay)
                    sb.Append($"<line x1=\"{F(ax + bw + 4)}\" y1=\"{F(ay + bh / 2)}\" x2=\"{F(bx - 6)}\" y2=\"{F(by + bh / 2)}\" stroke=\"{Connector}\" stroke-width=\"2.5\" marker-end=\"url(#sa-arrow)\"/>");
                else
                {
                    double midY = ay + bh + gapY / 2;
                    sb.Append($"<path d=\"M{F(ax + bw / 2)} {F(ay + bh + 2)}V{F(midY)}H{F(bx + bw / 2)}V{F(by - 6)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"2.5\" marker-end=\"url(#sa-arrow)\"/>");
                }
            }
            for (int i = 0; i < n; i++) Box(sb, pos[i].x, pos[i].y, bw, bh, Accent(i), items[i]);
            int rows = (n + cols - 1) / cols;
            return (BaseW, Pad * 2 + rows * bh + (rows - 1) * gapY);
        }

        private static (double, double) DrawSteps(StringBuilder sb, List<Item> items, bool up)
        {
            int n = items.Count;
            double gap = 8;
            double bw = Math.Min(200, (BaseW - Pad * 2 - (n - 1) * gap) / n);
            double bh = Math.Min(200, Math.Max(64, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 22));
            double rise = Math.Min(48, bh * 0.6);
            double total = n * bw + (n - 1) * gap;
            double x0 = (BaseW - total) / 2;
            double h = Pad * 2 + bh + (n - 1) * rise;
            double Y(int i) => Pad + (up ? (n - 1 - i) : i) * rise;
            // A thin stair line running under the boxes shows the direction of travel.
            var stair = new StringBuilder($"M{F(x0)} {F(Y(0) + bh + 6)}");
            for (int i = 0; i < n; i++)
            {
                stair.Append($"H{F(x0 + i * (bw + gap) + bw)}");
                if (i < n - 1) stair.Append($"V{F(Y(i + 1) + bh + 6)}");
            }
            sb.Append($"<path d=\"{stair}\" fill=\"none\" stroke=\"#c8c6c4\" stroke-width=\"3\" stroke-linejoin=\"round\" marker-end=\"url(#sa-arrow)\"/>");
            for (int i = 0; i < n; i++) Box(sb, x0 + i * (bw + gap), Y(i), bw, bh, Accent(i), items[i]);
            return (BaseW, h + 8);
        }

        private static (double, double) DrawTimeline(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double span = BaseW - Pad * 2 - 40;
            double step = span / n;
            double labelW = Math.Min(190, step * 1.8 - 12);
            double labelH = Math.Min(170, Math.Max(44, items.Max(i => Measure(i.Text, i.Bullets, labelW - 8, 13)) + 10));
            double stem = 26;
            double lineY = Pad + labelH + stem + 8;
            // Labels alternate above and below the line; a lone item has nothing below it.
            double h = n == 1 ? lineY + 12 + Pad : lineY + 8 + stem + labelH + Pad;
            sb.Append($"<line x1=\"{F(Pad)}\" y1=\"{F(lineY)}\" x2=\"{F(BaseW - Pad)}\" y2=\"{F(lineY)}\" stroke=\"#c8c6c4\" stroke-width=\"4\" marker-end=\"url(#sa-arrow)\"/>");
            for (int i = 0; i < n; i++)
            {
                double cx = Pad + 20 + (i + 0.5) * step;
                bool above = i % 2 == 0;
                string color = Accent(i);
                double ly = above ? lineY - stem - labelH : lineY + stem;
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(items[i]));
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(lineY)}\" x2=\"{F(cx)}\" y2=\"{F(above ? ly + labelH : ly)}\" stroke=\"{color}\" stroke-width=\"2\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(lineY)}\" r=\"9\" fill=\"{color}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                // The end labels slide inwards rather than hang past the drawing's edge.
                double lx = Math.Clamp(cx - labelW / 2, Pad / 2, BaseW - Pad / 2 - labelW);
                sb.Append($"<rect x=\"{F(lx)}\" y=\"{F(ly)}\" width=\"{F(labelW)}\" height=\"{F(labelH)}\" rx=\"6\" fill=\"#ffffff\" stroke=\"{Tint(color, 0.5)}\"/>");
                Text(sb, lx, ly, labelW, labelH, items[i].Text, items[i].Bullets, Ink, maxFs: 13);
                sb.Append("</g>");
            }
            return (BaseW, h);
        }

        // ------------------------------------------------------------------ cycles & relationships

        private static (double, double) DrawCycle(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (n == 1) { Circle(sb, BaseW / 2, 170, 120, Accent(0), items[0]); return (BaseW, 340); }
            double R = n <= 3 ? 130 : n <= 6 ? 160 : 180;
            double s = Math.Sin(Math.PI / n) * 0.82;
            double r = Math.Max(Math.Min(64, R * s), Math.Min(86, RadiusForWords(items)));
            R = Math.Max(R, r / s); // a grown circle pushes the ring out rather than into its neighbours
            double cx = BaseW / 2, cy = Pad + R + r;
            double h = cy + R + r + Pad;
            double delta = (r + 10) / R;
            for (int i = 0; i < n; i++)
            {
                double a1 = 2 * Math.PI * i / n - Math.PI / 2 + delta;
                double a2 = 2 * Math.PI * (i + 1) / n - Math.PI / 2 - delta;
                if (a2 <= a1) continue;
                sb.Append($"<path d=\"M{F(cx + R * Math.Cos(a1))} {F(cy + R * Math.Sin(a1))}A{F(R)} {F(R)} 0 0 1 {F(cx + R * Math.Cos(a2))} {F(cy + R * Math.Sin(a2))}\" fill=\"none\" stroke=\"#c8c6c4\" stroke-width=\"3\" marker-end=\"url(#sa-arrow)\"/>");
            }
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n - Math.PI / 2;
                Circle(sb, cx + R * Math.Cos(a), cy + R * Math.Sin(a), r, Accent(i), items[i]);
            }
            return (BaseW, h);
        }

        private static (double, double) DrawRadial(StringBuilder sb, List<Item> items)
        {
            Item center;
            List<Item> around;
            if (items[0].Children.Count > 0) { center = new Item { Text = items[0].Text }; around = items[0].Children.Concat(items.Skip(1)).ToList(); }
            else { center = items[0]; around = items.Skip(1).ToList(); }
            int n = around.Count;
            if (n == 0) { Circle(sb, BaseW / 2, 170, 110, Accent(0), center); return (BaseW, 340); }
            double R = n <= 4 ? 150 : 175;
            double s = Math.Sin(Math.PI / Math.Max(2, n)) * 0.8;
            double r = Math.Max(Math.Min(56, R * s), Math.Min(80, RadiusForWords(around)));
            double rc = Math.Max(Math.Min(78, R - r - 12), Math.Min(100, Math.Max(RadiusForWords(new[] { center }), r * 1.1))); // the hub stays the biggest circle
            R = Math.Max(R, Math.Max(r / s, rc + r + 12));
            double cx = BaseW / 2, cy = Pad + R + r;
            double h = cy + R + r + Pad;
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n - Math.PI / 2;
                sb.Append($"<line x1=\"{F(cx + rc * Math.Cos(a))}\" y1=\"{F(cy + rc * Math.Sin(a))}\" x2=\"{F(cx + (R - r) * Math.Cos(a))}\" y2=\"{F(cy + (R - r) * Math.Sin(a))}\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            }
            Circle(sb, cx, cy, rc, Accent(0), center, withBullets: false);
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n - Math.PI / 2;
                Circle(sb, cx + R * Math.Cos(a), cy + R * Math.Sin(a), r, Accent(i + 1), around[i]);
            }
            return (BaseW, h);
        }

        private static readonly string[] MatrixDefaultTitles = { "Quadrant 1", "Quadrant 2", "Quadrant 3", "Quadrant 4" };

        private static (double, double) DrawMatrix(StringBuilder sb, List<Item> items)
        {
            // One item with children = a titled matrix: the item sits in the centre, its children
            // fill the quadrants.
            Item? title = items.Count == 1 && items[0].Children.Count > 0 ? items[0] : null;
            var cells = title != null ? title.Children : items;
            double gap = 10, size = 420;
            double cw = (Math.Min(BaseW - Pad * 2, 560) - gap) / 2, ch = (size - gap) / 2;
            double x0 = (BaseW - (cw * 2 + gap)) / 2, y0 = Pad;
            for (int i = 0; i < 4; i++)
            {
                double x = x0 + (i % 2) * (cw + gap), y = y0 + (i / 2) * (ch + gap);
                if (i < cells.Count)
                    Box(sb, x, y, cw, ch, Accent(i), cells[i], rx: 8);
                else
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"8\" fill=\"#ffffff\" stroke=\"#c8c6c4\" stroke-dasharray=\"6 4\"/>");
                    sb.Append($"<text x=\"{F(x + cw / 2)}\" y=\"{F(y + ch / 2 + 5)}\" text-anchor=\"middle\" font-size=\"13\" fill=\"#a19f9d\">{MatrixDefaultTitles[i]}</text>");
                }
            }
            if (title != null)
                Circle(sb, x0 + cw + gap / 2, y0 + ch + gap / 2, 62, "#ffffff", new Item { Text = title.Text }, withBullets: false, textColor: Ink);
            if (cells.Count > 4)
                sb.Append($"<text x=\"{F(BaseW / 2)}\" y=\"{F(y0 + size + 22)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#605e5c\">A matrix shows four items — {cells.Count - 4} more not shown.</text>");
            return (BaseW, y0 + size + (cells.Count > 4 ? 34 : 0) + Pad);
        }

        private static (double, double) DrawPyramid(StringBuilder sb, List<Item> nodes, bool inverted)
        {
            int n = nodes.Count;
            // Few tiers get taller slices, and the base narrows with the height so a one- or
            // three-tier pyramid keeps a pyramid's proportions instead of flattening into a wedge.
            double layerH = Math.Max(40, Math.Min(n <= 2 ? 110 : 70, 420.0 / n));
            double h = Pad * 2 + n * layerH;
            double cx = BaseW / 2;
            double baseW = Math.Min(640, Math.Max(260, n * layerH * 2.2)), apexW = inverted ? Math.Min(150, baseW * 0.4) : 0;
            double WidthAt(double f) => inverted ? baseW - f * (baseW - apexW) : apexW + f * (baseW - apexW);
            for (int i = 0; i < n; i++)
            {
                double yTop = Pad + i * layerH, yBot = yTop + layerH - 3;
                // Normal: the apex is a point and the base is widest. Inverted: wide top, narrow neck.
                double wTop = WidthAt((double)i / n), wBot = WidthAt((double)(i + 1) / n);
                string pts = $"{F(cx - wTop / 2)},{F(yTop)} {F(cx + wTop / 2)},{F(yTop)} {F(cx + wBot / 2)},{F(yBot)} {F(cx - wBot / 2)},{F(yBot)}";
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(nodes[i]));
                sb.Append($"<polygon points=\"{pts}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                // A label's width is the slice's width at the label's top line. The apex is a point,
                // so its label tries ever lower (and wider) spots before giving up on the inside.
                bool apex = !inverted && i == 0;
                double[] spots = apex ? new[] { 0.3, 0.5, 0.62 } : new[] { inverted ? 0.0 : 0.15 };
                double ty = yTop, textW = 0;
                bool fits = false;
                // First choice: a spot where the label fits on one line; failing that, any spot.
                foreach (int lines in new[] { 1, 3 })
                {
                    foreach (var spot in spots)
                    {
                        ty = yTop + (yBot - yTop) * spot;
                        textW = wTop + (wBot - wTop) * spot - 12;
                        if (inverted) textW = Math.Min(textW, wBot - 12 + (wTop - wBot) * 0.2);
                        if (fits = FitsClean(nodes[i].Text, textW - 8, yBot - ty - 8, 10, lines)) break;
                    }
                    if (fits) break;
                }
                if (fits)
                {
                    // The apex keeps its own size so a tight tip doesn't shrink every tier's text.
                    Text(sb, cx - textW / 2, ty, textW, yBot - ty, nodes[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 4, group: apex ? "apex" : "tier");
                }
                else
                {
                    // Too narrow to hold the words at a readable size (the tip of a tall pyramid): a
                    // leader to a label beside it, clear of the wider slices below.
                    double midY = (yTop + yBot) / 2;
                    double clear = inverted ? Math.Max(wTop, wBot) : WidthAt(Math.Min(1, (i + 2.0) / n));
                    double lx = cx + clear / 2 + 18, lw = BaseW - Pad - lx;
                    sb.Append($"<path d=\"M{F(cx)} {F(midY)}H{F(lx - 6)}\" fill=\"none\" stroke=\"{Ink}\" stroke-width=\"1\" stroke-opacity=\"0.45\"/>");
                    sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(midY)}\" r=\"3.5\" fill=\"#ffffff\" stroke=\"{Ink}\" stroke-width=\"1\"/>");
                    Text(sb, lx, midY - 30, lw, 60, nodes[i].Text, Array.Empty<string>(), Ink, maxFs: 13, alignLeft: true, inset: 0, group: "callout");
                }
                sb.Append("</g>");
            }
            return (BaseW, h);
        }

        private static (double, double) DrawVenn(StringBuilder sb, List<Item> items)
        {
            // Word's Basic Venn draws every set, so this does too: past six the circles shrink and
            // spread round a wider ring (each still overlapping its neighbours).
            int n = Math.Min(items.Count, MaxVennSets);
            double cx = BaseW / 2;
            double r = n == 1 ? 130 : n == 2 ? 130 : n == 3 ? 115 : n <= 6 ? 95 : 78;
            double ring = n == 1 ? 0 : n == 2 ? 80 : n == 3 ? 72 : n <= 6 ? 85 : r / Math.Sin(Math.PI / n) * 0.78;
            var centers = new List<(double x, double y, double a)>();
            for (int i = 0; i < n; i++)
            {
                double a = n == 2 ? (i == 0 ? Math.PI : 0) : 2 * Math.PI * i / n - Math.PI / 2;
                centers.Add((cx + ring * Math.Cos(a), ring * Math.Sin(a), a));
            }
            double minY = centers.Min(c => c.y) - r, maxY = centers.Max(c => c.y) + r;
            for (int i = 0; i < n; i++) centers[i] = (centers[i].x, centers[i].y - minY + Pad, centers[i].a);
            double cy = Pad + (maxY - minY) / 2, extentBottom = Pad + (maxY - minY);
            for (int i = 0; i < n; i++)
            {
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(items[i]));
                sb.Append($"<circle cx=\"{F(centers[i].x)}\" cy=\"{F(centers[i].y)}\" r=\"{F(r)}\" fill=\"{Accent(i)}\" fill-opacity=\"0.55\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                sb.Append("</g>");
            }
            // Labels go in each circle's own (non-overlapping) lobe, on top of every circle.
            for (int i = 0; i < n; i++)
            {
                double push = n == 1 ? 0 : r * 0.42;
                double lx = centers[i].x + push * Math.Cos(centers[i].a), ly = centers[i].y + push * Math.Sin(centers[i].a);
                // A label may run past its lobe into the overlaps; the halo keeps it readable there.
                double tw = r * (n == 1 ? 1.4 : n == 2 ? 0.95 : 1.2);
                double th = n == 1 ? r * 1.4 : Math.Max(80, r * 0.9);
                Text(sb, lx - tw / 2, ly - th / 2, tw, th, items[i].Text, n == 1 ? items[i].Bullets : Array.Empty<string>(), Ink, maxFs: 14, inset: n > 2 ? 4 : 10, halo: n > 2);
            }
            if (items.Count > n)
                sb.Append($"<text x=\"{F(cx)}\" y=\"{F(extentBottom + 18)}\" text-anchor=\"middle\" font-size=\"12\" fill=\"#605e5c\">{items.Count - n} more not shown.</text>");
            return (BaseW, extentBottom + Pad + (items.Count > n ? 16 : 0));
        }

        private const int MaxVennSets = 12;

        private static (double, double) DrawLinearVenn(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = Math.Min(110, (BaseW - Pad * 2) / (2 + (n - 1) * 1.45) );
            double step = r * 1.45;
            double total = 2 * r + (n - 1) * step;
            double x0 = (BaseW - total) / 2 + r, cy = Pad + r;
            for (int i = 0; i < n; i++)
            {
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(items[i]));
                sb.Append($"<circle cx=\"{F(x0 + i * step)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{Accent(i)}\" fill-opacity=\"0.55\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                sb.Append("</g>");
            }
            for (int i = 0; i < n; i++)
            {
                double tw = step * 0.9;
                Text(sb, x0 + i * step - tw / 2, cy - r * 0.6, tw, r * 1.2, items[i].Text, Array.Empty<string>(), Ink, maxFs: 14);
            }
            return (BaseW, cy + r + Pad);
        }

        /// <summary>Nested rings resting on a common base (Office's target / stacked-venn look): the
        /// first item is the outer ring, each ring's visible band carries a level leader to its label.</summary>
        private static (double, double) DrawTarget(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 7);
            double rMax = 170, rMin = Math.Max(34, rMax / (n + 0.6));
            double step = n == 1 ? 0 : (rMax - rMin) / (n - 1);
            double cx = Pad + rMax + 10, bottom = Pad + 2 * rMax;
            double labelX = cx + rMax + 36, labelW = BaseW - labelX - Pad;
            double R(int i) => rMax - i * step;
            for (int i = 0; i < n; i++)
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(bottom - R(i))}\" r=\"{F(R(i))}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            for (int i = 0; i < n; i++)
            {
                // The band between this ring's top and the next ring's top (the innermost: its middle).
                double top = bottom - 2 * R(i);
                double bandY = i < n - 1 ? top + step : bottom - R(i);
                // A lone ring has the whole height to itself (its bullets were cut to "CEO …").
                double slotH = n == 1 ? 2 * rMax * 0.8 : Math.Max(2 * step, 40);
                sb.Append("<g class=\"sa-s\">").Append(Tooltip(items[i]));
                sb.Append($"<path d=\"M{F(cx)} {F(bandY)}H{F(labelX - 8)}\" fill=\"none\" stroke=\"{Ink}\" stroke-width=\"1\" stroke-opacity=\"0.45\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(bandY)}\" r=\"3.5\" fill=\"#ffffff\" stroke=\"{Ink}\" stroke-width=\"1\"/>");
                Text(sb, labelX, bandY - slotH / 2, labelW, slotH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true);
                sb.Append("</g>");
            }
            if (items.Count > n)
                sb.Append($"<text x=\"{F(labelX)}\" y=\"{F(bottom + 18)}\" font-size=\"12\" fill=\"#605e5c\">{items.Count - n} more not shown.</text>");
            return (BaseW, bottom + Pad + (items.Count > n ? 16 : 0));
        }

        private static (double, double) DrawBalance(StringBuilder sb, List<Item> items)
        {
            // Two sides: two items with children weigh their children; otherwise split the list.
            Item left, right;
            if (items.Count == 2 && items.Any(i => i.Children.Count > 0)) { left = items[0]; right = items[1]; }
            else
            {
                int split = (items.Count + 1) / 2;
                left = new Item { Text = "", Children = items.Take(split).ToList() };
                right = new Item { Text = "", Children = items.Skip(split).ToList() };
            }
            const double bw = 210, bh = 40, gap = 6, headH = 44;
            double Stack(Item side) => (string.IsNullOrEmpty(side.Text) ? 0 : headH + 2) + Math.Min(6, side.Children.Count) * (bh + gap);
            int lw = Math.Min(6, left.Children.Count), rw = Math.Min(6, right.Children.Count);
            double tilt = Math.Sign(lw - rw) * 6 * Math.PI / 180;
            double cx = BaseW / 2, half = 270;
            // The beam sits just low enough for the taller stack (plus the tilt) to clear the top.
            double d = half * Math.Sin(tilt);
            double beamY = Pad + 6 + Math.Max(Stack(left) - d, Stack(right) + d);
            double lx = cx - half * Math.Cos(tilt), ly = beamY + half * Math.Sin(tilt);
            double rx = cx + half * Math.Cos(tilt), ry = beamY - half * Math.Sin(tilt);
            sb.Append($"<polygon points=\"{F(cx)},{F(beamY + 4)} {F(cx - 40)},{F(beamY + 64)} {F(cx + 40)},{F(beamY + 64)}\" fill=\"#8a8886\"/>");
            sb.Append($"<line x1=\"{F(lx)}\" y1=\"{F(ly)}\" x2=\"{F(rx)}\" y2=\"{F(ry)}\" stroke=\"#605e5c\" stroke-width=\"8\" stroke-linecap=\"round\"/>");
            void Side(Item side, double ex, double ey, int accent)
            {
                double y = ey - 6;
                if (!string.IsNullOrEmpty(side.Text))
                {
                    y -= headH + 2;
                    Box(sb, ex - bw / 2 - 10, y, bw + 20, headH, Accent(accent), new Item { Text = side.Text }, withBullets: false);
                }
                foreach (var w in side.Children.Take(6))
                {
                    y -= bh + gap;
                    Box(sb, ex - bw / 2, y, bw, bh, Tint(Accent(accent), 0.3), w, withBullets: false, rx: 4);
                }
            }
            Side(left, lx + 40, ly, 0);
            Side(right, rx - 40, ry, 1);
            return (BaseW, beamY + 64 + Pad);
        }

        private static (double, double) DrawEquation(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (n == 1) { Circle(sb, BaseW / 2, 130, 100, Accent(0), items[0]); return (BaseW, 260); }
            double opW = 36;
            double r = Math.Min(80, (BaseW - Pad * 2 - (n - 1) * opW) / n / 2);
            double total = n * 2 * r + (n - 1) * opW;
            double x = (BaseW - total) / 2, cy = Pad + r;
            for (int i = 0; i < n; i++)
            {
                bool result = i == n - 1;
                Circle(sb, x + r, cy, r, result ? Accent(2) : Accent(0), items[i]);
                x += 2 * r;
                if (i < n - 1)
                {
                    string op = i == n - 2 ? "=" : "+";
                    sb.Append($"<text x=\"{F(x + opW / 2)}\" y=\"{F(cy + 12)}\" text-anchor=\"middle\" font-size=\"34\" font-weight=\"600\" fill=\"#8a8886\">{op}</text>");
                    x += opW;
                }
            }
            return (BaseW, cy + r + Pad);
        }

        /// <summary>Picture layouts: a card per item with a picture placeholder and a caption.</summary>
        private static (double, double) DrawPictures(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = Math.Min(n, 4);
            double gap = 16;
            double cw = Math.Min(220, (BaseW - Pad * 2 - (cols - 1) * gap) / cols);
            double picH = cw * 0.62;
            double capH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, cw - 20, 13)) + 16);
            double y = Pad;
            for (int r = 0; r * cols < n; r++)
            {
                var row = items.Skip(r * cols).Take(cols).ToList();
                double rowW = row.Count * cw + (row.Count - 1) * gap;
                double x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < row.Count; c++)
                {
                    double x = x0 + c * (cw + gap);
                    string color = Accent(r * cols + c);
                    sb.Append("<g class=\"sa-s\">").Append(Tooltip(row[c]));
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(picH + capH)}\" rx=\"6\" fill=\"#ffffff\" stroke=\"#e1dfdd\"/>");
                    sb.Append($"<rect x=\"{F(x + 6)}\" y=\"{F(y + 6)}\" width=\"{F(cw - 12)}\" height=\"{F(picH - 6)}\" rx=\"3\" fill=\"#edebe9\"/>");
                    // Placeholder glyph: a sun over two hills.
                    double gx = x + cw / 2, gy = y + 6 + (picH - 6) / 2;
                    sb.Append($"<circle cx=\"{F(gx + 14)}\" cy=\"{F(gy - 12)}\" r=\"6\" fill=\"#c8c6c4\"/>");
                    sb.Append($"<path d=\"M{F(gx - 28)} {F(gy + 16)}L{F(gx - 8)} {F(gy - 6)}L{F(gx + 4)} {F(gy + 6)}L{F(gx + 12)} {F(gy - 2)}L{F(gx + 28)} {F(gy + 16)}Z\" fill=\"#c8c6c4\"/>");
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + picH + 4)}\" width=\"4\" height=\"{F(capH - 10)}\" fill=\"{color}\"/>");
                    Text(sb, x + 4, y + picH + 2, cw - 4, capH - 4, row[c].Text, row[c].Bullets, Ink, maxFs: 13, alignLeft: true);
                    sb.Append("</g>");
                }
                y += picH + capH + gap;
            }
            return (BaseW, y - gap + Pad);
        }
    }
}
