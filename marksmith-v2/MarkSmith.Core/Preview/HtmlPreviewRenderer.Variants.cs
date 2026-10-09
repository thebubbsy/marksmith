using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MarkSmith.Core.Glox;

namespace MarkSmith.Core.Preview
{
    /// <summary>How one layout differs from the rest of its family. A family is the kind of
    /// drawing (a cycle, a radial, a process); a variant is the layout's own look within it. 176
    /// layouts used to share 25 drawings, so "Basic Cycle", "Block Cycle" and "Segmented Cycle" had
    /// the same thumbnail and the same preview, and the gallery couldn't be used to choose
    /// between them.</summary>
    internal enum PreviewVariant
    {
        Default,

        // Cycles
        CycleText, CycleBlocks, CycleContinuous, CycleNondirectional, CycleMultidirectional,
        CycleSegmented, CyclePie, CycleGears, CycleCircleArrows, CycleTabbedArc,

        // Radials
        RadialCycle, RadialVenn, RadialConverging, RadialDiverging, RadialList, RadialCluster,
        RadialCircleRelationship, RadialHexagons, RadialPictureCallout, RadialPictureList,

        // Processes
        ProcessAccent, ProcessStacked, ProcessDetailed, ProcessCallout, ProcessAlternating,
        ProcessContinuousArrow, ProcessContinuousBlock, ProcessCircleArrows, ProcessCircles,
        ProcessPie, ProcessInterconnected, ProcessSubSteps, ProcessPhased,

        // Vertical lists
        ListAction, ListStackedTabs, ListSideLineNumbered, ListSideLineQuote,
        ListSideLineIcon, ListSideLineImage, ListShortLine, ListShortLineNumber, ListShortLineQuote,
        ListShortLineWide, ListNumberCard, ListNumberCircle, ListHomePlate, ListChevronAccent,
        ListIconCircles, ListCurved, ListCircleLine, ListBracket, ListVaryingWidth, ListReverse,

        // Pictures
        PicCircles, PicTeamCard, PicHexagons, PicOverlay, PicGrid, PicSide, PicAlternating,
        PicLineup, PicFrames, PicSpiral, PicBlocks, PicCallout, PicAccent,
    }

    public static partial class HtmlPreviewRenderer
    {
        // Which variant each built-in layout draws as, keyed by the tail of its URN. The shapes
        // come from the layout definitions themselves (pie slices for Segmented Cycle and Basic
        // Pie, gears for Gear, hexagons for Hexagon Radial, block arcs for Phased Process...).
        // Layouts not listed draw their family's default.
        private static readonly Dictionary<string, PreviewVariant> Variants = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cycle1"] = PreviewVariant.CycleText,
            ["cycle3"] = PreviewVariant.CycleContinuous,
            ["cycle5"] = PreviewVariant.CycleBlocks,
            ["cycle6"] = PreviewVariant.CycleNondirectional,
            ["cycle7"] = PreviewVariant.CycleMultidirectional,
            ["cycle8"] = PreviewVariant.CycleSegmented,
            ["chart3"] = PreviewVariant.CyclePie,
            ["gear1"] = PreviewVariant.CycleGears,
            ["CircleArrowProcess"] = PreviewVariant.CycleCircleArrows,
            ["TabbedArc+Icon"] = PreviewVariant.CycleTabbedArc,

            ["radial6"] = PreviewVariant.RadialCycle,
            ["radial3"] = PreviewVariant.RadialVenn,
            ["radial4"] = PreviewVariant.RadialConverging,
            ["radial5"] = PreviewVariant.RadialDiverging,
            ["radial2"] = PreviewVariant.RadialList,
            ["RadialCluster"] = PreviewVariant.RadialCluster,
            ["CircleRelationship"] = PreviewVariant.RadialCircleRelationship,
            ["HexagonRadial"] = PreviewVariant.RadialHexagons,
            ["CircularPictureCallout"] = PreviewVariant.RadialPictureCallout,
            ["RadialPictureList"] = PreviewVariant.RadialPictureList,

            ["process3"] = PreviewVariant.ProcessAccent,
            ["hProcess10"] = PreviewVariant.ProcessStacked,
            ["hProcess7"] = PreviewVariant.ProcessDetailed,
            ["process4"] = PreviewVariant.ProcessCallout,
            ["hProcess4"] = PreviewVariant.ProcessAlternating,
            ["hProcess3"] = PreviewVariant.ProcessContinuousArrow,
            ["hProcess9"] = PreviewVariant.ProcessContinuousBlock,
            ["hProcess6"] = PreviewVariant.ProcessCircleArrows,
            ["CircleProcess"] = PreviewVariant.ProcessCircles,
            ["PieProcess"] = PreviewVariant.ProcessPie,
            ["InterconnectedBlockProcess"] = PreviewVariant.ProcessInterconnected,
            ["SubStepProcess"] = PreviewVariant.ProcessSubSteps,
            ["PhasedProcess"] = PreviewVariant.ProcessPhased,

            ["VerticalActionList"] = PreviewVariant.ListAction,
            ["vList5"] = PreviewVariant.ListStackedTabs,
            ["TextCardSideLineNumbered"] = PreviewVariant.ListSideLineNumbered,
            ["TextCardSideLineQuote"] = PreviewVariant.ListSideLineQuote,
            ["TextCardSideLineIcon"] = PreviewVariant.ListSideLineIcon,
            ["TextCardSideLineWideImage"] = PreviewVariant.ListSideLineImage,
            ["TextCardShortLine"] = PreviewVariant.ListShortLine,
            ["TextCardShortLineNumber"] = PreviewVariant.ListShortLineNumber,
            ["TextCardShortLineQuote"] = PreviewVariant.ListShortLineQuote,
            ["TextCardShortLineWide"] = PreviewVariant.ListShortLineWide,
            ["NumberedTitleCardList"] = PreviewVariant.ListNumberCard,
            ["NumberedTitleList"] = PreviewVariant.ListNumberCircle,
            ["vList3"] = PreviewVariant.ListHomePlate,
            ["VerticalAccentList"] = PreviewVariant.ListChevronAccent,
            ["IconCircleLabelList"] = PreviewVariant.ListIconCircles,
            ["VerticalCurvedList"] = PreviewVariant.ListCurved,
            ["VerticalCircleList"] = PreviewVariant.ListCircleLine,
            ["BracketList"] = PreviewVariant.ListBracket,
            ["VaryingWidthList"] = PreviewVariant.ListVaryingWidth,
            ["ReverseList"] = PreviewVariant.ListReverse,

            ["MeetTheTeam"] = PreviewVariant.PicCircles,
            ["MeetTheTeamOval"] = PreviewVariant.PicCircles,
            ["AlternatingPictureCircles"] = PreviewVariant.PicCircles,
            ["BubblePictureList"] = PreviewVariant.PicCircles,
            ["MeetTheTeamCard"] = PreviewVariant.PicTeamCard,
            ["MeetTheTeamCardVertical"] = PreviewVariant.PicTeamCard,
            ["HexagonCluster"] = PreviewVariant.PicHexagons,
            ["BendingPictureSemiTransparentText"] = PreviewVariant.PicOverlay,
            ["ThemePictureAccent"] = PreviewVariant.PicOverlay,
            ["PictureGrid"] = PreviewVariant.PicGrid,
            ["ThemePictureGrid"] = PreviewVariant.PicGrid,
            ["vList4"] = PreviewVariant.PicSide,
            ["pList2"] = PreviewVariant.PicSide,
            ["PictureAccentList"] = PreviewVariant.PicSide,
            ["PictureStrips"] = PreviewVariant.PicSide,
            ["AlternatingPictureBlocks"] = PreviewVariant.PicAlternating,
            ["ThemePictureAlternatingAccent"] = PreviewVariant.PicAlternating,
            ["PictureLineup"] = PreviewVariant.PicLineup,
            ["TitlePictureLineup"] = PreviewVariant.PicLineup,
            ["Picture Frame"] = PreviewVariant.PicFrames,
            ["FramedTextPicture"] = PreviewVariant.PicFrames,
            ["SnapshotPictureList"] = PreviewVariant.PicFrames,
            ["SpiralPicture"] = PreviewVariant.PicSpiral,
            ["PictureAccentBlocks"] = PreviewVariant.PicBlocks,
            ["BendingPictureBlocks"] = PreviewVariant.PicBlocks,
            ["TitledPictureBlocks"] = PreviewVariant.PicBlocks,
            ["bList2"] = PreviewVariant.PicBlocks,
            ["BendingPictureCaptionList"] = PreviewVariant.PicCallout,
            ["AccentedPicture"] = PreviewVariant.PicAccent,
        };

        /// <summary>The look a layout alias, URN or title previews as within its family.</summary>
        internal static PreviewVariant ResolveVariant(string? layoutAlias)
        {
            var alias = (layoutAlias ?? string.Empty).Trim();
            GloxPackage? pkg = null;
            try { pkg = SmartArtLayoutCatalog.Shared.TryResolve(alias); } catch { /* catalog unavailable: go by the alias */ }
            return Variants.TryGetValue(Tail(pkg?.UniqueId ?? alias), out var v) ? v : PreviewVariant.Default;
        }

        private static bool IsPictureVariant(PreviewVariant v) =>
            v is >= PreviewVariant.PicCircles or PreviewVariant.RadialPictureCallout or PreviewVariant.RadialPictureList
                or PreviewVariant.ListSideLineImage;

        /// <summary>Thumbnail outlines for variants that need a particular shape of data to read
        /// (three gears, bodies under process headers); null keeps the family's sample.</summary>
        private static List<Item>? VariantSample(PreviewVariant v)
        {
            static Item I(string t, params Item[] kids) => new() { Text = t, Children = kids.ToList() };
            static List<Item> Flat(int n) => Enumerable.Range(0, n).Select(i => I(((char)('A' + i)).ToString())).ToList();
            static List<Item> Bodies(int n) => Enumerable.Range(0, n).Select(i => I(((char)('A' + i)).ToString(), I("a"))).ToList();
            return v switch
            {
                PreviewVariant.CycleGears => Flat(3),
                // Four boxes leave long links between them, which is all that tells these four apart at tile size.
                PreviewVariant.CycleBlocks or PreviewVariant.CycleNondirectional or PreviewVariant.CycleMultidirectional
                    or PreviewVariant.CycleContinuous or PreviewVariant.CycleText => Flat(4),
                PreviewVariant.CyclePie or PreviewVariant.CycleSegmented => Flat(5),
                PreviewVariant.CycleCircleArrows => Flat(3),
                PreviewVariant.CycleTabbedArc => Flat(4),
                PreviewVariant.RadialList or PreviewVariant.RadialPictureList or PreviewVariant.RadialPictureCallout => Flat(5),
                PreviewVariant.RadialHexagons => Flat(7),
                PreviewVariant.ProcessAccent or PreviewVariant.ProcessStacked or PreviewVariant.ProcessDetailed
                    or PreviewVariant.ProcessCallout or PreviewVariant.ProcessAlternating or PreviewVariant.ProcessContinuousArrow
                    or PreviewVariant.ProcessCircles or PreviewVariant.ProcessPie or PreviewVariant.ProcessSubSteps => Bodies(3),
                PreviewVariant.ListReverse => Flat(2),
                PreviewVariant.ListIconCircles => Bodies(3),
                PreviewVariant.PicSpiral => Flat(5),
                PreviewVariant.PicAccent => Flat(4),
                PreviewVariant.PicGrid => Flat(4),
                _ => null,
            };
        }

