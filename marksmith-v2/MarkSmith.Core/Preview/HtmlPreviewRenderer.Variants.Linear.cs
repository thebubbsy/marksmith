using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MarkSmith.Core.Preview
{
    // Run #51: the timelines, horizontal lists, chevrons, balances and org charts. 38 layouts drew
    // as 5 pictures: all nine timelines were the same alternating line, the eight balance and arrow
    // layouts were all a see-saw, and Half Circle and Circle Picture org charts were plain boxes.
    public static partial class HtmlPreviewRenderer
    {
        // ------------------------------------------------------------------ shared shapes

        /// <summary>A block arrow filling (x, y, w, h), pointing R, L, U or D. The shaft is
        /// <paramref name="shaft"/> of the arrow's thickness.</summary>
        private static void BlockArrow(StringBuilder sb, double x, double y, double w, double h, char dir, string fill, double shaft = 0.62)
        {
            string pts;
            if (dir is 'R' or 'L')
            {
                double head = Math.Min(h * 0.75, w * 0.4), s = h * shaft, top = y + (h - s) / 2, bot = top + s, cy = y + h / 2;
                pts = dir == 'R'
                    ? $"{F(x)},{F(top)} {F(x + w - head)},{F(top)} {F(x + w - head)},{F(y)} {F(x + w)},{F(cy)} {F(x + w - head)},{F(y + h)} {F(x + w - head)},{F(bot)} {F(x)},{F(bot)}"
                    : $"{F(x + w)},{F(top)} {F(x + head)},{F(top)} {F(x + head)},{F(y)} {F(x)},{F(cy)} {F(x + head)},{F(y + h)} {F(x + head)},{F(bot)} {F(x + w)},{F(bot)}";
            }
            else
            {
                double head = Math.Min(w * 0.75, h * 0.4), s = w * shaft, l = x + (w - s) / 2, r = l + s, cx = x + w / 2;
                pts = dir == 'U'
                    ? $"{F(l)},{F(y + h)} {F(l)},{F(y + head)} {F(x)},{F(y + head)} {F(cx)},{F(y)} {F(x + w)},{F(y + head)} {F(r)},{F(y + head)} {F(r)},{F(y + h)}"
                    : $"{F(l)},{F(y)} {F(l)},{F(y + h - head)} {F(x)},{F(y + h - head)} {F(cx)},{F(y + h)} {F(x + w)},{F(y + h - head)} {F(r)},{F(y + h - head)} {F(r)},{F(y)}";
            }
            sb.Append($"<polygon points=\"{pts}\" fill=\"{fill}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
        }

        /// <summary>A right-pointing chevron w wide (point included); the first of a run has a flat back.</summary>
        private static string ChevronPts(double x, double y, double w, double h, double point, bool flatBack) =>
            $"{F(x)},{F(y)} {F(x + w - point)},{F(y)} {F(x + w)},{F(y + h / 2)} {F(x + w - point)},{F(y + h)} {F(x)},{F(y + h)}"
            + (flatBack ? "" : $" {F(x + point)},{F(y + h / 2)}");

        /// <summary>A box with its top-left and bottom-right corners rounded (Office's round2DiagRect).</summary>
        private static string DiagRoundD(double x, double y, double w, double h, double r)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            return $"M{F(x + r)} {F(y)}H{F(x + w)}V{F(y + h - r)}A{F(r)} {F(r)} 0 0 1 {F(x + w - r)} {F(y + h)}H{F(x)}V{F(y + r)}A{F(r)} {F(r)} 0 0 1 {F(x + r)} {F(y)}Z";
        }

        /// <summary>The number of an item, white in its marker (stripped from thumbnails with the rest of the text).</summary>
        private static void Number(StringBuilder sb, double cx, double cy, double r, int i) =>
            Text(sb, cx - r, cy - r, r * 2, r * 2, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Array.Empty<string>(), "#ffffff", maxFs: Math.Min(18, r), inset: 0, group: "number");

        /// <summary>Text hanging from a marker: as tall as it needs and set from the top, so a short
        /// item's label lines up with a long one's instead of sagging to the middle of a shared slot.</summary>
        private static void TopText(StringBuilder sb, double x, double y, double w, string title, IReadOnlyList<string> bullets, string color, double fs, bool alignLeft = false, double inset = 4) =>
            Text(sb, x, y, w, Measure(title, bullets, w - inset * 2, fs) + 8, title, bullets, color, maxFs: fs, alignLeft: alignLeft, inset: inset);

        /// <summary>Columns of up to <paramref name="maxCols"/> items, wrapping to more rows; <paramref name="row"/>
        /// draws one row (its items, the index of the first, its top, the column width, the gap) and
        /// returns the row's height.</summary>
        private static (double, double) InColumns(List<Item> items, int maxCols, double gap, Func<List<Item>, int, double, double, double> row)
        {
            int cols = Math.Max(1, Math.Min(items.Count, maxCols));
            double cw = (BaseW - Pad * 2 - (cols - 1) * gap) / cols, y = Pad;
            for (int start = 0; start < items.Count; start += cols)
                y += row(items.Skip(start).Take(cols).ToList(), start, y, cw) + gap * 1.5;
            return (BaseW, y - gap * 1.5 + Pad);
        }

        private static double ColX(int c, double cw, double gap) => Pad + c * (cw + gap);

        // ------------------------------------------------------------------ timelines

        /// <summary>Numbered Dots and Small Dots, horizontal: one line with a marker per item and the
        /// text under it. Numbered draws large numbered circles with the text centred beneath; Small
        /// Dots draws small dots with the text hanging left-aligned from each.</summary>
        private static (double, double) DrawDotTimeline(StringBuilder sb, List<Item> items, bool numbered)
        {
            int n = items.Count;
            double colW = Math.Max(110, (BaseW - Pad * 2) / n), w = Math.Max(BaseW, Pad * 2 + n * colW);
            double r = numbered ? 20 : 9, lineY = Pad + r;
            double textH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, colW - 18, 13)) + 10);
            sb.Append($"<line x1=\"{F(Pad)}\" y1=\"{F(lineY)}\" x2=\"{F(w - Pad)}\" y2=\"{F(lineY)}\" stroke=\"#c8c6c4\" stroke-width=\"{(numbered ? 4 : 2)}\"/>");
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * colW, cx = numbered ? x + colW / 2 : x + r + 4;
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(lineY)}\" r=\"{F(r)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"{(numbered ? 3 : 2)}\"/>");
                if (numbered) Number(sb, cx, lineY, r, i);
                TopText(sb, numbered ? x + 4 : x, lineY + r + 10, colW - 8, items[i].Text, items[i].Bullets, Ink, 13, alignLeft: !numbered, inset: numbered ? 6 : 2);
                EndGroup(sb);
            }
            return (w, lineY + r + 10 + textH + Pad);
        }

        /// <summary>Numbered Dots and Small Dots, vertical. Numbered: numbered circles down a line
        /// with the text beside each. Small Dots: the title left of the line and the detail right of it.</summary>
        private static (double, double) DrawDotTimelineVertical(StringBuilder sb, List<Item> items, bool numbered)
        {
            int n = items.Count;
            double r = numbered ? 20 : 9, lineX = numbered ? Pad + 24 : 250;
            double titleX = Pad, titleW = lineX - 24 - Pad, bodyX = lineX + (numbered ? r + 16 : 24), bodyW = BaseW - Pad - bodyX;
            var rowH = items.Select(i => numbered
                ? Math.Max(r * 2 + 16, Measure(i.Text, i.Bullets, bodyW - 8) + 14)
                : Math.Max(40, Math.Max(Measure(i.Text, Array.Empty<string>(), titleW - 8), Measure("", i.Bullets, bodyW - 8, 13)) + 12)).ToList();
            double top = Pad, y = Pad;
            double firstC = top + rowH[0] / 2, lastC = top + rowH.Take(n - 1).Sum() + 10 * (n - 1) + rowH[^1] / 2;
            sb.Append($"<line x1=\"{F(lineX)}\" y1=\"{F(n == 1 ? Pad : firstC)}\" x2=\"{F(lineX)}\" y2=\"{F(n == 1 ? Pad + rowH[0] : lastC)}\" stroke=\"#c8c6c4\" stroke-width=\"{(numbered ? 4 : 2)}\"/>");
            for (int i = 0; i < n; i++)
            {
                double cy = y + rowH[i] / 2;
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(lineX)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{Accent(i)}\" stroke=\"#ffffff\" stroke-width=\"{(numbered ? 3 : 2)}\"/>");
                if (numbered)
                {
                    Number(sb, lineX, cy, r, i);
                    Text(sb, bodyX, y, bodyW, rowH[i], items[i].Text, items[i].Bullets, Ink, maxFs: 14, alignLeft: true, inset: 2);
                }
                else
                {
                    Text(sb, titleX, y, titleW, rowH[i], items[i].Text, Array.Empty<string>(), Ink, maxFs: 14, inset: 4);
                    if (items[i].Children.Count > 0) Text(sb, bodyX, y, bodyW, rowH[i], "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 2);
                }
                EndGroup(sb);
                y += rowH[i] + 10;
            }
            return (BaseW, y - 10 + Pad);
        }

        /// <summary>Bullet Timeline: a broad arrow; each item's title in a coloured tag pinned to it
        /// from above, its detail below. Inverted swaps the two sides.</summary>
        private static (double, double) DrawBulletTimeline(StringBuilder sb, List<Item> items, bool inverted)
        {
            int n = items.Count;
            double colW = Math.Max(120, (BaseW - Pad * 2 - 20) / n), w = Math.Max(BaseW, Pad * 2 + 20 + n * colW);
            double tagW = colW - 20;
            double tagH = Math.Max(40, items.Max(i => Measure(i.Text, Array.Empty<string>(), tagW - 16)) + 14);
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(30, items.Max(i => Measure("", i.Bullets, colW - 16, 13)) + 10) : 0;
            const double pin = 14, barH = 16, gap = 10;
            double barY = inverted ? Pad + (anyBody ? bodyH + gap : 0) : Pad + tagH + pin + 4;
            string bar = Tint(Accent(0), 0.6);
            sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(barY)}\" width=\"{F(w - Pad * 2 - 24)}\" height=\"{F(barH)}\" fill=\"{bar}\"/>");
            Tri(sb, w - Pad, barY + barH / 2, 0, 28, bar);
            for (int i = 0; i < n; i++)
            {
                double cx = Pad + (i + 0.5) * colW;
                string c = Accent(i);
                double tagY = inverted ? barY + barH + pin + 4 : Pad;
                double bodyY = inverted ? Pad : barY + barH + gap;
                Group(sb, items[i]);
                sb.Append($"<rect x=\"{F(cx - tagW / 2)}\" y=\"{F(tagY)}\" width=\"{F(tagW)}\" height=\"{F(tagH)}\" rx=\"8\" fill=\"{c}\"/>");
                // The tag's pin reaches the bar.
                if (inverted) Tri(sb, cx, barY + barH + 2, -Math.PI / 2, pin + 4, c);
                else Tri(sb, cx, barY - 2, Math.PI / 2, pin + 4, c);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(barY + barH / 2)}\" r=\"7\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"3\"/>");
                Text(sb, cx - tagW / 2, tagY, tagW, tagH, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 8);
                if (items[i].Children.Count > 0)
                {
                    // Below the bar the detail hangs from it; above it, it sits on it.
                    double need = Measure("", items[i].Bullets, colW - 20, 13) + 8;
                    Text(sb, cx - colW / 2 + 6, inverted ? bodyY + bodyH - need : bodyY, colW - 12, need, "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 4);
                }
                EndGroup(sb);
            }
            double bottom = inverted ? barY + barH + pin + 4 + tagH : barY + barH + (anyBody ? gap + bodyH : 0);
            return (w, bottom + Pad);
        }

        /// <summary>Circle Accent Timeline: a ring on the line for each item, a small dot after it for
        /// each of its sub-items, labels under them.</summary>
        private static (double, double) DrawCircleAccentTimeline(StringBuilder sb, List<Item> items)
        {
            double units = items.Sum(i => 1 + 0.6 * i.Children.Count);
            double unit = Math.Max(110, (BaseW - Pad * 2) / units), w = Math.Max(BaseW, Pad * 2 + units * unit);
            double R = 30, lineY = Pad + R;
            double titleH = Math.Max(36, items.Max(i => Measure(i.Text, Array.Empty<string>(), unit - 12, 14)) + 8);
            double kidH = items.SelectMany(i => i.Children).Select(k => Measure(k.Text, Array.Empty<string>(), unit * 0.6 - 6, 12)).DefaultIfEmpty(0).Max() + 8;
            sb.Append($"<line x1=\"{F(Pad)}\" y1=\"{F(lineY)}\" x2=\"{F(w - Pad)}\" y2=\"{F(lineY)}\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            double x = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                string c = Accent(i);
                double cx = x + unit / 2;
                Group(sb, items[i]);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(lineY)}\" r=\"{F(R)}\" fill=\"#ffffff\" stroke=\"{c}\" stroke-width=\"12\"/>");
                TopText(sb, x + 4, lineY + R + 10, unit - 8, items[i].Text, Array.Empty<string>(), Ink, 14, inset: 2);
                x += unit;
                foreach (var k in items[i].Children)
                {
                    double kw = unit * 0.6, kx = x + kw / 2;
                    sb.Append($"<circle cx=\"{F(kx)}\" cy=\"{F(lineY)}\" r=\"9\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                    TopText(sb, x, lineY + 16, kw, k.Text, Array.Empty<string>(), Ink, 12, inset: 2);
                    x += kw;
                }
                EndGroup(sb);
            }
            return (w, lineY + R + 10 + Math.Max(titleH, kidH) + Pad);
        }

        /// <summary>Alternating Circle Process: numbered circles down the middle; each title in a card
        /// on one side, its detail on the other, the sides swapping every step.</summary>
        private static (double, double) DrawAlternatingCircles(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double r = 26, cx = BaseW / 2, sideW = cx - r - 22 - Pad;
            var rowH = items.Select(i => Math.Max(r * 2 + 8, Math.Max(Measure(i.Text, Array.Empty<string>(), sideW - 24), Measure("", i.Bullets, sideW - 16, 13)) + 18)).ToList();
            double y = Pad;
            if (n > 1)
                sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(Pad + rowH[0] / 2)}\" x2=\"{F(cx)}\" y2=\"{F(Pad + rowH.Take(n - 1).Sum() + 14 * (n - 1) + rowH[^1] / 2)}\" stroke=\"#c8c6c4\" stroke-width=\"3\"/>");
            for (int i = 0; i < n; i++)
            {
                string c = Accent(i);
                double cy = y + rowH[i] / 2;
                bool left = i % 2 == 0;
                double cardX = left ? Pad : cx + r + 22, otherX = left ? cx + r + 22 : Pad;
                Group(sb, items[i]);
                sb.Append($"<line x1=\"{F(left ? cardX + sideW : cx + r)}\" y1=\"{F(cy)}\" x2=\"{F(left ? cx - r : cardX)}\" y2=\"{F(cy)}\" stroke=\"{Tint(c, 0.4)}\" stroke-width=\"2\"/>");
                sb.Append($"<rect x=\"{F(cardX)}\" y=\"{F(y)}\" width=\"{F(sideW)}\" height=\"{F(rowH[i])}\" rx=\"10\" fill=\"{Tint(c, 0.82)}\"/>");
                Text(sb, cardX, y, sideW, rowH[i], items[i].Text, Array.Empty<string>(), Ink, maxFs: 15, inset: 12);
                if (items[i].Children.Count > 0) Text(sb, otherX, y, sideW, rowH[i], "", items[i].Bullets, Ink, maxFs: 13, alignLeft: true, inset: 8);
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Number(sb, cx, cy, r, i);
                EndGroup(sb);
                y += rowH[i] + 14;
            }
            return (BaseW, y - 14 + Pad);
        }

        // ------------------------------------------------------------------ horizontal lists

        /// <summary>Horizontal Picture List: a picture over each column's coloured title band, the
        /// detail under it.</summary>
        private static (double, double) DrawHListPictures(StringBuilder sb, List<Item> items) => InColumns(items, 5, 14, (part, first, y, cw) =>
        {
            double picH = Math.Min(110, cw * 0.62);
            double headH = Math.Max(40, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 20)) + 14);
            bool anyBody = part.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(40, part.Max(i => Measure("", i.Bullets, cw - 20, 13)) + 14) : 0;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 14);
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(picH + headH + bodyH)}\" rx=\"6\" fill=\"{Tint(col, 0.88)}\"/>");
                Picture(sb, x + 6, y + 6, cw - 12, picH - 6);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + picH + 4)}\" width=\"{F(cw)}\" height=\"{F(headH)}\" fill=\"{col}\"/>");
                Text(sb, x, y + picH + 4, cw, headH, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                if (anyBody) Text(sb, x, y + picH + 4 + headH, cw, bodyH - 4, "", part[c].Bullets, Ink, maxFs: 13, alignLeft: true);
                EndGroup(sb);
            }
            return picH + headH + bodyH;
        });

        /// <summary>Circle-headed columns: each title in a circle that sits on the top edge of its card.</summary>
        private static (double, double) DrawHListCircleHeads(StringBuilder sb, List<Item> items) => InColumns(items, 5, 16, (part, first, y, cw) =>
        {
            double r = Math.Min(cw / 2 - 6, Math.Max(46, Math.Min(64, RadiusForWords(part) + 6)));
            bool anyBody = part.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(44, part.Max(i => Measure("", i.Bullets, cw - 20, 13)) + 16) : 10;
            double cardY = y + r * 1.4, cardH = r * 0.6 + bodyH + 8;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 16), cx = x + cw / 2;
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(cardY)}\" width=\"{F(cw)}\" height=\"{F(cardH)}\" rx=\"8\" fill=\"{Tint(col, 0.85)}\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(y + r)}\" r=\"{F(r)}\" fill=\"{col}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Text(sb, cx - r * 0.75, y + r * 0.35, r * 1.5, r * 1.3, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 2);
                if (anyBody) Text(sb, x, y + r * 2 + 6, cw, bodyH, "", part[c].Bullets, Ink, maxFs: 13, alignLeft: true);
                EndGroup(sb);
            }
            return cardY + cardH - y;
        });

        /// <summary>Horizontal Action List: a tab per step with its detail card under it, arrows
        /// leading from one tab to the next.</summary>
        private static (double, double) DrawHListAction(StringBuilder sb, List<Item> items) => InColumns(items, 5, 28, (part, first, y, cw) =>
        {
            double headH = Math.Max(44, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 20)) + 16);
            bool anyBody = part.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(60, part.Max(i => Measure("", i.Bullets, cw - 20, 13)) + 18) : 0;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 28);
                string col = Accent(first + c);
                Group(sb, part[c]);
                if (anyBody)
                {
                    sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + headH - 2)}\" width=\"{F(cw)}\" height=\"{F(bodyH + 2)}\" fill=\"#ffffff\" stroke=\"{Tint(col, 0.45)}\" stroke-width=\"1.5\"/>");
                    Text(sb, x, y + headH, cw, bodyH, "", part[c].Bullets, Ink, maxFs: 13, alignLeft: true);
                }
                sb.Append($"<path d=\"{TabD(x, y, cw, headH, 12)}\" fill=\"{col}\"/>");
                Text(sb, x, y, cw, headH, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 14);
                EndGroup(sb);
                if (c < part.Count - 1) Tri(sb, x + cw + 22, y + headH / 2, 0, 14, Connector);
            }
            return headH + bodyH;
        });

        /// <summary>Trapezoid List: each item a trapezoid, narrow at the top, its title and detail inside.</summary>
        private static (double, double) DrawHListTrapezoids(StringBuilder sb, List<Item> items) => InColumns(items, 5, 6, (part, first, y, cw) =>
        {
            double inset = cw * 0.14;
            double h = Math.Max(120, part.Max(i => Measure(i.Text, i.Bullets, cw - inset * 2 - 8)) + 34);
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 6);
                Group(sb, part[c]);
                sb.Append($"<polygon points=\"{F(x + inset)},{F(y)} {F(x + cw - inset)},{F(y)} {F(x + cw)},{F(y + h)} {F(x)},{F(y + h)}\" fill=\"{Accent(first + c)}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                Text(sb, x + inset * 0.6, y + 6, cw - inset * 1.2, h - 12, part[c].Text, part[c].Bullets, "#ffffff", maxFs: 14, inset: 6);
                EndGroup(sb);
            }
            return h;
        });

        /// <summary>Table List: the first item across the top as the table's heading, everything
        /// under it as columns, closed by a base rule.</summary>
        private static (double, double) DrawHListTable(StringBuilder sb, List<Item> items)
        {
            SplitHub(items, out var head, out var cols);
            double w = BaseW - Pad * 2;
            double headH = Math.Max(48, Measure(head.Text, head.Bullets, w - 40) + 18);
            Box(sb, Pad, Pad, w, headH, Accent(0), head, rx: 4);
            if (cols.Count == 0) return (BaseW, headH + Pad * 2);
            int per = Math.Min(cols.Count, 6);
            double gap = 6, cw = (w - (per - 1) * gap) / per, y = Pad + headH + gap;
            for (int start = 0; start < cols.Count; start += per)
            {
                var part = cols.Skip(start).Take(per).ToList();
                double ch = Math.Max(90, part.Max(i => Measure(i.Text, i.Bullets, cw - 20)) + 24);
                for (int c = 0; c < part.Count; c++)
                    Box(sb, Pad + c * (cw + gap), y, cw, ch, Tint(Accent(0), 0.18 + 0.22 * ((start + c) % 2)), part[c], rx: 2);
                y += ch + gap;
            }
            sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"10\" fill=\"{Tint(Accent(0), 0.5)}\"/>");
            return (BaseW, y + 10 + Pad);
        }

        /// <summary>Stacked List: a rounded title over a stack of pills, one per detail line.</summary>
        private static (double, double) DrawHListStacked(StringBuilder sb, List<Item> items) => InColumns(items, 5, 16, (part, first, y, cw) =>
        {
            double headH = Math.Max(48, part.Max(i => Measure(i.Text, Array.Empty<string>(), cw - 24)) + 18);
            const double pillGap = 6;
            double PillH(string b) => Math.Max(30, Measure("", new[] { b }, cw - 36, 13) + 10);
            double tallest = 0;
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 16), py = y + headH + pillGap;
                string col = Accent(first + c);
                Group(sb, part[c]);
                foreach (var b in part[c].Bullets)
                {
                    double ph = PillH(b);
                    sb.Append($"<rect x=\"{F(x + 8)}\" y=\"{F(py)}\" width=\"{F(cw - 16)}\" height=\"{F(ph)}\" rx=\"{F(ph / 2)}\" fill=\"{Tint(col, 0.82)}\"/>");
                    Text(sb, x + 8, py, cw - 16, ph, "", new[] { b }, Ink, maxFs: 13, inset: 12);
                    py += ph + pillGap;
                }
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(cw)}\" height=\"{F(headH)}\" rx=\"{F(headH / 2)}\" fill=\"{col}\"/>");
                Text(sb, x, y, cw, headH, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 15, inset: 14);
                EndGroup(sb);
                tallest = Math.Max(tallest, py - pillGap - y);
            }
            return tallest;
        });

        /// <summary>Tab List: tabs standing on one long rule, each item's detail under its tab.</summary>
        private static (double, double) DrawHListTabs(StringBuilder sb, List<Item> items) => InColumns(items, 5, 14, (part, first, y, cw) =>
        {
            double tabW = cw * 0.86, headH = Math.Max(40, part.Max(i => Measure(i.Text, Array.Empty<string>(), tabW - 20)) + 14);
            bool anyBody = part.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(36, part.Max(i => Measure("", i.Bullets, cw - 12, 13)) + 12) : 0;
            double ruleY = y + headH;
            double lastX = ColX(part.Count - 1, cw, 14) + cw;
            sb.Append($"<rect x=\"{F(Pad)}\" y=\"{F(ruleY)}\" width=\"{F(lastX - Pad)}\" height=\"4\" fill=\"{Tint(Accent(first), 0.35)}\"/>");
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 14);
                Group(sb, part[c]);
                sb.Append($"<path d=\"{TabD(x, y, tabW, headH, 10)}\" fill=\"{Accent(first + c)}\"/>");
                Text(sb, x, y, tabW, headH, part[c].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, alignLeft: true, inset: 12);
                if (anyBody) TopText(sb, x, ruleY + 10, cw, "", part[c].Bullets, Ink, 13, alignLeft: true);
                EndGroup(sb);
            }
            return headH + 4 + (anyBody ? 10 + bodyH : 0);
        });

        // ------------------------------------------------------------------ chevrons

        /// <summary>Vertical Chevron List: downward chevrons stacked one into the next, each step's
        /// detail in a card beside it.</summary>
        private static (double, double) DrawVerticalChevrons(StringBuilder sb, List<Item> items)
        {
            const double cw = 128, tip = 22, gap = 6;
            double bodyX = Pad + cw + 16, bodyW = BaseW - Pad - bodyX, y = Pad;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string c = Accent(i);
                double h = Math.Max(76, Math.Max(Measure(it.Text, Array.Empty<string>(), cw - 20) + tip + 20, Measure("", it.Bullets, bodyW - 24, 13) + 20));
                Group(sb, it);
                string notch = i == 0 ? "" : $" {F(Pad + cw / 2)},{F(y + tip)}";
                sb.Append($"<polygon points=\"{F(Pad)},{F(y)}{notch} {F(Pad + cw)},{F(y)} {F(Pad + cw)},{F(y + h)} {F(Pad + cw / 2)},{F(y + h + tip)} {F(Pad)},{F(y + h)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, Pad, y + (i == 0 ? 0 : tip), cw, h - (i == 0 ? 0 : tip) + tip / 2, it.Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 8);
                sb.Append($"<path d=\"M{F(bodyX)} {F(y)}H{F(BaseW - Pad - 12)}A12 12 0 0 1 {F(BaseW - Pad)} {F(y + 12)}V{F(y + h - 12)}A12 12 0 0 1 {F(BaseW - Pad - 12)} {F(y + h)}H{F(bodyX)}Z\" fill=\"{Tint(c, 0.86)}\"/>");
                if (it.Children.Count > 0) Text(sb, bodyX, y, bodyW, h, "", it.Bullets, Ink, maxFs: 13, alignLeft: true, inset: 12);
                EndGroup(sb);
                y += h + gap;
            }
            return (BaseW, y - gap + tip + Pad);
        }

        /// <summary>Numbered Linear Arrow Process: a run of slim chevrons, a numbered circle on each,
        /// the step's text beneath.</summary>
        private static (double, double) DrawNumberedChevrons(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double colW = Math.Max(110, (BaseW - Pad * 2) / n), w = Math.Max(BaseW, Pad * 2 + n * colW);
            const double r = 22, ch = 30;
            double chevY = Pad + r - ch / 2;
            double textH = Math.Max(40, items.Max(i => Measure(i.Text, i.Bullets, colW - 16, 13)) + 10);
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * colW, cx = x + colW / 2;
                string c = Accent(i);
                Group(sb, items[i]);
                sb.Append($"<polygon points=\"{ChevronPts(x, chevY, colW + (i < n - 1 ? 6 : 0), ch, 16, i == 0)}\" fill=\"{Tint(c, 0.55)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(Pad + r)}\" r=\"{F(r)}\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"3\"/>");
                Number(sb, cx, Pad + r, r, i);
                TopText(sb, x + 4, Pad + r * 2 + 12, colW - 8, items[i].Text, items[i].Bullets, Ink, 13);
                EndGroup(sb);
            }
            return (w, Pad + r * 2 + 12 + textH + Pad);
        }

        /// <summary>Increasing Arrows Process: one arrow per step, each starting a column further
        /// right and all reaching the edge, with each step's detail under its arrow's start.</summary>
        private static (double, double) DrawIncreasingArrows(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            double colW = Math.Max(120, (BaseW - Pad * 2) / n), w = Math.Max(BaseW, Pad * 2 + n * colW);
            double ah = Math.Max(44, items.Max(i => Measure(i.Text, Array.Empty<string>(), colW - 24)) + 12);
            const double gap = 6;
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * colW, y = Pad + i * (ah + gap);
                Group(sb, items[i]);
                BlockArrow(sb, x, y, w - Pad - x, ah, 'R', Accent(i), shaft: 0.78);
                Text(sb, x + 4, y, colW - 8, ah, items[i].Text, Array.Empty<string>(), "#ffffff", maxFs: 14, alignLeft: true, inset: 10);
                EndGroup(sb);
            }
            double bodyY = Pad + n * (ah + gap) + 6;
            bool anyBody = items.Any(i => i.Children.Count > 0);
            double bodyH = anyBody ? Math.Max(30, items.Max(i => Measure("", i.Bullets, colW - 12, 13)) + 10) : 0;
            for (int i = 0; i < n && anyBody; i++)
            {
                if (items[i].Children.Count == 0) continue;
                double x = Pad + i * colW;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(bodyY)}\" width=\"3\" height=\"{F(bodyH)}\" fill=\"{Accent(i)}\"/>");
                TopText(sb, x + 6, bodyY, colW - 10, "", items[i].Bullets, Ink, 13, alignLeft: true);
            }
            return (w, bodyY - 6 + (anyBody ? 6 + bodyH : 0) + Pad);
        }

        /// <summary>Chevron Accent Process: rounded cards, each with a chevron tucked behind its top-left corner.</summary>
        private static (double, double) DrawChevronAccentCards(StringBuilder sb, List<Item> items) => InColumns(items, 5, 18, (part, first, y, cw) =>
        {
            double h = Math.Max(84, part.Max(i => Measure(i.Text, i.Bullets, cw - 24)) + 30);
            for (int c = 0; c < part.Count; c++)
            {
                double x = ColX(c, cw, 18);
                string col = Accent(first + c);
                Group(sb, part[c]);
                sb.Append($"<polygon points=\"{ChevronPts(x, y, Math.Min(cw * 0.55, 90), 34, 14, true)}\" fill=\"{col}\"/>");
                sb.Append($"<rect x=\"{F(x + 8)}\" y=\"{F(y + 14)}\" width=\"{F(cw - 8)}\" height=\"{F(h - 14)}\" rx=\"10\" fill=\"{Tint(col, 0.85)}\" stroke=\"#ffffff\" stroke-width=\"2\"/>");
                Text(sb, x + 8, y + 20, cw - 8, h - 22, part[c].Text, part[c].Bullets, Ink, maxFs: 14);
                EndGroup(sb);
            }
            return h;
        });

        /// <summary>Closed Chevron Process: a home plate and then chevrons, set apart, in one colour
        /// growing lighter, each holding its whole step.</summary>
        private static (double, double) DrawClosedChevrons(StringBuilder sb, List<Item> items)
        {
            int n = items.Count;
            const double point = 26, gap = 6;
            double cw = Math.Max(110, (BaseW - Pad * 2 - (n - 1) * gap) / n), w = Math.Max(BaseW, Pad * 2 + n * cw + (n - 1) * gap);
            double h = Math.Max(90, items.Max(i => Measure(i.Text, i.Bullets, cw - point * 2 - 8, 13)) + 24);
            for (int i = 0; i < n; i++)
            {
                double x = Pad + i * (cw + gap);
                double t = n == 1 ? 0 : 0.5 * i / (n - 1);
                Group(sb, items[i]);
                sb.Append($"<polygon points=\"{ChevronPts(x, Pad, cw, h, point, i == 0)}\" fill=\"{Tint(Accent(0), t)}\"/>");
                Text(sb, x + (i == 0 ? 6 : point), Pad, cw - point - (i == 0 ? 6 : point), h, items[i].Text, items[i].Bullets, "#ffffff", maxFs: 14, inset: 2);
                EndGroup(sb);
            }
            return (w, h + Pad * 2);
        }

        // ------------------------------------------------------------------ balance and arrows

        /// <summary>Two sides: two items with sub-items weigh one against the other; otherwise the
        /// list splits in half. A side made from a split has no title of its own.</summary>
        private static void SplitSides(List<Item> items, out Item left, out Item right)
        {
            if (items.Count == 2 && items.Any(i => i.Children.Count > 0)) { left = items[0]; right = items[1]; return; }
            int split = (items.Count + 1) / 2;
            left = new Item { Text = "", Children = items.Take(split).ToList() };
            right = new Item { Text = "", Children = items.Skip(split).ToList() };
        }

        /// <summary>Every line under a side, however deep: its items, and below them each level
        /// dashed. (<see cref="Item.Bullets"/> stops two levels down, and an org chart split into
        /// two sides is deeper than that.)</summary>
        private static List<string> SideLines(Item side)
        {
            var lines = new List<string>();
            void Walk(Item i, bool top)
            {
                lines.Add(top ? i.Text : "–" + (char)160 + i.Text);
                foreach (var c in i.Children) Walk(c, false);
            }
            foreach (var c in side.Children) Walk(c, true);
            return lines;
        }

        /// <summary>One side's text: its title and every line under it, in a slot sized to hold them all.</summary>
        private static double SideH(Item side, double w, double min) => Math.Max(min, Measure(side.Text, SideLines(side), w - 20) + 24);

        private static void SideText(StringBuilder sb, Item side, double x, double y, double w, double h, string color, bool left = true)
        {
            var lines = SideLines(side);
            Text(sb, x, y, w, h, side.Text, lines, color, maxFs: 15, alignLeft: left && lines.Count > 0);
        }

        /// <summary>Opposing Ideas: two cards with diagonal corners rounded, a rule between them and
        /// arrows pushing them apart.</summary>
        private static (double, double) DrawOpposingIdeas(StringBuilder sb, List<Item> items)
        {
            SplitSides(items, out var l, out var r);
            double cx = BaseW / 2, bw = cx - Pad - 22;
            double h = Math.Max(SideH(l, bw, 110), SideH(r, bw, 110));
            foreach (var (side, x, i) in new[] { (l, Pad, 0), (r, cx + 22, 1) })
            {
                Group(sb, side);
                sb.Append($"<path d=\"{DiagRoundD(x, Pad, bw, h, 26)}\" fill=\"{Tint(Accent(i), 0.82)}\"/>");
                SideText(sb, side, x, Pad, bw, h, Ink);
                EndGroup(sb);
            }
            sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(Pad - 6)}\" x2=\"{F(cx)}\" y2=\"{F(Pad + h + 6)}\" stroke=\"{Connector}\" stroke-width=\"3\"/>");
            double ay = Pad + h + 16;
            BlockArrow(sb, cx - 150, ay, 130, 34, 'L', Tint(Accent(0), 0.25));
            BlockArrow(sb, cx + 20, ay, 130, 34, 'R', Tint(Accent(1), 0.25));
            return (BaseW, ay + 34 + Pad);
        }

        /// <summary>Plus and Minus: a plus over one column and a minus over the other.</summary>
        private static (double, double) DrawPlusMinus(StringBuilder sb, List<Item> items)
        {
            SplitSides(items, out var l, out var r);
            double cx = BaseW / 2, bw = cx - Pad - 14, sym = 56, top = Pad + sym + 14;
            double h = Math.Max(SideH(l, bw, 110), SideH(r, bw, 110));
            foreach (var (side, x, i) in new[] { (l, Pad, 1), (r, cx + 14, 7) })
            {
                string c = Accent(i);
                double sx = x + bw / 2, sy = Pad + sym / 2, arm = sym / 2, th = 16;
                Group(sb, side);
                sb.Append($"<rect x=\"{F(sx - arm)}\" y=\"{F(sy - th / 2)}\" width=\"{F(arm * 2)}\" height=\"{F(th)}\" rx=\"3\" fill=\"{c}\"/>");
                if (i == 1) sb.Append($"<rect x=\"{F(sx - th / 2)}\" y=\"{F(sy - arm)}\" width=\"{F(th)}\" height=\"{F(arm * 2)}\" rx=\"3\" fill=\"{c}\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(bw)}\" height=\"{F(h)}\" rx=\"6\" fill=\"{Tint(c, 0.86)}\"/>");
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(bw)}\" height=\"6\" fill=\"{c}\"/>");
                SideText(sb, side, x, top + 4, bw, h - 4, Ink);
                EndGroup(sb);
            }
            return (BaseW, top + h + Pad);
        }

        /// <summary>Counterbalance Arrows: an up arrow and a down arrow side by side in the middle,
        /// each side's text beside its arrow.</summary>
        private static (double, double) DrawCounterArrows(StringBuilder sb, List<Item> items)
        {
            SplitSides(items, out var l, out var r);
            double cx = BaseW / 2, aw = 64, tw = cx - aw - 24 - Pad;
            double h = Math.Max(150, Math.Max(SideH(l, tw, 80), SideH(r, tw, 80)));
            Group(sb, l);
            BlockArrow(sb, cx - aw - 6, Pad, aw, h, 'U', Accent(0));
            SideText(sb, l, Pad, Pad, tw, h, Ink);
            EndGroup(sb);
            Group(sb, r);
            BlockArrow(sb, cx + 6, Pad, aw, h, 'D', Accent(1));
            SideText(sb, r, cx + aw + 24, Pad, tw, h, Ink);
            EndGroup(sb);
            return (BaseW, h + Pad * 2);
        }

        /// <summary>Up and down arrows stacked on the left, each side's text to its right.</summary>
        private static (double, double) DrawUpDownArrows(StringBuilder sb, List<Item> items)
        {
            SplitSides(items, out var l, out var r);
            double aw = 76, tx = Pad + aw + 28, tw = BaseW - Pad - tx;
            double h1 = Math.Max(96, SideH(l, tw, 60)), h2 = Math.Max(96, SideH(r, tw, 60));
            Group(sb, l);
            BlockArrow(sb, Pad + 10, Pad, aw, h1, 'U', Accent(0));
            sb.Append($"<rect x=\"{F(tx - 12)}\" y=\"{F(Pad)}\" width=\"4\" height=\"{F(h1)}\" fill=\"{Tint(Accent(0), 0.5)}\"/>");
            SideText(sb, l, tx, Pad, tw, h1, Ink);
            EndGroup(sb);
            double y2 = Pad + h1 + 10;
            Group(sb, r);
            BlockArrow(sb, Pad + 10, y2, aw, h2, 'D', Accent(1));
            sb.Append($"<rect x=\"{F(tx - 12)}\" y=\"{F(y2)}\" width=\"4\" height=\"{F(h2)}\" fill=\"{Tint(Accent(1), 0.5)}\"/>");
            SideText(sb, r, tx, y2, tw, h2, Ink);
            EndGroup(sb);
            return (BaseW, y2 + h2 + Pad);
        }

        /// <summary>A side's text inside a horizontal block arrow: in the shaft, clear of the head.
        /// The arrow is made tall enough for its shaft to hold the text.</summary>
        private static void ArrowWithText(StringBuilder sb, Item side, double x, double y, double w, double h, char dir, string fill, double shaft)
        {
            double head = Math.Min(h * 0.75, w * 0.4), s = h * shaft;
            BlockArrow(sb, x, y, w, h, dir, fill, shaft);
            SideText(sb, side, dir == 'R' ? x + 6 : x + head + 6, y + (h - s) / 2, w - head - 12, s, "#ffffff");
        }

        /// <summary>How tall a horizontal arrow w wide must be for its shaft to hold a side's text.</summary>
        private static double ArrowH(Item side, double w, double shaft, double min)
        {
            double h = min;
            // The head grows with the height, which narrows the shaft's text; two passes settle it.
            for (int k = 0; k < 2; k++) h = Math.Max(min, SideH(side, w - Math.Min(h * 0.75, w * 0.4) - 12, 40) / shaft);
            return h;
        }

        /// <summary>Arrow Ribbon: one broad arrow pointing right over another pointing left.</summary>
        private static (double, double) DrawArrowRibbon(StringBuilder sb, List<Item> items)
        {
            SplitSides(items, out var l, out var r);
            const double shaft = 0.72;
            double aw = BaseW - Pad * 2 - 70;
            double h1 = ArrowH(l, aw, shaft, 84), h2 = ArrowH(r, aw, shaft, 84);
            Group(sb, l);
            ArrowWithText(sb, l, Pad + 70, Pad, aw, h1, 'R', Accent(0), shaft);
            EndGroup(sb);
            // The second arrow tucks under the first one's head, the way the ribbon interlocks.
            double y2 = Pad + h1 * (1 + shaft) / 2 + 6;
            Group(sb, r);
            ArrowWithText(sb, r, Pad, y2, aw, h2, 'L', Accent(1), shaft);
            EndGroup(sb);
            return (BaseW, y2 + h2 + Pad);
        }

        /// <summary>Converging Arrows (pointing at each other) and Diverging Arrows (pointing apart),
        /// each side's text in its arrow.</summary>
        private static (double, double) DrawFacingArrows(StringBuilder sb, List<Item> items, bool converging)
        {
            SplitSides(items, out var l, out var r);
            const double shaft = 0.74;
            double cx = BaseW / 2, aw = cx - Pad - 8;
            double h = Math.Max(ArrowH(l, aw, shaft, 100), ArrowH(r, aw, shaft, 100));
            Group(sb, l);
            ArrowWithText(sb, l, Pad, Pad, aw, h, converging ? 'R' : 'L', Accent(0), shaft);
            EndGroup(sb);
            Group(sb, r);
            ArrowWithText(sb, r, cx + 8, Pad, aw, h, converging ? 'L' : 'R', Accent(1), shaft);
            EndGroup(sb);
            return (BaseW, h + Pad * 2);
        }

        // ------------------------------------------------------------------ org charts

        internal enum TreeStyle { Org, Rounded, Pictures, NameTitle, HalfCircle, CirclePictures, Labeled }

        /// <summary>A top-down tree (the org chart and its variants). Wide charts hang a parent's
        /// leaf children beneath it. <paramref name="style"/> says how a node is drawn.</summary>
        private static (double, double) DrawVerticalTree(StringBuilder sb, List<Item> items, TreeStyle style)
        {
            var edges = new StringBuilder();
            var nodes = new StringBuilder();
            var (roots, leaves, depth) = BuildTree(items);
            // More than six side-by-side leaves won't stay legible at 800 px: hang them instead.
            if (leaves > 6) (roots, leaves, depth) = BuildTree(items, hang: true);
            double slot = Math.Max(118, (BaseW - Pad * 2) / Math.Max(1, leaves));
            double w = Math.Max(BaseW, Pad * 2 + leaves * slot);
            double boxW = Math.Min(170, slot - 14);
            double boxH = style switch
            {
                TreeStyle.CirclePictures => 104,
                TreeStyle.NameTitle => 66,
                TreeStyle.HalfCircle or TreeStyle.Pictures => 60,
                _ => 56,
            };
            double gapY = style == TreeStyle.Labeled ? 60 : 44, kidH = style == TreeStyle.CirclePictures ? 48 : 40, kidGap = 8, indent = 18;
            double ox = (w - leaves * slot) / 2;
            double X(TNode n) => ox + n.Pos * slot;
            double Y(TNode n) => Pad + n.Depth * (boxH + gapY);
            double bottom = Pad + depth * boxH + (depth - 1) * gapY;

            void Node(StringBuilder s, double x, double y, double nw, double nh, int d, Item item)
            {
                string c = LevelColors[d % LevelColors.Length];
                switch (style)
                {
                    case TreeStyle.Org:
                    case TreeStyle.Labeled:
                        Box(s, x, y, nw, nh, c, item, withBullets: false, rx: style == TreeStyle.Labeled ? 10 : 6);
                        return;
                }
                Group(s, item);
                switch (style)
                {
                    case TreeStyle.Rounded:
                        // Word's Hierarchy: a rounded card over a paler one, offset like a stack.
                        s.Append($"<rect x=\"{F(x + 6)}\" y=\"{F(y + 6)}\" width=\"{F(nw)}\" height=\"{F(nh)}\" rx=\"12\" fill=\"{Tint(c, 0.6)}\"/>");
                        s.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(nw)}\" height=\"{F(nh)}\" rx=\"12\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        Text(s, x, y, nw, nh, item.Text, Array.Empty<string>(), "#ffffff");
                        break;
                    case TreeStyle.Pictures:
                    {
                        double ps = nh - 12;
                        s.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(nw)}\" height=\"{F(nh)}\" rx=\"6\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        Picture(s, x + 6, y + 6, ps, ps);
                        Text(s, x + ps + 8, y, nw - ps - 8, nh, item.Text, Array.Empty<string>(), "#ffffff", maxFs: 14, inset: 4);
                        break;
                    }
                    case TreeStyle.NameTitle:
                    {
                        // Name over a title tab, the tab overlapping the name box's lower edge.
                        double tabH = Math.Min(18, nh * 0.3), mainH = nh - tabH / 2;
                        s.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(nw)}\" height=\"{F(mainH)}\" rx=\"4\" fill=\"{c}\" stroke=\"#ffffff\" stroke-width=\"1.5\"/>");
                        s.Append($"<rect x=\"{F(x + nw * 0.3)}\" y=\"{F(y + nh - tabH)}\" width=\"{F(nw * 0.62)}\" height=\"{F(tabH)}\" rx=\"2\" fill=\"#ffffff\" stroke=\"{Tint(c, 0.35)}\" stroke-width=\"1.5\"/>");
                        Text(s, x, y, nw, mainH - tabH / 2, item.Text, Array.Empty<string>(), "#ffffff");
                        break;
                    }
                    case TreeStyle.HalfCircle:
                    {
                        // The name between two half-ring arcs: over the top right and under the bottom left.
                        double ecx = x + nw / 2, ecy = y + nh / 2, rx = nw / 2, ry = nh / 2;
                        string P(double a) => $"{F(ecx + rx * Math.Cos(a))} {F(ecy + ry * Math.Sin(a))}";
                        s.Append($"<rect x=\"{F(x + 4)}\" y=\"{F(y + 4)}\" width=\"{F(nw - 8)}\" height=\"{F(nh - 8)}\" rx=\"{F((nh - 8) / 2)}\" fill=\"{Tint(c, 0.9)}\"/>");
                        s.Append($"<path d=\"M{P(Math.PI * 1.15)}A{F(rx)} {F(ry)} 0 0 1 {P(Math.PI * 1.95)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"5\" stroke-linecap=\"round\"/>");
                        s.Append($"<path d=\"M{P(Math.PI * 0.15)}A{F(rx)} {F(ry)} 0 0 1 {P(Math.PI * 0.95)}\" fill=\"none\" stroke=\"{c}\" stroke-width=\"5\" stroke-linecap=\"round\"/>");
                        Text(s, x + 8, y + 6, nw - 16, nh - 12, item.Text, Array.Empty<string>(), Ink, maxFs: 14, inset: 4);
                        break;
                    }
                    case TreeStyle.CirclePictures:
                    {
                        if (nh >= 80)
                        {
                            // A round picture with the name under it.
                            double r = (nh - 40) / 2, pcx = x + nw / 2;
                            s.Append($"<circle cx=\"{F(pcx)}\" cy=\"{F(y + r)}\" r=\"{F(r + 3)}\" fill=\"{c}\"/>");
                            Picture(s, pcx - r, y + 3, r * 2, r * 2 - 6, circle: true);
                            Text(s, x - 6, y + r * 2 + 4, nw + 12, 36, item.Text, Array.Empty<string>(), Ink, maxFs: 14, inset: 2);
                        }
                        else
                        {
                            // Hung children: the picture beside the name.
                            double r = nh / 2 - 2;
                            s.Append($"<circle cx=\"{F(x + r + 2)}\" cy=\"{F(y + nh / 2)}\" r=\"{F(r + 2)}\" fill=\"{c}\"/>");
                            Picture(s, x + 2, y + 2, r * 2, r * 2, circle: true);
                            Text(s, x + r * 2 + 8, y, nw - r * 2 - 8, nh, item.Text, Array.Empty<string>(), Ink, maxFs: 13, alignLeft: true, inset: 2);
                        }
                        break;
                    }
                }
                EndGroup(s);
            }

            if (style == TreeStyle.Labeled)
                for (int d = 0; d < depth; d++)
                {
                    // A pale band per level, the labelled tiers of Word's Labeled Hierarchy.
                    double by = Pad + d * (boxH + gapY) - gapY * 0.3;
                    sb.Append($"<rect x=\"{F(Pad / 2)}\" y=\"{F(by)}\" width=\"{F(w - Pad)}\" height=\"{F(boxH + gapY * 0.6)}\" rx=\"8\" fill=\"{Tint(LevelColors[d % LevelColors.Length], 0.9)}\"/>");
                }

            foreach (var n in All(roots))
            {
                if (n.Hung)
                {
                    double left = X(n) - boxW / 2;
                    for (int j = 0; j < n.Kids.Count; j++)
                    {
                        double ky = Y(n) + boxH + kidGap + j * (kidH + kidGap);
                        edges.Append($"<path d=\"M{F(left + indent / 2)} {F(Y(n) + boxH)}V{F(ky + kidH / 2)}H{F(left + indent)}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                        Node(nodes, left + indent, ky, boxW - indent, kidH, n.Depth + 1, n.Kids[j].Item);
                        bottom = Math.Max(bottom, ky + kidH);
                    }
                    Node(nodes, left, Y(n), boxW, boxH, n.Depth, n.Item);
                    continue;
                }
                foreach (var k in n.Kids)
                {
                    double midY = Y(n) + boxH + gapY / 2;
                    edges.Append($"<path d=\"M{F(X(n))} {F(Y(n) + boxH)}V{F(midY)}H{F(X(k))}V{F(Y(k))}\" fill=\"none\" stroke=\"{Connector}\" stroke-width=\"1.5\"/>");
                }
                Node(nodes, X(n) - boxW / 2, Y(n), boxW, boxH, n.Depth, n.Item);
            }
            sb.Append(edges).Append(nodes);
            return (w, bottom + Pad + (style == TreeStyle.Labeled ? gapY * 0.3 : style == TreeStyle.Rounded ? 6 : 0));
        }
    }
}