        /// <summary>Lines and unfilled paths and circles (connectors, arcs, rings) drawn up to 2.4 times as thick, and
        /// the arrowhead marker with them, so they survive being shrunk to a gallery tile.</summary>
        internal static string ThickenForThumbnail(string svg)
        {
            static string Scale(System.Text.RegularExpressions.Match m) =>
                System.Text.RegularExpressions.Regex.Replace(m.Value, "stroke-width=\"([0-9.]+)\"", w =>
                {
                    // Thin lines grow to about 7 px; a line already broad (Continuous Cycle's band) keeps its width.
                    double v = double.Parse(w.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    return $"stroke-width=\"{F(Math.Max(v, Math.Min(v * 2.4, 9)))}\"";
                });
            svg = System.Text.RegularExpressions.Regex.Replace(svg, @"<line\b[^>]*>|<(?:path|circle)\b[^>]*fill=""none""[^>]*>", Scale);
            return svg.Replace("markerWidth=\"13\" markerHeight=\"13\"", "markerWidth=\"22\" markerHeight=\"22\"");
        }

        [ThreadStatic] private static bool _thumbBars;

        /// <summary>Thumbnail stand-in for text on the page: a darker bar per title line and lighter
        /// ones for the bullets, at least half the slot wide so single-letter samples still read as
        /// lines of text.</summary>
        private static void TextBars(StringBuilder sb, double x, double top, double w, double inset, bool left, TextFit fit)
        {
            double avail = Math.Max(10, w - inset * 2);
            void Bar(double y, double fs, int len, bool bold, double minShare, string fill)
            {
                double bw = Math.Min(avail, Math.Max(avail * minShare, len * CharW(fs, bold)));
                double bx = left ? x + inset : x + (w - bw) / 2;
                sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(y + fs * 0.2)}\" width=\"{F(bw)}\" height=\"{F(fs * 0.62)}\" rx=\"{F(fs * 0.3)}\" fill=\"{fill}\"/>");
            }
            for (int i = 0; i < fit.Title.Count; i++) Bar(top + i * fit.Fs * 1.22, fit.Fs, fit.Title[i].Length, true, 0.5, "#a19f9d");
            double by = top + fit.Title.Count * fit.Fs * 1.22 + (fit.Title.Count > 0 ? 6 : 0);
            for (int i = 0; i < fit.Bullets.Count; i++) Bar(by + i * fit.Bfs * 1.3, fit.Bfs, fit.Bullets[i].Length, false, 0.72, "#d2d0ce");
        }

        private static (double, double)? DrawVariant(StringBuilder sb, PreviewVariant v, List<Item> items) => v switch
        {
            PreviewVariant.CycleText => DrawBlockCycle(sb, items, CycleLinks.Text),
            PreviewVariant.CycleBlocks => DrawBlockCycle(sb, items, CycleLinks.Arrows),
            PreviewVariant.CycleNondirectional => DrawBlockCycle(sb, items, CycleLinks.Plain),
            PreviewVariant.CycleMultidirectional => DrawBlockCycle(sb, items, CycleLinks.Both),
            PreviewVariant.CycleContinuous => DrawBlockCycle(sb, items, CycleLinks.Band),
            PreviewVariant.CycleSegmented => DrawPieCycle(sb, items, donut: true),
            PreviewVariant.CyclePie => DrawPieCycle(sb, items, donut: false),
            PreviewVariant.CycleGears => DrawGears(sb, items),
            PreviewVariant.CycleCircleArrows => DrawCircleArrows(sb, items),
            PreviewVariant.CycleTabbedArc => DrawTabbedArc(sb, items),

            PreviewVariant.RadialCycle => DrawHub(sb, items, HubStyle.Ring),
            PreviewVariant.RadialVenn => DrawHub(sb, items, HubStyle.Venn),
            PreviewVariant.RadialConverging => DrawHub(sb, items, HubStyle.Converging),
            PreviewVariant.RadialDiverging => DrawHub(sb, items, HubStyle.Diverging),
            PreviewVariant.RadialList => DrawRadialList(sb, items, pictures: false, callout: false),
            PreviewVariant.RadialPictureList => DrawRadialList(sb, items, pictures: true, callout: false),
            PreviewVariant.RadialPictureCallout => DrawRadialList(sb, items, pictures: true, callout: true),
            PreviewVariant.RadialCluster => DrawRadialCluster(sb, items),
            PreviewVariant.RadialCircleRelationship => DrawCircleRelationship(sb, items),
            PreviewVariant.RadialHexagons => DrawHexagonRadial(sb, items),

            PreviewVariant.ProcessAccent => DrawHeadedProcess(sb, items, HeadStyle.Accent),
            PreviewVariant.ProcessStacked => DrawHeadedProcess(sb, items, HeadStyle.Stacked),
            PreviewVariant.ProcessDetailed => DrawHeadedProcess(sb, items, HeadStyle.Detailed),
            PreviewVariant.ProcessCallout => DrawCalloutProcess(sb, items),
            PreviewVariant.ProcessAlternating => DrawAlternatingFlow(sb, items),
            PreviewVariant.ProcessContinuousArrow => DrawContinuousArrow(sb, items),
            PreviewVariant.ProcessContinuousBlock => DrawContinuousBlock(sb, items),
            PreviewVariant.ProcessCircleArrows => DrawCircleProcess(sb, items, filled: true),
            PreviewVariant.ProcessCircles => DrawCircleProcess(sb, items, filled: false),
            PreviewVariant.ProcessPie => DrawPieProcess(sb, items),
            PreviewVariant.ProcessInterconnected => DrawInterconnected(sb, items),
            PreviewVariant.ProcessSubSteps => DrawSubSteps(sb, items),
            PreviewVariant.ProcessPhased => DrawPhased(sb, items),

            PreviewVariant.ListSideLineNumbered => DrawCards(sb, items, sideLine: true, CardMark.Number, wide: false),
            PreviewVariant.ListSideLineQuote => DrawCards(sb, items, sideLine: true, CardMark.Quote, wide: false),
            PreviewVariant.ListSideLineIcon => DrawCards(sb, items, sideLine: true, CardMark.Icon, wide: false),
            PreviewVariant.ListSideLineImage => DrawCards(sb, items, sideLine: true, CardMark.Image, wide: true),
            PreviewVariant.ListShortLine => DrawCards(sb, items, sideLine: false, CardMark.None, wide: false),
            PreviewVariant.ListShortLineNumber => DrawCards(sb, items, sideLine: false, CardMark.Number, wide: false),
            PreviewVariant.ListShortLineQuote => DrawCards(sb, items, sideLine: false, CardMark.Quote, wide: false),
            PreviewVariant.ListShortLineWide => DrawCards(sb, items, sideLine: false, CardMark.None, wide: true),
            PreviewVariant.ListAction => DrawActionList(sb, items),
            PreviewVariant.ListStackedTabs => DrawStackedTabs(sb, items),
            PreviewVariant.ListNumberCard => DrawNumberedRows(sb, items, circle: false),
            PreviewVariant.ListNumberCircle => DrawNumberedRows(sb, items, circle: true),
            PreviewVariant.ListHomePlate => DrawHomePlates(sb, items),
            PreviewVariant.ListChevronAccent => DrawChevronAccentList(sb, items),
            PreviewVariant.ListIconCircles => DrawIconCircles(sb, items),
            PreviewVariant.ListCurved => DrawCurvedList(sb, items),
            PreviewVariant.ListCircleLine => DrawCircleLineList(sb, items),
            PreviewVariant.ListBracket => DrawBracketList(sb, items),
            PreviewVariant.ListVaryingWidth => DrawVaryingWidth(sb, items),
            PreviewVariant.ListReverse => DrawReverseList(sb, items),

            PreviewVariant.PicCircles => DrawPictureCircles(sb, items, cards: false),
            PreviewVariant.PicTeamCard => DrawPictureCircles(sb, items, cards: true),
            PreviewVariant.PicHexagons => DrawPictureHexagons(sb, items),
            PreviewVariant.PicOverlay => DrawPictureOverlay(sb, items, tight: false),
            PreviewVariant.PicGrid => DrawPictureOverlay(sb, items, tight: true),
            PreviewVariant.PicSide => DrawPictureRows(sb, items, alternate: false),
            PreviewVariant.PicAlternating => DrawPictureRows(sb, items, alternate: true),
            PreviewVariant.PicLineup => DrawPictureLineup(sb, items),
            PreviewVariant.PicFrames => DrawPictureFrames(sb, items),
            PreviewVariant.PicSpiral => DrawPictureSpiral(sb, items),
            PreviewVariant.PicBlocks => DrawPictureBlocks(sb, items, callout: false),
            PreviewVariant.PicCallout => DrawPictureBlocks(sb, items, callout: true),
            PreviewVariant.PicAccent => DrawAccentedPicture(sb, items),
            _ => null,
        };

        // ------------------------------------------------------------------ shared shapes

        private static double Ang(int i, int n) => 2 * Math.PI * i / n - Math.PI / 2;

        /// <summary>A clockwise arc as an absolute path (M + A), from angle a1 to a2.</summary>
        private static string ArcD(double cx, double cy, double r, double a1, double a2)
        {
            int large = a2 - a1 > Math.PI ? 1 : 0;
            return $"M{F(cx + r * Math.Cos(a1))} {F(cy + r * Math.Sin(a1))}A{F(r)} {F(r)} 0 {large} 1 {F(cx + r * Math.Cos(a2))} {F(cy + r * Math.Sin(a2))}";
        }

        /// <summary>A pie slice (r0 = 0) or a donut segment between angles a1 and a2.</summary>
        private static string WedgeD(double cx, double cy, double r0, double r1, double a1, double a2)
        {
            int large = a2 - a1 > Math.PI ? 1 : 0;
            string P(double r, double a) => $"{F(cx + r * Math.Cos(a))} {F(cy + r * Math.Sin(a))}";
            return r0 <= 0
                ? $"M{F(cx)} {F(cy)}L{P(r1, a1)}A{F(r1)} {F(r1)} 0 {large} 1 {P(r1, a2)}Z"
                : $"M{P(r1, a1)}A{F(r1)} {F(r1)} 0 {large} 1 {P(r1, a2)}L{P(r0, a2)}A{F(r0)} {F(r0)} 0 {large} 0 {P(r0, a1)}Z";
        }

        /// <summary>A box with only its top corners rounded (Office's round2SameRect: tabs).</summary>
        private static string TabD(double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            return $"M{F(x)} {F(y + h)}V{F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}H{F(x + w - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w)} {F(y + r)}V{F(y + h)}Z";
        }

        private static string HexPoints(double cx, double cy, double s) =>
            string.Join(" ", Enumerable.Range(0, 6).Select(k =>
            {
                double a = Math.PI / 180 * (60 * k - 90);
                return $"{F(cx + s * Math.Cos(a))},{F(cy + s * Math.Sin(a))}";
            }));

        /// <summary>A grey picture placeholder (a sun over two hills), as a rectangle or a circle.</summary>
        private static void Picture(StringBuilder sb, double x, double y, double w, double h, bool circle = false, string? hexAround = null)
        {
            if (hexAround != null) sb.Append($"<polygon points=\"{hexAround}\" fill=\"#edebe9\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            else if (circle) sb.Append($"<circle cx=\"{F(x + w / 2)}\" cy=\"{F(y + h / 2)}\" r=\"{F(Math.Min(w, h) / 2)}\" fill=\"#edebe9\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            else sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"3\" fill=\"#edebe9\"/>");
            double s = Math.Clamp(Math.Min(w, h) / 70, 0.35, 1.6);
            double gx = x + w / 2, gy = y + h / 2;
            sb.Append($"<circle cx=\"{F(gx + 14 * s)}\" cy=\"{F(gy - 12 * s)}\" r=\"{F(6 * s)}\" fill=\"#c8c6c4\"/>");
            sb.Append($"<path d=\"M{F(gx - 28 * s)} {F(gy + 16 * s)}L{F(gx - 8 * s)} {F(gy - 6 * s)}L{F(gx + 4 * s)} {F(gy + 6 * s)}L{F(gx + 12 * s)} {F(gy - 2 * s)}L{F(gx + 28 * s)} {F(gy + 16 * s)}Z\" fill=\"#c8c6c4\"/>");
        }

        private static void Group(StringBuilder sb, Item item) => sb.Append("<g class=\"sa-s\">").Append(Tooltip(item));
        private static void EndGroup(StringBuilder sb) => sb.Append("</g>");

        private static void Note(StringBuilder sb, double x, double y, int more, string anchor = "middle") =>
            sb.Append($"<text x=\"{F(x)}\" y=\"{F(y)}\" text-anchor=\"{anchor}\" font-size=\"12\" fill=\"#605e5c\">{more} more not shown.</text>");

        private static void SplitHub(List<Item> items, out Item center, out List<Item> around)
        {
            if (items[0].Children.Count > 0) { center = new Item { Text = items[0].Text }; around = items[0].Children.Concat(items.Skip(1)).ToList(); }
            else { center = items[0]; around = items.Skip(1).ToList(); }
        }

        private static void Tri(StringBuilder sb, double tipX, double tipY, double angle, double size, string fill)
        {
            double bx = tipX - size * Math.Cos(angle), by = tipY - size * Math.Sin(angle);
            double px = -Math.Sin(angle) * size * 0.55, py = Math.Cos(angle) * size * 0.55;
            sb.Append($"<polygon points=\"{F(tipX)},{F(tipY)} {F(bx + px)},{F(by + py)} {F(bx - px)},{F(by - py)}\" fill=\"{fill}\"/>");
        }

        // ------------------------------------------------------------------ cycles

        private enum CycleLinks { Arrows, Plain, Both, Text, Band }

        /// <summary>Boxes (or bare labels, for Text Cycle) round a ring. The links between them say
        /// which cycle it is: one-way arcs (Block), plain arcs (Nondirectional), two-way arcs
        /// (Multidirectional), or one continuous band under the boxes (Continuous).</summary>
        private static (double, double) DrawBlockCycle(StringBuilder sb, List<Item> items, CycleLinks links)
        {
            int n = items.Count;
            bool text = links == CycleLinks.Text;
            double bw = n <= 4 ? 160 : n <= 8 ? 136 : 112;
            double bh = Math.Min(110, Math.Max(text ? 44 : 56, items.Max(i => Measure(i.Text, text ? Array.Empty<string>() : i.Bullets, bw - 20)) + 20));
            if (n == 1)
            {
                Box(sb, BaseW / 2 - bw / 2, Pad, bw, bh, Accent(0), items[0], rx: 10);
                return (BaseW, bh + Pad * 2);
            }
            // The ring is set wider than the boxes strictly need, so the links between them are arcs a
            // reader can follow rather than stubs between near-touching boxes.
            double R = Math.Max(n <= 3 ? 140 : 190, (Math.Max(bw, bh) + 60) / (2 * Math.Sin(Math.PI / n)));
            R = Math.Min(R, BaseW / 2 - Pad - bw / 2);
            double cx = BaseW / 2, cy = Pad + R + bh / 2;
            // Where an arc leaves a box: its half-extent along the ring's tangent at that angle.
            double Clear(double a) => (Math.Abs(bw / 2 * Math.Sin(a)) + Math.Abs(bh / 2 * Math.Cos(a)) + 7) / R;
            if (links == CycleLinks.Band)
            {
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(R)}\" fill=\"none\" stroke=\"{Tint(Accent(0), 0.6)}\" stroke-width=\"22\"/>");
                // One arrowhead on the band, between the last box and the first, gives it a direction.
                double aEnd = Ang(0, n) - Clear(Ang(0, n)) * 0.9;
                Tri(sb, cx + (R) * Math.Cos(aEnd), cy + R * Math.Sin(aEnd), aEnd + Math.PI / 2, 30, Tint(Accent(0), 0.35));
            }
            else if (links == CycleLinks.Plain)
            {
                // Nondirectional: the boxes sit on one unbroken ring; nothing says which way round.
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(R)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    double a1 = Ang(i, n) + Clear(Ang(i, n)), a2 = Ang(i + 1, n) - Clear(Ang(i + 1, n));
                    if (a2 <= a1) continue;
                    string color = text ? Accent(i) : Connector;
                    // Arrowheads are drawn as triangles, not markers: the gallery's Direct2D SVG
                    // renderer draws no markers, and without heads Block Cycle's tile looked like
                    // Nondirectional Cycle's.
                    double hs = text ? 16 : 14, back = (hs - 2) / R;
                    bool tail = links == CycleLinks.Both;
                    sb.Append($"<path d=\"{ArcD(cx, cy, R, a1 + (tail ? back : 0), a2 - back)}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{(text ? 4 : 3)}\"/>");
                    Tri(sb, cx + R * Math.Cos(a2), cy + R * Math.Sin(a2), a2 + Math.PI / 2, hs, color);
                    if (tail) Tri(sb, cx + R * Math.Cos(a1), cy + R * Math.Sin(a1), a1 - Math.PI / 2, hs, color);
                }
            }
            for (int i = 0; i < n; i++)
            {
                double a = Ang(i, n);
                double x = cx + R * Math.Cos(a) - bw / 2, y = cy + R * Math.Sin(a) - bh / 2;
                if (text)
                {
                    Group(sb, items[i]);
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" rx=\"{F(bh / 2)}\" fill=\"{Tint(Accent(i), 0.8)}\"/>");
                    Text(sb, x, y, bw, bh, items[i].Text, Array.Empty<string>(), Ink, maxFs: 14);
                    EndGroup(sb);
                }
                else Box(sb, x, y, bw, bh, Accent(i), items[i], rx: 10);
            }
            return (BaseW, cy + R + bh / 2 + Pad);
        }

        /// <summary>Basic Pie (slices) and Segmented Cycle (a ring of segments with arrows round
        /// the outside).</summary>
        private static (double, double) DrawPieCycle(StringBuilder sb, List<Item> items, bool donut)
        {
            int n = items.Count;
            double rO = 165, rI = donut ? 80 : 0, ring = donut ? 18 : 0;
            double cx = BaseW / 2, cy = Pad + ring + rO;
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(rO)}\" fill=\"#ffffff\"/>");
            double gap = n > 1 ? (donut ? 0.035 : 0.012) : 0;
            for (int i = 0; i < n; i++)
            {
                double a1 = Ang(i, n) + gap, a2 = Ang(i + 1, n) - gap, am = (a1 + a2) / 2;
                Group(sb, items[i]);
                if (n == 1)
                {
                    sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(rO)}\" fill=\"{Accent(0)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    if (donut) sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(rI)}\" fill=\"#ffffff\"/>");
                }
                else sb.Append($"<path d=\"{WedgeD(cx, cy, rI, rO, a1, a2)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                double rm = n == 1 ? (donut ? (rO + rI) / 2 : 0) : donut ? (rO + rI) / 2 : rO * 0.6;
                double tw = n == 1 ? (donut ? rO - rI : rO * 1.3) : Math.Min(donut ? 120 : 140, 2 * rm * Math.Sin(Math.PI / n) * 0.95);
                double th = donut ? (rO - rI) * 0.85 : Math.Min(80, rO * 0.5);
                double lx = cx + rm * Math.Cos(n == 1 ? -Math.PI / 2 : am), ly = cy + rm * Math.Sin(n == 1 ? -Math.PI / 2 : am);
                if (n == 1 && !donut) { lx = cx; ly = cy; }
                Text(sb, lx - tw / 2, ly - th / 2, tw, th, items[i].Text, n <= 2 && !donut ? items[i].Bullets : Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            if (donut && n > 1)
                for (int i = 0; i < n; i++)
                {
                    double a = Ang(i + 1, n);
                    sb.Append($"<path d=\"{ArcD(cx, cy, rO + 12, a - 0.22, a + 0.1)}\" fill=\"none\" stroke=\"#8a8886\" stroke-width=\"2.5\" marker-end=\"url(#sa-arrow)\"/>");
                }
            return (BaseW, cy + rO + ring + Pad);
        }

        private static string GearPoints(double cx, double cy, double r, int teeth, double phase)
        {
            var pts = new List<string>();
            double ri = r * 0.84, step = 2 * Math.PI / teeth;
            for (int k = 0; k < teeth; k++)
            {
                double b = phase + k * step;
                foreach (var (a, rr) in new[] { (b - step * 0.3, ri), (b - step * 0.17, r), (b + step * 0.17, r), (b + step * 0.3, ri) })
                    pts.Add($"{F(cx + rr * Math.Cos(a))},{F(cy + rr * Math.Sin(a))}");
            }
            return string.Join(" ", pts);
        }

        /// <summary>Gear: up to three interlocking gears, as Word's layout holds.</summary>
        private static (double, double) DrawGears(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(3, items.Count);
            var g = new List<(double x, double y, double r, int t)> { (0, 0, 120, 12) };
            if (n > 1) g.Add((Math.Cos(-0.7) * 188, Math.Sin(-0.7) * 188, 80, 9));
            if (n > 2) g.Add((Math.Cos(0.62) * 180, Math.Sin(0.62) * 180, 72, 8));
            double minX = g.Min(q => q.x - q.r), maxX = g.Max(q => q.x + q.r), minY = g.Min(q => q.y - q.r), maxY = g.Max(q => q.y + q.r);
            double ox = BaseW / 2 - (minX + maxX) / 2, oy = Pad - minY;
            for (int i = 0; i < n; i++)
            {
                var (x, y, r, t) = g[i];
                x += ox; y += oy;
                Group(sb, items[i]);
                sb.Append($"<polygon points=\"{GearPoints(x, y, r, t, i * 0.2)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"2\" stroke-linejoin=\"round\"/>");
                double tw = r * 1.3, th = r * 1.05;
                Text(sb, x - tw / 2, y - th / 2, tw, th, items[i].Text, i == 0 ? items[i].Bullets : Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            double h = maxY - minY + Pad * 2;
            if (items.Count > n) { Note(sb, BaseW / 2, h - Pad + 16, items.Count - n); h += 18; }
            return (BaseW, h);
        }

        /// <summary>Circle Arrow Process: a column of circular arrows, each holding its step, with
        /// the step's detail beside it.</summary>
        private static (double, double) DrawCircleArrows(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = 54, step = 104, y = Pad + r;
            bool anyBody = items.Any(i => i.Children.Count > 0);
            for (int i = 0; i < n; i++, y += step)
            {
                double cx = Pad + 20 + r + (i % 2) * 64;
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(y)}\" r=\"{F(r - 8)}\" fill=\"{Tint(c, 0.9)}\"/>");
                double a1 = (i % 2 == 0 ? -0.35 : Math.PI - 0.35), a2 = a1 + Math.PI * 1.62;
                sb.Append($"<path d=\"{ArcD(cx, y, r, a1, a2)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"14\"/>");
                double ex = cx + r * Math.Cos(a2), ey = y + r * Math.Sin(a2);
                Tri(sb, ex + 14 * Math.Cos(a2 + Math.PI / 2), ey + 14 * Math.Sin(a2 + Math.PI / 2), a2 + Math.PI / 2, 26, c);
                Text(sb, cx - (r - 10), y - (r - 14), (r - 10) * 2, (r - 14) * 2, items[i].Text, Array.Empty<string>(), Ink, maxFs: 13, inset: 0);
                if (anyBody && items[i].Children.Count > 0)
                {
                    double bx = Pad + 20 + r * 2 + 64 + 24;
                    Text(sb, bx, y - step / 2 + 6, BaseW - Pad - bx, step - 12, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 0);
                }
                EndGroup(sb);
            }
            return (BaseW, Pad * 2 + 2 * r + (n - 1) * step + 6);
        }

        /// <summary>Tabbed Arc: tabs set along the top of a wide arc.</summary>
        private static (double, double) DrawTabbedArc(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double R = 270, cx = BaseW / 2;
            double span = Math.PI * 0.72, start = -Math.PI / 2 - span / 2;
            double da = n > 1 ? span / (n - 1) : 0;
            double tw = n == 1 ? 180 : Math.Min(150, 2 * R * Math.Sin(da / 2) - 12);
            double th = Math.Min(120, Math.Max(52, items.Max(i => Measure(i.Text, i.Bullets, tw - 20, 13)) + 18));
            double cy = Pad + th / 2 + R;
            sb.Append($"<path d=\"{ArcD(cx, cy, R + th / 2 + 10, start - 0.12, start + span + 0.12)}\" fill=\"none\" stroke=\"{Tint(Accent(0), 0.55)}\" stroke-width=\"12\" stroke-linecap=\"round\"/>");
            // The arc's own ends hang lower than a lone tab at its crown.
            double lowest = cy + (R + th / 2 + 10) * Math.Sin(start - 0.12) + 8;
            for (int i = 0; i < n; i++)
            {
                double a = n == 1 ? -Math.PI / 2 : start + i * da;
                double x = cx + R * Math.Cos(a) - tw / 2, y = cy + R * Math.Sin(a) - th / 2;
                lowest = Math.Max(lowest, y + th);
                Group(sb, items[i]);
                sb.Append($"<path d=\"{TabD(x, y, tw, th, 12)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                Text(sb, x, y, tw, th, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14);
                EndGroup(sb);
            }
            return (BaseW, lowest + Pad);
        }

        // ------------------------------------------------------------------ radials

        private enum HubStyle { Ring, Venn, Converging, Diverging }

        /// <summary>A hub with its items round it: on a ring (Radial Cycle), overlapping it (Radial
        /// Venn), or linked by arrows pointing in (Converging) or out (Diverging).</summary>
        private static (double, double) DrawHub(StringBuilder sb, List<Item> items, HubStyle style)
        {
            SplitHub(items, out var center, out var around);
            int n = around.Count;
            if (n == 0) { Circle(sb, BaseW / 2, 170, 110, Accent(0), center); return (BaseW, 340); }
            if (style == HubStyle.Venn)
            {
                double rc = 96, r = n <= 4 ? 84 : n <= 6 ? 70 : 58, R = rc + r * 0.45;
                double vx = BaseW / 2, vy = Pad + R + r;
                for (int i = 0; i < n; i++)
                {
                    double a = Ang(i, n);
                    Group(sb, around[i]);
                    sb.Append($"<circle cx=\"{F(vx + R * Math.Cos(a))}\" cy=\"{F(vy + R * Math.Sin(a))}\" r=\"{F(r)}\" fill=\"{Accent(i + 1)}\" fill-opacity=\"0.55\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    EndGroup(sb);
                }
                Group(sb, center);
                sb.Append($"<circle cx=\"{F(vx)}\" cy=\"{F(vy)}\" r=\"{F(rc)}\" fill=\"{Accent(0)}\" fill-opacity=\"0.55\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                EndGroup(sb);
                Text(sb, vx - rc * 0.7, vy - rc * 0.45, rc * 1.4, rc * 0.9, center.Text, Array.Empty<string>(), Ink, maxFs: 15, inset: 2, halo: true);
                for (int i = 0; i < n; i++)
                {
                    double a = Ang(i, n), lr = R + r * 0.42, tw = r * 1.15, th = r * 0.9;
                    Text(sb, vx + lr * Math.Cos(a) - tw / 2, vy + lr * Math.Sin(a) - th / 2, tw, th, around[i].Text, Array.Empty<string>(), Ink, maxFs: 13, inset: 2, halo: true);
                }
                return (BaseW, vy + R + r + Pad);
            }
            bool boxes = style == HubStyle.Converging;
            double bw = 140, bh = boxes ? Math.Min(90, Math.Max(50, around.Max(i => Measure(i.Text, Array.Empty<string>(), bw - 16)) + 16)) : 0;
            double s = Math.Sin(Math.PI / Math.Max(2, n)) * 0.8;
            double rr = boxes ? 0 : Math.Max(Math.Min(style == HubStyle.Diverging ? 48 : 56, 160 * s), Math.Min(78, RadiusForWords(around)));
            double rcH = Math.Max(72, Math.Min(96, Math.Max(RadiusForWords(new[] { center }), rr * 1.15)));
            double ext = boxes ? Math.Max(bw, bh) / 2 + 6 : rr;
            double Rr = Math.Max(n <= 4 ? 150 : 175, Math.Max(boxes ? (bw + 16) / (2 * Math.Sin(Math.PI / Math.Max(2, n))) : rr / Math.Max(0.2, s), rcH + ext + (style == HubStyle.Ring ? 18 : style == HubStyle.Diverging ? 96 : 52)));
            double ccx = BaseW / 2, ccy = Pad + Rr + (boxes ? bh / 2 : rr);
            if (style == HubStyle.Ring)
                sb.Append($"<circle cx=\"{F(ccx)}\" cy=\"{F(ccy)}\" r=\"{F(Rr)}\" fill=\"none\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            for (int i = 0; i < n; i++)
            {
                double a = Ang(i, n), cos = Math.Cos(a), sin = Math.Sin(a);
                double outer = boxes ? Rr - (Math.Abs(bw / 2 * cos) + Math.Abs(bh / 2 * sin)) / Math.Max(Math.Abs(cos) + Math.Abs(sin), 1) - 8 : Rr - rr - 6;
                double inner = rcH + 6;
                switch (style)
                {
                    case HubStyle.Ring:
                        sb.Append($"<line x1=\"{F(ccx + inner * cos)}\" y1=\"{F(ccy + inner * sin)}\" x2=\"{F(ccx + (Rr - rr) * cos)}\" y2=\"{F(ccy + (Rr - rr) * sin)}\" stroke=\"#c8c6c4\" stroke-width=\"2\"/>");
                        break;
                    case HubStyle.Converging:
                        sb.Append($"<line x1=\"{F(ccx + outer * cos)}\" y1=\"{F(ccy + outer * sin)}\" x2=\"{F(ccx + (inner + 4) * cos)}\" y2=\"{F(ccy + (inner + 4) * sin)}\" stroke=\"{Tint(Accent(i + 1), 0.2)}\" stroke-width=\"4\" marker-end=\"url(#sa-arrow)\"/>");
                        break;
                    case HubStyle.Diverging:
                        sb.Append($"<line x1=\"{F(ccx + inner * cos)}\" y1=\"{F(ccy + inner * sin)}\" x2=\"{F(ccx + (outer - 2) * cos)}\" y2=\"{F(ccy + (outer - 2) * sin)}\" stroke=\"{Tint(Accent(i + 1), 0.2)}\" stroke-width=\"4\" marker-end=\"url(#sa-arrow)\"/>");
                        break;
                }
            }
            Circle(sb, ccx, ccy, rcH, Accent(0), center, withBullets: false);
            for (int i = 0; i < n; i++)
            {
                double a = Ang(i, n), px = ccx + Rr * Math.Cos(a), py = ccy + Rr * Math.Sin(a);
                if (boxes) Box(sb, px - bw / 2, py - bh / 2, bw, bh, Accent(i + 1), around[i], withBullets: false, rx: 10);
                else Circle(sb, px, py, rr, Accent(i + 1), around[i], withBullets: false);
            }
            return (BaseW, ccy + Rr + (boxes ? bh / 2 : rr) + Pad);
        }

        /// <summary>Radial List (and the two picture radials): the hub on the left, its items on an
        /// arc to its right, each label beside its marker. Circular Picture Callout points labels
        /// straight at a big picture instead.</summary>
        private static (double, double) DrawRadialList(StringBuilder sb, List<Item> items, bool pictures, bool callout)
        {
            SplitHub(items, out var center, out var around);
            int n = around.Count;
            double rc = pictures ? 118 : 96, Ra = rc + (callout ? 40 : 74), mr = callout ? 5 : n <= 6 ? 26 : 20;
            // A callout row is a line of text, not a marker: it gets a text line's height.
            double rowH = callout ? 44 : mr * 2 + 14;
            double theta = n <= 1 ? 0 : Math.Min(1.25, (n - 1) * rowH / (2 * Ra));
            double hx = Pad + rc + 6, cy = Pad + Math.Max(rc, Ra * Math.Sin(theta) + rowH / 2);
            double labelX = hx + Ra + mr + 14;
            for (int i = 0; i < n; i++)
            {
                double a = n == 1 ? 0 : -theta + 2 * theta * i / (n - 1);
                double px = hx + Ra * Math.Cos(a), py = cy + Ra * Math.Sin(a);
                double lx = callout ? labelX : px + mr + 12;
                Group(sb, around[i]);
                if (callout)
                {
                    sb.Append($"<path d=\"M{F(hx + (rc - 6) * Math.Cos(a))} {F(cy + (rc - 6) * Math.Sin(a))}L{F(px)} {F(py)}H{F(lx - 6)}\" fill=\"none\" stroke=\"{Accent(i + 1)}\" stroke-width=\"2\"/>");
                    sb.Append($"<circle cx=\"{F(hx + (rc - 6) * Math.Cos(a))}\" cy=\"{F(cy + (rc - 6) * Math.Sin(a))}\" r=\"4\" fill=\"#ffffff\" stroke=\"{Accent(i + 1)}\" stroke-width=\"2\"/>");
                }
                else
                {
                    sb.Append($"<line x1=\"{F(hx + rc * Math.Cos(a))}\" y1=\"{F(cy + rc * Math.Sin(a))}\" x2=\"{F(px - mr * Math.Cos(a))}\" y2=\"{F(py - mr * Math.Sin(a))}\" stroke=\"#c8c6c4\" stroke-width=\"2\"/>");
                    if (pictures) Picture(sb, px - mr, py - mr, mr * 2, mr * 2, circle: true);
                    else sb.Append($"<circle cx=\"{F(px)}\" cy=\"{F(py)}\" r=\"{F(mr)}\" fill=\"{Accent(i + 1)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                }
                Text(sb, lx, py - rowH / 2 + 1, BaseW - Pad - lx, rowH - 2, around[i].Text, around[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 0);
                EndGroup(sb);
            }
            Group(sb, center);
            if (pictures)
            {
                Picture(sb, hx - rc, cy - rc, rc * 2, rc * 2, circle: true);
                double bw = rc * 1.5, bh = 40;
                sb.Append($"<rect x=\"{F(hx - bw / 2)}\" y=\"{F(cy + rc * 0.42)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" rx=\"{F(bh / 2)}\" fill=\"{Accent(0)}\"/>");
                Text(sb, hx - bw / 2, cy + rc * 0.42, bw, bh, center.Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 8);
            }
            else
            {
                sb.Append($"<circle cx=\"{F(hx)}\" cy=\"{F(cy)}\" r=\"{F(rc)}\" fill=\"{Accent(0)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, hx - rc * 0.75, cy - rc * 0.65, rc * 1.5, rc * 1.3, center.Text, center.Bullets, "#ffffff", maxFs: 15, inset: 3);
            }
            EndGroup(sb);
            return (BaseW, cy + Math.Max(rc, Ra * Math.Sin(theta) + rowH / 2) + Pad);
        }

        /// <summary>Radial Cluster: a rounded hub with rounded boxes on an ellipse round it.</summary>
        private static (double, double) DrawRadialCluster(StringBuilder sb, List<Item> items)
        {
            SplitHub(items, out var center, out var around);
            int n = around.Count;
            double hw = 180, hh = 92, bw = 136, bh = 58;
            double rx = 262, ry = n <= 6 ? 140 : 170;
            double cx = BaseW / 2, cy = n == 0 ? Pad + hh / 2 : Pad + ry + bh / 2;
            for (int i = 0; i < n; i++)
            {
                double a = Ang(i, n);
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(cy)}\" x2=\"{F(cx + rx * Math.Cos(a))}\" y2=\"{F(cy + ry * Math.Sin(a))}\" stroke=\"#c8c6c4\" stroke-width=\"2\"/>");
            }
            Box(sb, cx - hw / 2, cy - hh / 2, hw, hh, Accent(0), center, rx: 16);
            for (int i = 0; i < n; i++)
            {
                double a = Ang(i, n);
                Box(sb, cx + rx * Math.Cos(a) - bw / 2, cy + ry * Math.Sin(a) - bh / 2, bw, bh, Tint(Accent(0), 0.25 + 0.5 * (i % 2)), around[i], withBullets: false, rx: 12, textColor: i % 2 == 0 ? "#ffffff" : Ink);
            }
            return (BaseW, n == 0 ? hh + Pad * 2 : cy + ry + bh / 2 + Pad);
        }

        /// <summary>Circle Relationship: one big circle with its items as small circles on its rim.</summary>
        private static (double, double) DrawCircleRelationship(StringBuilder sb, List<Item> items)
        {
            SplitHub(items, out var center, out var around);
            int n = around.Count;
            double R = 150, r = n <= 4 ? 52 : n <= 8 ? 44 : 34;
            double cx = BaseW / 2, cy = Pad + r + R;
            Group(sb, center);
            sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(R)}\" fill=\"{Accent(0)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
            Text(sb, cx - R * 0.62, cy - R * 0.5, R * 1.24, R, center.Text, center.Bullets, "#ffffff", maxFs: 16);
            EndGroup(sb);
            for (int i = 0; i < n; i++)
            {
                // Word sets the small circles round the upper right of the rim.
                double a = -Math.PI / 2 + (n == 1 ? Math.PI / 4 : Math.PI * 1.5 * i / Math.Max(1, n - 1)) * (n > 6 ? 4.0 / 3 * (n - 1) / n : 1);
                Circle(sb, cx + R * Math.Cos(a), cy + R * Math.Sin(a), r, Accent(i + 1), around[i], withBullets: false);
            }
            return (BaseW, cy + R + r + Pad);
        }

        /// <summary>Hexagon Radial: a honeycomb, the hub hexagon ringed by up to six more.</summary>
        private static (double, double) DrawHexagonRadial(StringBuilder sb, List<Item> items)
        {
            SplitHub(items, out var center, out var around);
            int n = Math.Min(6, around.Count);
            double s = 78, d = Math.Sqrt(3) * s + 8;
            double cx = BaseW / 2, cy = Pad + d + s * 0.87;
            void Hex(double x, double y, string fill, Item item, bool bullets)
            {
                Group(sb, item);
                sb.Append($"<polygon points=\"{HexPoints(x, y, s)}\" fill=\"{fill}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, x - s * 0.78, y - s * 0.55, s * 1.56, s * 1.1, item.Text, bullets ? item.Bullets : Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            Hex(cx, cy, Accent(0), center, false);
            for (int i = 0; i < n; i++)
            {
                double a = Math.PI / 180 * (-60 + 60 * i);
                Hex(cx + d * Math.Cos(a), cy + d * Math.Sin(a), Accent(i + 1), around[i], false);
            }
            double h = cy + d * 0.87 + s + Pad;
            if (around.Count > n) { Note(sb, cx, h - Pad + 14, around.Count - n); h += 18; }
            return (BaseW, h);
        }

        // ------------------------------------------------------------------ processes

        /// <summary>Lays n steps across the page: box width and the x of the first, with room for a
        /// link of <paramref name="link"/> between neighbours. Null when they'd be too narrow.</summary>
        private static (double bw, double x0)? Row(int n, double link, double maxW = 200, double minW = 92)
        {
            double bw = Math.Min(maxW, (BaseW - Pad * 2 - (n - 1) * link) / n);
            if (bw < minW) return null;
            return (bw, (BaseW - (n * bw + (n - 1) * link)) / 2);
        }

        private enum HeadStyle { Accent, Stacked, Detailed }

        /// <summary>Steps with a header and a body. Accent: the header overlaps the corner of a
        /// tinted card. Stacked: a header over a white card, linked by arrows at header height.
        /// Detailed: one tall card per step with a coloured head, wedges between them.</summary>
        private static (double, double) DrawHeadedProcess(StringBuilder sb, List<Item> items, HeadStyle style)
        {
            int n = items.Count;
            double link = style == HeadStyle.Accent ? 46 : style == HeadStyle.Stacked ? 40 : 26;
            if (Row(n, link) is not (double bw, double x0)) return DrawBending(sb, items);
            double headH = Math.Min(90, Math.Max(46, items.Max(i => Measure(i.Text, Array.Empty<string>(), bw - 28)) + 16));
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(220, Math.Max(64, items.Max(i => Measure("", i.Bullets, bw - 24, 13)) + 20)) : 36;
            double y = Pad, h = 0;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + link);
                string c = Accent(i);
                Group(sb, items[i]);
                switch (style)
                {
                    case HeadStyle.Accent:
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + headH * 0.5)}\" width=\"{F(bw)}\" height=\"{F(bodyH + headH * 0.5)}\" rx=\"8\" fill=\"{Tint(c, 0.86)}\" stroke=\"{Tint(c, 0.6)}\"/>");
                        Text(sb, x, y + headH, bw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 12);
                        sb.Append($"<rect x=\"{F(x - 8)}\" y=\"{F(y)}\" width=\"{F(bw - 12)}\" height=\"{F(headH)}\" rx=\"4\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        Text(sb, x - 8, y, bw - 12, headH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                        h = y + headH + bodyH;
                        break;
                    case HeadStyle.Stacked:
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(headH)}\" rx=\"10\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        Text(sb, x, y, bw, headH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + headH + 8)}\" width=\"{F(bw)}\" height=\"{F(bodyH)}\" rx=\"10\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.4)}\" stroke-width=\"1.5\"/>");
                        Text(sb, x, y + headH + 8, bw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 12);
                        h = y + headH + 8 + bodyH;
                        break;
                    default:
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(headH + bodyH)}\" rx=\"6\" fill=\"{Tint(c, 0.82)}\"/>");
                        sb.Append($"<path d=\"{TabD(x, y, bw, headH, 6)}\" fill=\"{c}\"/>");
                        Text(sb, x, y, bw, headH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                        Text(sb, x, y + headH, bw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 12);
                        h = y + headH + bodyH;
                        break;
                }
                EndGroup(sb);
                if (i == n - 1) continue;
                double lx = x + bw;
                switch (style)
                {
                    case HeadStyle.Accent:
                        RightArrow(sb, lx + 8, y + headH * 0.5 + (bodyH + headH * 0.5) / 2, link - 16, 28);
                        break;
                    case HeadStyle.Stacked:
                        sb.Append($"<line x1=\"{F(lx + 4)}\" y1=\"{F(y + headH / 2)}\" x2=\"{F(lx + link - 6)}\" y2=\"{F(y + headH / 2)}\" stroke=\"{Connector}\" stroke-width=\"2.5\" marker-end=\"url(#sa-arrow)\"/>");
                        break;
                    default:
                        double my = y + (headH + bodyH) / 2;
                        sb.Append($"<polygon points=\"{F(lx + 5)},{F(my - 14)} {F(lx + link - 5)},{F(my)} {F(lx + 5)},{F(my + 14)}\" fill=\"{Accent(i)}\"/>");
                        break;
                }
            }
            return (BaseW, h + Pad);
        }

        /// <summary>Upward-arrow callouts: each step's heading in a callout pointing up at its
        /// detail.</summary>
        private static (double, double) DrawCalloutProcess(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 12) is not (double bw, double x0)) return DrawBending(sb, items);
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(200, Math.Max(60, items.Max(i => Measure("", i.Bullets, bw - 20, 13)) + 18)) : 0;
            double headH = Math.Min(90, Math.Max(48, items.Max(i => Measure(i.Text, Array.Empty<string>(), bw - 20)) + 16));
            double ah = 30, y = Pad;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 12);
                string c = Accent(i);
                Group(sb, items[i]);
                if (anyBody)
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bodyH)}\" rx=\"6\" fill=\"{Tint(c, 0.86)}\" stroke=\"{Tint(c, 0.6)}\"/>");
                    Text(sb, x, y, bw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true);
                }
                double yb = y + bodyH + 6;
                string pts = $"{F(x)},{F(yb + ah)} {F(x + bw * 0.36)},{F(yb + ah)} {F(x + bw * 0.36)},{F(yb + ah * 0.5)} {F(x + bw * 0.26)},{F(yb + ah * 0.5)} {F(x + bw / 2)},{F(yb)} {F(x + bw * 0.74)},{F(yb + ah * 0.5)} {F(x + bw * 0.64)},{F(yb + ah * 0.5)} {F(x + bw * 0.64)},{F(yb + ah)} {F(x + bw)},{F(yb + ah)} {F(x + bw)},{F(yb + ah + headH)} {F(x)},{F(yb + ah + headH)}";
                sb.Append($"<polygon points=\"{pts}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\" stroke-linejoin=\"round\"/>");
                Text(sb, x, yb + ah, bw, headH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                EndGroup(sb);
            }
            return (BaseW, y + bodyH + 6 + ah + headH + Pad);
        }

        /// <summary>Alternating Flow: headings in a row of arrows, details alternating above and below.</summary>
        private static (double, double) DrawAlternatingFlow(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 40) is not (double bw, double x0)) return DrawBending(sb, items);
            double headH = Math.Min(80, Math.Max(48, items.Max(i => Measure(i.Text, Array.Empty<string>(), bw - 20)) + 16));
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(180, Math.Max(56, items.Max(i => Measure("", i.Bullets, bw - 16, 13)) + 16)) : 0;
            double stem = anyBody ? 18 : 0;
            double hy = Pad + bodyH + stem;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 40);
                string c = Accent(i);
                Group(sb, items[i]);
                if (anyBody && items[i].Children.Count > 0)
                {
                    bool above = i % 2 == 0;
                    double by = above ? Pad : hy + headH + stem;
                    sb.Append($"<line x1=\"{F(x + bw / 2)}\" y1=\"{F(above ? by + bodyH : hy + headH)}\" x2=\"{F(x + bw / 2)}\" y2=\"{F(above ? hy : by)}\" stroke=\"{Tint(c, 0.4)}\" stroke-width=\"2\"/>");
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(by)}\" width=\"{F(bw)}\" height=\"{F(bodyH)}\" rx=\"6\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.4)}\" stroke-width=\"1.5\"/>");
                    Text(sb, x, by, bw, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 8);
                }
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(hy)}\" width=\"{F(bw)}\" height=\"{F(headH)}\" rx=\"6\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                Text(sb, x, hy, bw, headH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                EndGroup(sb);
                if (i < n - 1) RightArrow(sb, x + bw + 6, hy + headH / 2, 28, 26);
            }
            return (BaseW, hy + headH + stem + bodyH + Pad);
        }

        /// <summary>Continuous Arrow Process: one wide arrow carrying every step, with each step's
        /// detail under its stretch of the arrow.</summary>
        private static (double, double) DrawContinuousArrow(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double ah = 86, head = 46, x0 = Pad, x1 = BaseW - Pad, y = Pad + 10;
            double seg = (x1 - head - x0) / n;
            string c = Accent(0);
            sb.Append($"<polygon points=\"{F(x0)},{F(y)} {F(x1 - head)},{F(y)} {F(x1 - head)},{F(y - 10)} {F(x1)},{F(y + ah / 2)} {F(x1 - head)},{F(y + ah + 10)} {F(x1 - head)},{F(y + ah)} {F(x0)},{F(y + ah)}\" fill=\"{c}\"/>");
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(200, Math.Max(50, items.Max(i => Measure("", i.Bullets, seg - 16, 13)) + 12)) : 0;
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * seg;
                Group(sb, items[i]);
                if (i > 0) sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(y + 14)}\" x2=\"{F(x)}\" y2=\"{F(y + ah - 14)}\" stroke=\"#ffffff\" stroke-opacity=\"0.7\" stroke-width=\"2\"/>");
                sb.Append($"<rect x=\"{F(x + 4)}\" y=\"{F(y + 6)}\" width=\"{F(seg - 8)}\" height=\"{F(ah - 12)}\" fill=\"{c}\" fill-opacity=\"0\"/>");
                Text(sb, x, y, seg, ah, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 15);
                if (anyBody && items[i].Children.Count > 0)
                    Text(sb, x, y + ah + 16, seg, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 8);
                EndGroup(sb);
            }
            return (BaseW, y + ah + 10 + (anyBody ? bodyH + 10 : 0) + Pad);
        }

        /// <summary>Continuous Block Process: rounded blocks in front of one broad pale arrow.</summary>
        private static (double, double) DrawContinuousBlock(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double x0 = Pad, x1 = BaseW - Pad, head = 70;
            double seg = (x1 - x0 - head * 0.4) / n, bw = Math.Min(180, seg * 0.82);
            double bh = Math.Min(200, Math.Max(90, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 24));
            double ah = bh + 50, y = Pad;
            sb.Append($"<polygon points=\"{F(x0)},{F(y + 24)} {F(x1 - head)},{F(y + 24)} {F(x1 - head)},{F(y)} {F(x1)},{F(y + ah / 2)} {F(x1 - head)},{F(y + ah)} {F(x1 - head)},{F(y + ah - 24)} {F(x0)},{F(y + ah - 24)}\" fill=\"{Tint(Accent(0), 0.78)}\"/>");
            for (int i = 0; i < n; i++)
                Box(sb, x0 + i * seg + (seg - bw) / 2, y + (ah - bh) / 2, bw, bh, Accent(i), items[i], rx: 12);
            return (BaseW, y + ah + Pad);
        }

        /// <summary>Circles in a row: filled circles joined by block arrows, or (Circle Process)
        /// ringed circles on a line with the step's detail beneath.</summary>
        private static (double, double) DrawCircleProcess(StringBuilder sb, List<Item> items, bool filled)
        {
            int n = items.Count;
            double link = filled ? 46 : 24;
            double slot = (BaseW - Pad * 2 - (n - 1) * link) / n;
            double r = Math.Min(filled ? 78 : 62, slot / 2);
            if (r < 34) return DrawBending(sb, items);
            if (filled) r = Math.Max(r, Math.Min(r, RadiusForWords(items)));
            double cy = Pad + r, x = (BaseW - (n * slot + (n - 1) * link)) / 2;
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Min(200, Math.Max(40, items.Max(i => Measure(filled ? "" : i.Text, i.Bullets, slot - 8, 13)) + 10))
                                   : filled ? 0 : Math.Max(36, items.Max(i => Measure(i.Text, Array.Empty<string>(), slot - 8, 13)) + 12);
            if (!filled)
                sb.Append($"<line x1=\"{F(x + slot / 2)}\" y1=\"{F(cy)}\" x2=\"{F(x + (n - 1) * (slot + link) + slot / 2)}\" y2=\"{F(cy)}\" stroke=\"#c8c6c4\" stroke-width=\"4\"/>");
            for (int i = 0; i < n; i++, x += slot + link)
            {
                double ccx = x + slot / 2;
                string c = Accent(i);
                Group(sb, items[i]);
                if (filled)
                {
                    sb.Append($"<circle cx=\"{F(ccx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    Text(sb, ccx - r * 0.75, cy - r * 0.65, r * 1.5, r * 1.3, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 3);
                    if (anyBody) Text(sb, x, cy + r + 10, slot, bodyH, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: slot >= 120, inset: 4);
                }
                else
                {
                    sb.Append($"<circle cx=\"{F(ccx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"7\"/>");
                    sb.Append($"<text x=\"{F(ccx)}\" y=\"{F(cy + r * 0.2)}\" text-anchor=\"middle\" font-size=\"{F(r * 0.62)}\" font-weight=\"700\" fill=\"{c}\">{i + 1}</text>");
                    Text(sb, x, cy + r + 10, slot, bodyH, items[i].Text, items[i].Bullets, Ink, maxFs: 13, inset: 4);
                }
                EndGroup(sb);
                if (filled && i < n - 1) RightArrow(sb, x + slot + 6, cy, link - 12, 30);
            }
            return (BaseW, cy + r + (bodyH > 0 ? 10 + bodyH : 0) + Pad);
        }

        /// <summary>Pie Process: a pie per step that fills a little more each time.</summary>
        private static (double, double) DrawPieProcess(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 16, maxW: 180, minW: 80) is not (double bw, double x0)) return DrawBending(sb, items);
            double r = Math.Min(38, bw * 0.3), y = Pad;
            double textH = Math.Min(220, Math.Max(50, items.Max(i => Measure(i.Text, i.Bullets, bw - 8, 13)) + 10));
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 16), cx = x + r + 4, cy = y + r;
                string c = Accent(0);
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{Tint(c, 0.85)}\"/>");
                double frac = (i + 1.0) / n;
                if (frac >= 0.999) sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{c}\"/>");
                else sb.Append($"<path d=\"{WedgeD(cx, cy, 0, r, -Math.PI / 2, -Math.PI / 2 + 2 * Math.PI * frac)}\" fill=\"{c}\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + r * 2 + 12)}\" width=\"{F(bw)}\" height=\"3\" fill=\"{Tint(c, 0.5)}\"/>");
                Text(sb, x, y + r * 2 + 20, bw, textH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 4);
                EndGroup(sb);
            }
            return (BaseW, y + r * 2 + 20 + textH + Pad);
        }

        /// <summary>Interconnected Block Process: staggered blocks, each pointing on to the next.</summary>
        private static (double, double) DrawInterconnected(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 18, maxW: 190) is not (double bw, double x0)) return DrawBending(sb, items);
            double bh = Math.Min(200, Math.Max(70, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 22));
            double drop = Math.Min(44, bh * 0.4);
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 18), y = Pad + (i % 2) * drop;
                string c = Accent(i);
                Group(sb, items[i]);
                if (i < n - 1)
                {
                    // A callout pointer from this block's side, aimed at the next block.
                    double my = y + bh / 2, ny = Pad + ((i + 1) % 2) * drop + bh / 2;
                    sb.Append($"<polygon points=\"{F(x + bw - 2)},{F(my - bh * 0.18)} {F(x + bw - 2)},{F(my + bh * 0.18)} {F(x + bw + 16)},{F((my + ny) / 2)}\" fill=\"{c}\"/>");
                }
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(bw)}\" height=\"{F(bh)}\" rx=\"2\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                Text(sb, x, y, bw, bh, items[i].Text, items[i].Bullets, "#ffffff");
                EndGroup(sb);
            }
            return (BaseW, Pad * 2 + bh + (n > 1 ? drop : 0));
        }

        /// <summary>Sub-Step Process: numbered steps on a line, each with its sub-steps listed
        /// under it and marked as small dots along the line.</summary>
        private static (double, double) DrawSubSteps(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 20, maxW: 210, minW: 86) is not (double bw, double x0)) return DrawBending(sb, items);
            double r = 22, ly = Pad + r;
            double textH = Math.Min(240, Math.Max(44, items.Max(i => Measure(i.Text, i.Bullets, bw - 8, 13)) + 10));
            sb.Append($"<line x1=\"{F(x0 + r)}\" y1=\"{F(ly)}\" x2=\"{F(x0 + (n - 1) * (bw + 20) + bw - 4)}\" y2=\"{F(ly)}\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 20), cx = x + r;
                string c = Accent(i);
                Group(sb, items[i]);
                int dots = Math.Min(6, items[i].Children.Count);
                for (int d = 0; d < dots; d++)
                    sb.Append($"<circle cx=\"{F(cx + r + 18 + d * Math.Min(18, (bw - r * 2 - 24) / Math.Max(1, dots)))}\" cy=\"{F(ly)}\" r=\"5\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"2\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(ly)}\" r=\"{F(r)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                sb.Append($"<text x=\"{F(cx)}\" y=\"{F(ly + 6)}\" text-anchor=\"middle\" font-size=\"16\" font-weight=\"700\" fill=\"#ffffff\">{i + 1}</text>");
                Text(sb, x, ly + r + 10, bw, textH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                EndGroup(sb);
            }
            return (BaseW, ly + r + 10 + textH + Pad);
        }

        /// <summary>Phased Process: each phase is a card capped by its own arch, the cards stepping
        /// up from one phase to the next.</summary>
        private static (double, double) DrawPhased(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            if (Row(n, 14) is not (double bw, double x0)) return DrawBending(sb, items);
            double bh = Math.Min(200, Math.Max(70, items.Max(i => Measure(i.Text, i.Bullets, bw - 20)) + 22));
            double archR = bw / 2, rise = n > 1 ? Math.Min(30, 120.0 / (n - 1)) : 0;
            double baseTop = Pad + archR + (n - 1) * rise; // the first (lowest) card's top
            for (int i = 0; i < n; i++)
            {
                double x = x0 + i * (bw + 14), cx = x + bw / 2, top = baseTop - i * rise;
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<path d=\"{WedgeD(cx, top - 4, archR * 0.6, archR, Math.PI, 2 * Math.PI)}\" fill=\"{Tint(c, 0.35)}\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(bw)}\" height=\"{F(bh + i * rise)}\" rx=\"6\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                Text(sb, x, top, bw, bh, items[i].Text, items[i].Bullets, "#ffffff");
                EndGroup(sb);
            }
            return (BaseW, baseTop + bh + Pad);
        }

        // ------------------------------------------------------------------ vertical lists

        private enum CardMark { None, Number, Quote, Icon, Image }

        /// <summary>Word's text-card layouts: a side line (a rule down the card's left) or a short
        /// line (a short rule above the title), optionally with a number, a quote mark, an icon or a
        /// picture; two columns unless <paramref name="wide"/>.</summary>
        private static (double, double) DrawCards(StringBuilder sb, List<Item> items, bool sideLine, CardMark mark, bool wide)
        {
            int cols = wide || items.Count == 1 ? 1 : 2;
            double gapX = 30, gapY = 22;
            double cw = (BaseW - Pad * 2 - (cols - 1) * gapX) / cols;
            double markW = sideLine && mark is CardMark.Number or CardMark.Quote or CardMark.Icon ? 50 : 0;
            double imageH = mark == CardMark.Image ? 70 : 0;
            double topMark = !sideLine && mark is CardMark.Number or CardMark.Quote ? 36 : 0;
            double lead = sideLine ? markW + 18 : 0, ruleH = sideLine ? 0 : 14;
            double textW = cw - lead;
            double y = Pad;
            string quote = ((char)0x201C).ToString();
            for (int r = 0; r * cols < items.Count; r++)
            {
                var row = items.Skip(r * cols).Take(cols).ToList();
                // FitText keeps 8 px clear of a slot's edges; a slot only just tall enough shrank the font.
                double th = Math.Max(36, row.Max(i => Measure(i.Text, i.Bullets, textW - 4, 14)) + 14);
                double ch = imageH + topMark + ruleH + th;
                for (int c = 0; c < row.Count; c++)
                {
                    int idx = r * cols + c;
                    double x = Pad + c * (cw + gapX);
                    string color = Accent(idx);
                    Group(sb, row[c]);
                    double ty = y;
                    if (imageH > 0) { Picture(sb, x + lead, ty, textW, imageH - 8); ty += imageH; }
                    if (sideLine)
                    {
                        if (mark == CardMark.Number)
                            sb.Append($"<text x=\"{F(x + markW / 2)}\" y=\"{F(y + 34)}\" text-anchor=\"middle\" font-size=\"34\" font-weight=\"300\" fill=\"{color}\">{idx + 1:00}</text>");
                        else if (mark == CardMark.Quote)
                            sb.Append($"<text x=\"{F(x + markW / 2)}\" y=\"{F(y + 44)}\" text-anchor=\"middle\" font-size=\"56\" font-family=\"Georgia, serif\" fill=\"{color}\">{quote}</text>");
                        else if (mark == CardMark.Icon)
                        {
                            sb.Append($"<circle cx=\"{F(x + markW / 2)}\" cy=\"{F(y + 22)}\" r=\"20\" fill=\"{color}\"/>");
                            sb.Append($"<circle cx=\"{F(x + markW / 2)}\" cy=\"{F(y + 22)}\" r=\"8\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"2.5\"/>");
                        }
                        sb.Append($"<rect x=\"{F(x + markW + 6)}\" y=\"{F(y)}\" width=\"4\" height=\"{F(ch)}\" rx=\"2\" fill=\"{color}\"/>");
                    }
                    else
                    {
                        if (mark == CardMark.Number)
                            sb.Append($"<text x=\"{F(x)}\" y=\"{F(ty + 28)}\" font-size=\"30\" font-weight=\"300\" fill=\"{color}\">{idx + 1:00}</text>");
                        else if (mark == CardMark.Quote)
                            sb.Append($"<text x=\"{F(x)}\" y=\"{F(ty + 40)}\" font-size=\"52\" font-family=\"Georgia, serif\" fill=\"{color}\">{quote}</text>");
                        ty += topMark;
                        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(ty + 2)}\" width=\"44\" height=\"4\" rx=\"2\" fill=\"{color}\"/>");
                        ty += ruleH;
                    }
                    Text(sb, x + lead, ty, textW, th, row[c].Text, row[c].Bullets, Ink, maxFs: 15, alignLeft: true, inset: 2);
                    EndGroup(sb);
                }
                y += ch + gapY;
            }
            return (BaseW, y - gapY + Pad);
        }

        /// <summary>Vertical Action List: the label in a block on the left, its actions in a
        /// separate top-rounded panel on the right.</summary>
        private static (double, double) DrawActionList(StringBuilder sb, List<Item> items)
        {
            double lw = 200, gap = 10, y = Pad, w = BaseW - Pad * 2;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double rh = Math.Max(56, Math.Max(Measure(it.Text, Array.Empty<string>(), lw - 24), Measure("", it.Bullets, w - lw - gap - 24, 13)) + 22);
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(lw)}\" height=\"{F(rh)}\" rx=\"3\" fill=\"{c}\"/>");
                Text(sb, Pad, y, lw, rh, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 15);
                sb.Append($"<path d=\"{TabD(Pad + lw + gap, y, w - lw - gap, rh, 14)}\" fill=\"{Tint(c, 0.86)}\" stroke=\"{Tint(c, 0.55)}\"/>");
                Text(sb, Pad + lw + gap + 6, y, w - lw - gap - 6, rh, "", it.Bullets, Ink, maxFs: 13, alignLeft: true);
                EndGroup(sb);
                y += rh + 12;
            }
            return (BaseW, y - 12 + Pad);
        }

        /// <summary>Vertical Box List: a tab carrying the heading above each panel.</summary>
        private static (double, double) DrawStackedTabs(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = BaseW - Pad * 2, tw = 240;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double tabH = Math.Max(38, Measure(it.Text, Array.Empty<string>(), tw - 24) + 12);
                double bodyH = it.Children.Count > 0 ? Math.Max(44, Measure("", it.Bullets, w - 30, 13) + 18) : 18;
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y + tabH - 2)}\" width=\"{F(w)}\" height=\"{F(bodyH + 2)}\" rx=\"4\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.45)}\" stroke-width=\"1.5\"/>");
                sb.Append($"<path d=\"{TabD(Pad + 14, y, tw, tabH, 10)}\" fill=\"{c}\"/>");
                Text(sb, Pad + 14, y, tw, tabH, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                if (it.Children.Count > 0) Text(sb, Pad + 10, y + tabH, w - 20, bodyH, "", it.Bullets, Ink, maxFs: 13, alignLeft: true);
                EndGroup(sb);
                y += tabH + bodyH + 14;
            }
            return (BaseW, y - 14 + Pad);
        }

        /// <summary>Numbered rows: a number in a rounded square beside a card, or (Numbered Title
        /// List) in a circle beside the text, with a rule under each row.</summary>
        private static (double, double) DrawNumberedRows(StringBuilder sb, List<Item> items, bool circle)
        {
            double y = Pad, badge = 50, w = BaseW - Pad * 2, tx = Pad + badge + 16, tw = BaseW - Pad - tx;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double rh = Math.Max(badge + 8, Measure(it.Text, it.Bullets, tw - 24) + 20);
                Group(sb, it);
                if (circle)
                {
                    sb.Append($"<circle cx=\"{F(Pad + badge / 2)}\" cy=\"{F(y + badge / 2 + 4)}\" r=\"{F(badge / 2)}\" fill=\"{c}\"/>");
                    if (i < items.Count - 1) sb.Append($"<rect x=\"{F(tx)}\" y=\"{F(y + rh + 6)}\" width=\"{F(tw)}\" height=\"1.5\" fill=\"#e1dfdd\"/>");
                    Text(sb, tx - 10, y, tw + 10, rh, it.Text, it.Bullets, Ink, alignLeft: true);
                }
                else
                {
                    sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y + 4)}\" width=\"{F(badge)}\" height=\"{F(badge)}\" rx=\"10\" fill=\"{c}\"/>");
                    sb.Append($"<rect x=\"{F(tx)}\" y=\"{F(y)}\" width=\"{F(tw)}\" height=\"{F(rh)}\" rx=\"8\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.5)}\" stroke-width=\"1.5\"/>");
                    Text(sb, tx, y, tw, rh, it.Text, it.Bullets, Ink, alignLeft: true, inset: 14);
                }
                sb.Append($"<text x=\"{F(Pad + badge / 2)}\" y=\"{F(y + badge / 2 + 12)}\" text-anchor=\"middle\" font-size=\"22\" font-weight=\"700\" fill=\"#ffffff\">{i + 1}</text>");
                EndGroup(sb);
                y += rh + 14;
            }
            _ = w;
            return (BaseW, y - 14 + Pad);
        }

        /// <summary>Vertical Circle-Arrow List (vList3): a circle set into the tail of a pentagon arrow.</summary>
        private static (double, double) DrawHomePlates(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = BaseW - Pad * 2;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double rh = Math.Max(64, Measure(it.Text, it.Bullets, w - 140) + 20);
                double r = Math.Min(rh / 2 + 6, 44), cx = Pad + r, cy = y + rh / 2;
                double x0 = cx, x1 = BaseW - Pad, tip = Math.Min(40, rh / 2);
                Group(sb, it);
                sb.Append($"<polygon points=\"{F(x0)},{F(y)} {F(x1 - tip)},{F(y)} {F(x1)},{F(cy)} {F(x1 - tip)},{F(y + rh)} {F(x0)},{F(y + rh)}\" fill=\"{Tint(c, 0.8)}\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Text(sb, cx + r + 8, y, x1 - tip - cx - r - 10, rh, it.Text, it.Bullets, Ink, alignLeft: true, inset: 6);
                EndGroup(sb);
                y += rh + 12;
            }
            return (BaseW, y - 12 + Pad);
        }

        /// <summary>Vertical Accent List: a heading bar with a run of chevrons under it, then the text.</summary>
        private static (double, double) DrawChevronAccentList(StringBuilder sb, List<Item> items)
        {
            double y = Pad, w = BaseW - Pad * 2;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double headH = Math.Max(38, Measure(it.Text, Array.Empty<string>(), w - 24) + 12);
                Group(sb, it);
                sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(headH)}\" rx=\"2\" fill=\"{c}\"/>");
                Text(sb, Pad, y, w, headH, it.Text, Array.Empty<string>(), "#ffffff", alignLeft: true, inset: 14);
                double cy = y + headH + 6;
                for (int k = 0; k < 12; k++)
                {
                    double cx = Pad + k * 26;
                    sb.Append($"<polygon points=\"{F(cx)},{F(cy)} {F(cx + 16)},{F(cy)} {F(cx + 24)},{F(cy + 6)} {F(cx + 16)},{F(cy + 12)} {F(cx)},{F(cy + 12)} {F(cx + 8)},{F(cy + 6)}\" fill=\"{Tint(c, 0.25 + k * 0.06)}\"/>");
                }
                double bodyH = it.Children.Count > 0 ? Math.Max(30, Measure("", it.Bullets, w - 20, 13) + 10) : 0;
                if (bodyH > 0) Text(sb, Pad, cy + 18, w, bodyH, "", it.Bullets, Ink, maxFs: 13, alignLeft: true, inset: 6);
                EndGroup(sb);
                y = cy + 18 + bodyH + 14;
            }
            return (BaseW, y - 14 + Pad);
        }

        /// <summary>Icon Circle Label List: a row of icon circles, each labelled underneath.</summary>
        private static (double, double) DrawIconCircles(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = Math.Min(n, n == 4 ? 4 : 3);
            double gap = 24, cw = (BaseW - Pad * 2 - (cols - 1) * gap) / cols, r = Math.Min(44, cw * 0.24);
            double y = Pad;
            for (int row = 0; row * cols < n; row++)
            {
                var part = items.Skip(row * cols).Take(cols).ToList();
                double th = Math.Max(30, part.Max(i => Measure(i.Text, i.Bullets, cw - 12, 14)) + 14);
                double rowW = part.Count * cw + (part.Count - 1) * gap, x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < part.Count; c++)
                {
                    int idx = row * cols + c;
                    double x = x0 + c * (cw + gap), cx = x + cw / 2;
                    string color = Accent(idx);
                    Group(sb, part[c]);
                    sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(y + r)}\" r=\"{F(r)}\" fill=\"{color}\"/>");
                    sb.Append($"<rect x=\"{F(cx - r * 0.36)}\" y=\"{F(y + r * 0.64)}\" width=\"{F(r * 0.72)}\" height=\"{F(r * 0.72)}\" rx=\"{F(r * 0.12)}\" fill=\"none\" stroke=\"#ffffff\" stroke-width=\"2.5\"/>");
                    Text(sb, x, y + r * 2 + 10, cw, th, part[c].Text, part[c].Bullets, Ink, maxFs: 14, inset: 4);
                    EndGroup(sb);
                }
                y += r * 2 + 10 + th + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Vertical Curved List: circles riding a curve down the left, each with its bar.</summary>
        private static (double, double) DrawCurvedList(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double rowH = Math.Max(62, items.Max(i => Measure(i.Text, i.Bullets, 440)) + 18), r = Math.Min(26, rowH / 2 - 4);
            double total = n * rowH, cy = Pad + total / 2;
            double half0 = total / 2 - rowH * 0.35, sag = 46;
            // A circle through both ends of the curve (at the page margin) and its middle 46 px in.
            double big = Math.Max(160, (half0 * half0 + sag * sag) / (2 * sag)), bigCx = Pad + sag - big;
            double Xat(double y) { double dy = y - cy; return bigCx + Math.Sqrt(Math.Max(0, big * big - dy * dy)); }
            double y0 = Pad, y1 = Pad + total;
            double half = half0, aTop = -Math.Asin(Math.Min(1, half / big)), aBot = -aTop;
            sb.Append($"<path d=\"{ArcD(bigCx, cy, big, aTop, aBot)}\" fill=\"none\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            for (int i = 0; i < n; i++)
            {
                double yc = y0 + (i + 0.5) * rowH, xc = Xat(yc);
                string c = Accent(i);
                Group(sb, items[i]);
                double bx = xc + r * 0.4;
                sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(yc - rowH / 2 + 6)}\" width=\"{F(BaseW - Pad - bx)}\" height=\"{F(rowH - 12)}\" rx=\"4\" fill=\"{c}\"/>");
                sb.Append($"<circle cx=\"{F(xc)}\" cy=\"{F(yc)}\" r=\"{F(r)}\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"5\"/>");
                Text(sb, xc + r + 8, yc - rowH / 2 + 6, BaseW - Pad - xc - r - 8, rowH - 12, items[i].Text, items[i].Bullets, "#ffffff", alignLeft: true, inset: 6);
                EndGroup(sb);
            }
            _ = y1;
            return (BaseW, total + Pad * 2);
        }

        /// <summary>Vertical Circle List: a line down the left with a circle per item on it.</summary>
        private static (double, double) DrawCircleLineList(StringBuilder sb, List<Item> items)
        {
            double y = Pad, lx = Pad + 18, tx = lx + 34, tw = BaseW - Pad - tx;
            var rows = items.Select(i => Math.Max(46, Measure(i.Text, i.Bullets, tw - 10) + 14)).ToList();
            double total = rows.Sum() + (items.Count - 1) * 10;
            sb.Append($"<rect x=\"{F(lx - 1.5)}\" y=\"{F(Pad + 8)}\" width=\"3\" height=\"{F(Math.Max(0, total - 16))}\" fill=\"#c8c6c4\"/>");
            for (int i = 0; i < items.Count; i++)
            {
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(lx)}\" cy=\"{F(y + 20)}\" r=\"13\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Text(sb, tx, y, tw, rows[i], items[i].Text, items[i].Bullets, Ink, alignLeft: true, inset: 2);
                if (i < items.Count - 1) sb.Append($"<rect x=\"{F(tx)}\" y=\"{F(y + rows[i] + 4)}\" width=\"{F(tw)}\" height=\"1\" fill=\"#e1dfdd\"/>");
                EndGroup(sb);
                y += rows[i] + 10;
            }
            return (BaseW, y - 10 + Pad);
        }

        /// <summary>Bracket List: the heading on the left, a brace, then its points.</summary>
        private static (double, double) DrawBracketList(StringBuilder sb, List<Item> items)
        {
            double y = Pad, lw = 210, bx = Pad + lw + 16, tx = bx + 26, tw = BaseW - Pad - tx;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double rh = Math.Max(52, Math.Max(Measure(it.Text, Array.Empty<string>(), lw - 10), Measure("", it.Bullets, tw - 10, 13)) + 14);
                Group(sb, it);
                Text(sb, Pad, y, lw, rh, it.Text, Array.Empty<string>(), c == Accent(0) ? Ink : Ink, maxFs: 16, alignLeft: false, inset: 4);
                double r = 8, mid = y + rh / 2;
                sb.Append($"<path d=\"M{F(bx + 2 * r)} {F(y + 2)}A{F(r)} {F(r)} 0 0 0 {F(bx + r)} {F(y + 2 + r)}V{F(mid - r)}A{F(r)} {F(r)} 0 0 1 {F(bx)} {F(mid)}A{F(r)} {F(r)} 0 0 1 {F(bx + r)} {F(mid + r)}V{F(y + rh - 2 - r)}A{F(r)} {F(r)} 0 0 0 {F(bx + 2 * r)} {F(y + rh - 2)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"3\" stroke-linecap=\"round\"/>");
                if (it.Children.Count > 0) Text(sb, tx, y, tw, rh, "", it.Bullets, Ink, maxFs: 13, alignLeft: true, inset: 2);
                EndGroup(sb);
                y += rh + 14;
            }
            return (BaseW, y - 14 + Pad);
        }

        /// <summary>Varying Width List: centred bars, each as wide as its text needs.</summary>
        private static (double, double) DrawVaryingWidth(StringBuilder sb, List<Item> items)
        {
            double y = Pad, maxW = BaseW - Pad * 2;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                double want = Math.Max(it.Text.Length * CharW(15, true), it.Bullets.Select(b => b.Length * CharW(13, false)).DefaultIfEmpty(0).Max()) + 60;
                double w = Math.Clamp(want, 110, maxW);
                double rh = Math.Max(48, Measure(it.Text, it.Bullets, w - 24) + 18);
                Box(sb, (BaseW - w) / 2, y, w, rh, Accent(i), it, rx: 4);
                y += rh + 8;
            }
            return (BaseW, y - 8 + Pad);
        }

        /// <summary>Reverse List: blocks stacked with a pair of curved arrows beside each pair,
        /// showing they swap places.</summary>
        private static (double, double) DrawReverseList(StringBuilder sb, List<Item> items)
        {
            double w = 470, x = (BaseW - w) / 2 - 40, y = Pad, gap = 22;
            var tops = new List<(double y, double h)>();
            for (int i = 0; i < items.Count; i++)
            {
                double rh = Math.Max(64, Measure(items[i].Text, items[i].Bullets, w - 24) + 22);
                Box(sb, x, y, w, rh, Accent(i), items[i], rx: 12);
                tops.Add((y, rh));
                y += rh + gap;
            }
            // Word's Reverse List swaps a pair of blocks: one pair of arrows per two blocks.
            for (int i = 0; i + 1 < tops.Count; i += 2)
            {
                double a = tops[i].y + tops[i].h / 2, b = tops[i + 1].y + tops[i + 1].h / 2, ax = x + w + 14, r = (b - a) / 2;
                sb.Append($"<path d=\"M{F(ax)} {F(a)}A{F(r)} {F(r)} 0 0 1 {F(ax)} {F(b)}\" fill=\"none\" stroke=\"{Accent(i)}\" stroke-width=\"5\" marker-end=\"url(#sa-arrow)\"/>");
                sb.Append($"<path d=\"M{F(ax + 26)} {F(b - 6)}A{F(r - 10)} {F(r - 10)} 0 0 0 {F(ax + 26)} {F(a + 6)}\" fill=\"none\" stroke=\"{Accent(i + 1)}\" stroke-width=\"5\" marker-end=\"url(#sa-arrow)\"/>");
            }
            return (BaseW, y - gap + Pad);
        }

        // ------------------------------------------------------------------ pictures

        private static int PicCols(int n, int max = 4) => n <= max ? n : n == 4 ? 2 : (n <= 6 ? 3 : max);

        /// <summary>Round portraits with a caption under each (Meet the Team, Bubble Picture List…),
        /// or the same on a card (Meet the Team Card).</summary>
        private static (double, double) DrawPictureCircles(StringBuilder sb, List<Item> items, bool cards)
        {
            int n = items.Count, cols = PicCols(n);
            double gap = 20, cw = Math.Min(200, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), r = Math.Min(64, cw * 0.36);
            double capH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, cw - 16, 13)) + 10);
            double y = Pad;
            for (int row = 0; row * cols < n; row++)
            {
                var part = items.Skip(row * cols).Take(cols).ToList();
                double rowW = part.Count * cw + (part.Count - 1) * gap, x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < part.Count; c++)
                {
                    int idx = row * cols + c;
                    double x = x0 + c * (cw + gap), cx = x + cw / 2, top = y + (cards ? 14 : 0);
                    Group(sb, part[c]);
                    if (cards) sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(r * 2 + capH + 40)}\" rx=\"10\" fill=\"#ffffff\" stroke=\"{Tint(Accent(idx), 0.5)}\" stroke-width=\"1.5\"/>");
                    sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(top + r)}\" r=\"{F(r + 4)}\" fill=\"{Accent(idx)}\"/>");
                    Picture(sb, cx - r, top + r - r, r * 2, r * 2, circle: true);
                    Text(sb, x, top + r * 2 + 12, cw, capH, part[c].Text, part[c].Bullets, Ink, maxFs: 14, inset: 6);
                    EndGroup(sb);
                }
                y += r * 2 + capH + (cards ? 40 : 12) + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Hexagon Cluster: hexagonal pictures in a staggered row, captions beneath.</summary>
        private static (double, double) DrawPictureHexagons(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            double s = Math.Min(72, (BaseW - Pad * 2) / (n * 1.8 + 0.2)), step = s * 1.78, drop = s * 0.9;
            double x0 = (BaseW - ((n - 1) * step)) / 2;
            double capH = Math.Max(36, items.Take(n).Max(i => Measure(i.Text, i.Bullets, step - 8, 13)) + 12);
            double bottom = 0;
            for (int i = 0; i < n; i++)
            {
                double cx = x0 + i * step, cy = Pad + s + (i % 2) * drop;
                Group(sb, items[i]);
                sb.Append($"<polygon points=\"{HexPoints(cx, cy, s + 5)}\" fill=\"{Accent(i)}\"/>");
                Picture(sb, cx - s * 0.75, cy - s * 0.75, s * 1.5, s * 1.5, hexAround: HexPoints(cx, cy, s));
                Text(sb, cx - step / 2, cy + s + 10, step, capH, items[i].Text, items[i].Bullets, Ink, maxFs: 13, inset: 2);
                bottom = Math.Max(bottom, cy + s + 10 + capH);
                EndGroup(sb);
            }
            if (items.Count > n) { Note(sb, BaseW / 2, bottom + 16, items.Count - n); bottom += 18; }
            return (BaseW, bottom + Pad);
        }

        /// <summary>Pictures with the caption laid over their lower edge: on a translucent colour
        /// band, or (Picture Grid) edge to edge with a dark band.</summary>
        private static (double, double) DrawPictureOverlay(StringBuilder sb, List<Item> items, bool tight)
        {
            int n = items.Count, cols = tight ? Math.Min(n, n <= 4 ? 2 : 3) : PicCols(n, 3);
            double gap = tight ? 4 : 16, cw = Math.Min(tight ? 300 : 240, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.72;
            double capH = Math.Min(ph * 0.55, Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, cw - 20, 13)) + 12));
            double y = Pad;
            for (int row = 0; row * cols < n; row++)
            {
                var part = items.Skip(row * cols).Take(cols).ToList();
                double rowW = part.Count * cw + (part.Count - 1) * gap, x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < part.Count; c++)
                {
                    int idx = row * cols + c;
                    double x = x0 + c * (cw + gap);
                    Group(sb, part[c]);
                    Picture(sb, x, y, cw, ph);
                    string band = tight ? Ink : Accent(idx);
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + ph - capH)}\" width=\"{F(cw)}\" height=\"{F(capH)}\" fill=\"{band}\" fill-opacity=\"{(tight ? "0.62" : "0.86")}\"/>");
                    Text(sb, x, y + ph - capH, cw, capH, part[c].Text, part[c].Bullets, "#ffffff", maxFs: 14, alignLeft: true, inset: 10);
                    EndGroup(sb);
                }
                y += ph + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>A picture beside each item's text, on the same side every row or alternating.</summary>
        private static (double, double) DrawPictureRows(StringBuilder sb, List<Item> items, bool alternate)
        {
            double y = Pad, pw = 170, gap = 14, tw = BaseW - Pad * 2 - pw - gap;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double rh = Math.Max(pw * 0.62, Measure(it.Text, it.Bullets, tw - 28) + 22);
                bool right = alternate && i % 2 == 1;
                double px = right ? BaseW - Pad - pw : Pad, tx = right ? Pad : Pad + pw + gap;
                Group(sb, it);
                Picture(sb, px, y, pw, rh);
                if (alternate)
                {
                    sb.Append($"<rect x=\"{F(tx)}\" y=\"{F(y)}\" width=\"{F(tw)}\" height=\"{F(rh)}\" rx=\"4\" fill=\"{c}\"/>");
                    Text(sb, tx, y, tw, rh, it.Text, it.Bullets, "#ffffff", alignLeft: true, inset: 14);
                }
                else
                {
                    sb.Append($"<rect x=\"{F(tx)}\" y=\"{F(y)}\" width=\"5\" height=\"{F(rh)}\" fill=\"{c}\"/>");
                    Text(sb, tx + 5, y, tw - 5, rh, it.Text, it.Bullets, Ink, alignLeft: true, inset: 14);
                }
                EndGroup(sb);
                y += rh + 14;
            }
            return (BaseW, y - 14 + Pad);
        }

        /// <summary>Picture Lineup: tall pictures side by side over a shared rule, captions below it.</summary>
        private static (double, double) DrawPictureLineup(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 6);
            double gap = 10, cw = (BaseW - Pad * 2 - (n - 1) * gap) / n, ph = Math.Min(240, Math.Max(150, cw * 1.25));
            double capH = Math.Max(40, items.Take(n).Max(i => Measure(i.Text, i.Bullets, cw - 8, 13)) + 12);
            double y = Pad;
            sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y + ph + 8)}\" width=\"{F(BaseW - Pad * 2)}\" height=\"4\" fill=\"{Accent(0)}\"/>");
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * (cw + gap);
                Group(sb, items[i]);
                Picture(sb, x, y, cw, ph);
                Text(sb, x, y + ph + 18, cw, capH, items[i].Text, items[i].Bullets, Ink, maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            double h = y + ph + 18 + capH;
            if (items.Count > n) { Note(sb, BaseW / 2, h + 14, items.Count - n); h += 18; }
            return (BaseW, h + Pad);
        }

        /// <summary>Snapshot-style frames: a broad white border round each picture, the caption in
        /// the frame's deeper bottom edge.</summary>
        private static (double, double) DrawPictureFrames(StringBuilder sb, List<Item> items)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 22, cw = Math.Min(220, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), border = 10, ph = (cw - border * 2) * 0.8;
            double capH = Math.Max(44, items.Max(i => Measure(i.Text, i.Bullets, cw - 24, 13)) + 12);
            double ch = border + ph + capH, y = Pad;
            for (int row = 0; row * cols < n; row++)
            {
                var part = items.Skip(row * cols).Take(cols).ToList();
                double rowW = part.Count * cw + (part.Count - 1) * gap, x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < part.Count; c++)
                {
                    double x = x0 + c * (cw + gap);
                    Group(sb, part[c]);
                    sb.Append($"<rect x=\"{F(x + 4)}\" y=\"{F(y + 5)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"2\" fill=\"#000000\" fill-opacity=\"0.12\"/>");
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(ch)}\" rx=\"2\" fill=\"#ffffff\" stroke=\"#d2d0ce\"/>");
                    Picture(sb, x + border, y + border, cw - border * 2, ph);
                    Text(sb, x, y + border + ph, cw, capH, part[c].Text, part[c].Bullets, Ink, maxFs: 14, inset: 10);
                    EndGroup(sb);
                }
                y += ch + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Spiral Picture: each picture takes part of the space the last one left, turning
        /// round like a golden spiral.</summary>
        private static (double, double) DrawPictureSpiral(StringBuilder sb, List<Item> items)
        {
            int n = Math.Min(items.Count, 7);
            double x = Pad, y = Pad, w = BaseW - Pad * 2, h = 360, gap = 6;
            for (int i = 0; i < n; i++)
            {
                double px = x, py = y, pw = w, ph = h;
                if (i < n - 1)
                {
                    switch (i % 4)
                    {
                        case 0: pw = w * 0.62; x += pw + gap; w -= pw + gap; break;
                        case 1: ph = h * 0.62; y += ph + gap; h -= ph + gap; break;
                        case 2: pw = w * 0.62; px = x + w - pw; w -= pw + gap; break;
                        case 3: ph = h * 0.62; py = y + h - ph; h -= ph + gap; break;
                    }
                }
                Group(sb, items[i]);
                Picture(sb, px, py, pw, ph);
                double capH = Math.Min(ph * 0.4, 44);
                if (pw > 110 && ph > 80) // the spiral's innermost cells are too small to caption
                {
                    sb.Append($"<rect x=\"{F(px)}\" y=\"{F(py + ph - capH)}\" width=\"{F(pw)}\" height=\"{F(capH)}\" fill=\"{Accent(i)}\" fill-opacity=\"0.86\"/>");
                    Text(sb, px, py + ph - capH, pw, capH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, alignLeft: true, inset: 8);
                }
                EndGroup(sb);
            }
            double total = 360 + Pad * 2;
            if (items.Count > n) { Note(sb, BaseW / 2, total, items.Count - n); total += 18; }
            return (BaseW, total);
        }

        /// <summary>A picture with a colour block overlapping its corner (Picture Accent Blocks), or
        /// with the caption in a speech callout pointing up at it (Bending Picture Caption List).</summary>
        private static (double, double) DrawPictureBlocks(StringBuilder sb, List<Item> items, bool callout)
        {
            int n = items.Count, cols = PicCols(n, 3);
            double gap = 26, cw = Math.Min(230, (BaseW - Pad * 2 - (cols - 1) * gap) / cols), ph = cw * 0.68;
            double capH = Math.Max(46, items.Max(i => Measure(i.Text, i.Bullets, cw - 30, 13)) + 14);
            double over = callout ? -16 : capH * 0.5, y = Pad;
            for (int row = 0; row * cols < n; row++)
            {
                var part = items.Skip(row * cols).Take(cols).ToList();
                double rowW = part.Count * cw + (part.Count - 1) * gap, x0 = (BaseW - rowW) / 2;
                for (int c = 0; c < part.Count; c++)
                {
                    int idx = row * cols + c;
                    double x = x0 + c * (cw + gap);
                    string color = Accent(idx);
                    Group(sb, part[c]);
                    Picture(sb, x, y, cw, ph);
                    double by = y + ph - over, bx = callout ? x : x + cw * 0.18, bw = callout ? cw : cw * 0.82 + 8;
                    if (callout)
                        sb.Append($"<polygon points=\"{F(bx)},{F(by)} {F(bx + 24)},{F(by)} {F(bx + 34)},{F(by - 14)} {F(bx + 46)},{F(by)} {F(bx + bw)},{F(by)} {F(bx + bw)},{F(by + capH)} {F(bx)},{F(by + capH)}\" fill=\"{color}\"/>");
                    else
                        sb.Append($"<rect x=\"{F(bx)}\" y=\"{F(by)}\" width=\"{F(bw)}\" height=\"{F(capH)}\" fill=\"{color}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    Text(sb, bx, by, bw, capH, part[c].Text, part[c].Bullets, "#ffffff", maxFs: 14, alignLeft: true, inset: 10);
                    EndGroup(sb);
                }
                y += ph - over + capH + gap;
            }
            return (BaseW, y - gap + Pad);
        }

        /// <summary>Accented Picture: one large picture for the first item, the rest listed beside it.</summary>
        private static (double, double) DrawAccentedPicture(StringBuilder sb, List<Item> items)
        {
            double pw = 380, ph = 300, x = Pad, y = Pad;
            var first = items[0];
            Group(sb, first);
            Picture(sb, x, y, pw, ph);
            double capH = Math.Max(48, Measure(first.Text, first.Bullets, pw - 40) + 12);
            sb.Append($"<rect x=\"{F(x + 20)}\" y=\"{F(y + ph - capH - 20)}\" width=\"{F(pw - 40)}\" height=\"{F(capH)}\" rx=\"4\" fill=\"{Accent(0)}\" fill-opacity=\"0.9\"/>");
            Text(sb, x + 20, y + ph - capH - 20, pw - 40, capH, first.Text, first.Bullets, "#ffffff", alignLeft: true, inset: 12);
            EndGroup(sb);
            var rest = items.Skip(1).ToList();
            double lx = x + pw + 24, lw = BaseW - Pad - lx;
            double rowH = rest.Count == 0 ? 0 : Math.Min(80, (ph - (rest.Count - 1) * 10) / rest.Count);
            double h = ph;
            for (int i = 0; i < rest.Count; i++)
            {
                double ry = y + i * (rowH + 10);
                double need = Math.Max(rowH, Measure(rest[i].Text, rest[i].Bullets, lw - 40, 13) + 10);
                Group(sb, rest[i]);
                sb.Append($"<circle cx=\"{F(lx + 12)}\" cy=\"{F(ry + 16)}\" r=\"10\" fill=\"{Accent(i + 1)}\"/>");
                Text(sb, lx + 30, ry, lw - 30, need, rest[i].Text, rest[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                EndGroup(sb);
                h = Math.Max(h, ry + need - y);
            }
            return (BaseW, y + h + Pad);
        }
    }
}
